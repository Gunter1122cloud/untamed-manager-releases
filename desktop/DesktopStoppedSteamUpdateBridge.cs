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
        public Guid MapId;
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
    private readonly Func<LocalMap,CancellationToken,Task<AsaSteamBuildCheck>> publishedBuild;
    // Each local map has its own approval; SteamCMD execution stays serialized.
    private readonly System.Collections.Generic.Dictionary<Guid,Preview> pendingByMap=new();
    private readonly System.Collections.Generic.Dictionary<Guid,Job> jobsById=new();
    private Job? job;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private const long Reserve=10L*1024*1024*1024;

    public DesktopStoppedSteamUpdateBridge(ClusterService owner,
        Func<LocalMap,SteamUpdatePreview>? plan=null,
        Func<LocalMap,IProgress<SteamMaintenanceStage>,Task<SteamStoppedMapUpdater.Receipt>>? execute=null,
        Func<LocalMap,CancellationToken,Task<AsaSteamBuildCheck>>? inspectPublished=null)
    {
        cluster=owner??throw new ArgumentNullException(nameof(owner));
        planner=plan??(map=>SteamUpdateDryRun.Plan(
            cluster.Config.SteamCmdExecutable,map.InstallationPath,
            cluster.Config.LocalMaps.ToArray(),
            member=>ObserveForSteam(member),Reserve));
        runner=execute??((map,progress)=>map.HistoricalStoreProtected?
            SteamStoppedMapUpdater.UpdateOriginalStoppedAsync(
                cluster,map,cluster.Config.SteamCmdExecutable,Reserve,
                TimeSpan.FromHours(2),
                member=>ObserveForSteam(member),progress,
                originalStoppedObserverUnderLease:
                    member=>ObserveForSteam(member,true)):
            SteamStoppedMapUpdater.UpdateAsync(
                cluster,map,cluster.Config.SteamCmdExecutable,Reserve,
                TimeSpan.FromHours(2),progress));
        publishedBuild=inspectPublished??((map,token)=>
            AsaSteamBuildCatalog.CheckAsync(cluster.Config.SteamCmdExecutable,
                map.InstallationPath,"public",TimeSpan.FromSeconds(90),token));
    }
    private ProcessCheck ObserveForSteam(LocalMap map,bool underOwnOperation=false)=>
        map.HistoricalStoreProtected?
            HistoricalLocalConfigGuard.DemandStopped(cluster,map,underOwnOperation):
            cluster.Check(map);

    private bool Stopped(LocalMap map)
    {
        // An imported original Kova map uses the exact independent Windows
        // ASA/Loader stopped-family observer; normal maps retain the same
        // existing Core updater. No Store session or writer is touched.
        try
        {
            if(map.HistoricalStoreProtected)ObserveForSteam(map);
            SteamStoppedMapUpdater.DemandSafeLocalInstallation(
                cluster,map,member=>ObserveForSteam(member));
            return true;
        }
        catch(Exception ex) when(ex is InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        { return false; }
    }
    private bool HasBlockedJournal()
    {
        var journal=new SteamSerialMaintenanceQueue(
            cluster.Store.Root,cluster.Config.LocalHostId).Observe();
        return journal!=null && journal.Phase is
            (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress
                or SteamQueuePhase.NeedsOperator);
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
            // Progress<T> posts asynchronously. A delayed Backup/VerifyBuild
            // callback must never turn a verified or failed job back into
            // "running" after the runner has reached a terminal state.
            if(item.State is "completed" or "needsOperator")return;
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
            jobId=item.Id,mapId=item.MapId,state=item.State,stage=item.Stage,
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
            if(method is not ("steam.stoppedDiagnostics" or "steam.stoppedPreview" or
                "steam.stoppedApply" or "steam.stoppedStatus" or "steam.stoppedCurrent" or
                "steam.publishedBuild"))
                return Fail(id,"UM-METHOD-DENIED");
            await mutex.WaitAsync();
            try
            {
                if(method=="steam.stoppedDiagnostics")
                {
                    if(!GuidField(data,"mapId",out var diagnosedId))
                        return Fail(id,"UM-STEAM-MAP");
                    var owned=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==diagnosedId);
                    if(owned==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    string root=Path.GetFullPath(owned.InstallationPath);
                    var related=cluster.Config.LocalMaps.Where(m=>
                        !string.IsNullOrWhiteSpace(m.InstallationPath)&&
                        string.Equals(Path.GetFullPath(m.InstallationPath),root,
                            StringComparison.OrdinalIgnoreCase)).ToArray();
                    var processes=related.Select(m=>{
                        ProcessCheck process;
                        try{process=ObserveForSteam(m);}
                        catch(Exception){process=new(false,
                            "Native ASA/Loader stopped proof unavailable",null,false);}
                        return new{mapName=m.Name,confirmedStopped=process.ConfirmedStopped,
                            running=process.Running,
                            pid=process.Identity?.Pid,
                            loaderPid=process.LoaderIdentity?.Pid,
                            reason=process.Status};
                    }).ToArray();
                    var overlaps=cluster.Config.LocalMaps.Where(m=>
                        !string.IsNullOrWhiteSpace(m.InstallationPath)&&
                        !string.Equals(Path.GetFullPath(m.InstallationPath),root,
                            StringComparison.OrdinalIgnoreCase)&&
                        (Path.GetFullPath(m.InstallationPath).StartsWith(
                            root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)||
                         root.StartsWith(Path.GetFullPath(m.InstallationPath).TrimEnd(
                            Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)))
                        .Select(m=>new{mapName=m.Name,installRoot=m.InstallationPath}).ToArray();
                    string steamExe=cluster.Config.SteamCmdExecutable;
                    bool steamFound=!string.IsNullOrWhiteSpace(steamExe)&&
                        File.Exists(steamExe);
                    long free=new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
                    bool stopped=processes.All(p=>p.confirmedStopped&&!p.running&&
                        p.pid==null&&p.loaderPid==null);
                    bool journalBlocked=HasBlockedJournal();
                    bool historical=related.Any(m=>m.Maintenance||
                        m.ObservedReadOnlyImported||
                        (!m.HistoricalStoreProtected&&
                         !FreshStoreEnrollmentPolicy.IsEnrolled(m)&&(
                            !string.IsNullOrWhiteSpace(m.HistoricalArchiveSha256)||
                            !string.IsNullOrWhiteSpace(m.HistoricalMapKey)||
                            !string.IsNullOrWhiteSpace(m.FreshStoreClusterId)||
                            !string.IsNullOrWhiteSpace(m.FreshStoreTransferDirectory)||
                            !string.IsNullOrWhiteSpace(m.StoreTestRoot)||
                            KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(m)))||
                        KovaCompatibleWorldBackup.InstallationRecoveryRequired(m));
                    bool safeInstallation=Stopped(owned);
                    return Ok(id,new{
                        mapId=owned.Id,mapName=owned.Name,installRoot=root,
                        processes,overlaps,
                        steamCmdFound=steamFound,steamCmdConfigured=
                            !string.IsNullOrWhiteSpace(steamExe),
                        availableBytes=free,requiredBytes=Reserve,
                        journalBlocked,historicalBlocked=historical,
                        safeInstallation,
                        canPreview=stopped&&!historical&&safeInstallation&&
                            !journalBlocked&&steamFound&&free>=Reserve&&
                            overlaps.Length==0,
                        noProcessAutoStop=true,remoteWrites=false,
                        evidence="REAL_STOPPED_MAP_STEAM_DIAGNOSTICS_READ_ONLY"
                    });
                }
                if(method=="steam.publishedBuild")
                {
                    if(!GuidField(data,"mapId",out var candidateId))
                        return Fail(id,"UM-STEAM-MAP");
                    var candidate=cluster.Config.LocalMaps.SingleOrDefault(
                        x=>x.Id==candidateId);
                    if(candidate==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    if(job!=null)
                    {
                        lock(sync)
                            if(job.State is "queued" or "running")
                                return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    }
                    if(HasBlockedJournal())
                        return Fail(id,"UM-STEAM-JOURNAL-NEEDS-REVIEW");
                    if(!AsaReadyProbe.IsRegisteredServerBinary(candidate) ||
                       string.IsNullOrWhiteSpace(cluster.Config.SteamCmdExecutable))
                        return Fail(id,"UM-STEAM-PUBLISHED-PREFLIGHT");
                    using var deadline=new CancellationTokenSource(
                        TimeSpan.FromSeconds(95));
                    var report=await publishedBuild(candidate,deadline.Token);
                    if(!string.Equals(Path.GetFullPath(report.InstallationRoot),
                           Path.GetFullPath(candidate.InstallationPath),
                           StringComparison.OrdinalIgnoreCase)||
                       report.Branch!="public")
                        return Fail(id,"UM-STEAM-PUBLISHED-RECEIPT-INVALID");
                    return Ok(id,new{
                        mapId=candidate.Id,mapName=candidate.Name,
                        installedBuild=report.InstalledBuild,
                        availableBuild=report.AvailableBuild,
                        state=report.State.ToString(),
                        checkedUtc=report.CheckedUtc,
                        explanation=report.Explanation,
                        updateAvailable=report.State==AsaSteamBuildState.UpdateAvailable,
                        downloadStarted=false,serverStopped=false,
                        storeWriterTouched=false,remoteHostTouched=false,
                        evidence="STEAM_PUBLIC_BRANCH_APPINFO_READ_ONLY_NO_UPDATE"
                    });
                }
                if(method=="steam.stoppedCurrent")
                {
                    // Recovery is observational. Only the Core journal can
                    // establish a completed queue after a Manager UI restart.
                    var journal=new SteamSerialMaintenanceQueue(
                        cluster.Store.Root,cluster.Config.LocalHostId).Observe();
                    // WebView2 may reload or the operator may leave this
                    // tab while native SteamCMD remains active. Expose only
                    // this bridge's in-memory job receipt to REATTACH the
                    // renderer, never a capability to replay an update.
                    object? activeJob=null;
                    if(job!=null)
                    {
                        lock(sync)
                        {
                            if(job.State is "queued" or "running")
                                activeJob=Snapshot(job);
                        }
                    }
                    bool active=activeJob!=null;
                    if(journal==null)
                        return Ok(id,new{exists=false,needsOperatorReview=false,
                            autoResumeAllowed=false,activeJob});
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
                        activeJob,
                        hostName=cluster.Config.LocalHostName,
                        evidence="DURABLE_MACHINE_LOCAL_STEAM_QUEUE_JOURNAL"
                    });
                }
                if(method=="steam.stoppedStatus")
                {
                    if(!GuidField(data,"jobId",out var jobId))
                        return Fail(id,"UM-STEAM-JOB-MISSING");
                    Job? requested;
                    lock(sync)jobsById.TryGetValue(jobId,out requested);
                    if(requested==null)return Fail(id,"UM-STEAM-JOB-MISSING");
                    return Ok(id,Snapshot(requested));
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
                    if(HasBlockedJournal())
                        return Fail(id,"UM-STEAM-JOURNAL-NEEDS-REVIEW");
                    var steam=SteamCmdPreflight.Inspect(cluster.Config.SteamCmdExecutable);
                    var plan=planner(owned);
                    if(!plan.DryRunAllowed||string.IsNullOrWhiteSpace(plan.InstalledBuild)||
                       !string.Equals(Path.GetFullPath(plan.InstallRoot),
                            Path.GetFullPath(owned.InstallationPath),
                            StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-STEAM-PREFLIGHT-FAILED");
                    var proposed=new Preview(Guid.NewGuid(),owned.Id,owned.InstallationPath,
                        plan.InstalledBuild,steam.ExecutablePath,DateTimeOffset.UtcNow);
                    pendingByMap[owned.Id]=proposed;
                    return Ok(id,new{
                        token=proposed.Token,mapName=owned.Name,mapId=owned.Id,
                        maps=plan.Maps,installedBuild=plan.InstalledBuild,
                        reserveBytes=plan.EstimatedDownloadBytes,
                        availableBytes=plan.AvailableBytes,
                        evidence="REAL_LOCAL_STOPPED_STEAM_PREFLIGHT_NO_WRITE"
                    });
                }
                if(!GuidField(data,"token",out var approval))
                    return Fail(id,"UM-STEAM-TOKEN-EXPIRED");
                var saved=pendingByMap.Values.SingleOrDefault(p=>p.Token==approval);
                if(saved==null||DateTimeOffset.UtcNow-saved.Created>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-STEAM-TOKEN-EXPIRED");
                if(!data.TryGetProperty("confirm",out var confirm)||
                   confirm.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-STEAM-CONFIRMATION-REQUIRED");
                // One-shot approval is consumed for its own map only.
                pendingByMap.Remove(saved.MapId);
                if(HasBlockedJournal())
                    return Fail(id,"UM-STEAM-JOURNAL-NEEDS-REVIEW");
                var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==saved.MapId);
                if(map==null||!Stopped(map)||
                   !string.Equals(map.InstallationPath,saved.Root,
                       StringComparison.OrdinalIgnoreCase)||
                   !string.Equals(cluster.Config.SteamCmdExecutable,saved.Steam,
                       StringComparison.OrdinalIgnoreCase)||
                   AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath)!=saved.Build)
                    return Fail(id,"UM-STEAM-PREFLIGHT-CHANGED");
                var task=new Job{MapId=map.Id};
                lock(sync)
                {
                    job=task;
                    if(jobsById.Count>=64)
                    {
                        var completed=jobsById.Values.Where(j=>
                            j.State is "completed" or "needsOperator")
                            .OrderBy(j=>j.Changed).Take(jobsById.Count-63).ToArray();
                        foreach(var old in completed)jobsById.Remove(old.Id);
                    }
                    jobsById[task.Id]=task;
                }
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
