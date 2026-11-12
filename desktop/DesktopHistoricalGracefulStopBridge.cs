using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Real two-stage original ACTIVE Store-v1 graceful stop. Only the exact
/// locally owned historical WMI PID + TCP RCON owner can receive SaveWorld,
/// and DoExit is sent only after a NEW ASA save log + stable .ark witness.
/// Never wipes, migrates, restores, force-kills, retries or rewrites
/// original Store-v1 metadata, characters, tribes or transfer records.
/// </summary>
public sealed class DesktopHistoricalGracefulStopBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,int Pid,
        DateTimeOffset StartedUtc,string Profile,string SourceSha,
        int RconPort,DateTimeOffset Created,string Kind);
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public Guid MapId;
        public string Kind;
        public Job(Guid mapId,string kind){MapId=mapId;Kind=kind;}
        public string State="queued",Step="preflight";
        public bool ConfirmedStopped,PreExitSaveObserved,PreExitWorldMetadataObserved;
        public DateTimeOffset ChangedUtc=DateTimeOffset.UtcNow;
    }
    private readonly ClusterService cluster;
    private readonly Func<Guid,LegacyStoreV1GracefulStop.Owned> preview;
    private readonly Func<Guid,int,DateTimeOffset,string,Action<string>,Task<AsaGracefulStopReceipt>> stop;
    private readonly Func<Guid,int,DateTimeOffset,string,Action<string>,Task<LegacyStoreV1GracefulStop.SaveOnlyReceipt>> saveOnly;
    private readonly SemaphoreSlim serial=new(1,1);
    private readonly object sync=new();
    private Approval? pending;
    private sealed record ReviewApproval(Guid Token,Guid MapId,
        Guid ReceiptOperation,Guid? CoordinatorOperation,
        string SourceSha,DateTimeOffset Created);
    private ReviewApproval? pendingReview;
    private readonly Func<Guid,string,bool> verifyReviewSource;
    private Job? currentJob;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopHistoricalGracefulStopBridge(ClusterService service):
        this(service,null,null){}

    // Internal seams are restricted to isolated Windows-native tests.
    internal DesktopHistoricalGracefulStopBridge(ClusterService service,
        Func<Guid,LegacyStoreV1GracefulStop.Owned>? probe,
        Func<Guid,int,DateTimeOffset,string,Action<string>,Task<AsaGracefulStopReceipt>>? executor,
        Func<Guid,int,DateTimeOffset,string,Action<string>,Task<LegacyStoreV1GracefulStop.SaveOnlyReceipt>>? saver=null,
        Func<Guid,string,bool>? reviewVerifier=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        verifyReviewSource=reviewVerifier??VerifyReviewOriginal;
        preview=probe??(map=>LegacyStoreV1GracefulStop.Preview(cluster,map));
        stop=executor??((map,pid,started,source,stage)=>
            LegacyStoreV1GracefulStop.StopAsync(cluster,map,pid,started,
                source,TimeSpan.FromMinutes(3),stage:stage));
        saveOnly=saver??((map,pid,started,source,stage)=>
            LegacyStoreV1GracefulStop.SaveWorldOnlyAsync(cluster,map,pid,started,
                source,TimeSpan.FromMinutes(3),stage:stage));
    }
    private static bool GuidValue(JsonElement item,string name,out Guid id)
    {
        id=Guid.Empty;
        return item.TryGetProperty(name,out var value)&&
            value.ValueKind==JsonValueKind.String&&
            Guid.TryParse(value.GetString(),out id)&&id!=Guid.Empty;
    }
    private LocalMap? Owned(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id);
    private void Update(Job job,string state,string step,
        AsaGracefulStopReceipt? receipt=null)
    {
        lock(sync)
        {
            job.State=state;job.Step=step;job.ChangedUtc=DateTimeOffset.UtcNow;
            if(receipt!=null)
            {
                job.ConfirmedStopped=receipt.ConfirmedStopped;
                job.PreExitSaveObserved=receipt.PreExitSaveLogAndWorldObserved;
                job.PreExitWorldMetadataObserved=receipt.PreExitWorldMetadataObserved;
            }
        }
    }
    private void UpdateSave(Job job,string state,string step,
        LegacyStoreV1GracefulStop.SaveOnlyReceipt receipt)
    {
        lock(sync)
        {
            job.State=state;job.Step=step;job.ChangedUtc=DateTimeOffset.UtcNow;
            job.PreExitSaveObserved=receipt.SaveCompleteObserved;
            job.ConfirmedStopped=false;
        }
    }

    private object Snapshot(Job job)
    {
        lock(sync)return new{
            jobId=job.Id,kind=job.Kind,state=job.State,step=job.Step,
            changedUtc=job.ChangedUtc,
            confirmedStopped=job.ConfirmedStopped,
            preExitSaveLogAndWorldObserved=job.PreExitSaveObserved,
            preExitWorldMetadataObserved=job.PreExitWorldMetadataObserved,
            finalDoExitSaveCertified=false,
            gameClientRejoinVerified=false,
            automaticRetry=false,forceKill=false,storeTouched=false,
            evidence=job.Kind=="SaveWorldOnly"?
                "ORIGINAL_STORE_V1_SAVEWORLD_ONLY_NO_DOEXIT_STATE":
                "ORIGINAL_STORE_V1_REAL_SAVEWORLD_DOEXIT_STATE"
        };
    }

    private bool VerifyReviewOriginal(Guid reviewedMapId,string reviewedSourceSha)
    {
        var reviewedMap=Owned(reviewedMapId);
        if(reviewedMap==null||!reviewedMap.HistoricalStoreProtected||
           reviewedSourceSha.Length!=64||reviewedSourceSha.Any(c=>!Uri.IsHexDigit(c)))
            return false;
        var observedProcess=cluster.ObserveLocalProcessesReadOnly(reviewedMap);
        if(observedProcess.Running||!observedProcess.ConfirmedStopped||
           observedProcess.Identity!=null||observedProcess.LoaderIdentity!=null)
            return false;
        var pendingRestart=new AsaManualRestartJournal(
            reviewedMap.InstallationPath).Observe();
        if(pendingRestart!=null&&pendingRestart.Phase is not (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed))
            return false;
        lock(sync)
            if(currentJob!=null&&currentJob.MapId==reviewedMapId&&
               currentJob.State is "queued" or "running")return false;
        // Paired local Stop/Restart journal review needs the exact same
        // owned-map source fingerprint, NOT a fresh Kova peer DONE marker.
        // No SaveWorld/DoExit command is replayed in this read-only review.
        string source=LegacyStoreV1GracefulStop.OriginalSource(
            cluster,reviewedMap);
        return string.Equals(source,reviewedSourceSha,StringComparison.Ordinal);
    }

    private object Recover(Guid mapId)
    {
        var map=Owned(mapId);
        if(map==null||!map.HistoricalStoreProtected)
            throw new UnauthorizedAccessException("UM-ORIGINAL-STOP-RECOVERY-LOCAL-ONLY");
        // The journal is the durable authority across GUI/Manager crashes.
        // It is always READ ONLY here; no phase reset, retry, TTL unlock,
        // original Store/INI mutation or process launch is permitted.
        var journal=cluster.Operations.Observe(map.InstallationPath);
        // The manual original Restart has a separate durable journal.
        // It must also block SaveWorld and Stop UI during Stopping/Starting.
        var restart=new AsaManualRestartJournal(map.InstallationPath).Observe();
        bool restartPending=restart!=null&&restart.Phase is not (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed);
        Job? running;
        lock(sync)
            running=currentJob!=null&&currentJob.MapId==mapId&&
                currentJob.State is "queued" or "running"?currentJob:null;
        bool operationBlocked=journal!=null&&journal.Phase is
            UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or
            UntamedOperationPhase.NeedsOperator;
        var saveEvidence=LegacyStoreV1SaveEvidenceJournal.Inspect(
            cluster.Store.Root,mapId);
        // Never accept a successful *previous* operation journal as a
        // substitute for the pending SaveComplete receipt. This is a
        // second, Manager-local crash-recovery signal, not Kova ownership.
        bool pendingSaveEvidence=saveEvidence?.Phase is "prepared" or "needsOperator";
        bool blocked=operationBlocked||restartPending||pendingSaveEvidence;
        bool needsOperator=pendingSaveEvidence||(journal!=null&&(
            journal.Phase==UntamedOperationPhase.NeedsOperator||
            operationBlocked&&running==null))||
            restart?.Phase==AsaRestartPhase.NeedsOperator;
        return new{
            mapId,operationId=journal?.Operation,
            phase=journal?.Phase.ToString()??"None",
            operationKind=journal?.Kind.ToString()??"None",
            updatedUtc=journal?.UpdatedUtc,
            blocked,requiresOperator=needsOperator,
            inMemoryRunning=running!=null,
            jobId=running?.Id,
            // Distinguish the live task kind without exposing Store secrets.
            // Durable journal state alone cannot safely infer a resumed RCON action.
            activeJobKind=running?.Kind,
            saveEvidencePhase=saveEvidence?.Phase??"none",
            localSaveCompleteObserved=saveEvidence?.LocalSaveCompleteObserved==true,
            bothHostsSaveCompleteCertified=false,
            storeWriterCertified=false,
            productionRestoreAllowed=false,
            automaticRecovery=false,automaticRetry=false,
            sharedStoreMutated=false,lockCleared=false,
            evidence="ORIGINAL_STORE_V1_DURABLE_LOCAL_OPERATION_RECOVERY_READ_ONLY"
        };
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3072)
                return Fail(null,"UM-ORIGINAL-STOP-REQUEST");
            using var parsed=JsonDocument.Parse(raw);
            var request=parsed.RootElement;
            if(request.ValueKind!=JsonValueKind.Object||
               !GuidValue(request,"id",out var rid)||
               !request.TryGetProperty("method",out var action)||
               action.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-ORIGINAL-STOP-REQUEST");
            id=rid.ToString("D");
            string? method=action.GetString();
            if(method is not ("historical.gracefulStopPreview" or
                "historical.gracefulStopApply" or
                "historical.gracefulStopStatus" or
                "historical.gracefulStopRecovery" or
                "historical.saveWorldPreview" or
                "historical.saveWorldApply" or
                "historical.saveWorldStatus" or
                "historical.saveWorldEvidence" or
                "historical.saveWorldReviewPreview" or
                "historical.saveWorldReviewApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="historical.saveWorldReviewPreview")
                {
                    pendingReview=null;
                    if(!GuidValue(request,"mapId",out var reviewedMapId)||
                       Owned(reviewedMapId) is not {HistoricalStoreProtected:true} reviewedMap)
                        return Fail(id,"UM-ORIGINAL-SAVE-REVIEW-MAP");
                    var reviewedEvidence=LegacyStoreV1SaveEvidenceJournal.Inspect(
                        cluster.Store.Root,reviewedMapId);
                    var reviewedCoordinator=cluster.Operations.Observe(
                        reviewedMap.InstallationPath);
                    bool globalPending=reviewedCoordinator?.Phase is
                        UntamedOperationPhase.Prepared or
                        UntamedOperationPhase.Mutating or
                        UntamedOperationPhase.NeedsOperator;
                    if(reviewedEvidence==null||
                       reviewedEvidence.Phase is not ("prepared" or "needsOperator" or "reviewed")||
                       (reviewedEvidence.Phase=="reviewed"&&!globalPending))
                        return Fail(id,"UM-ORIGINAL-SAVE-REVIEW-NOT-NEEDED");
                    if(globalPending&&reviewedCoordinator?.Kind!=UntamedOperationKind.Backup)
                        return Fail(id,"UM-ORIGINAL-SAVE-REVIEW-OTHER-OPERATION");
                    if(!verifyReviewSource(reviewedMapId,reviewedEvidence.OriginalSourceSha256))
                        return Fail(id,"UM-ORIGINAL-SAVE-REVIEW-STOP-OR-SOURCE-UNVERIFIED");
                    var reviewToken=Guid.NewGuid();
                    pendingReview=new(reviewToken,reviewedMapId,reviewedEvidence.OperationId,
                        reviewedCoordinator?.Operation,reviewedEvidence.OriginalSourceSha256,
                        DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=reviewToken,mapId=reviewedMapId,phase=reviewedEvidence.Phase,
                        operationId=reviewedEvidence.OperationId,
                        coordinatorPhase=reviewedCoordinator?.Phase.ToString()??"None",
                        originalSourceRevalidated=true,confirmedStopped=true,
                        preservesOriginalReceipt=true,originalStoreFilesWritten=false,
                        automaticRetry=false,requiresTypedApproval=true,
                        evidence="OPERATOR_REVIEW_STABLE_STOPPED_ORIGINAL_STORE_SAVE"
                    });
                }
                if(method=="historical.saveWorldReviewApply")
                {
                    if(!GuidValue(request,"token",out var reviewedToken)||
                       !request.TryGetProperty("confirm",out var reviewConsent)||
                       reviewConsent.ValueKind!=JsonValueKind.True||
                       !request.TryGetProperty("confirmation",out var reviewedPhrase)||
                       reviewedPhrase.ValueKind!=JsonValueKind.String||
                       reviewedPhrase.GetString()!="REVIEW STORE SAVE"||
                       pendingReview?.Token!=reviewedToken)
                        return Fail(id,"UM-ORIGINAL-SAVE-REVIEW-APPROVAL-REQUIRED");
                    var approvedReview=pendingReview!;
                    pendingReview=null;
                    if(DateTimeOffset.UtcNow-approvedReview.Created>TimeSpan.FromMinutes(5))
                        return Fail(id,"UM-ORIGINAL-SAVE-REVIEW-EXPIRED");
                    var currentReviewMap=Owned(approvedReview.MapId);
                    if(currentReviewMap is not {HistoricalStoreProtected:true})
                        return Fail(id,"UM-ORIGINAL-SAVE-REVIEW-MAP-CHANGED");
                    var currentReviewReceipt=LegacyStoreV1SaveEvidenceJournal.Inspect(
                        cluster.Store.Root,approvedReview.MapId);
                    if(currentReviewReceipt?.OperationId!=approvedReview.ReceiptOperation||
                       currentReviewReceipt.OriginalSourceSha256!=approvedReview.SourceSha)
                        return Fail(id,"UM-ORIGINAL-SAVE-REVIEW-RECEIPT-CHANGED");
                    cluster.Operations.ReviewStoppedBackup(
                        currentReviewMap.InstallationPath,approvedReview.CoordinatorOperation,
                        ()=>verifyReviewSource(approvedReview.MapId,
                            approvedReview.SourceSha),
                        ()=>LegacyStoreV1SaveEvidenceJournal.ReviewUncertain(
                            cluster.Store.Root,approvedReview.MapId,
                            approvedReview.ReceiptOperation,approvedReview.SourceSha,
                            ()=>verifyReviewSource(approvedReview.MapId,
                                approvedReview.SourceSha)));
                    var updatedReviewReceipt=LegacyStoreV1SaveEvidenceJournal.Inspect(
                        cluster.Store.Root,approvedReview.MapId);
                    return Ok(id,new{
                        mapId=approvedReview.MapId,
                        phase=updatedReviewReceipt?.Phase??"none",reviewed=true,
                        oldReceiptArchived=true,originalStoreFilesWritten=false,
                        autoRetry=false,serverStarted=false,
                        nextOperationRequiresNewApproval=true,
                        evidence="OPERATOR_REVIEW_DURABLE_LOCAL_JOURNALS_NO_STORE_MUTATION"
                    });
                }
                if(method=="historical.saveWorldEvidence")
                {
                    if(!GuidValue(request,"mapId",out var evidenceMapId)||
                       Owned(evidenceMapId) is not {HistoricalStoreProtected:true})
                        return Fail(id,"UM-ORIGINAL-SAVE-EVIDENCE-LOCAL-ONLY");
                    var evidence=LegacyStoreV1SaveEvidenceJournal.Inspect(
                        cluster.Store.Root,evidenceMapId);
                    return Ok(id,new{
                        mapId=evidenceMapId,
                        phase=evidence?.Phase??"none",
                        operationId=evidence?.OperationId,
                        pid=evidence?.Pid,
                        pidStartedUtc=evidence?.PidStartedUtc,
                        saveObservedUtc=evidence?.SaveObservedUtc,
                        localSaveCompleteObserved=evidence?.LocalSaveCompleteObserved==true,
                        bothHostsSaveCompleteCertified=false,
                        sharedStoreWriterCertified=false,
                        gameClientRejoinVerified=false,
                        productionRestoreAllowed=false,
                        fsyncCertified=false,
                        sourceStateCertifiedForCurrentWriter=false,
                        evidence="MANAGER_LOCAL_DURABLE_SAVE_LOG_WITNESS_NO_SHARED_STORE_QUORUM"
                    });
                }
                if(method=="historical.gracefulStopRecovery")
                {
                    if(!GuidValue(request,"mapId",out var recoveryMapId))
                        return Fail(id,"UM-ORIGINAL-STOP-RECOVERY-MAP");
                    return Ok(id,Recover(recoveryMapId));
                }
                if(method is "historical.gracefulStopStatus" or "historical.saveWorldStatus")
                {
                    if(!GuidValue(request,"jobId",out var jobId)||
                       currentJob==null||currentJob.Id!=jobId||
                       (method=="historical.saveWorldStatus")!=(currentJob.Kind=="SaveWorldOnly"))
                        return Fail(id,"UM-ORIGINAL-STOP-JOB-NOT-FOUND");
                    return Ok(id,Snapshot(currentJob));
                }
                if(currentJob!=null)
                {
                    lock(sync)
                        if(currentJob.State is "queued" or "running")
                            return Fail(id,"UM-ORIGINAL-STOP-JOB-BUSY");
                }
                if(method is "historical.gracefulStopPreview" or "historical.saveWorldPreview")
                {
                    // A half-written or unfinished local receipt must block
                    // all subsequent mutating Store operations. Inspect() 
                    // itself fails closed on tamper or interrupted .next.
                    var saved=LegacyStoreV1SaveEvidenceJournal.Inspect(
                        cluster.Store.Root,
                        GuidValue(request,"mapId",out var checkedMapId)?
                            checkedMapId:Guid.Empty);
                    if(saved?.Phase is "prepared" or "needsOperator")
                        return Fail(id,"UM-ORIGINAL-SAVE-EVIDENCE-NEEDS-REVIEW");
                    bool saveWorldOnly=method=="historical.saveWorldPreview";
                    pending=null;
                    if(!GuidValue(request,"mapId",out var mapId))
                        return Fail(id,"UM-ORIGINAL-STOP-MAP");
                    var original=Owned(mapId);
                    if(original==null||!original.HistoricalStoreProtected||
                       original.Maintenance)
                        return Fail(id,"UM-ORIGINAL-STOP-LOCAL-ONLY");
                    // A durable installation journal blocks even a new
                    // approval PREVIEW after a previous crash. Its read-only
                    // recovery screen must appear before any new RCON action.
                    if(cluster.Operations.Observe(original.InstallationPath)?.Phase is
                        UntamedOperationPhase.Prepared or
                        UntamedOperationPhase.Mutating or
                        UntamedOperationPhase.NeedsOperator)
                        return Fail(id,"UM-ORIGINAL-STOP-DURABLE-RECOVERY-REQUIRED");
                    var owned=await Task.Run(()=>preview(mapId));
                    if(!ReferenceEquals(owned.Map,original)||
                       owned.Process.Pid<=0||
                       owned.Process.StartedUtc==default||
                       owned.SourceSha.Length!=64)
                        return Fail(id,"UM-ORIGINAL-STOP-SOURCE-INVALID");
                    pending=new Approval(Guid.NewGuid(),mapId,owned.Process.Pid,
                        owned.Process.StartedUtc,JsonSerializer.Serialize(original),
                        owned.SourceSha,original.RconPort,DateTimeOffset.UtcNow,
                        saveWorldOnly?"SaveWorldOnly":"Stop");
                    return Ok(id,new{
                        token=pending.Token,mapId,mapName=original.Name,
                        pid=pending.Pid,rconPort=pending.RconPort,
                        originalTwoPeersVerified=true,usesSaveWorld=true,
                        usesDoExit=!saveWorldOnly,forceKillAllowed=false,
                        automaticRestart=false,originalStoreTouched=false,
                        evidence=saveWorldOnly?
                            "ORIGINAL_STORE_V1_SAVEWORLD_ONLY_PREFLIGHT":
                            "ORIGINAL_STORE_V1_SAVEWORLD_DOEXIT_PREFLIGHT_ONLY"
                    });
                }
                if(!GuidValue(request,"token",out var token)||pending==null||
                   pending.Token!=token||
                   DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-ORIGINAL-STOP-TOKEN-EXPIRED");
                bool wantsSaveOnly=method=="historical.saveWorldApply";
                if(wantsSaveOnly!=(pending.Kind=="SaveWorldOnly"))
                    return Fail(id,"UM-ORIGINAL-STOP-OPERATION-MISMATCH");
                if(!request.TryGetProperty("confirm",out var confirmed)||
                   confirmed.ValueKind!=JsonValueKind.True||
                   !request.TryGetProperty("typedApproval",out var text)||
                   text.ValueKind!=JsonValueKind.String||
                   text.GetString()!=(wantsSaveOnly?
                       "STORE-V1 SAVEWORLD":"STORE-V1 SAVEWORLD DOEXIT"))
                    return Fail(id,"UM-ORIGINAL-STOP-TYPED-CONFIRMATION");
                Approval approved=pending;pending=null;
                var map=Owned(approved.MapId);
                if(map==null||!map.HistoricalStoreProtected||
                   JsonSerializer.Serialize(map)!=approved.Profile||
                   map.RconPort!=approved.RconPort)
                    return Fail(id,"UM-ORIGINAL-STOP-MAP-CHANGED");
                var rechecked=await Task.Run(()=>preview(map.Id));
                if(!ReferenceEquals(rechecked.Map,map)||
                   rechecked.Process.Pid!=approved.Pid||
                   rechecked.Process.StartedUtc!=approved.StartedUtc||
                   rechecked.SourceSha!=approved.SourceSha)
                    return Fail(id,"UM-ORIGINAL-STOP-ORIGINAL-SOURCE-CHANGED");
                var job=new Job(map.Id,approved.Kind);
                if(approved.Kind=="SaveWorldOnly")
                {
                    // Journal BEFORE the native task can send RCON. A crash
                    // leaves a durable prepared record, never false success.
                    LegacyStoreV1SaveEvidenceJournal.Begin(cluster.Store.Root,
                        job.Id,map.Id,approved.Pid,approved.StartedUtc,
                        approved.SourceSha);
                }
                currentJob=job;
                _=Task.Run(async()=>{
                    Update(job,"running",approved.Kind=="SaveWorldOnly"?
                        "Eredeti Store-v1 SaveWorld és mentésigazolás":
                        "Eredeti SaveWorld → log+világfájl → DoExit");
                    try
                    {
                        if(approved.Kind=="SaveWorldOnly")
                        {
                            var receipt=await saveOnly(map.Id,approved.Pid,
                                approved.StartedUtc,approved.SourceSha,
                                stage=>Update(job,"running",stage));
                            if(receipt.MapId!=map.Id||receipt.Pid!=approved.Pid||
                               !receipt.SaveCompleteObserved||receipt.DoExitSent)
                                throw new InvalidDataException("UM-ORIGINAL-SAVE-RECEIPT-MISMATCH");
                            // Persist the same owned-PID SaveComplete witness
                            // before reporting success to React.
                            LegacyStoreV1SaveEvidenceJournal.Complete(
                                cluster.Store.Root,job.Id,map.Id,approved.Pid,
                                approved.StartedUtc,approved.SourceSha,
                                receipt.ObservedUtc,receipt.SaveCompleteObserved,
                                receipt.DoExitSent);
                            UpdateSave(job,"completed","Store-v1 SaveWorld igazolva; az ASA tovább fut",receipt);
                        }
                        else
                        {
                            var receipt=await stop(map.Id,approved.Pid,
                                approved.StartedUtc,approved.SourceSha,
                                stage=>Update(job,"running",stage));
                            if(receipt.MapId!=map.Id||receipt.Pid!=approved.Pid||
                               !receipt.ConfirmedStopped||!receipt.UsedDoExit)
                                throw new InvalidDataException("UM-ORIGINAL-STOP-RECEIPT-MISMATCH");
                            Update(job,"completed","A régi ASA-folyamat kilépett",receipt);
                        }
                    }
                    catch(Exception ex)
                    {
                        if(approved.Kind=="SaveWorldOnly")
                        {
                            try
                            {
                                LegacyStoreV1SaveEvidenceJournal.MarkUncertain(
                                    cluster.Store.Root,job.Id,map.Id,approved.Pid,
                                    approved.StartedUtc,approved.SourceSha);
                            }
                            catch
                            {
                                // Prepared/broken journal remains fail-closed.
                            }
                        }
                        Update(job,"needsOperator",
                            "Store-v1 RCON művelet megszakadt ["+
                            NativeLifecycleFailureCode.From(ex)+
                            "]; PID és napló kézi ellenőrzése szükséges");
                    }
                });
                return Ok(id,new{
                    jobId=job.Id,started=true,
                    automaticRetry=false,forceKill=false,storeTouched=false,
                    evidence=approved.Kind=="SaveWorldOnly"?
                        "ORIGINAL_STORE_V1_REAL_SAVEWORLD_ONLY_JOB_QUEUED":
                        "ORIGINAL_STORE_V1_REAL_STOP_JOB_QUEUED"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-ORIGINAL-STOP-SAFETY-CHECK-FAILED");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>serial.Dispose();
}
