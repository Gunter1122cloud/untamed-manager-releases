using System;
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
        int RconPort,DateTimeOffset Created);

    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public Guid MapId;
        public int OldPid,NewPid;
        public bool UseApi;
        public string State="queued",Step="preflight";
        public bool OldProcessStopped,PreExitSaveObserved,PreExitWorldMetadataObserved;
        public DateTimeOffset ChangedUtc=DateTimeOffset.UtcNow;
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
    private Approval? pending;
    private Job? current;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

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
        bool? worldMetadata=null)
    {
        lock(sync)
        {
            job.State=state;job.Step=step;
            job.ChangedUtc=DateTimeOffset.UtcNow;
            if(oldStopped.HasValue)job.OldProcessStopped=oldStopped.Value;
            if(saved.HasValue)job.PreExitSaveObserved=saved.Value;
            if(worldMetadata.HasValue)job.PreExitWorldMetadataObserved=worldMetadata.Value;
            if(newPid>0)job.NewPid=newPid;
        }
    }
    private object Snapshot(Job job)
    {
        lock(sync)return new{
            jobId=job.Id,mapId=job.MapId,state=job.State,step=job.Step,
            launchMode=job.UseApi?"asaApiLoader":"directAsa",
            changedUtc=job.ChangedUtc,
            oldPid=job.OldPid,newPid=job.NewPid,
            oldProcessStopped=job.OldProcessStopped,
            preExitSaveLogAndWorldObserved=job.PreExitSaveObserved,
            preExitWorldMetadataObserved=job.PreExitWorldMetadataObserved,
            readyCertified=false,gameClientRejoinVerified=false,
            finalDoExitSaveCertified=false,automaticRetry=false,
            forceKill=false,storeTouched=false,
            evidence="ORIGINAL_STORE_V1_NATIVE_MANUAL_RESTART_JOB"
        };
    }

    private object Recover(Guid mapId)
    {
        var map=Owned(mapId);
        if(map==null||!map.HistoricalStoreProtected)
            throw new UnauthorizedAccessException("UM-ORIGINAL-RESTART-LOCAL-ONLY");
        var entry=new AsaManualRestartJournal(map.InstallationPath).Observe();
        Job? running;
        lock(sync)
            running=current!=null&&current.MapId==mapId&&
                current.State is "queued" or "running"?current:null;
        bool pendingRestart=entry!=null&&entry.Phase is not (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed);
        var operation=cluster.Operations.Observe(map.InstallationPath);
        bool pendingOperation=operation?.Phase is UntamedOperationPhase.Prepared or
            UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator;
        bool same=entry?.MapId==mapId;
        return new{
            mapId,phase=entry?.Phase.ToString()??"None",
            entryBelongsToMap=same,
            blocked=pendingRestart||pendingOperation,
            requiresOperator=(pendingRestart||pendingOperation)&&running==null,
            inMemoryRunning=running!=null,jobId=running?.Id,
            oldPid=entry?.OldPid,newPid=entry?.NewPid,
            automaticRecovery=false,automaticRetry=false,
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
                    if(!ReadId(request,"jobId",out var jobId)||
                       current==null||current.Id!=jobId)
                        return Fail(id,"UM-ORIGINAL-RESTART-JOB-MISSING");
                    return Ok(id,Snapshot(current));
                }
                if(current!=null)
                {
                    lock(sync)
                        if(current.State is "queued" or "running")
                            return Fail(id,"UM-ORIGINAL-RESTART-BUSY");
                }
                if(method=="historical.restartPreview")
                {
                    pending=null;
                    if(!ReadId(request,"mapId",out var mapId))
                        return Fail(id,"UM-ORIGINAL-RESTART-MAP");
                    var map=Owned(mapId);
                    if(map==null||!map.HistoricalStoreProtected||map.Maintenance)
                        return Fail(id,"UM-ORIGINAL-RESTART-LOCAL-ONLY");
                    // Preserve the exact per-map launch mode. A Loader-based
                    // restart is permitted only when the verified ASA CHILD
                    // owns RCON; never send DoExit to the Loader parent.
                    if((map.StartWithAsaApi&&string.IsNullOrWhiteSpace(map.ApiLoaderPath))||
                       !string.IsNullOrWhiteSpace(map.ApiLoaderArguments))
                        return Fail(id,"UM-ORIGINAL-RESTART-LOADER-NOT-SUPPORTED");
                    new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
                    var existing=cluster.Operations.Observe(map.InstallationPath);
                    if(existing?.Phase is UntamedOperationPhase.Prepared or
                        UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)
                        return Fail(id,"UM-ORIGINAL-RESTART-OPERATION-RECOVERY");
                    var owned=await Task.Run(()=>preview(mapId));
                    if(!ReferenceEquals(owned.Map,map)||owned.Process.Pid<=0||
                       owned.Process.StartedUtc==default||owned.SourceSha.Length!=64)
                        return Fail(id,"UM-ORIGINAL-RESTART-SOURCE-INVALID");
                    pending=new Approval(Guid.NewGuid(),mapId,owned.Process.Pid,
                        owned.Process.StartedUtc,JsonSerializer.Serialize(map),
                        owned.SourceSha,map.RconPort,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pending.Token,mapId,mapName=map.Name,
                        pid=owned.Process.Pid,rconPort=map.RconPort,
                        launchMode=map.StartWithAsaApi?"asaApiLoader":"directAsa",
                        usesSaveWorld=true,usesDoExit=true,launchesOriginalActive=true,
                        conversionAllowed=false,automaticRetry=false,
                        gameplayReadinessCertified=false,originalStoreTouched=false,
                        evidence="ORIGINAL_STORE_V1_RESTART_TWO_STAGE_PREFLIGHT"
                    });
                }
                if(!ReadId(request,"token",out var token)||pending==null||
                   pending.Token!=token||
                   DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-ORIGINAL-RESTART-TOKEN-EXPIRED");
                if(!request.TryGetProperty("confirm",out var confirm)||
                   confirm.ValueKind!=JsonValueKind.True||
                   !request.TryGetProperty("typedApproval",out var phrase)||
                   phrase.ValueKind!=JsonValueKind.String||
                   phrase.GetString()!="STORE-V1 SAVEWORLD DOEXIT START")
                    return Fail(id,"UM-ORIGINAL-RESTART-TYPED-APPROVAL");
                var approved=pending;pending=null;
                var original=Owned(approved.MapId);
                if(original==null||!original.HistoricalStoreProtected||
                   original.Maintenance||
                   (original.StartWithAsaApi&&
                    string.IsNullOrWhiteSpace(original.ApiLoaderPath))||
                   !string.IsNullOrWhiteSpace(original.ApiLoaderArguments)||
                   JsonSerializer.Serialize(original)!=approved.Profile||
                   original.RconPort!=approved.RconPort)
                    return Fail(id,"UM-ORIGINAL-RESTART-MAP-CHANGED");
                var rechecked=await Task.Run(()=>preview(approved.MapId));
                if(!Same(rechecked,original,approved))
                    return Fail(id,"UM-ORIGINAL-RESTART-SOURCE-CHANGED");
                var restart=new AsaManualRestartJournal(original.InstallationPath);
                AsaRestartRecord record;
                using(var lease=cluster.Operations.Acquire(
                    original.InstallationPath,UntamedOperationKind.Configuration))
                {
                    restart.DemandNoPending();
                    var underLease=await Task.Run(()=>preview(approved.MapId));
                    if(!Same(underLease,original,approved))
                        return Fail(id,"UM-ORIGINAL-RESTART-LEASE-OWNER-CHANGED");
                    // Establish durable restart boundary before a single RCON
                    // byte can be sent; interruptions require operator review.
                    lease.MarkMutationStarted();
                    record=restart.Begin(original.Id,approved.Pid);
                    lease.Complete();
                }
                var job=new Job{MapId=original.Id,OldPid=approved.Pid,
                    UseApi=original.StartWithAsaApi};
                current=job;
                _=Task.Run(async()=>{
                    Update(job,"running","SaveWorld → stabil világfájl → DoExit");
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
                        restart.Change(record.Id,AsaRestartPhase.Starting);
                        // Start uses the SAME selected original mode. For
                        // API mode Process.Start yields ONLY a Loader parent,
                        // never an already verified ASA child.
                        var started=await Task.Run(()=>start(original.Id,record.Id,
                            stage=>Update(job,"running",stage)));
                        string expectedExecutable=original.StartWithAsaApi?
                            original.ApiLoaderPath:original.ExecutablePath;
                        if(started.Pid<=0||started.Pid==approved.Pid||
                           started.StartedUtc<=approved.StartedUtc||
                           !string.Equals(Path.GetFullPath(started.ExecutablePath),
                             Path.GetFullPath(expectedExecutable),
                             StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(
                                "UM-ORIGINAL-RESTART-NEW-PID-INVALID");
                        int verifiedNewServerPid=started.Pid;
                        if(original.StartWithAsaApi)
                        {
                            Update(job,"running",
                                "ASA API Loader elindult; ASA gyermek PID és RCON ellenőrzése");
                            bool verified=false;
                            var limit=DateTimeOffset.UtcNow.AddMinutes(2);
                            while(DateTimeOffset.UtcNow<limit)
                            {
                                var observed=inspectFamily(original);
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
                                    verified=true;
                                    break;
                                }
                                await Task.Delay(500);
                            }
                            if(!verified)
                                throw new InvalidOperationException(
                                    "UM-ORIGINAL-RESTART-API-CHILD-NOT-VERIFIED");
                        }
                        restart.Change(record.Id,AsaRestartPhase.Completed,
                            verifiedNewServerPid);
                        Update(job,"completed",original.StartWithAsaApi?
                            "Új ASA API gyermek PID és RCON tulajdonlás igazolt; játékbeli készenlét még nem":
                            "Új eredeti ASA PID létrejött; játékbeli készenlét nem igazolt",
                            newPid:verifiedNewServerPid);
                    }
                    catch(Exception ex)
                    {
                        try{restart.Change(record.Id,AsaRestartPhase.NeedsOperator);}
                        catch{/* Durable previous state remains; never clear a journal. */}
                        Update(job,"needsOperator",
                            "Store-v1 Restart megszakadt ["+NativeLifecycleFailureCode.From(ex)+
                            "]: helyi PID és tartós napló kézi ellenőrzése szükséges");
                    }
                });
                return Ok(id,new{
                    jobId=job.Id,started=true,automaticRetry=false,
                    forceKill=false,storeTouched=false,
                    evidence="ORIGINAL_STORE_V1_MANUAL_RESTART_JOB_QUEUED"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-ORIGINAL-RESTART-SAFETY-FAILED");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>serial.Dispose();
}
