using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
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
        Guid? RestartId,string? RestartSnapshot,bool RestartOnly,DateTimeOffset ExpiresUtc,
        bool StoppedReview=false);
    internal sealed record StoppedProof(LocalMap Map,int OldPid,string SourceSha);
    private readonly ClusterService cluster;
    private readonly Func<Guid,LegacyStoreV1GracefulStop.Owned> ownerProof;
    private readonly Func<Guid,int,StoppedProof> stoppedProof;
    private readonly bool stoppedReviewEnabled;
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
        Func<Guid,LegacyStoreV1GracefulStop.Owned>? proof,
        Func<Guid,int,StoppedProof>? stopped=null)
    {
        cluster=local??throw new ArgumentNullException(nameof(local));
        ownerProof=proof??(mapId=>
            LegacyStoreV1GracefulStop.PreviewForUnfinishedRestartReview(cluster,mapId));
        stoppedProof=stopped??ProductionStoppedProof;
        // Injected running-owner tests must not reach the production
        // Windows/legacy-profile inspection accidentally.
        stoppedReviewEnabled=proof==null||stopped!=null;
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
    private StoppedProof ProductionStoppedProof(Guid mapId,int oldPid)
    {
        var map=Owned(mapId);
        if(!map.HistoricalStoreProtected||map.Id==Guid.Empty||oldPid<=0||
           map.AutoStart||map.MonitorRcon||map.RconPort is <1024 or >65535||
           map.HistoricalArchiveSha256?.Length!=64||
           !map.HistoricalArchiveSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("UM-RECOVERY-STOPPED-MAP-NOT-APPROVED");
        var state=LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(
            map,cluster.Config.LocalMaps);
        if(state.Running||!state.ConfirmedStopped||state.Identity!=null||
           state.LoaderIdentity!=null)
            throw new InvalidOperationException(
                "UM-RECOVERY-STOPPED-PROCESS-NOT-VERIFIED");
        // A stopped map must not inherit a live RCON owner. Absence is
        // checked again under the installation-wide operation semaphore.
        try
        {
            if(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(p=>p.Port==map.RconPort))
                throw new InvalidOperationException(
                    "UM-RECOVERY-STOPPED-RCON-STILL-LISTENING");
        }
        catch(NetworkInformationException)
        {
            throw new IOException("UM-RECOVERY-STOPPED-RCON-OWNERSHIP-UNKNOWN");
        }
        string legacy=Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData),"Kova");
        string profile=LegacyStoreV1PinnedSource.PreferredProfile(
            cluster.Store.Root,legacy);
        // Verify CURRENT five selected historical owners even with a sixth
        // local event map. The original session, map roots, lock and source
        // hashes are still checked by the native source readers.
        var current=LegacyStoreV1OriginalImport.Inspect(legacy);
        var local=LegacyStoreV1LocalReader.Inspect(legacy);
        if(!local.LocalSnapshotReady||current.Maps.Length!=5||
           !local.Maps.Any(m=>m.MapId==mapId&&m.Selected&&
               m.Active&&m.SessionMatches&&m.RootMatches&&
               m.BackupIdentityPresent))
            throw new InvalidDataException(
                "UM-RECOVERY-STOPPED-HISTORICAL-SELECTION-INVALID");
        var source=LegacyStoreV1OriginalImport.Inspect(profile);
        var original=source.Maps.SingleOrDefault(m=>m.MapId==mapId);
        var currentMap=current.Maps.SingleOrDefault(m=>m.MapId==mapId);
        if(original==null||currentMap==null||
           original.MapKey!=map.HistoricalMapKey||
           currentMap.MapKey!=map.HistoricalMapKey||
           !Path.GetFullPath(original.OriginalInstallationRoot).Equals(
               Path.GetFullPath(map.InstallationPath),StringComparison.OrdinalIgnoreCase)||
           !Path.GetFullPath(currentMap.OriginalInstallationRoot).Equals(
               Path.GetFullPath(map.InstallationPath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "UM-RECOVERY-STOPPED-MAP-IDENTITY-CHANGED");
        bool pinned=!string.Equals(profile,legacy,StringComparison.OrdinalIgnoreCase);
        if(pinned)LegacyStoreV1PinnedSource.RequireImmutableIdentity(map,source);
        else if(map.HistoricalArchiveSha256!=source.FourFilesSha256&&
            local.Evidence!=
                "ORIGINAL_FIVE_SELECTED_PINNED_IN_EXPANDED_LOCAL_ROSTER_NO_PEER_CERTIFICATION")
            throw new InvalidDataException(
                "UM-RECOVERY-STOPPED-HISTORICAL-SOURCE-CHANGED");
        if(!AsaRconConfigurationEvidence.Matches(map))
            throw new InvalidDataException(
                "UM-RECOVERY-STOPPED-RCON-CONFIGURATION-CHANGED");
        using var held=new VaultReadSnapshot();
        string IniTag(string file,int limit)=>File.Exists(file)?
            Convert.ToHexString(SHA256.HashData(held.Read(file,limit))):"ABSENT";
        string gus=IniTag(AsaInitialRconSettings.NewIniPath(map.InstallationPath),131072);
        string game=IniTag(Path.Combine(map.InstallationPath,"ShooterGame",
            "Saved","Config","WindowsServer","Game.ini"),4_000_000);
        string evidence=string.Join("\n",source.FourFilesSha256,
            source.LaunchProfileSha256,current.FourFilesSha256,
            current.LaunchProfileSha256,local.SessionTag,
            map.HistoricalArchiveSha256,JsonSerializer.Serialize(map),
            gus,game,oldPid.ToString());
        return new(map,oldPid,Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(evidence))));
    }

    private StoppedProof VerifyStopped(Guid mapId,int oldPid)
    {
        var witness=stoppedProof(mapId,oldPid);
        if(witness.Map.Id!=mapId||!ReferenceEquals(witness.Map,Owned(mapId))||
           !witness.Map.HistoricalStoreProtected||witness.OldPid!=oldPid||
           witness.SourceSha.Length!=64||!witness.SourceSha.All(Uri.IsHexDigit))
            throw new InvalidDataException("UM-RECOVERY-STOPPED-OWNER-UNVERIFIED");
        return witness;
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
                bool restartOnlyReviewable=map.HistoricalStoreProtected&&
                    !PendingPhase(old)&&restart?.Phase is
                        (AsaRestartPhase.Stopping or AsaRestartPhase.NeedsOperator)&&
                    restart.NewPid==0;
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
                                restartOnlyReviewable?
                                    "UM-RECOVERY-RESTART-ONLY-REVIEW-AVAILABLE":
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
                        recoverySupported=isOriginalPending&&restartReviewable||
                            restartOnlyReviewable,
                        needsOperatorReview=PendingPhase(old)||restartPending,
                        autoResumeAllowed=false,storeTouched=false,
                        evidence="DURABLE_INSTALLATION_OPERATION_JOURNAL_READ_ONLY"
                    });
                if(!map.HistoricalStoreProtected)
                    return Error(id,"UM-RECOVERY-NOT-ORIGINAL-STORE");
                if(method=="servers.lifecycleRecoveryPreview")
                {
                    pending=null;
                    if(!isOriginalPending&&!restartOnlyReviewable)
                        return Error(id,"UM-RECOVERY-LAUNCH-OR-RESTART-PENDING-ONLY");
                    if(isOriginalPending&&old!=null&&
                       DateTimeOffset.UtcNow-old.UpdatedUtc<TimeSpan.FromMinutes(3)||
                       restartOnlyReviewable&&restart!=null&&
                       DateTimeOffset.UtcNow-restart.ChangedUtc<TimeSpan.FromMinutes(3))
                        return Error(id,"UM-RECOVERY-OPERATION-TOO-RECENT");
                    if(!restartReviewable)
                        return Error(id,"UM-RECOVERY-RESTART-PHASE-NOT-REVIEWABLE");
                    LegacyStoreV1GracefulStop.Owned? running=null;
                    StoppedProof? stopped=null;
                    if(stoppedReviewEnabled&&isOriginalPending&&
                       restart?.OldPid>0&&restart.NewPid==0&&
                       restart.Phase is (AsaRestartPhase.Stopping or
                           AsaRestartPhase.NeedsOperator or AsaRestartPhase.OperatorReviewed))
                    {
                        // A failed running proof is NOT a stopped proof.
                        // The independent stopped witness must establish
                        // Windows process-family absence and absent RCON.
                        try{running=await Task.Run(()=>Proof(mapId));}
                        catch(Exception ex) when(ex is InvalidDataException or
                            InvalidOperationException or UnauthorizedAccessException or IOException)
                        {stopped=await Task.Run(()=>VerifyStopped(mapId,restart.OldPid));}
                    }
                    else running=await Task.Run(()=>Proof(mapId));
                    int oldPid=stopped?.OldPid??running!.Process.Pid;
                    DateTimeOffset oldStart=running?.Process.StartedUtc??default;
                    string sourceSha=stopped?.SourceSha??running!.SourceSha;
                    bool sourcePinned=LegacyStoreV1PinnedSource.HasApprovedLocalSource(
                        cluster.Store.Root,map);
                    if(restart!=null&&restart.Phase is not AsaRestartPhase.Completed &&
                       (restart.MapId!=mapId||restart.OldPid!=oldPid||
                        restart.NewPid!=0))
                        return Error(id,"UM-RECOVERY-RESTART-OLD-PID-MISMATCH");
                    var token=Guid.NewGuid();
                    pending=new(token,mapId,old?.Operation??Guid.Empty,
                        oldPid,oldStart,sourceSha,JsonSerializer.Serialize(map),
                        restart?.Phase==AsaRestartPhase.Completed?null:restart?.Id,
                        restart?.Phase==AsaRestartPhase.Completed?null:
                            JsonSerializer.Serialize(restart),
                        restartOnlyReviewable,DateTimeOffset.UtcNow.AddMinutes(3),
                        StoppedReview:stopped!=null);
                    return Ok(id,new{
                        token,mapId,mapName=map.Name,
                        operationId=old?.Operation??Guid.Empty,
                        kind=restartOnlyReviewable?"Restart":old?.Kind.ToString()??"Launch",
                        phase=restartOnlyReviewable?restart?.Phase.ToString():
                            old?.Phase.ToString(),
                        pid=oldPid,
                        restartId=pending.RestartId,
                        restartPhase=restart?.Phase.ToString(),
                        restartOldPid=restart?.OldPid,
                        restartNewPid=restart?.NewPid,
                        pairedJournals=pending.RestartId!=null&&!restartOnlyReviewable?2:1,
                        processStartedUtc=oldStart,
                        rconPort=map.RconPort,
                        expiresUtc=pending.ExpiresUtc,
                        reviewMode=stopped!=null?"stopped":"running",
                        exactWindowsProcessVerified=running!=null,
                        stoppedWindowsProcessVerified=stopped!=null,
                        rconListenerAbsent=stopped!=null,
                        historicalSelectedOwnerVerified=stopped!=null,
                        originalStoreSessionVerified=running!=null&&!sourcePinned,
                        approvedObsidianSourcePinned=sourcePinned,
                        legacyPeerCheckRequiredForLocalReview=!sourcePinned,
                        rconListenerOwnerVerified=running!=null,
                        priorCommandOutcomeUnknown=true,
                        previousOperationWillNotBeRetried=true,requiresTypedApproval=true,
                        auditRequired=true,storeBytesModified=false,
                        evidence=stopped!=null?
                            "ORIGINAL_STORE_STOPPED_PAIRED_JOURNAL_REVIEW_PREVIEW":
                            "ORIGINAL_STORE_RUNNING_LAUNCH_JOURNAL_RECOVERY_PREVIEW"
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
                    if(pinned.StoppedReview)
                    {
                        try
                        {
                            var checkedStopped=VerifyStopped(mapId,pinned.Pid);
                            return checkedStopped.SourceSha==pinned.SourceSha&&
                                JsonSerializer.Serialize(map)==pinned.ProfileJson;
                        }
                        catch(Exception ex) when(ex is InvalidDataException or
                            InvalidOperationException or UnauthorizedAccessException or IOException)
                        {return false;}
                    }
                    var now=Proof(mapId);
                    return ReferenceEquals(now.Map,map)&&
                        now.Process.Pid==pinned.Pid&&
                        now.Process.StartedUtc==pinned.StartedUtc&&
                        now.SourceSha==pinned.SourceSha&&
                        JsonSerializer.Serialize(now.Map)==pinned.ProfileJson;
                }
                if(pinned.StoppedReview)
                {
                    if(pinned.RestartId is not Guid stoppedRestart)
                        return Error(id,"UM-RECOVERY-STOPPED-PAIRED-RESTART-REQUIRED");
                    cluster.Operations.ReviewStoppedOriginalPaired(
                        map.InstallationPath,pinned.Operation,mapId,
                        stoppedRestart,pinned.Pid,CheckPinned);
                }
                else if(pinned.RestartOnly)
                {
                    if(pinned.RestartId is not Guid restartId)
                        return Error(id,"UM-RECOVERY-RESTART-ID-MISSING");
                    cluster.Operations.ReviewRunningOriginalRestartOnly(
                        map.InstallationPath,mapId,restartId,pinned.Pid,CheckPinned);
                }
                else cluster.Operations.ReviewRunningOriginalLaunch(
                    map.InstallationPath,pinned.Operation,CheckPinned,
                    mapId,pinned.RestartId,pinned.Pid);
                return Ok(id,new{
                    mapId,operationId=pinned.Operation,
                    state="operatorReviewed",previousOutcomeStillUnknown=true,
                    originalJournalAudited=!pinned.RestartOnly,
                    restartJournalAudited=pinned.RestartId!=null,
                    pairedJournalsReviewed=pinned.RestartOnly?1:
                        pinned.RestartId!=null?2:1,
                    restartMarkedSuccessful=false,
                    oldRunningPid=pinned.StoppedReview?(int?)null:pinned.Pid,
                    oldStoppedPid=pinned.StoppedReview?pinned.Pid:(int?)null,
                    reviewMode=pinned.StoppedReview?"stopped":"running",
                    storeBytesModified=false,
                    processStarted=false,processStopped=false,rconCommandSent=false,
                    noAutomaticRetry=true,newOperationRequiresFreshApproval=true,
                    evidence=pinned.StoppedReview?
                        "ORIGINAL_STORE_VERIFIED_STOPPED_OWNER_AUDITED_PAIRED_JOURNAL_REVIEW":
                        "ORIGINAL_STORE_EXACT_RUNNING_OWNER_AUDITED_MANUAL_JOURNAL_REVIEW"
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
