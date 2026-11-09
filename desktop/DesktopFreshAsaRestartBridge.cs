using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit one-shot manual restart of a NEW, local, non-Store ASA server.
/// Graceful RCON SaveWorld/DoExit must complete with the old OS process gone
/// before starting any replacement. Never kills processes, never retries
/// automatically, and never replays after a failed or interrupted operation.
/// </summary>
public sealed class DesktopFreshAsaRestartBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,int Pid,
        DateTimeOffset StartedUtc,string Root,string Args,int RconPort,
        DateTimeOffset Created);
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public string State="queued",Stage="preflight";
        public int OldPid,NewPid;
        public string NewProcessRole="asaServer";
        public bool PreExitSaveEvidenceObserved;
        public DateTimeOffset Changed=DateTimeOffset.UtcNow;
    }
    private readonly ClusterService cluster;
    private readonly bool nativeRunner;
    private readonly CancellationTokenSource closing=new();
    private readonly CancellationToken closingToken;
    private readonly Func<int,int,bool> portOwner;
    private readonly Func<Guid,ProcessIdentity,Guid,Action<string>,
        Task<(AsaGracefulStopReceipt Stop,ProcessIdentity Started)>> execute;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly object sync=new();
    private Approval? pending;
    private Job? current;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopFreshAsaRestartBridge(ClusterService service,
        Func<int,int,bool>? ownsPort=null,
        Func<Guid,ProcessIdentity,Guid,Action<string>,
            Task<(AsaGracefulStopReceipt Stop,ProcessIdentity Started)>>? runner=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        nativeRunner=runner==null;
        closingToken=closing.Token;
        portOwner=ownsPort??RconPortOwnership.IsOwnedListener;
        execute=runner??(async(mapId,old,journalId,progress)=>{
            progress("SaveWorld → DoExit");
            var stopped=await AsaGracefulStop.StopAsync(cluster,mapId,old,
                TimeSpan.FromMinutes(2),approvedRestartId:journalId,
                evidenceStage:progress);
            if(!stopped.ConfirmedStopped||stopped.MapId!=mapId)
                throw new InvalidOperationException("ASA old process did not stop");
            progress("Starting new ASA process");
            var map=Owned(mapId)??throw new InvalidDataException("ASA local map disappeared");
            new AsaManualRestartJournal(map.InstallationPath).Change(
                journalId,AsaRestartPhase.Starting);
            var started=cluster.LaunchFreshAsa(mapId,true,journalId);
            return(stopped,started);
        });
    }
    private LocalMap? Owned(Guid mapId)=>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
    private ProcessIdentity Eligible(LocalMap map)
    {
        bool newStore=FreshStoreEnrollmentPolicy.IsEnrolled(map);
        if(newStore)FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);
        else FreshAsaInstallationSafety.Demand(cluster,map);
        if(map.Maintenance || !AsaReadyProbe.IsRegisteredServerBinary(map) ||
           map.RconHost!="127.0.0.1" || map.RconPort is <1024 or >65535 ||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected) ||
           !AsaRconConfigurationEvidence.Matches(map) ||
           string.IsNullOrWhiteSpace(AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath)))
            throw new InvalidOperationException("Restart requires a new local non-Store ASA installation");
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        var state=cluster.Check(map);
        if(!ReleaseCapabilityGate.Check(ManagerOperation.RealAsaRestart,true,
            map.Maintenance,state.ConfirmedStopped,state.Running,
            verifiedFreshInstall:true,explicitOperatorApproval:true).Allowed ||
           state.Identity is not {Pid:>0} identity ||
           !portOwner(map.RconPort,identity.Pid))
            throw new InvalidOperationException("Restart requires verified local running process and RCON listener");
        return identity;
    }
    private static bool ReadGuid(JsonElement data,string name,out Guid value)
    {
        value=Guid.Empty;
        return data.TryGetProperty(name,out var element) &&
            element.ValueKind==JsonValueKind.String &&
            Guid.TryParse(element.GetString(),out value)&&value!=Guid.Empty;
    }
    private void Change(Job item,string state,string stage,int newPid=0)
    {
        lock(sync)
        {
            item.State=state;item.Stage=stage;
            item.Changed=DateTimeOffset.UtcNow;
            if(newPid>0)item.NewPid=newPid;
        }
    }
    private object Snapshot(Job item)
    {
        lock(sync)return new{
            jobId=item.Id,state=item.State,stage=item.Stage,
            oldPid=item.OldPid,newPid=item.NewPid,
            newProcessRole=item.NewProcessRole,
            changedUtc=item.Changed,
            saveCompleteProven=false,readinessCertified=false,
            preExitSaveEvidenceObserved=item.PreExitSaveEvidenceObserved,
            finalDoExitSaveCertified=false,gameClientRejoinVerified=false,
            killPerformed=false,storeTouched=false,
            autoRetry=false
        };
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3000)return Fail(null,"UM-ASA-RESTART-MESSAGE");
            using var document=JsonDocument.Parse(raw);
            var args=document.RootElement;
            if(args.ValueKind!=JsonValueKind.Object ||
               !ReadGuid(args,"id",out var request) ||
               !args.TryGetProperty("method",out var methodField) ||
               methodField.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-ASA-RESTART-MESSAGE");
            id=request.ToString("D");
            var method=methodField.GetString();
            if(method is not ("servers.restartPreview" or
                "servers.restartApply" or "servers.restartStatus" or
                "servers.restartCurrent"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="servers.restartCurrent")
                {
                    if(!ReadGuid(args,"mapId",out var mapId))
                        return Fail(id,"UM-ASA-RESTART-MAP");
                    var map=Owned(mapId);
                    if(map==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    var saved=new AsaManualRestartJournal(map.InstallationPath).Observe();
                    bool belongs=saved!=null&&saved.MapId==map.Id;
                    bool pending=saved!=null&&saved.Phase!=AsaRestartPhase.Completed;
                    return Ok(id,new{
                        exists=saved!=null,belongsToMap=belongs,
                        phase=saved?.Phase.ToString(),
                        oldPid=saved?.OldPid,newPid=saved?.NewPid,
                        changedUtc=saved?.ChangedUtc,
                        needsOperatorReview=pending,autoResumeAllowed=false,
                        evidence="DURABLE_MACHINE_LOCAL_MANUAL_ASA_RESTART"
                    });
                }
                if(method=="servers.restartStatus")
                {
                    if(!ReadGuid(args,"jobId",out var jobId) ||
                       current==null||current.Id!=jobId)
                        return Fail(id,"UM-ASA-RESTART-JOB-MISSING");
                    return Ok(id,Snapshot(current));
                }
                if(current!=null)
                {
                    lock(sync)
                        if(current.State is "queued" or "running")
                            return Fail(id,"UM-ASA-RESTART-BUSY");
                }
                if(method=="servers.restartPreview")
                {
                    pending=null;
                    if(!ReadGuid(args,"mapId",out var mapId))
                        return Fail(id,"UM-ASA-RESTART-MAP");
                    var map=Owned(mapId);
                    if(map==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    var original=Eligible(map);
                    pending=new(Guid.NewGuid(),map.Id,original.Pid,
                        original.StartedUtc,map.InstallationPath,
                        map.Arguments,map.RconPort,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pending.Token,mapId=map.Id,mapName=map.Name,
                        currentPid=original.Pid,saveWorldBeforeExit=true,
                        verifiesOldProcessExit=true,autoRetry=false,
                        killPerformed=false,storeTouched=false,
                        evidence="REAL_LOCAL_NONSTORE_RESTART_PREVIEW_NO_WRITES"
                    });
                }
                if(!ReadGuid(args,"token",out var token) ||
                   pending==null||pending.Token!=token ||
                   DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-ASA-RESTART-TOKEN-EXPIRED");
                if(!args.TryGetProperty("confirm",out var confirmation)||
                   confirmation.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-ASA-RESTART-APPROVAL");
                var approved=pending;pending=null;
                var currentMap=Owned(approved.MapId);
                if(currentMap==null ||
                   currentMap.InstallationPath!=approved.Root ||
                   currentMap.Arguments!=approved.Args ||
                   currentMap.RconPort!=approved.RconPort)
                    return Fail(id,"UM-ASA-RESTART-PROFILE-CHANGED");
                var observed=Eligible(currentMap);
                if(observed.Pid!=approved.Pid||
                   observed.StartedUtc!=approved.StartedUtc)
                    return Fail(id,"UM-ASA-RESTART-PROCESS-CHANGED");
                var restartJournal=new AsaManualRestartJournal(currentMap.InstallationPath);
                // Establish the persistent restart boundary while holding the
                // SAME installation lease used by ordinary Start/Stop. A
                // concurrent independent Start cannot pass its under-lease
                // journal check between approval and Begin().
                AsaRestartRecord persisted;
                using(var prepareLease=cluster.Operations.Acquire(
                    currentMap.InstallationPath,UntamedOperationKind.Configuration))
                {
                    var onceMore=Eligible(currentMap);
                    if(onceMore.Pid!=approved.Pid ||
                       onceMore.StartedUtc!=approved.StartedUtc)
                        return Fail(id,"UM-ASA-RESTART-PROCESS-CHANGED");
                    prepareLease.MarkMutationStarted();
                    persisted=restartJournal.Begin(currentMap.Id,approved.Pid);
                    prepareLease.Complete();
                }
                var job=new Job{
                    OldPid=approved.Pid,
                    NewProcessRole=currentMap.StartWithAsaApi?"loaderParent":"asaServer"
                };
                current=job;
                _=Task.Run(async()=>{
                    Change(job,"running","SaveWorld → DoExit");
                    try
                    {
                        var receipt=await execute(currentMap.Id,observed,persisted.Id,
                            stage=>Change(job,"running",stage));
                        if(!receipt.Stop.ConfirmedStopped||
                           receipt.Stop.MapId!=currentMap.Id ||
                           receipt.Stop.Pid!=approved.Pid ||
                           receipt.Started.Pid<=0 ||
                           receipt.Started.StartedUtc<=approved.StartedUtc)
                            throw new InvalidDataException("UM-ASA-RESTART-RECEIPT-INVALID");
                        job.PreExitSaveEvidenceObserved=
                            receipt.Stop.PreExitSaveLogAndWorldObserved;
                        restartJournal.Change(persisted.Id,AsaRestartPhase.Completed,
                            receipt.Started.Pid);
                        Change(job,"completed",
                            job.NewProcessRole=="loaderParent"?
                                "New Loader parent spawned; ASA child and RCON readiness unverified":
                                "New Windows ASA process started; readiness unverified",
                            receipt.Started.Pid);
                        if(nativeRunner&&currentMap.StartWithAsaApi&&
                           FreshStoreEnrollmentPolicy.IsEnrolled(currentMap))
                            FreshStoreApiPairObserver.Begin(
                                cluster,currentMap.Id,receipt.Started,closingToken);
                    }
                    catch(Exception)
                    {
                        try{restartJournal.Change(persisted.Id,AsaRestartPhase.NeedsOperator);}
                        catch{/* Keep the original pending journal; never clear it. */}
                        Change(job,"needsOperator",
                            "Interrupted restart: inspect local process and operation journal");
                    }
                });
                return Ok(id,new{
                    jobId=job.Id,started=true,
                    autoRetry=false,killPerformed=false,storeTouched=false,
                    evidence="REAL_LOCAL_ASA_RESTART_JOB_QUEUED"
                });
            }
            finally{gate.Release();}
        }
        catch(Exception){return Fail(id,"UM-ASA-RESTART-FAILED");}
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()
    {
        closing.Cancel();
        closing.Dispose();
        gate.Dispose();
    }
}
