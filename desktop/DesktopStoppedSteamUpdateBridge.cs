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

    private sealed class InlineProgress<T>(Action<T> report):IProgress<T>
    {
        public void Report(T value)=>report(value);
    }
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public Guid MapId;
        public string State="queued",Stage="preflight";
        public string OldBuild="",NewBuild="";
        public bool ProtectedFilesIntact;
        public string? ErrorCode;
        public DateTimeOffset Changed=DateTimeOffset.UtcNow;
    }
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim mutex=new(1,1);
    private readonly object sync=new();
    private readonly Func<LocalMap,SteamUpdatePreview> planner;
    private readonly bool isolatedExecution;
    private readonly Func<LocalMap,IProgress<SteamMaintenanceStage>,Task<SteamStoppedMapUpdater.Receipt>> runner;
    private readonly Func<LocalMap,CancellationToken,Task<AsaSteamBuildCheck>> publishedBuild;
    private readonly Func<string?> pickSteamCmd;
    private readonly Func<CancellationToken,Task<string>> prepareSteamCmd;
    // Each local map has its own approval; SteamCMD execution stays serialized.
    private readonly System.Collections.Generic.Dictionary<Guid,Preview> pendingByMap=new();
    private readonly System.Collections.Generic.Dictionary<Guid,Job> jobsById=new();
    private Job? job;
    private readonly System.Collections.Generic.HashSet<Guid> acceptedDirectRequests=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private const long Reserve=10L*1024*1024*1024;

    public DesktopStoppedSteamUpdateBridge(ClusterService owner,
        Func<LocalMap,SteamUpdatePreview>? plan=null,
        Func<LocalMap,IProgress<SteamMaintenanceStage>,Task<SteamStoppedMapUpdater.Receipt>>? execute=null,
        Func<LocalMap,CancellationToken,Task<AsaSteamBuildCheck>>? inspectPublished=null,
        Func<string?>? selectSteamCmd=null,
        Func<CancellationToken,Task<string>>? prepareOfficialSteamCmd=null)
    {
        cluster=owner??throw new ArgumentNullException(nameof(owner));
        isolatedExecution=execute!=null;
        pickSteamCmd=selectSteamCmd??PickSteamCmd;
        prepareSteamCmd=prepareOfficialSteamCmd??PrepareOfficialSteamCmdAsync;
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
    private static Task<string> PrepareOfficialSteamCmdAsync(CancellationToken ct)
    {
        // Same managed tool path and bounded Valve HTTPS / validated ZIP
        // implementation as the fresh-install wizard, without app_update.
        string target=Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "UntamedManager","Tools","SteamCMD","steamcmd.exe");
        return SteamFreshInstall.PrepareSteamCmdAsync(target,true,cancellationToken:ct);
    }
    private static string? PickSteamCmd()
    {
        using var dialog=new System.Windows.Forms.OpenFileDialog{
            Title="SteamCMD kiválasztása (csak Manager-beállítás)",
            Filter="SteamCMD (steamcmd.exe)|steamcmd.exe",
            CheckFileExists=true,Multiselect=false,RestoreDirectory=true};
        return dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK?dialog.FileName:null;
    }
    private static string? SteamCmdError(string path)
    {
        if(string.IsNullOrWhiteSpace(path))return "UM-STEAMCMD-NOT-CONFIGURED";
        try {SteamCmdPreflight.Inspect(path);return null;}
        catch(FileNotFoundException){return "UM-STEAMCMD-NOT-FOUND";}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {return "UM-STEAMCMD-INVALID";}
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
            protectedFilesIntact=item.ProtectedFilesIntact,errorCode=item.ErrorCode,
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
                "steam.stoppedApply" or "steam.stoppedStart" or "steam.stoppedStatus" or "steam.stoppedCurrent" or
                "steam.publishedBuild" or "steam.selectExecutable" or "steam.prepareExecutable"))
                return Fail(id,"UM-METHOD-DENIED");
            await mutex.WaitAsync();
            try
            {
                if(method is "steam.selectExecutable" or "steam.prepareExecutable")
                {
                    if(job!=null)
                    {
                        lock(sync)if(job.State is "queued" or "running")
                            return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    }
                    if(HasBlockedJournal())return Fail(id,"UM-STEAM-JOURNAL-NEEDS-REVIEW");
                    // The renderer supplies no execute path; the operator selects an existing local file.
                    string? selected;
                    if(method=="steam.prepareExecutable")
                    {
                        // Existing valid settings remain unchanged. Otherwise
                        // prepare only the Manager-owned SteamCMD tool; never
                        // download or execute an ASA app_update.
                        if(SteamCmdError(cluster.Config.SteamCmdExecutable)==null)
                            selected=cluster.Config.SteamCmdExecutable;
                        else
                        {
                            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
                            try{selected=await prepareSteamCmd(deadline.Token);}
                            catch(Exception){return Fail(id,"UM-STEAMCMD-PREPARE-FAILED");}
                        }
                    }
                    else selected=pickSteamCmd();
                    if(selected==null)return Ok(id,new{cancelled=true,configured=false,updateStarted=false,serverStopped=false});
                    string? problem=SteamCmdError(selected);
                    if(problem!=null)return Fail(id,problem);
                    if(HasBlockedJournal())return Fail(id,"UM-STEAM-JOURNAL-NEEDS-REVIEW");
                    string full=Path.GetFullPath(selected);
                    cluster.UpdateSettings(config=>config.SteamCmdExecutable=full);
                    pendingByMap.Clear(); // previously issued tokens bind the old executable
                    return Ok(id,new{cancelled=false,configured=true,updateStarted=false,serverStopped=false});
                }
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
                    string? steamCmdError=SteamCmdError(steamExe);
                    bool steamFound=steamCmdError==null;
                    string? externalSteamError=null;
                    if(!isolatedExecution)
                    {
                        try{SteamStoppedMapUpdater.DemandNoConcurrentSteamCmd();}
                        catch(IOException e){externalSteamError=e.Message is "UM-STEAM-EXTERNAL-PROCESS-BUSY" or "UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED"?e.Message:"UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED";}
                    }
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
                        installedBuild=AsaSteamBuildCatalog.InstalledBuild(root),
                        processes,overlaps,steamCmdError,
                        asaBinaryRegistered=AsaReadyProbe.IsRegisteredServerBinary(owned),
                        externalSteamError,steamCmdFound=steamFound,steamCmdConfigured=
                            !string.IsNullOrWhiteSpace(steamExe),
                        availableBytes=free,requiredBytes=Reserve+Reserve/4,
                        journalBlocked,historicalBlocked=historical,
                        safeInstallation,
                        canPreview=AsaReadyProbe.IsRegisteredServerBinary(owned)&&stopped&&!historical&&safeInstallation&&
                            !journalBlocked&&steamFound&&externalSteamError==null&&free>=Reserve+Reserve/4&&
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
                    if(!AsaReadyProbe.IsRegisteredServerBinary(candidate))
                        return Fail(id,"UM-STEAM-ASA-BINARY-INVALID");
                    string? steamProblem=SteamCmdError(cluster.Config.SteamCmdExecutable);
                    if(steamProblem!=null)return Fail(id,steamProblem);
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
                        explanation=report.Explanation,diagnosticCode=report.DiagnosticCode,
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
                bool direct=method=="steam.stoppedStart";
                Preview? saved=null;
                if(direct&&(acceptedDirectRequests.Contains(requestId)||acceptedDirectRequests.Count>=4096))
                    return Fail(id,"UM-STEAM-DIRECT-REQUEST-CONSUMED");
                if(method=="steam.stoppedPreview"||direct)
                {
                    if(!GuidField(data,"mapId",out var mapId))
                        return Fail(id,"UM-STEAM-MAP");
                    var owned=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId);
                    if(owned==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    if(!Stopped(owned))return Fail(id,"UM-STEAM-STOPPED-ONLY");
                    if(HasBlockedJournal())
                        return Fail(id,"UM-STEAM-JOURNAL-NEEDS-REVIEW");
                    string? steamProblem=SteamCmdError(cluster.Config.SteamCmdExecutable);
                    if(steamProblem!=null)return Fail(id,steamProblem);
                    var steam=SteamCmdPreflight.Inspect(cluster.Config.SteamCmdExecutable);
                    var plan=planner(owned);
                    if(!plan.DryRunAllowed||string.IsNullOrWhiteSpace(plan.InstalledBuild)||
                       !string.Equals(Path.GetFullPath(plan.InstallRoot),
                            Path.GetFullPath(owned.InstallationPath),
                            StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-STEAM-PREFLIGHT-FAILED");
                    var proposed=new Preview(Guid.NewGuid(),owned.Id,owned.InstallationPath,
                        plan.InstalledBuild,steam.ExecutablePath,DateTimeOffset.UtcNow);
                    if(direct)
                    {
                        // A single explicit native request creates and consumes its own scoped preflight.
                        // Shared installation scope must have been displayed by the read-only diagnostics.
                        if(plan.Maps.Length>1&&(!data.TryGetProperty("affectedMaps",out var affected)||
                           affected.ValueKind!=JsonValueKind.Array||
                           !affected.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.String)||
                           !affected.EnumerateArray().Select(x=>x.GetString()).OrderBy(x=>x,StringComparer.Ordinal)
                               .SequenceEqual(plan.Maps.OrderBy(x=>x,StringComparer.Ordinal))))
                            return Fail(id,"UM-STEAM-SHARED-SCOPE-CHANGED");
                        saved=proposed;
                    }
                    else
                    {
                    pendingByMap[owned.Id]=proposed;
                    return Ok(id,new{
                        token=proposed.Token,mapName=owned.Name,mapId=owned.Id,
                        maps=plan.Maps,installedBuild=plan.InstalledBuild,
                        reserveBytes=plan.EstimatedDownloadBytes,
                        availableBytes=plan.AvailableBytes,
                        evidence="REAL_LOCAL_STOPPED_STEAM_PREFLIGHT_NO_WRITE"
                    });
                    }
                }
                else
                {
                if(!GuidField(data,"token",out var approval))
                    return Fail(id,"UM-STEAM-TOKEN-EXPIRED");
                saved=pendingByMap.Values.SingleOrDefault(p=>p.Token==approval);
                if(saved==null||DateTimeOffset.UtcNow-saved.Created>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-STEAM-TOKEN-EXPIRED");
                if(!data.TryGetProperty("confirm",out var confirm)||
                   confirm.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-STEAM-CONFIRMATION-REQUIRED");
                }
                if(saved==null||DateTimeOffset.UtcNow-saved.Created>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-STEAM-TOKEN-EXPIRED");
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
                var fresh=planner(map);
                if(!fresh.DryRunAllowed||fresh.InstalledBuild!=saved.Build||
                   !string.Equals(Path.GetFullPath(fresh.InstallRoot),Path.GetFullPath(saved.Root),StringComparison.OrdinalIgnoreCase))
                    return Fail(id,"UM-STEAM-PREFLIGHT-CHANGED");
                if(direct&&fresh.Maps.Length>1&&(!data.TryGetProperty("affectedMaps",out var freshScope)||
                   freshScope.ValueKind!=JsonValueKind.Array||
                   !freshScope.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.String)||
                   !freshScope.EnumerateArray().Select(x=>x.GetString()).OrderBy(x=>x,StringComparer.Ordinal)
                       .SequenceEqual(fresh.Maps.OrderBy(x=>x,StringComparer.Ordinal))))
                    return Fail(id,"UM-STEAM-SHARED-SCOPE-CHANGED");
                if(!isolatedExecution)
                {
                    try{SteamStoppedMapUpdater.DemandNoConcurrentSteamCmd();}
                    catch(IOException e){return Fail(id,e.Message is "UM-STEAM-EXTERNAL-PROCESS-BUSY" or "UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED"?e.Message:"UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED");}
                }
                if(direct)acceptedDirectRequests.Add(requestId);
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
                    var progress=new InlineProgress<SteamMaintenanceStage>(
                        value=>Stage(task,"running",value.ToString()));
                    try
                    {
                        var verified=await runner(map,progress);
                        if(verified.MapId!=map.Id||!verified.ProtectedFilesIntact)
                            throw new InvalidDataException("UM-STEAM-RECEIPT-INVALID");
                        Stage(task,"completed","verified",verified);
                    }
                    catch(Exception ex)
                    {
                        lock(sync)
                        {
                            task.ErrorCode=ex.Message is "UM-STEAM-EXTERNAL-PROCESS-BUSY" or "UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED" or "UM-STEAM-INSTALLED-BUILD-INVALID" or "UM-STEAM-INSTALLED-BUILD-CHANGED"?ex.Message:"UM-STEAM-UPDATE-FAILED";
                            Stage(task,"needsOperator",task.Stage);
                        }
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
