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
/// Explicit manual restart of an ORIGINAL ACTIVE/DONE Store-v1 map.
/// A single operator approval authorizes exactly one guarded
/// SaveWorld -> observed stable world -> DoExit -> confirmed stopped ->
/// revalidated ORIGINAL -usestore Start. It never converts maps, touches
/// shared session/lock/transfer metadata, force-kills, or auto-retries.
/// A durable machine-local restart journal remains pending after interruption.
/// </summary>
public sealed class DesktopHistoricalRestartBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,int Pid,
        DateTimeOffset StartedUtc,string Profile,string SourceSha,
        int RconPort,DateTimeOffset Created,
        LegacyStoreV1ProcessObserver.LiveModOrder? LiveMods,
        LegacyStoreV1ProcessObserver.LiveModOrder? RestartMods);

    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public Guid MapId;
        public int OldPid,NewPid;
        public DateTimeOffset? NewStartedUtc;
        public bool UseApi;
        public string State="queued",Step="preflight";
        public bool OldProcessStopped,PreExitSaveObserved,PreExitWorldMetadataObserved;
        public DateTimeOffset ChangedUtc=DateTimeOffset.UtcNow;
        public RestartStartupProof? Readiness;
        public string? FailureCode;
        public int StartupAttempts=1;
        public bool StartupRetriesEnabled;
    }

    private readonly ClusterService cluster;
    private readonly Func<Guid,LegacyStoreV1GracefulStop.Owned> preview;
    private readonly Func<Guid,int,DateTimeOffset,string,Guid,Action<string>,
        Task<AsaGracefulStopReceipt>> stop;
    private readonly Func<Guid,Guid,Action<string>,ProcessIdentity> start;
    private readonly Func<LocalMap,ProcessCheck> inspectFamily;
    private readonly Func<int,int,bool> owner;
    private readonly bool nativeLauncher;
    private readonly SemaphoreSlim serial=new(1,1);
    private readonly object sync=new();
    private readonly Dictionary<Guid,Approval> approvalsByMap=new();
    // Preserve the in-memory Restart identity of every local ASA map.
    private readonly Dictionary<Guid,Job> jobsByMap=new();
    internal bool RecoveryLaneBusy(){lock(sync)return jobsByMap.Values.Any(x=>x.State is "queued" or "running");}
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private bool HasActive(Guid mapId)
    {
        lock(sync)return jobsByMap.TryGetValue(mapId,out var existing)&&
            existing.State is ("queued" or "running");
    }

    private LegacyStoreV1SaveEvidence? PendingSaveEvidence(Guid mapId)
    {
        LegacyStoreV1SaveEvidenceJournal.AutoRecoverInterrupted(cluster.Store.Root,mapId);
        var receipt=LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,mapId);
        return receipt?.Phase is "prepared" or "needsOperator" ? receipt : null;
    }

    public DesktopHistoricalRestartBridge(ClusterService service):
        this(service,null,null,null){}

    // Native isolated-test seams only. No UI supplies process, source,
    // command line, journal IDs, or callback implementations.
    internal DesktopHistoricalRestartBridge(ClusterService service,
        Func<Guid,LegacyStoreV1GracefulStop.Owned>? probe,
        Func<Guid,int,DateTimeOffset,string,Guid,Action<string>,
            Task<AsaGracefulStopReceipt>>? stopper,
        Func<Guid,Guid,Action<string>,ProcessIdentity>? launcher,
        Func<LocalMap,ProcessCheck>? apiInspector=null,
        Func<int,int,bool>? apiPortOwner=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        nativeLauncher=launcher==null;
        inspectFamily=apiInspector??(map=>
            LegacyStoreV1ProcessObserver.Observe(map,cluster.Config.LocalMaps));
        owner=apiPortOwner??RconPortOwnership.IsOwnedListener;
        preview=probe??(map=>LegacyStoreV1GracefulStop.Preview(cluster,map));
        stop=stopper??((map,pid,started,source,restartId,report)=>
            LegacyStoreV1GracefulStop.StopAsync(cluster,map,pid,started,
                source,TimeSpan.FromMinutes(3),stage:report,
                approvedRestartId:restartId));
        start=launcher??((mapId,restartId,report)=>{
            var map=Owned(mapId)??throw new UnauthorizedAccessException(
                "UM-ORIGINAL-RESTART-LOCAL-MAP");
            // The fresh ORIGINAL active start fingerprint is derived only
            // AFTER the previous PID is confirmed gone. No old fingerprint
            // or WebView-supplied launch arguments are reused.
            var plan=LegacyStoreV1NativeStartPlanner.Prepare(
                cluster,map,restartId);
            // Confirm the planner actually consumed the synchronized
            // profile rather than an obsolete immutable source list.
            var plannedMods=ModStateController.Read(new LocalMap{Arguments=plan.Arguments});
            var configured=ModStateController.Read(map);
            if(!configured.EnabledOrder.SequenceEqual(plannedMods.EnabledOrder)||
               !configured.Passive.SequenceEqual(plannedMods.Passive))
                throw new InvalidDataException("UM-ORIGINAL-RESTART-PLAN-MODS-MISMATCH");
            report("Eredeti ACTIVE Store indítási állapot újraellenőrizve");
            return cluster.LaunchOriginalActiveStoreAsa(
                mapId,plan.Fingerprint,true,restartId);
        });
    }

    private LocalMap? Owned(Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id);
    private static bool ReadId(JsonElement root,string key,out Guid id)
    {
        id=Guid.Empty;
        return root.TryGetProperty(key,out var value)&&
            value.ValueKind==JsonValueKind.String&&
            Guid.TryParse(value.GetString(),out id)&&id!=Guid.Empty;
    }
    private static bool Same(LegacyStoreV1GracefulStop.Owned observed,
        LocalMap map,Approval approved)=>
        ReferenceEquals(observed.Map,map)&&
        observed.Process.Pid==approved.Pid&&
        observed.Process.StartedUtc==approved.StartedUtc&&
        observed.SourceSha==approved.SourceSha;

    private void Update(Job job,string state,string step,
        bool? oldStopped=null,bool? saved=null,int newPid=0,
        bool? worldMetadata=null,DateTimeOffset? newStartedUtc=null)
    {
        lock(sync)
        {
            // Async ASA/RCON progress belongs only to an active Restart;
            // a committed terminal Store-v1 receipt is immutable.
            if(job.State is "completed" or "needsOperator" or "failed")return;
            job.State=state;job.Step=step;
            job.ChangedUtc=DateTimeOffset.UtcNow;
            if(oldStopped.HasValue)job.OldProcessStopped=oldStopped.Value;
            if(saved.HasValue)job.PreExitSaveObserved=saved.Value;
            if(worldMetadata.HasValue)job.PreExitWorldMetadataObserved=worldMetadata.Value;
            if(newPid>0){job.NewPid=newPid;job.NewStartedUtc=newStartedUtc;}
        }
    }
    private object Snapshot(Job job)
    {
        lock(sync)return new{
            jobId=job.Id,mapId=job.MapId,state=job.State,step=job.Step,
            launchMode=job.UseApi?"asaApiLoader":"directAsa",
            changedUtc=job.ChangedUtc,
            oldPid=job.OldPid,newPid=job.NewPid,newStartedUtc=job.NewStartedUtc,
            oldProcessStopped=job.OldProcessStopped,
            preExitSaveLogAndWorldObserved=job.PreExitSaveObserved,
            preExitWorldMetadataObserved=job.PreExitWorldMetadataObserved,
            readyCertified=job.Readiness?.Ready(job.UseApi)??false,
            loaderRunning=job.Readiness?.LoaderRunning??false,
            asaRunning=job.Readiness?.AsaRunning??false,
            asaApiDllLoaded=job.Readiness?.ApiDllLoaded??false,
            pluginLoadingComplete=job.Readiness?.PluginsLoaded??false,
            rconResponding=job.Readiness?.RconResponding??false,
            joinAdvertised=job.Readiness?.JoinAdvertised??false,
            readinessFailureCode=job.Readiness?.Reason,
            failureCode=job.FailureCode,
            startupAttempts=job.StartupAttempts,startupRetriesEnabled=job.StartupRetriesEnabled,
            gameClientRejoinVerified=false,
            finalDoExitSaveCertified=false,automaticRetry=job.StartupRetriesEnabled,
            forceKill=false,storeTouched=false,
            evidence="ORIGINAL_STORE_V1_NATIVE_MANUAL_RESTART_JOB"
        };
    }

    private object Recover(Guid mapId)
    {
        var map=Owned(mapId);
        if(map==null||!map.HistoricalStoreProtected)
            throw new UnauthorizedAccessException("UM-ORIGINAL-RESTART-LOCAL-ONLY");
        cluster.Operations.AutoRecoverPending(map.InstallationPath);
        new AsaManualRestartJournal(map.InstallationPath).AutoRecoverPending();
        new AsaManualStopJournal(map.InstallationPath).AutoRecoverPending(mapId);
        LegacyStoreV1SaveEvidenceJournal.AutoRecoverInterrupted(cluster.Store.Root,mapId);
        var journalDiagnosis=AsaLocalJournalReadOnly.Inspect(
            cluster.Operations,map.InstallationPath);
        var entry=journalDiagnosis.Restart;
        Job? running;
        bool localNeedsOperator;
        lock(sync)
        {
            jobsByMap.TryGetValue(mapId,out var tracked);
            running=tracked?.State is "queued" or "running"?tracked:null;
            localNeedsOperator=tracked?.State=="needsOperator"&&
                entry?.Phase!=AsaRestartPhase.OperatorReviewed;
        }
        bool pendingRestart=journalDiagnosis.RestartUnreadable||
            entry!=null&&entry.Phase is not
                (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed or AsaRestartPhase.Recovered);
        var operation=journalDiagnosis.Operation;
        bool pendingOperation=journalDiagnosis.OperationUnreadable||
            operation?.Phase is UntamedOperationPhase.Prepared or
                UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator;
        // Recovery is read-only. A half-written/.next or tampered local
        // SaveWorld receipt must still be reported with its exact map ID;
        // mutating Restart preview/apply continue using strict Inspect().
        var (saved,saveUnreadable)=LegacyStoreV1SaveEvidenceJournal.
            InspectReadOnly(cluster.Store.Root,mapId);
        bool pendingSave=saveUnreadable||
            saved?.Phase is "prepared" or "needsOperator";
        bool same=entry?.MapId==mapId;
        return new{
            mapId,phase=entry?.Phase.ToString()??"None",
            entryBelongsToMap=same,
            saveEvidencePhase=saveUnreadable?"unreadable":saved?.Phase??"none",
            saveEvidencePending=pendingSave,
            saveEvidenceUnreadable=saveUnreadable,
            saveEvidenceErrorCode=saveUnreadable?
                "UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE":null,
            operationJournalUnreadable=journalDiagnosis.OperationUnreadable,
            restartJournalUnreadable=journalDiagnosis.RestartUnreadable,
            operationJournalErrorCode=journalDiagnosis.OperationErrorCode,
            restartJournalErrorCode=journalDiagnosis.RestartErrorCode,
            blocked=pendingRestart||pendingOperation||pendingSave||
                running!=null||localNeedsOperator,
            requiresOperator=false,
            inMemoryRunning=running!=null,jobId=running?.Id,
            oldPid=entry?.OldPid,newPid=entry?.NewPid,
            automaticRecovery=true,automaticRetry=true,
            originalStoreTouched=false,lockCleared=false,
            evidence="ORIGINAL_STORE_V1_DURABLE_RESTART_RECOVERY_READ_ONLY"
        };
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3072)
                return Fail(null,"UM-ORIGINAL-RESTART-REQUEST");
            using var parsed=JsonDocument.Parse(raw);
            var request=parsed.RootElement;
            if(request.ValueKind!=JsonValueKind.Object||
               !ReadId(request,"id",out var requestId)||
               !request.TryGetProperty("method",out var methodField)||
               methodField.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-ORIGINAL-RESTART-REQUEST");
            id=requestId.ToString("D");
            var method=methodField.GetString();
            if(method is not ("historical.restartRecovery" or
                "historical.restartPreview" or
                "historical.restartApply" or
                "historical.restartStatus"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="historical.restartRecovery")
                {
                    if(!ReadId(request,"mapId",out var mapId))
                        return Fail(id,"UM-ORIGINAL-RESTART-MAP");
                    return Ok(id,Recover(mapId));
                }
                if(method=="historical.restartStatus")
                {
                    if(!ReadId(request,"jobId",out var jobId))
                        return Fail(id,"UM-ORIGINAL-RESTART-JOB-MISSING");
                    Job? tracked;
                    lock(sync)tracked=jobsByMap.Values.FirstOrDefault(
                        item=>item.Id==jobId);
                    if(tracked==null)return Fail(id,"UM-ORIGINAL-RESTART-JOB-MISSING");
                    return Ok(id,Snapshot(tracked));
                }
                if(method=="historical.restartPreview")
                {
                    if(!ReadId(request,"mapId",out var mapId))
                        return Fail(id,"UM-ORIGINAL-RESTART-MAP");
                    var map=Owned(mapId);
                    if(map==null||!map.HistoricalStoreProtected||map.Maintenance)
                        return Fail(id,"UM-ORIGINAL-RESTART-LOCAL-ONLY");
                    if(HasActive(mapId))return Fail(id,"UM-ORIGINAL-RESTART-BUSY");
                    cluster.Operations.AutoRecoverPending(map.InstallationPath);
                    new AsaManualRestartJournal(map.InstallationPath).AutoRecoverPending();
                    if(PendingSaveEvidence(mapId)!=null)
                        return Fail(id,"UM-ORIGINAL-SAVE-EVIDENCE-NEEDS-REVIEW");
                    // Preserve the exact per-map launch mode. A Loader-based
                    // restart is permitted only when the verified ASA CHILD
                    // owns RCON; never send DoExit to the Loader parent.
                    if((map.StartWithAsaApi&&string.IsNullOrWhiteSpace(map.ApiLoaderPath))||
                       !string.IsNullOrWhiteSpace(map.ApiLoaderArguments))
                        return Fail(id,"UM-ORIGINAL-RESTART-LOADER-NOT-SUPPORTED");
                    new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
                    cluster.Operations.AutoRecoverPending(map.InstallationPath);
                    var existing=cluster.Operations.Observe(map.InstallationPath);
                    if(existing?.Phase is UntamedOperationPhase.Prepared or
                        UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)
                        return Fail(id,"UM-ORIGINAL-RESTART-OPERATION-IN-PROGRESS");
                    var owned=await Task.Run(()=>preview(mapId));
                    if(!ReferenceEquals(owned.Map,map)||owned.Process.Pid<=0||
                       owned.Process.StartedUtc==default||owned.SourceSha.Length!=64)
                        return Fail(id,"UM-ORIGINAL-RESTART-SOURCE-INVALID");
                    LegacyStoreV1ProcessObserver.LiveModOrder? observedMods=null,selectedMods=null;
                    if(nativeLauncher)
                    {
                        observedMods=LegacyStoreV1ProcessObserver.CaptureVerifiedModOrder(
                            cluster,map,owned.Process);
                        selectedMods=LegacyStoreV1ProcessObserver.SelectRestartMods(map,observedMods);
                        var saved=ModStateController.Read(map);
                        // The legacy fallback builds from immutable original
                        // source lists. Never stop a map whose intended next
                        // list cannot be honored by that launch planner.
                        if((map.ModChangesPending||
                            !saved.EnabledOrder.SequenceEqual(selectedMods.Enabled)||
                            !saved.Passive.SequenceEqual(selectedMods.Passive))&&
                           !LegacyStoreV1PinnedSource.HasApprovedLocalSource(cluster.Store.Root,map))
                            return Fail(id,"UM-ORIGINAL-RESTART-MOD-SYNC-REQUIRES-PIN");
                    }
                    var permit=new Approval(Guid.NewGuid(),mapId,owned.Process.Pid,
                        owned.Process.StartedUtc,JsonSerializer.Serialize(map),
                        owned.SourceSha,map.RconPort,DateTimeOffset.UtcNow,observedMods,selectedMods);
                    approvalsByMap[mapId]=permit;
                    return Ok(id,new{
                        token=permit.Token,mapId,mapName=map.Name,
                        pid=owned.Process.Pid,currentStartedUtc=owned.Process.StartedUtc,rconPort=map.RconPort,
                        launchMode=map.StartWithAsaApi?"asaApiLoader":"directAsa",
                        liveModOrder=permit.LiveMods?.Enabled,
                        nextModOrder=permit.RestartMods?.Enabled,
                        livePassiveModOrder=permit.LiveMods?.Passive,
                        nextPassiveModOrder=permit.RestartMods?.Passive,
                        nextModsSource=permit.RestartMods==null?"test":map.ModChangesPending?"pendingProfile":"verifiedRunning",
                        usesSaveWorld=true,usesDoExit=true,launchesOriginalActive=true,
                        conversionAllowed=false,automaticRetry=false,
                        gameplayReadinessCertified=false,originalStoreTouched=false,
                        evidence="ORIGINAL_STORE_V1_RESTART_TWO_STAGE_PREFLIGHT"
                    });
                }
                if(!ReadId(request,"token",out var token))
                    return Fail(id,"UM-ORIGINAL-RESTART-TOKEN-EXPIRED");
                var approved=approvalsByMap.Values.SingleOrDefault(
                    item=>item.Token==token);
                if(approved==null||
                   DateTimeOffset.UtcNow-approved.Created>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-ORIGINAL-RESTART-TOKEN-EXPIRED");
                // The exact Restart action and short-lived one-use native token
                // replace typed confirmation; verify owner and lease below.
                approvalsByMap.Remove(approved.MapId);
                var original=Owned(approved.MapId);
                if(original==null||!original.HistoricalStoreProtected||
                   original.Maintenance||
                   (original.StartWithAsaApi&&
                    string.IsNullOrWhiteSpace(original.ApiLoaderPath))||
                   !string.IsNullOrWhiteSpace(original.ApiLoaderArguments)||
                   JsonSerializer.Serialize(original)!=approved.Profile||
                   original.RconPort!=approved.RconPort)
                    return Fail(id,"UM-ORIGINAL-RESTART-MAP-CHANGED");
                if(HasActive(original.Id))return Fail(id,"UM-ORIGINAL-RESTART-BUSY");
                if(PendingSaveEvidence(original.Id)!=null)
                    return Fail(id,"UM-ORIGINAL-SAVE-EVIDENCE-NEEDS-REVIEW");
                var rechecked=await Task.Run(()=>preview(approved.MapId));
                if(!Same(rechecked,original,approved))
                    return Fail(id,"UM-ORIGINAL-RESTART-SOURCE-CHANGED");
                var restart=new AsaManualRestartJournal(original.InstallationPath);
                AsaRestartRecord record;
                using(var lease=cluster.Operations.Acquire(
                    original.InstallationPath,UntamedOperationKind.Configuration))
                {
                    restart.DemandNoPending();
                    if(PendingSaveEvidence(original.Id)!=null)
                        return Fail(id,"UM-ORIGINAL-SAVE-EVIDENCE-NEEDS-REVIEW");
                    var underLease=await Task.Run(()=>preview(approved.MapId));
                    if(!Same(underLease,original,approved))
                        return Fail(id,"UM-ORIGINAL-RESTART-LEASE-OWNER-CHANGED");
                    if(approved.LiveMods!=null)
                    {
                        var currentMods=LegacyStoreV1ProcessObserver.CaptureVerifiedModOrder(
                            cluster,original,underLease.Process);
                        if(!approved.LiveMods.Same(currentMods)||approved.RestartMods==null||
                           !approved.RestartMods.Same(LegacyStoreV1ProcessObserver.SelectRestartMods(original,currentMods)))
                            return Fail(id,"UM-ORIGINAL-RESTART-LIVE-MODS-CHANGED");
                    }
                    // Establish durable restart boundary before a single RCON
                    // byte can be sent; interruptions require operator review.
                    // Establish the durable Restart-v1 boundary BEFORE
                    // marking the generic operation mutating. A rejected
                    // Begin() sends no ASA/RCON bytes and must not create a
                    // second false NeedsOperator Launch journal.
                    var retry=ScheduledRestartRetryScope.Current;
                    retry?.Demand(original.Id);
                    record=retry==null?restart.Begin(original.Id,approved.Pid):
                        restart.BeginScheduled(original.Id,approved.Pid,retry.RunId);
                    lease.MarkMutationStarted();
                    lease.Complete();
                }
                var retryPermit=nativeLauncher?ScheduledRestartRetryScope.Current:null;
                var job=new Job{MapId=original.Id,OldPid=approved.Pid,
                    UseApi=original.StartWithAsaApi,StartupRetriesEnabled=nativeLauncher};
                var events=new DiscordOperationEvents.NativeRun(cluster,job.Id,new[]{original.Id},"Restart");
                events.Emit("claimed","in-progress","NATIVE_RESTART_JOURNAL_PREPARED");
                lock(sync)jobsByMap[original.Id]=job;
                _=Task.Run(async()=>{
                    events.Emit("started","in-progress","NATIVE_RESTART_EXECUTION_STARTED");
                    Update(job,"running","SaveWorld → stabil világfájl → DoExit");
                    DateTimeOffset? attemptedStartUtc=null;
                    try
                    {
                        var stopped=await stop(original.Id,approved.Pid,
                            approved.StartedUtc,approved.SourceSha,record.Id,
                            stage=>Update(job,"running",stage));
                        if(stopped.MapId!=original.Id||stopped.Pid!=approved.Pid||
                           !stopped.ConfirmedStopped||!stopped.UsedDoExit||
                           !(stopped.PreExitSaveLogAndWorldObserved||stopped.PreExitWorldMetadataObserved))
                            throw new InvalidDataException(
                                "UM-ORIGINAL-RESTART-OLD-STOP-NOT-PROVEN");
                        Update(job,"running","Régi eredeti PID kilépett; új indítás előellenőrzése",
                            oldStopped:true,saved:stopped.PreExitSaveLogAndWorldObserved,
                            worldMetadata:stopped.PreExitWorldMetadataObserved);
                        events.Emit("save-result",stopped.PreExitSaveLogAndWorldObserved?"verified-local":"limited",stopped.PreExitSaveLogAndWorldObserved?"LOCAL_SAVE_LOG_WORLD":"LOCAL_WORLD_METADATA_LOG_UNVERIFIED");
                        events.Emit("stop-result","verified-local","OLD_PROCESS_STOPPED");
                        // Old process is gone. Persist only the approved mod
                        // fields, under the same install lease, before the
                        // planner fingerprints and launches the new process.
                        // A failed save yields a durable NeedsOperator, not
                        // a fallback launch from the stale profile.
                        if(approved.RestartMods!=null)
                        {
                            using var modLease=cluster.Operations.AcquireMany(
                                new[]{cluster.Store.Root,original.InstallationPath},
                                UntamedOperationKind.Configuration);
                            if(JsonSerializer.Serialize(original)!=approved.Profile)
                                throw new InvalidDataException("UM-ORIGINAL-RESTART-MAP-CHANGED");
                            var target=approved.RestartMods;
                            var saved=ModStateController.Read(original);
                            if(!saved.EnabledOrder.SequenceEqual(target.Enabled)||
                               !saved.Passive.SequenceEqual(target.Passive))
                            {
                                if(!LegacyStoreV1PinnedSource.HasApprovedLocalSource(cluster.Store.Root,original))
                                    throw new InvalidDataException("UM-ORIGINAL-RESTART-MOD-SYNC-REQUIRES-PIN");
                                if(!HistoricalLiveModSync.WriteVerifiedBaseline(original,target,
                                    cluster.Save,modLease.MarkMutationStarted))
                                    throw new InvalidDataException("UM-ORIGINAL-RESTART-MOD-SYNC-CONFLICT");
                            }
                            modLease.Complete();
                        }
                        restart.Change(record.Id,AsaRestartPhase.Starting);
                        string? initialLaunchFingerprint=nativeLauncher?
                            LegacyStoreV1NativeStartPlanner.Prepare(cluster,original,record.Id).Fingerprint:null;
                        for(int attempt=1;;attempt++)
                        {
                        retryPermit?.Demand(original.Id);
                        attemptedStartUtc=null;
                        lock(sync){job.StartupAttempts=attempt;job.Readiness=null;job.FailureCode=null;}
                        try
                        {
                        if(nativeLauncher)
                        {
                            var plan=LegacyStoreV1NativeStartPlanner.Prepare(cluster,original,record.Id);
                            if(plan.Fingerprint!=initialLaunchFingerprint)
                                throw new InvalidOperationException("UM-RESTART-RETRY-LAUNCH-CONFIG-CHANGED");
                        }
                        // Start uses the SAME selected original mode. For
                        // API mode Process.Start yields ONLY a Loader parent,
                        // never an already verified ASA child.
                        ProcessIdentity started;
                        using(var boundary=retryPermit==null?null:RecoveryNativeStartBoundary.Pin(mapId=>{
                            retryPermit.Demand(mapId);
                            var fresh=LegacyStoreV1NativeStartPlanner.Prepare(cluster,original,record.Id);
                            if(fresh.Fingerprint!=initialLaunchFingerprint)
                                throw new InvalidOperationException("UM-RESTART-RETRY-LAUNCH-CONFIG-CHANGED");
                        }))
                            started=await Task.Run(()=>start(original.Id,record.Id,
                                stage=>Update(job,"running",stage)));
                        attemptedStartUtc=started.StartedUtc;
                        string expectedExecutable=original.StartWithAsaApi?
                            original.ApiLoaderPath:original.ExecutablePath;
                        if(started.Pid<=0||started.Pid==approved.Pid||
                           started.StartedUtc<=approved.StartedUtc||
                           !string.Equals(Path.GetFullPath(started.ExecutablePath),
                             Path.GetFullPath(expectedExecutable),
                             StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(
                                "UM-ORIGINAL-RESTART-NEW-PID-INVALID");
                        // Record attempted process before game readiness; this
                        // remains in Restart-v1 even if CurseForge/ASA exits.
                        restart.RecordNewAsaPid(record.Id,started.Pid);
                        int verifiedNewServerPid=started.Pid;
                        var verifiedNewStartedUtc=started.StartedUtc;
                        ProcessIdentity verifiedChildIdentity=started;
                        Update(job,"running","Új folyamat elindult; készültség ellenőrzése",
                            newPid:started.Pid,newStartedUtc:started.StartedUtc);
                        if(original.StartWithAsaApi)
                        {
                            Update(job,"running",
                                "ASA API Loader elindult; ASA gyermek PID és RCON ellenőrzése");
                            bool verified=false;
                            bool childEverSeen=false;
                            int missingLoader=0;
                            // Original Store-v1 Loader/ASA readiness must
                            // use the same ten-minute observer window as fresh
                            // ASA; timeout still requires manual review.
                            var limit=DateTimeOffset.UtcNow+
                                FreshStoreApiPairObserver.StartupObservationWindow;
                            while(DateTimeOffset.UtcNow<limit)
                            {
                                var observed=inspectFamily(original);
                                bool sameLoader=observed.LoaderIdentity is {Pid:>0} seenLoader&&
                                    seenLoader.Pid==started.Pid&&
                                    seenLoader.StartedUtc==started.StartedUtc;
                                if(sameLoader)missingLoader=0;
                                else if(++missingLoader>=3)
                                    throw new InvalidOperationException(
                                        "UM-ORIGINAL-RESTART-LOADER-EXITED-BEFORE-READY");
                                if(observed.Identity is {Pid:>0} seenChild&&
                                   seenChild.Pid!=approved.Pid)childEverSeen=true;
                                else if(childEverSeen)
                                    throw new InvalidOperationException(
                                        "UM-ORIGINAL-RESTART-ASA-EXITED-BEFORE-READY");
                                if(observed.Running&&
                                   observed.Identity is {Pid:>0} apiChild&&
                                   observed.LoaderIdentity is {Pid:>0} parent&&
                                   parent.Pid==started.Pid&&
                                   parent.StartedUtc==started.StartedUtc&&
                                   apiChild.Pid!=approved.Pid&&
                                   owner(original.RconPort,apiChild.Pid))
                                {
                                    if(nativeLauncher)
                                        cluster.RecordOriginalStoreApiPair(
                                            original.Id,observed);
                                    verifiedNewServerPid=apiChild.Pid;
                                    verifiedNewStartedUtc=apiChild.StartedUtc;
                                    verifiedChildIdentity=apiChild;
                                    attemptedStartUtc=apiChild.StartedUtc;
                                    Update(job,"running","ASA gyermek PID és RCON port ellenőrizve",
                                        newPid:apiChild.Pid,newStartedUtc:apiChild.StartedUtc);
                                    verified=true;
                                    break;
                                }
                                await Task.Delay(500);
                            }
                            if(!verified)
                                throw new InvalidOperationException(
                                    "UM-ORIGINAL-RESTART-API-CHILD-NOT-VERIFIED");
                        }
                        // A launched PID or an owned listener is NOT a ready
                        // ASA. Require current-run log, API DLL/plugins and
                        // actual authenticated RCON response before success.
                        if(nativeLauncher)
                        {
                            var expectedLoader=original.StartWithAsaApi?started:null;
                            var readiness=await RestartStartupReadiness.WaitAsync(
                                async token=>{
                                    var proof=await RestartStartupReadiness.ObserveAsync(
                                        original,verifiedChildIdentity,expectedLoader,
                                        inspectFamily,owner,token);
                                    lock(sync)job.Readiness=proof;
                                    return proof;
                                },original.StartWithAsaApi,
                                TimeSpan.FromMinutes(10),TimeSpan.FromSeconds(2),
                                retryPermit?.Cancellation??CancellationToken.None);
                            if(!readiness.Ready(original.StartWithAsaApi))
                                throw new InvalidOperationException(readiness.Reason);
                        }
                        restart.Change(record.Id,AsaRestartPhase.Completed,
                            verifiedNewServerPid);
                        Update(job,"completed",nativeLauncher?
                            "Új ASA PID, API/plugin, join és RCON válasz igazolt":
                            "Izolált új PID; éles készenlét nem igazolt",
                            newPid:verifiedNewServerPid,newStartedUtc:verifiedNewStartedUtc);
                        events.Emit("new-process","verified-local","NEW_PID_OBSERVED");
                        events.Emit("terminal",nativeLauncher?"verified-local":"limited",
                            nativeLauncher?"RESTART_NEW_INSTANCE_READINESS_VERIFIED":
                                "RESTART_SYNTHETIC_PID_ONLY");
                        break;
                        }
                        catch(Exception) when(retryPermit!=null&&attemptedStartUtc.HasValue&&
                            ScheduledRestartRetryScope.CanRetry(attempt,
                                RestartModServiceFailureProbe.Inspect(original,attemptedStartUtc.Value)))
                        {
                            var delay=ScheduledRestartRetryScope.DelayAfter(attempt);
                            lock(sync)job.FailureCode=RestartModServiceFailureProbe.TransientCurseForge;
                            Update(job,"running","CurseForge nem elérhető; következő indítás "+
                                delay.TotalMinutes+" perc múlva");
                            try
                            {
                                ScheduledRestartOutageLedger.Record(cluster.Store.Root,original.Id,
                                    retryPermit.RunId,original.Name,record.ChangedUtc,
                                    RestartModServiceFailureProbe.TransientCurseForge,attempt,
                                    "RETRY_AFTER_"+delay.TotalMinutes+"_MINUTES");
                                DesktopDiscordNotifications.Record(cluster,original.Id,"schedule",true);
                            }
                            catch{/* A notification receipt failure must not interrupt recovery. */}
                            // From here a failure is a retry gate failure, not
                            // another diagnosis of the previous CurseForge log.
                            attemptedStartUtc=null;
                            await retryPermit.Backoff(delay,retryPermit.Cancellation);
                            retryPermit.Demand(original.Id);
                            bool Stopped()
                            {
                                retryPermit.Demand(original.Id);
                                var check=LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(
                                    original,cluster.Config.LocalMaps);
                                return check.ConfirmedStopped&&!check.Running&&
                                    check.Identity==null&&check.LoaderIdentity==null&&
                                    AsaLocalPortPreflight.CanStart(original);
                            }
                            using(var lease=cluster.Operations.Acquire(original.InstallationPath,
                                UntamedOperationKind.Launch))
                            {
                                if(!Stopped())throw new InvalidOperationException(
                                    "UM-RESTART-RETRY-FAMILY-OR-PORT-BUSY");
                                restart.ClaimScheduledRetry(record.Id,retryPermit.RunId,attempt,Stopped);
                                var plan=LegacyStoreV1NativeStartPlanner.Prepare(cluster,original,record.Id);
                                if(plan.Fingerprint!=initialLaunchFingerprint)
                                    throw new InvalidOperationException("UM-RESTART-RETRY-LAUNCH-CONFIG-CHANGED");
                                lease.Complete();
                            }
                        }
                        catch(Exception ex) when(nativeLauncher&&attemptedStartUtc.HasValue&&
                            attempt<YarkRecoveryPolicy.MaxAttempts)
                        {
                            bool Stopped()
                            {
                                var check=LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(
                                    original,cluster.Config.LocalMaps);
                                return check.ConfirmedStopped&&!check.Running&&
                                    check.Identity==null&&check.LoaderIdentity==null&&
                                    AsaLocalPortPreflight.CanStart(original);
                            }
                            if(!Stopped())throw;
                            var backoff=YarkRecoveryPolicy.BackoffAfter(attempt);
                            lock(sync)job.FailureCode=NativeLifecycleFailureCode.From(ex);
                            Update(job,"running","ASA összeomlott; YARK-alapú automatikus újrapróbálás "+
                                (attempt+1)+"/"+YarkRecoveryPolicy.MaxAttempts+" · "+
                                backoff.TotalSeconds+" mp");
                            await Task.Delay(backoff);
                            using(var retryLease=cluster.Operations.Acquire(
                                original.InstallationPath,UntamedOperationKind.Launch))
                            {
                                if(!Stopped())throw new InvalidOperationException(
                                    "UM-RESTART-AUTO-RETRY-FAMILY-RUNNING");
                                record=restart.ClaimAutomaticRetry(record.Id,attempt,Stopped);
                                var plan=LegacyStoreV1NativeStartPlanner.Prepare(
                                    cluster,original,record.Id);
                                if(plan.Fingerprint!=initialLaunchFingerprint)
                                    throw new InvalidOperationException(
                                        "UM-RESTART-AUTO-RETRY-LAUNCH-CONFIG-CHANGED");
                                retryLease.Complete();
                            }
                            attemptedStartUtc=null;
                            continue;
                        }
                        }
                    }
                    catch(Exception ex)
                    {
                        string code=(attemptedStartUtc is { } since?
                            RestartModServiceFailureProbe.Inspect(original,since):null)
                            ??NativeLifecycleFailureCode.From(ex);
                        try{restart.Change(record.Id,AsaRestartPhase.NeedsOperator);}
                        catch{/* Preserve the original durable failed receipt. */}
                        lock(sync)job.FailureCode=code;
                        Update(job,"failed",
                            "Store-v1 Restart sikertelen ["+code+
                            "]. A következő Start automatikusan helyreállítja a régi műveleti állapotot.");
                        events.Emit("terminal","uncertain",code);
                    }
                });
                return Ok(id,new{
                    jobId=job.Id,started=true,automaticRetry=nativeLauncher,
                    forceKill=false,storeTouched=false,
                    evidence="ORIGINAL_STORE_V1_MANUAL_RESTART_JOB_QUEUED"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception e)
        {
            // Preserve known planner/recovery symbolic reason codes without
            // revealing raw exception messages or RCON secrets.
            return Fail(id,NativeLifecycleFailureCode.From(e));
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>serial.Dispose();
}
