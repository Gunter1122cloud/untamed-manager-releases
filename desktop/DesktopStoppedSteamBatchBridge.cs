using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Operator-approved batch maintenance for ONLY locally owned, positively
/// STOPPED, non-Store ASA installations. Delegates every write to the
/// existing SteamStoppedMapUpdater.UpdateAllStoppedAsync/Core queue, with a
/// single-use preview receipt and no auto-retry after interruption.
/// </summary>
public sealed class DesktopStoppedSteamBatchBridge : IDisposable
{
    private sealed record Candidate(Guid MapId,string Root,string Build);
    private sealed record Pending(Guid Token,Candidate[] Maps,string SteamExe,
        DateTimeOffset CreatedUtc);
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public string State="queued",Stage="preflight";
        public int Position,Total;
        public string ActiveMap="";
        public int CompletedInstallations;
        public DateTimeOffset ChangedUtc=DateTimeOffset.UtcNow;
    }
    private readonly ClusterService cluster;
    private readonly Func<IReadOnlyList<LocalMap>,IProgress<SteamQueueProgress>,
        Task<IReadOnlyList<SteamStoppedMapUpdater.Receipt>>> runBatch;
    private readonly Func<LocalMap,SteamUpdatePreview> plan;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly object sync=new();
    private const long Reserve=10L*1024*1024*1024;
    private Pending? pending;
    private Job? job;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopStoppedSteamBatchBridge(ClusterService owner,
        Func<IReadOnlyList<LocalMap>,IProgress<SteamQueueProgress>,
            Task<IReadOnlyList<SteamStoppedMapUpdater.Receipt>>>? isolatedRunner=null,
        Func<LocalMap,SteamUpdatePreview>? isolatedPlanner=null)
    {
        cluster=owner??throw new ArgumentNullException(nameof(owner));
        runBatch=isolatedRunner??((maps,progress)=>
            SteamStoppedMapUpdater.UpdateAllStoppedAsync(cluster,maps,
                cluster.Config.SteamCmdExecutable,Reserve,
                TimeSpan.FromHours(2),progress));
        plan=isolatedPlanner??(map=>SteamUpdateDryRun.Plan(
            cluster.Config.SteamCmdExecutable,map.InstallationPath,
            cluster.Config.LocalMaps.ToArray(),cluster.Check,Reserve));
    }

    private static string Ok(string? id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    private static bool GuidField(JsonElement doc,string key,out Guid value)
    {
        value=Guid.Empty;
        return doc.TryGetProperty(key,out var node) &&
            node.ValueKind==JsonValueKind.String &&
            Guid.TryParse(node.GetString(),out value)&&value!=Guid.Empty;
    }

    private bool BlockedJournal()
    {
        var current=new SteamSerialMaintenanceQueue(
            cluster.Store.Root,cluster.Config.LocalHostId).Observe();
        return current!=null&&current.Phase is
            (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress or
                SteamQueuePhase.NeedsOperator);
    }
    private bool RunningJob()
    {
        lock(sync)return job!=null&&job.State is "queued" or "running";
    }
    private object JobSnapshot(Job item)
    {
        lock(sync)return new{
            jobId=item.Id,state=item.State,stage=item.Stage,
            position=item.Position,total=item.Total,
            activeMapName=item.ActiveMap,
            completedInstallations=item.CompletedInstallations,
            changedUtc=item.ChangedUtc,
            serverStopRequested=false,remoteHostTouched=false,
            storeWriterTouched=false,automaticRetry=false
        };
    }
    private void Stage(Job item,string state,string stage,
        int position=0,string mapName="")
    {
        lock(sync)
        {
            item.State=state;item.Stage=stage;
            if(position>0)item.Position=position;
            if(mapName.Length>0)item.ActiveMap=mapName;
            item.ChangedUtc=DateTimeOffset.UtcNow;
        }
    }
    private (LocalMap[] Maps,Candidate[] Evidence,object[] Details) ValidateMaps(
        Guid[] requested)
    {
        if(requested.Length is <1 or >25 ||
           requested.Distinct().Count()!=requested.Length)
            throw new InvalidDataException("UM-STEAM-BATCH-INVALID-MAPS");
        if(BlockedJournal()||RunningJob())
            throw new InvalidOperationException("UM-STEAM-BATCH-BUSY-OR-JOURNAL");
        _=SteamCmdPreflight.Inspect(cluster.Config.SteamCmdExecutable);
        var chosen=new List<LocalMap>();
        foreach(var mapId in requested)
        {
            var local=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
            if(local==null)
                throw new UnauthorizedAccessException("UM-STEAM-BATCH-NOT-LOCAL");
            if(local.HistoricalStoreProtected || local.Maintenance ||
               !string.IsNullOrWhiteSpace(local.HistoricalArchiveSha256) ||
               !string.IsNullOrWhiteSpace(local.FreshStoreClusterId) ||
               !string.IsNullOrWhiteSpace(local.StoreTestRoot) ||
               KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(local) ||
               !AsaReadyProbe.IsRegisteredServerBinary(local))
                throw new UnauthorizedAccessException("UM-STEAM-BATCH-PROTECTED");
            var process=cluster.Check(local);
            if(!process.ConfirmedStopped||process.Running||
               process.Identity!=null||process.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-STEAM-BATCH-NOT-STOPPED");
            chosen.Add(local);
        }
        // Core updates one shared installation once. Preview must use the
        // SAME grouping/order as Core, including the first map per directory.
        var unique=chosen.GroupBy(m=>Path.GetFullPath(m.InstallationPath)
                .TrimEnd(Path.DirectorySeparatorChar),StringComparer.OrdinalIgnoreCase)
            .Select(g=>g.First()).ToArray();
        var evidence=new List<Candidate>();
        var details=new List<object>();
        foreach(var map in unique)
        {
            string root=Path.GetFullPath(map.InstallationPath);
            foreach(var sibling in cluster.Config.LocalMaps.Where(m=>
                !string.IsNullOrWhiteSpace(m.InstallationPath) &&
                string.Equals(Path.GetFullPath(m.InstallationPath),
                    root,StringComparison.OrdinalIgnoreCase)))
            {
                var state=cluster.Check(sibling);
                if(state.Running||!state.ConfirmedStopped||
                   state.Identity!=null||state.LoaderIdentity!=null||
                   sibling.HistoricalStoreProtected||sibling.Maintenance||
                   !string.IsNullOrWhiteSpace(sibling.FreshStoreClusterId)||
                   KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(sibling))
                    throw new InvalidOperationException(
                        "UM-STEAM-BATCH-SHARED-INSTALL-BLOCKED");
            }
            new AsaManualRestartJournal(root).DemandNoPending();
            var preview=plan(map);
            if(!preview.DryRunAllowed||
               string.IsNullOrWhiteSpace(preview.InstalledBuild)||
               !string.Equals(Path.GetFullPath(preview.InstallRoot),root,
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UM-STEAM-BATCH-PREFLIGHT");
            evidence.Add(new(map.Id,root,preview.InstalledBuild));
            details.Add(new{mapId=map.Id,mapName=map.Name,
                installedBuild=preview.InstalledBuild,
                sharedMapCount=preview.Maps.Length,
                availableBytes=preview.AvailableBytes,
                requiredReserveBytes=preview.EstimatedDownloadBytes});
        }
        return (unique,evidence.ToArray(),details.ToArray());
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3200)return Fail(null,"UM-STEAM-BATCH-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            if(data.ValueKind!=JsonValueKind.Object||
               !GuidField(data,"id",out var requestId) ||
               !data.TryGetProperty("method",out var node) ||
               node.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-STEAM-BATCH-MESSAGE");
            id=requestId.ToString("D");
            string? method=node.GetString();
            if(method is not ("steam.batchPreview" or "steam.batchApply" or
                "steam.batchStatus" or "steam.batchCurrent"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="steam.batchCurrent")
                {
                    var journal=new SteamSerialMaintenanceQueue(
                        cluster.Store.Root,cluster.Config.LocalHostId).Observe();
                    return Ok(id,new{
                        activeJob=job==null?null:JobSnapshot(job),
                        journalExists=journal!=null,
                        journalPhase=journal?.Phase.ToString(),
                        journalNextIndex=journal?.NextIndex,
                        journalTotal=journal?.MapIds.Length,
                        needsOperatorReview=journal?.Phase==SteamQueuePhase.NeedsOperator ||
                          (job==null&&journal?.Phase is SteamQueuePhase.Prepared or
                              SteamQueuePhase.MapInProgress),
                        automaticRetry=false,remoteWritesAllowed=false
                    });
                }
                if(method=="steam.batchStatus")
                {
                    if(!GuidField(data,"jobId",out var lookup)||
                       job==null||job.Id!=lookup)
                        return Fail(id,"UM-STEAM-BATCH-JOB-MISSING");
                    return Ok(id,JobSnapshot(job));
                }
                if(method=="steam.batchPreview")
                {
                    if(!data.TryGetProperty("mapIds",out var rawIds)||
                       rawIds.ValueKind!=JsonValueKind.Array||
                       rawIds.GetArrayLength() is <1 or >25)
                        return Fail(id,"UM-STEAM-BATCH-INVALID-MAPS");
                    var ids=new List<Guid>();
                    foreach(var element in rawIds.EnumerateArray())
                    {
                        if(element.ValueKind!=JsonValueKind.String ||
                           !Guid.TryParse(element.GetString(),out var mapId)||
                           mapId==Guid.Empty)
                            return Fail(id,"UM-STEAM-BATCH-INVALID-MAPS");
                        ids.Add(mapId);
                    }
                    var selected=ValidateMaps(ids.ToArray());
                    var token=Guid.NewGuid();
                    pending=new(token,selected.Evidence,
                        cluster.Config.SteamCmdExecutable,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token,requestedMaps=ids.Count,
                        installations=selected.Evidence.Length,
                        details=selected.Details,
                        requiresTypedApproval=true,
                        serverStopRequested=false,storeWriterTouched=false,
                        remoteHostTouched=false,
                        evidence="LOCAL_STOPPED_STEAM_SERIAL_BATCH_PREFLIGHT_NO_WRITES"
                    });
                }
                if(!GuidField(data,"token",out var approval)||
                   pending==null||pending.Token!=approval ||
                   DateTimeOffset.UtcNow-pending.CreatedUtc>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-STEAM-BATCH-TOKEN-EXPIRED");
                if(!data.TryGetProperty("confirm",out var confirmed) ||
                   confirmed.ValueKind!=JsonValueKind.True ||
                   !data.TryGetProperty("typedApproval",out var phrase) ||
                   phrase.ValueKind!=JsonValueKind.String ||
                   !string.Equals(phrase.GetString(),"HELYI SOROS STEAMCMD",
                       StringComparison.Ordinal))
                    return Fail(id,"UM-STEAM-BATCH-CONFIRMATION-REQUIRED");
                var saved=pending;
                pending=null; // one use only, even if later preflight fails
                if(!string.Equals(cluster.Config.SteamCmdExecutable,saved.SteamExe,
                    StringComparison.OrdinalIgnoreCase))
                    return Fail(id,"UM-STEAM-BATCH-CHANGED");
                var rechecked=ValidateMaps(saved.Maps.Select(x=>x.MapId).ToArray());
                if(rechecked.Evidence.Length!=saved.Maps.Length ||
                   rechecked.Evidence.Where((x,i)=>
                       x.MapId!=saved.Maps[i].MapId ||
                       !string.Equals(x.Root,saved.Maps[i].Root,
                           StringComparison.OrdinalIgnoreCase) ||
                       !string.Equals(x.Build,saved.Maps[i].Build,
                           StringComparison.Ordinal)).Any())
                    return Fail(id,"UM-STEAM-BATCH-CHANGED");
                var task=new Job{Total=rechecked.Maps.Length};
                job=task;
                _=Task.Run(async()=>
                {
                    Stage(task,"running","preflight");
                    var progress=new Progress<SteamQueueProgress>(item=>
                        Stage(task,"running",item.Stage,
                            item.Position,item.MapName));
                    try
                    {
                        var receipt=await runBatch(rechecked.Maps,progress);
                        if(receipt.Count!=rechecked.Maps.Length ||
                           receipt.Any(x=>!x.ProtectedFilesIntact)||
                           !receipt.Select(x=>x.MapId).SequenceEqual(
                               rechecked.Maps.Select(m=>m.Id)))
                            throw new InvalidDataException(
                                "UM-STEAM-BATCH-RECEIPTS-INVALID");
                        lock(sync)task.CompletedInstallations=receipt.Count;
                        Stage(task,"completed","verified");
                    }
                    catch(Exception)
                    {
                        Stage(task,"needsOperator","Check native queue and backups");
                    }
                });
                return Ok(id,new{
                    jobId=task.Id,started=true,
                    serverStopRequested=false,remoteHostTouched=false,
                    storeWriterTouched=false,automaticRetry=false,
                    evidence="EXPLICIT_LOCAL_STOPPED_SERIAL_STEAM_BATCH_STARTED"
                });
            }
            finally{gate.Release();}
        }
        catch(UnauthorizedAccessException){return Fail(id,"UM-STEAM-BATCH-PROTECTED-OR-REMOTE");}
        catch(InvalidDataException){return Fail(id,"UM-STEAM-BATCH-INVALID-PREFLIGHT");}
        catch(InvalidOperationException){return Fail(id,"UM-STEAM-BATCH-NOT-READY");}
        catch(Exception){return Fail(id,"UM-STEAM-BATCH-UNAVAILABLE");}
    }
    public void Dispose()=>gate.Dispose();
}
