using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Real SteamCMD 2430930 stopped-map update. Work is delegated to the existing
/// verified Core backup/queue/Steam updater. No live stop, remote host restart,
/// Store conversion, SQL or arbitrary paths are accepted from the renderer.
/// </summary>
public sealed class DesktopStoppedSteamUpdateBridge : IDisposable
{
    private sealed record Preview(Guid Token,Guid MapId,string Root,string Build,
        string Steam,DateTimeOffset Created);
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public string State="queued",Stage="preflight";
        public string OldBuild="",NewBuild="";
        public bool ProtectedFilesIntact;
        public DateTimeOffset Changed=DateTimeOffset.UtcNow;
    }
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim mutex=new(1,1);
    private readonly object sync=new();
    private readonly Func<LocalMap,SteamUpdatePreview> planner;
    private readonly Func<LocalMap,IProgress<SteamMaintenanceStage>,Task<SteamStoppedMapUpdater.Receipt>> runner;
    private Preview? pending;
    private Job? job;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private const long Reserve=10L*1024*1024*1024;

    public DesktopStoppedSteamUpdateBridge(ClusterService owner,
        Func<LocalMap,SteamUpdatePreview>? plan=null,
        Func<LocalMap,IProgress<SteamMaintenanceStage>,Task<SteamStoppedMapUpdater.Receipt>>? execute=null)
    {
        cluster=owner??throw new ArgumentNullException(nameof(owner));
        planner=plan??(map=>SteamUpdateDryRun.Plan(
            cluster.Config.SteamCmdExecutable,map.InstallationPath,
            cluster.Config.LocalMaps.ToArray(),cluster.Check,Reserve));
        runner=execute??((map,progress)=>SteamStoppedMapUpdater.UpdateAsync(
            cluster,map,cluster.Config.SteamCmdExecutable,Reserve,
            TimeSpan.FromHours(2),progress));
    }
    private bool Stopped(LocalMap map)
    {
        if(map.HistoricalStoreProtected||map.Maintenance)return false;
        string root=Path.GetFullPath(map.InstallationPath);
        return cluster.Config.LocalMaps.Where(x=>
            !string.IsNullOrWhiteSpace(x.InstallationPath)&&
            string.Equals(Path.GetFullPath(x.InstallationPath),root,
                StringComparison.OrdinalIgnoreCase)).All(x=>{
                    var state=cluster.Check(x);
                    return state.ConfirmedStopped&&!state.Running&&
                        state.Identity==null&&state.LoaderIdentity==null;
                });
    }
    private static bool GuidField(JsonElement obj,string key,out Guid value)
    {
        value=Guid.Empty;
        return obj.TryGetProperty(key,out var prop)&&prop.ValueKind==JsonValueKind.String&&
            Guid.TryParse(prop.GetString(),out value)&&value!=Guid.Empty;
    }
    private void Stage(Job item,string state,string stage,
        SteamStoppedMapUpdater.Receipt? receipt=null)
    {
        lock(sync)
        {
            item.State=state;item.Stage=stage;item.Changed=DateTimeOffset.UtcNow;
            if(receipt!=null)
            {
                item.OldBuild=receipt.OldBuild;
                item.NewBuild=receipt.NewBuild;
                item.ProtectedFilesIntact=receipt.ProtectedFilesIntact;
            }
        }
    }
    private object Snapshot(Job item)
    {
        lock(sync)return new{
            jobId=item.Id,state=item.State,stage=item.Stage,
            changedUtc=item.Changed,oldBuild=item.OldBuild,newBuild=item.NewBuild,
            protectedFilesIntact=item.ProtectedFilesIntact,
            restartPerformed=false,storeTouched=false
        };
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3000)return Fail(null,"UM-STEAM-MESSAGE");
            using var document=JsonDocument.Parse(raw);
            var data=document.RootElement;
            if(data.ValueKind!=JsonValueKind.Object||
               !GuidField(data,"id",out var requestId)||
               !data.TryGetProperty("method",out var field)||
               field.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-STEAM-MESSAGE");
            id=requestId.ToString("D");
            string? method=field.GetString();
            if(method is not ("steam.stoppedPreview" or
                "steam.stoppedApply" or "steam.stoppedStatus" or "steam.stoppedCurrent"))
                return Fail(id,"UM-METHOD-DENIED");
            await mutex.WaitAsync();
            try
            {
                if(method=="steam.stoppedCurrent")
                {
                    // Recovery is observational. Only the Core journal can
                    // establish a completed queue after a Manager UI restart.
                    var journal=new SteamSerialMaintenanceQueue(
                        cluster.Store.Root,cluster.Config.LocalHostId).Observe();
                    if(journal==null)
                        return Ok(id,new{exists=false,needsOperatorReview=false,
                            autoResumeAllowed=false});
                    bool active=false;
                    if(job!=null)
                    {
                        lock(sync)active=job.State is "queued" or "running";
                    }
                    bool needsReview=!active && journal.Phase is
                        (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress
                            or SteamQueuePhase.NeedsOperator);
                    string? mapName=cluster.Config.LocalMaps.FirstOrDefault(
                        m=>m.Id==journal.ActiveMap)?.Name;
                    return Ok(id,new{
                        exists=true,queueId=journal.QueueId,
                        phase=journal.Phase.ToString(),
                        activeMapName=mapName,
                        nextIndex=journal.NextIndex,total=journal.MapIds.Length,
                        changedUtc=journal.ChangedUtc,
                        summary=journal.Summary,
                        needsOperatorReview=needsReview,autoResumeAllowed=false,
                        hostName=cluster.Config.LocalHostName,
                        evidence="DURABLE_MACHINE_LOCAL_STEAM_QUEUE_JOURNAL"
                    });
                }
                if(method=="steam.stoppedStatus")
                {
                    if(!GuidField(data,"jobId",out var jobId)||
                       job==null||job.Id!=jobId)
                        return Fail(id,"UM-STEAM-JOB-MISSING");
                    return Ok(id,Snapshot(job));
                }
                if(job!=null)
                {
                    lock(sync)
                    {
                        if(job.State is "queued" or "running")
                            return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    }
                }
                if(method=="steam.stoppedPreview")
                {
                    if(!GuidField(data,"mapId",out var mapId))
                        return Fail(id,"UM-STEAM-MAP");
                    var owned=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId);
                    if(owned==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    if(!Stopped(owned))return Fail(id,"UM-STEAM-STOPPED-ONLY");
                    var steam=SteamCmdPreflight.Inspect(cluster.Config.SteamCmdExecutable);
                    var plan=planner(owned);
                    if(!plan.DryRunAllowed||string.IsNullOrWhiteSpace(plan.InstalledBuild)||
                       !string.Equals(Path.GetFullPath(plan.InstallRoot),
                            Path.GetFullPath(owned.InstallationPath),
                            StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-STEAM-PREFLIGHT-FAILED");
                    pending=new(Guid.NewGuid(),owned.Id,owned.InstallationPath,
                        plan.InstalledBuild,steam.ExecutablePath,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pending.Token,mapName=owned.Name,mapId=owned.Id,
                        maps=plan.Maps,installedBuild=plan.InstalledBuild,
                        reserveBytes=plan.EstimatedDownloadBytes,
                        availableBytes=plan.AvailableBytes,
                        evidence="REAL_LOCAL_STOPPED_STEAM_PREFLIGHT_NO_WRITE"
                    });
                }
                if(!GuidField(data,"token",out var approval)||
                    pending==null||pending.Token!=approval||
                    DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-STEAM-TOKEN-EXPIRED");
                if(!data.TryGetProperty("confirm",out var confirm)||
                   confirm.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-STEAM-CONFIRMATION-REQUIRED");
                var saved=pending;pending=null;
                var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==saved.MapId);
                if(map==null||!Stopped(map)||
                   !string.Equals(map.InstallationPath,saved.Root,
                       StringComparison.OrdinalIgnoreCase)||
                   !string.Equals(cluster.Config.SteamCmdExecutable,saved.Steam,
                       StringComparison.OrdinalIgnoreCase)||
                   AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath)!=saved.Build)
                    return Fail(id,"UM-STEAM-PREFLIGHT-CHANGED");
                var task=new Job();
                job=task;
                _=Task.Run(async()=>
                {
                    Stage(task,"running","preflight");
                    var progress=new Progress<SteamMaintenanceStage>(
                        value=>Stage(task,"running",value.ToString()));
                    try
                    {
                        var verified=await runner(map,progress);
                        if(verified.MapId!=map.Id||!verified.ProtectedFilesIntact)
                            throw new InvalidDataException("UM-STEAM-RECEIPT-INVALID");
                        Stage(task,"completed","verified",verified);
                    }
                    catch(Exception)
                    {
                        Stage(task,"needsOperator","update failed, inspect Core journal");
                    }
                });
                return Ok(id,new{jobId=task.Id,started=true,
                    restartPerformed=false,remoteHostTouched=false,
                    evidence="REAL_LOCAL_STOPPED_STEAM_UPDATE_STARTED"});
            }
            finally{mutex.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-STEAM-UPDATE-UNAVAILABLE");
        }
    }
    private static string Ok(string id,object value)=>
        JsonSerializer.Serialize(new{id,ok=true,data=value},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>mutex.Dispose();
}
