using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        string ProfileSnapshot,string IniFingerprint,string BinariesFingerprint,
        DateTimeOffset Created);
    private sealed record ReviewApproval(Guid Token,Guid MapId,Guid JournalId,
        int OldPid,DateTimeOffset OldStartedUtc,string ProfileSnapshot,
        string RestartSnapshot,DateTimeOffset ExpiresUtc);
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public Guid MapId;
        public string State="queued",Stage="preflight";
        public int OldPid,NewPid,NewAsaServerPid;
        public string NewProcessRole="asaServer";
        public bool PreExitSaveEvidenceObserved,PreExitWorldMetadataObserved;
        public bool ApiChildAndRconVerified;
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
    private readonly Dictionary<Guid,Approval> approvalsByMap=new();
    private ReviewApproval? reviewPending;
    private readonly Dictionary<Guid,Job> jobsByMap=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private bool HasActive(Guid mapId)
    {
        lock(sync)return jobsByMap.TryGetValue(mapId,out var existing)&&
            existing.State is ("queued" or "running");
    }

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
    private ProcessIdentity Eligible(LocalMap map,bool allowPendingRestart=false)
    {
        bool newStore=FreshStoreEnrollmentPolicy.IsEnrolled(map);
        if(newStore)FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);
        else
        {
            FreshAsaInstallationSafety.Demand(cluster,map);
            // Reject actual Store/cluster switches BEFORE SaveWorld/DoExit.
            // Otherwise a restart could stop the old ASA and only discover
            // at the final Process.Start that the replacement is forbidden.
            // Ordinary SessionName text containing "Store" is allowed.
            if(AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.Arguments)||
               AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.ApiLoaderArguments))
                throw new InvalidOperationException("UM-ASA-RESTART-UNENROLLED-STORE-SWITCH");
        }
        if(map.Maintenance || !AsaReadyProbe.IsRegisteredServerBinary(map) ||
           map.RconHost!="127.0.0.1" || map.RconPort is <1024 or >65535 ||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected) ||
           !AsaRconConfigurationEvidence.Matches(map) ||
           string.IsNullOrWhiteSpace(AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath)))
            throw new InvalidOperationException("Restart requires a new local non-Store ASA installation");
        if(!allowPendingRestart)
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
    private static bool SameApprovedSource(LocalMap map,Approval permit) =>
        JsonSerializer.Serialize(map)==permit.ProfileSnapshot &&
        DesktopFreshAsaStartBridge.IniFingerprint(map)==permit.IniFingerprint &&
        AsaReadyProbe.ApprovedLaunchBinariesFingerprint(map)==permit.BinariesFingerprint;

    // Read-only exact running-owner verification for an interrupted Restart.
    // It NEVER dispatches RCON, invents SaveComplete or resumes old Start.
    private bool ReviewOwner(LocalMap map,AsaRestartRecord record,
        DateTimeOffset? expectedStart=null,string? expectedProfile=null,
        bool allowJustReviewed=false)
    {
        // The Core recovery calls its final verifier AFTER it durably
        // writes OperatorReviewed. That terminal phase is allowed ONLY
        // during this last identity check, not for a fresh recovery preview.
        bool unresolved=record.Phase is AsaRestartPhase.Stopping or AsaRestartPhase.NeedsOperator;
        if(record.MapId!=map.Id||record.OldPid<=0||record.NewPid!=0||
           !(unresolved||(allowJustReviewed&&record.Phase==AsaRestartPhase.OperatorReviewed))||
           (expectedProfile!=null&&JsonSerializer.Serialize(map)!=expectedProfile))
            return false;
        var general=cluster.Operations.Observe(map.InstallationPath);
        if(general?.Phase is UntamedOperationPhase.Prepared or
            UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)
            return false;
        try
        {
            var existing=Eligible(map,allowPendingRestart:true);
            return existing.Pid==record.OldPid&&
                (expectedStart==null||existing.StartedUtc==expectedStart);
        }
        catch(Exception){return false;}
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
            newAsaServerPid=item.NewAsaServerPid,
            apiChildAndRconVerified=item.ApiChildAndRconVerified,
            preExitWorldMetadataObserved=item.PreExitWorldMetadataObserved,
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
        string? calledMethod=null;
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
            calledMethod=method;
            if(method is not ("servers.restartPreview" or
                "servers.restartApply" or "servers.restartStatus" or
                "servers.restartCurrent" or
                "servers.restartReviewPreview" or
                "servers.restartReviewApply"))
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
                    if(HasActive(mapId))return Fail(id,"UM-ASA-RESTART-BUSY");
                    var saved=new AsaManualRestartJournal(map.InstallationPath).Observe();
                    bool belongs=saved!=null&&saved.MapId==map.Id;
                    bool pending=saved!=null&&saved.Phase is not (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed);
                    Guid? inMemoryJobId=null;
                    string? inMemoryJobState=null;
                    lock(sync)
                    {
                        if(jobsByMap.TryGetValue(map.Id,out var tracked))
                        {
                            inMemoryJobId=tracked.Id;
                            inMemoryJobState=tracked.State;
                        }
                    }
                    return Ok(id,new{
                        exists=saved!=null,belongsToMap=belongs,
                        inMemoryJobId,inMemoryJobState,
                        inMemoryRunning=inMemoryJobState is "queued" or "running",
                        phase=saved?.Phase.ToString(),
                        oldPid=saved?.OldPid,newPid=saved?.NewPid,
                        changedUtc=saved?.ChangedUtc,
                        needsOperatorReview=pending,autoResumeAllowed=false,
                        evidence="DURABLE_MACHINE_LOCAL_MANUAL_ASA_RESTART"
                    });
                }
                if(method=="servers.restartStatus")
                {
                    if(!ReadGuid(args,"jobId",out var jobId))
                        return Fail(id,"UM-ASA-RESTART-JOB-MISSING");
                    Job? tracked;
                    lock(sync)tracked=jobsByMap.Values.FirstOrDefault(
                        item=>item.Id==jobId);
                    if(tracked==null)return Fail(id,"UM-ASA-RESTART-JOB-MISSING");
                    return Ok(id,Snapshot(tracked));
                }
                // A crashed fresh Restart with the ORIGINAL ASA still
                // running is not a permanent block. The same durable
                // installation coordinator audits the old Restart-v1 ID,
                // ONLY after fresh original PID/time, INI and local RCON
                // listener proof. A new Restart still needs fresh approval.
                if(method=="servers.restartReviewPreview")
                {
                    reviewPending=null;
                    if(!ReadGuid(args,"mapId",out var mapId))
                        return Fail(id,"UM-ASA-RESTART-REVIEW-MAP");
                    var map=Owned(mapId);
                    if(map==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    var saved=new AsaManualRestartJournal(map.InstallationPath).Observe();
                    if(saved==null||saved.MapId!=mapId||saved.NewPid!=0||
                       saved.Phase is not
                           (AsaRestartPhase.Stopping or AsaRestartPhase.NeedsOperator))
                        return Fail(id,"UM-ASA-RESTART-REVIEW-NOT-ELIGIBLE");
                    if(DateTimeOffset.UtcNow-saved.ChangedUtc<TimeSpan.FromMinutes(3))
                        return Fail(id,"UM-RESTART-REVIEW-TOO-RECENT");
                    if(!ReviewOwner(map,saved))
                        return Fail(id,"UM-ASA-RESTART-REVIEW-OLD-PID-NOT-RUNNING");
                    var oldProcess=Eligible(map,allowPendingRestart:true);
                    reviewPending=new(Guid.NewGuid(),map.Id,saved.Id,
                        oldProcess.Pid,oldProcess.StartedUtc,
                        JsonSerializer.Serialize(map),JsonSerializer.Serialize(saved),
                        DateTimeOffset.UtcNow.AddMinutes(3));
                    return Ok(id,new {
                        token=reviewPending.Token,mapId=map.Id,mapName=map.Name,
                        oldPid=oldProcess.Pid,
                        oldStartedUtc=oldProcess.StartedUtc,
                        journalId=saved.Id,phase=saved.Phase.ToString(),
                        exactOldProcessVerified=true,rconOwnerVerified=true,
                        previousOutcomeUnknown=true,restartWillNotReplay=true,
                        serverStarted=false,serverStopped=false,rconSent=false,
                        evidence="FRESH_ASA_OLD_RUNNING_PID_LOCAL_RESTART_REVIEW_PREVIEW"
                    });
                }
                if(method=="servers.restartReviewApply")
                {
                    if(!ReadGuid(args,"token",out var reviewToken)||
                       reviewPending==null||reviewPending.Token!=reviewToken)
                        return Fail(id,"UM-ASA-RESTART-REVIEW-APPROVAL-EXPIRED");
                    var reviewPermit=reviewPending;
                    reviewPending=null; // one shot even when verification fails
                    if(reviewPermit.ExpiresUtc<DateTimeOffset.UtcNow||
                       !args.TryGetProperty("confirm",out var confirmReview)||
                       confirmReview.ValueKind!=JsonValueKind.True)
                        return Fail(id,"UM-ASA-RESTART-REVIEW-APPROVAL");
                    var owned=Owned(reviewPermit.MapId);
                    if(owned==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    var journal=new AsaManualRestartJournal(owned.InstallationPath);
                    var reviewSnapshot=journal.Observe();
                    if(reviewSnapshot==null||reviewSnapshot.Id!=reviewPermit.JournalId||
                       JsonSerializer.Serialize(reviewSnapshot)!=reviewPermit.RestartSnapshot||
                       !ReviewOwner(owned,reviewSnapshot,reviewPermit.OldStartedUtc,
                           reviewPermit.ProfileSnapshot)||
                       reviewSnapshot.OldPid!=reviewPermit.OldPid)
                        return Fail(id,"UM-ASA-RESTART-REVIEW-SOURCE-CHANGED");
                    cluster.Operations.ReviewRunningOriginalRestartOnly(
                        owned.InstallationPath,owned.Id,reviewPermit.JournalId,
                        reviewPermit.OldPid,()=>{
                            var fresh=journal.Observe();
                            return fresh!=null&&fresh.Id==reviewPermit.JournalId&&
                                fresh.MapId==owned.Id&&fresh.OldPid==reviewPermit.OldPid&&
                                fresh.NewPid==0&&ReviewOwner(owned,fresh,
                                    reviewPermit.OldStartedUtc,reviewPermit.ProfileSnapshot,
                                    allowJustReviewed:true);
                        });
                    return Ok(id,new{
                        mapId=owned.Id,oldPid=reviewPermit.OldPid,
                        state="operatorReviewed",previousOutcomeUnknown=true,
                        newRestartSucceeded=false,serverStarted=false,
                        serverStopped=false,rconSent=false,
                        originalJournalAudited=true,
                        nextActionRequiresNewApproval=true,
                        evidence="FRESH_ASA_OLD_RUNNING_PID_RESTART_JOURNAL_AUDITED"
                    });
                }
                if(method=="servers.restartPreview")
                {
                    if(!ReadGuid(args,"mapId",out var mapId))
                        return Fail(id,"UM-ASA-RESTART-MAP");
                    var map=Owned(mapId);
                    if(map==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    if(HasActive(mapId))return Fail(id,"UM-ASA-RESTART-BUSY");
                    approvalsByMap.Remove(mapId);
                    var original=Eligible(map);
                    var permit=new Approval(Guid.NewGuid(),map.Id,original.Pid,
                        original.StartedUtc,map.InstallationPath,
                        map.Arguments,map.RconPort,JsonSerializer.Serialize(map),
                        DesktopFreshAsaStartBridge.IniFingerprint(map),
                        AsaReadyProbe.ApprovedLaunchBinariesFingerprint(map),
                        DateTimeOffset.UtcNow);
                    approvalsByMap[map.Id]=permit;
                    return Ok(id,new{
                        token=permit.Token,mapId=map.Id,mapName=map.Name,
                        currentPid=original.Pid,saveWorldBeforeExit=true,
                        verifiesOldProcessExit=true,autoRetry=false,
                        killPerformed=false,storeTouched=false,
                        evidence="REAL_LOCAL_NONSTORE_RESTART_PREVIEW_NO_WRITES"
                    });
                }
                if(!ReadGuid(args,"token",out var token))
                    return Fail(id,"UM-ASA-RESTART-TOKEN-EXPIRED");
                var approved=approvalsByMap.Values.SingleOrDefault(
                    item=>item.Token==token);
                if(approved==null||
                   DateTimeOffset.UtcNow-approved.Created>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-ASA-RESTART-TOKEN-EXPIRED");
                if(!args.TryGetProperty("confirm",out var confirmation)||
                   confirmation.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-ASA-RESTART-APPROVAL");
                approvalsByMap.Remove(approved.MapId);
                var currentMap=Owned(approved.MapId);
                if(currentMap==null ||
                   currentMap.InstallationPath!=approved.Root ||
                   currentMap.Arguments!=approved.Args ||
                   currentMap.RconPort!=approved.RconPort)
                    return Fail(id,"UM-ASA-RESTART-PROFILE-CHANGED");
                if(HasActive(currentMap.Id))
                    return Fail(id,"UM-ASA-RESTART-BUSY");
                // Reject a changed API launch mode, INI or ASA/Loader binary
                // before the old server receives any SaveWorld/DoExit.
                if(!SameApprovedSource(currentMap,approved))
                    return Fail(id,"UM-ASA-RESTART-SOURCE-CHANGED");
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
                    if(!SameApprovedSource(currentMap,approved))
                        return Fail(id,"UM-ASA-RESTART-SOURCE-CHANGED");
                    // A failed Restart-v1 Begin() is a PRE-MUTATION
                    // refusal; never strand the generic operation journal
                    // if another durable Restart already blocks the action.
                    persisted=restartJournal.Begin(currentMap.Id,approved.Pid);
                    prepareLease.MarkMutationStarted();
                    prepareLease.Complete();
                }
                var job=new Job{
                    MapId=currentMap.Id,OldPid=approved.Pid,
                    NewProcessRole=currentMap.StartWithAsaApi?"loaderParent":"asaServer"
                };
                lock(sync)jobsByMap[currentMap.Id]=job;
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
                        job.PreExitWorldMetadataObserved=
                            receipt.Stop.PreExitWorldMetadataObserved;
                        int verifiedAsaPid=receipt.Started.Pid;
                        if(nativeRunner&&currentMap.StartWithAsaApi)
                        {
                            // A loader parent alone is NOT a successful ASA
                            // restart. Observe its exact child owning RCON.
                            Change(job,"running",
                                "ASA API Loader indult; gyermek PID és RCON ellenőrzése",
                                receipt.Started.Pid);
                            bool childVerified=false;
                            var limit=DateTimeOffset.UtcNow.AddMinutes(2);
                            while(DateTimeOffset.UtcNow<limit)
                            {
                                closingToken.ThrowIfCancellationRequested();
                                var family=cluster.Check(currentMap);
                                if(family.Running&&!family.ConfirmedStopped&&
                                   family.LoaderIdentity is {Pid:>0} loader&&
                                   loader.Pid==receipt.Started.Pid&&
                                   loader.StartedUtc==receipt.Started.StartedUtc&&
                                   family.Identity is {Pid:>0} child&&
                                   child.Pid!=approved.Pid&&child.Pid!=loader.Pid&&
                                   string.Equals(Path.GetFullPath(child.ExecutablePath),
                                       Path.GetFullPath(currentMap.ExecutablePath),
                                       StringComparison.OrdinalIgnoreCase)&&
                                   portOwner(currentMap.RconPort,child.Pid))
                                {
                                    verifiedAsaPid=child.Pid;
                                    childVerified=true;
                                    break;
                                }
                                await Task.Delay(850,closingToken);
                            }
                            if(!childVerified)
                                throw new InvalidOperationException(
                                    "UM-ASA-RESTART-API-CHILD-RCON-NOT-VERIFIED");
                            job.ApiChildAndRconVerified=true;
                        }
                        job.NewAsaServerPid=verifiedAsaPid;
                        restartJournal.Change(persisted.Id,AsaRestartPhase.Completed,
                            verifiedAsaPid);
                        Change(job,"completed",
                            job.NewProcessRole=="loaderParent"?
                                "Új ASA gyermek PID és RCON ellenőrizve; játékbeli készenlét még nem":
                                "Új ASA Windows-folyamat indult; játékbeli készenlét még nem",
                            receipt.Started.Pid);
                        if(nativeRunner&&currentMap.StartWithAsaApi)
                            FreshStoreApiPairObserver.Begin(
                                cluster,currentMap.Id,receipt.Started,closingToken);
                    }
                    catch(Exception ex)
                    {
                        try{restartJournal.Change(persisted.Id,AsaRestartPhase.NeedsOperator);}
                        catch{/* Keep the original pending journal; never clear it. */}
                        Change(job,"needsOperator",
                            "Restart interrupted ["+NativeLifecycleFailureCode.From(ex)+
                            "]: inspect local process and operation journal");
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
        catch(Exception ex)
        {
            // Do not leak paths, credentials or arbitrary exception strings.
            // Only native-owned stable validation codes reach the renderer.
            if(calledMethod is ("servers.restartReviewPreview" or
                "servers.restartReviewApply")&&
               Regex.IsMatch(ex.Message,@"\A(?:UM|UNTAMED)[-_][A-Z0-9_-]{3,110}\z",
                   RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
                return Fail(id,ex.Message);
            return Fail(id,"UM-ASA-RESTART-FAILED");
        }
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
