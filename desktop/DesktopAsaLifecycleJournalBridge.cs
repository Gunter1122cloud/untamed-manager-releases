using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Read-only inspection plus explicitly approved, audited Store-v1 Launch
/// journal review. Never deletes a journal or sends RCON/DoExit/Start.
/// Unknown outcomes remain unknown; the review only authorizes a NEW,
/// independently approved native operation after fresh owner verification.
/// </summary>
public sealed class DesktopAsaLifecycleJournalBridge
{
    private sealed record Pending(Guid Token,Guid MapId,Guid Operation,
        int Pid,DateTimeOffset StartedUtc,string SourceSha,string ProfileJson,
        Guid? RestartId,string? RestartSnapshot,DateTimeOffset ExpiresUtc);
    private readonly ClusterService cluster;
    private readonly Func<Guid,LegacyStoreV1GracefulStop.Owned> ownerProof;
    private readonly SemaphoreSlim serial=new(1,1);
    private Pending? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly Regex SafeCode=new(
        @"\A(?:UM-[A-Z0-9_-]{3,110}|UNTAMED_[A-Z0-9_]{3,110})\z",
        RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));

    public DesktopAsaLifecycleJournalBridge(ClusterService local):
        this(local,null) { }
    // Synthetic owner proof is available only to isolated native tests.
    internal DesktopAsaLifecycleJournalBridge(ClusterService local,
        Func<Guid,LegacyStoreV1GracefulStop.Owned>? proof)
    {
        cluster=local??throw new ArgumentNullException(nameof(local));
        ownerProof=proof??(mapId=>
            LegacyStoreV1GracefulStop.PreviewForUnfinishedRestartReview(cluster,mapId));
    }

    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    private static string ErrorCode(Exception ex)=>
        SafeCode.IsMatch(ex.Message)?ex.Message:
        ex is IOException?"UM-RECOVERY-LOCAL-JOURNAL-IO":
        ex is UnauthorizedAccessException?"UM-RECOVERY-AUTHORIZATION-FAILED":
        ex is InvalidDataException?"UM-RECOVERY-SOURCE-OR-JOURNAL-INVALID":
        "UM-RECOVERY-VALIDATION-FAILED";
    private LocalMap Owned(Guid mapId)=>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId)??
        throw new UnauthorizedAccessException("UM-MAP-NOT-OWNED");
    private static string Classification(UntamedOperationRecord? record)=>record?.Phase switch{
        null=>"none",
        UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating=>"inProgressOrInterrupted",
        UntamedOperationPhase.NeedsOperator=>"unknownOutcome",
        UntamedOperationPhase.Completed or UntamedOperationPhase.CancelledBeforeMutation or
            UntamedOperationPhase.OperatorReviewed=>"closed",
        _=>"unverified"
    };
    private static bool PendingPhase(UntamedOperationRecord? record)=>record?.Phase is
        UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or
        UntamedOperationPhase.NeedsOperator;
    private LegacyStoreV1GracefulStop.Owned Proof(Guid mapId)
    {
        var proof=ownerProof(mapId);
        if(proof.Map.Id!=mapId||!proof.Map.HistoricalStoreProtected||
           proof.Process.Pid<=0||proof.Process.StartedUtc.Year<2020||
           proof.SourceSha.Length!=64||!proof.SourceSha.All(Uri.IsHexDigit)||
           proof.Map.RconPort is <1024 or >65535)
            throw new InvalidDataException("UM-RECOVERY-OWNER-UNVERIFIED");
        return proof;
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >1500)return Error(null,"UM-LIFECYCLE-JOURNAL-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||
               !root.TryGetProperty("id",out var rawId)||
               rawId.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(rawId.GetString(),out var requestId)||
               requestId==Guid.Empty||
               !root.TryGetProperty("method",out var methodProp)||
               methodProp.ValueKind!=JsonValueKind.String||
               methodProp.GetString() is not
                   ("servers.lifecycleJournal" or
                    "servers.lifecycleRecoveryPreview" or
                    "servers.lifecycleRecoveryApply")||
               !root.TryGetProperty("mapId",out var mapProp)||
               mapProp.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(mapProp.GetString(),out var mapId)||
               mapId==Guid.Empty)
                return Error(null,"UM-LIFECYCLE-JOURNAL-REQUEST");
            id=requestId.ToString("D");
            string method=methodProp.GetString()!;
            await serial.WaitAsync();
            try
            {
                var map=Owned(mapId);
                var old=cluster.Operations.Observe(map.InstallationPath);
                var restart=new AsaManualRestartJournal(map.InstallationPath).Observe();
                bool restartPending=restart?.Phase is (AsaRestartPhase.Stopping or
                    AsaRestartPhase.Starting or AsaRestartPhase.NeedsOperator);
                bool isOriginalPending=map.HistoricalStoreProtected&&
                    old?.Kind==UntamedOperationKind.Launch&&PendingPhase(old);
                bool restartReviewable=restart==null||restart.Phase is
                    (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed or
                     AsaRestartPhase.Stopping or AsaRestartPhase.NeedsOperator);
                if(method=="servers.lifecycleJournal")
                    return Ok(id,new{
                        mapId=map.Id,mapName=map.Name,
                        journalExists=old!=null,
                        operationId=old?.Operation,
                        phase=old?.Phase.ToString(),kind=old?.Kind.ToString(),
                        changedUtc=old?.UpdatedUtc,
                        classification=Classification(old),
                        blockingCode=restartPending?
                            (isOriginalPending&&restartReviewable?
                                "UM-RECOVERY-PAIRED-LAUNCH-RESTART-REVIEW-AVAILABLE":
                                "UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR"):
                            PendingPhase(old)?
                                (isOriginalPending?
                                    "UM-RECOVERY-ORIGINAL-LAUNCH-REVIEW-AVAILABLE":
                                    "UM-RECOVERY-OTHER-OPERATION-REQUIRES-REVIEW"):null,
                        restartJournalExists=restart!=null,
                        restartId=restart?.Id,restartPhase=restart?.Phase.ToString(),
                        restartChangedUtc=restart?.ChangedUtc,
                        restartOldPid=restart?.OldPid,restartNewPid=restart?.NewPid,
                        restartClassification=restartPending?"unknownOutcome":
                            restart?.Phase==AsaRestartPhase.OperatorReviewed?"operatorReviewed":"closed",
                        recoverySupported=isOriginalPending&&restartReviewable,
                        needsOperatorReview=PendingPhase(old)||restartPending,
                        autoResumeAllowed=false,storeTouched=false,
                        evidence="DURABLE_INSTALLATION_OPERATION_JOURNAL_READ_ONLY"
                    });
                if(!map.HistoricalStoreProtected)
                    return Error(id,"UM-RECOVERY-NOT-ORIGINAL-STORE");
                if(method=="servers.lifecycleRecoveryPreview")
                {
                    pending=null;
                    if(old==null||old.Kind!=UntamedOperationKind.Launch||
                       !PendingPhase(old))
                        return Error(id,"UM-RECOVERY-LAUNCH-PENDING-ONLY");
                    if(DateTimeOffset.UtcNow-old.UpdatedUtc<TimeSpan.FromMinutes(3))
                        return Error(id,"UM-RECOVERY-OPERATION-TOO-RECENT");
                    if(!restartReviewable)
                        return Error(id,"UM-RECOVERY-RESTART-PHASE-NOT-REVIEWABLE");
                    var witness=await Task.Run(()=>Proof(mapId));
                    if(restart!=null&&restart.Phase is not AsaRestartPhase.Completed &&
                       (restart.MapId!=mapId||restart.OldPid!=witness.Process.Pid||
                        restart.NewPid!=0))
                        return Error(id,"UM-RECOVERY-RESTART-OLD-PID-MISMATCH");
                    var token=Guid.NewGuid();
                    pending=new(token,mapId,old.Operation,
                        witness.Process.Pid,witness.Process.StartedUtc,
                        witness.SourceSha,JsonSerializer.Serialize(witness.Map),
                        restart?.Phase==AsaRestartPhase.Completed?null:restart?.Id,
                        restart?.Phase==AsaRestartPhase.Completed?null:
                            JsonSerializer.Serialize(restart),
                        DateTimeOffset.UtcNow.AddMinutes(3));
                    return Ok(id,new{
                        token,mapId,mapName=map.Name,
                        operationId=old.Operation,kind=old.Kind.ToString(),
                        phase=old.Phase.ToString(),pid=witness.Process.Pid,
                        restartId=pending.RestartId,
                        restartPhase=restart?.Phase.ToString(),
                        restartOldPid=restart?.OldPid,
                        restartNewPid=restart?.NewPid,
                        pairedJournals=pending.RestartId!=null?2:1,
                        processStartedUtc=witness.Process.StartedUtc,
                        rconPort=map.RconPort,
                        expiresUtc=pending.ExpiresUtc,
                        exactWindowsProcessVerified=true,originalStoreSessionVerified=true,
                        rconListenerOwnerVerified=true,priorCommandOutcomeUnknown=true,
                        previousOperationWillNotBeRetried=true,requiresTypedApproval=true,
                        auditRequired=true,storeBytesModified=false,
                        evidence="ORIGINAL_STORE_RUNNING_LAUNCH_JOURNAL_RECOVERY_PREVIEW"
                    });
                }
                if(pending==null||pending.MapId!=mapId||
                   !root.TryGetProperty("token",out var approval)||
                   approval.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(approval.GetString(),out var tokenId)||
                   tokenId!=pending.Token)
                    return Error(id,"UM-RECOVERY-APPROVAL-EXPIRED");
                var pinned=pending;
                pending=null;
                if(pinned.ExpiresUtc<DateTimeOffset.UtcNow||
                   !root.TryGetProperty("confirm",out var confirm)||
                   confirm.ValueKind!=JsonValueKind.True||
                   !root.TryGetProperty("ackUnknownOutcome",out var unknown)||
                   unknown.ValueKind!=JsonValueKind.True||
                   !root.TryGetProperty("typedApproval",out var typed)||
                   typed.ValueKind!=JsonValueKind.String||
                   typed.GetString()!="STORE-V1 NAPLÓ HELYREÁLLÍTÁS")
                    return Error(id,"UM-RECOVERY-EXPLICIT-APPROVAL-REQUIRED");
                // Pin both original journal IDs and the precise preflight
                // Restart-v1 record. No competing UI or Manager may silently
                // replace either during the three-minute approval window.
                var currentRestart=new AsaManualRestartJournal(map.InstallationPath).Observe();
                if((pinned.RestartSnapshot??"")!=
                   (currentRestart==null||currentRestart.Phase==AsaRestartPhase.Completed?
                       "":JsonSerializer.Serialize(currentRestart)))
                    return Error(id,"UM-RECOVERY-RESTART-JOURNAL-CHANGED");
                bool CheckPinned()
                {
                    var now=Proof(mapId);
                    return ReferenceEquals(now.Map,map)&&
                        now.Process.Pid==pinned.Pid&&
                        now.Process.StartedUtc==pinned.StartedUtc&&
                        now.SourceSha==pinned.SourceSha&&
                        JsonSerializer.Serialize(now.Map)==pinned.ProfileJson;
                }
                cluster.Operations.ReviewRunningOriginalLaunch(
                    map.InstallationPath,pinned.Operation,CheckPinned,
                    mapId,pinned.RestartId,pinned.Pid);
                return Ok(id,new{
                    mapId,operationId=pinned.Operation,
                    state="operatorReviewed",previousOutcomeStillUnknown=true,
                    originalJournalAudited=true,
                    restartJournalAudited=pinned.RestartId!=null,
                    pairedJournalsReviewed=pinned.RestartId!=null?2:1,
                    restartMarkedSuccessful=false,oldRunningPid=pinned.Pid,
                    storeBytesModified=false,
                    processStarted=false,processStopped=false,rconCommandSent=false,
                    noAutomaticRetry=true,newOperationRequiresFreshApproval=true,
                    evidence="ORIGINAL_STORE_EXACT_RUNNING_OWNER_AUDITED_MANUAL_JOURNAL_REVIEW"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception ex) when(ex is System.IO.IOException or
            InvalidDataException or JsonException or ArgumentException or
            UnauthorizedAccessException or InvalidOperationException)
        {return Error(id,ErrorCode(ex));}
    }
}
