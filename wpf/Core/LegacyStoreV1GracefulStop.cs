using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

/// <summary>
/// Real, strictly local graceful SaveWorld -> observed new ASA log+stable
/// world -> DoExit for a RUNNING original Store-v1 ACTIVE/DONE server.
/// This is a separate operation from fresh ASA stop. It never deletes or
/// mutates the original shared Store session, lock, transfer record, tribe
/// or inventory, and never force-kills/restarts the ASA server.
/// </summary>
internal static class LegacyStoreV1GracefulStop
{
    internal sealed record Owned(LocalMap Map,ProcessIdentity Process,string SourceSha);

    private static bool Same(ProcessIdentity left,ProcessIdentity right)=>
        left.Pid>0&&left.Pid==right.Pid&&
        left.StartedUtc==right.StartedUtc&&
        string.Equals(left.ExecutablePath,right.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);

    internal static Owned Require(ClusterService cluster,Guid mapId,
        Func<LocalMap,ProcessCheck> inspect,Func<LocalMap,string> source,
        Func<int,int,bool> portOwner,Guid? approvedRestartId=null,
        bool recoveryReadOnly=false)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(portOwner);
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId)
            ??throw new UnauthorizedAccessException("UM-ORIGINAL-STOP-OWNED-MAP-REQUIRED");
        if(!map.HistoricalStoreProtected||map.Maintenance||
           !string.IsNullOrWhiteSpace(map.ApiLoaderArguments)||
           (map.StartWithAsaApi&&
            (string.IsNullOrWhiteSpace(map.ApiLoaderPath)||
             !Path.GetFullPath(map.ApiLoaderPath).Equals(
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(map.ExecutablePath))!,
                    "AsaApiLoader.exe"),StringComparison.OrdinalIgnoreCase)))||
           !string.IsNullOrWhiteSpace(map.Arguments)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           map.RconHost!="127.0.0.1"||map.RconPort is <1024 or >65535||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected)||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||
           !AsaRconConfigurationEvidence.Matches(map)||
           KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
            throw new InvalidOperationException("UM-ORIGINAL-STOP-CONFIGURATION");
        var restart=new AsaManualRestartJournal(map.InstallationPath);
        if(approvedRestartId is Guid restartId)
        {
            var record=restart.Observe();
            if(record==null||record.Id!=restartId||record.MapId!=mapId||
               record.Phase!=AsaRestartPhase.Stopping)
                throw new InvalidOperationException("UM-ORIGINAL-RESTART-STOP-NOT-AUTHORIZED");
            restart.DemandAuthorizedStop(restartId);
        }
        else if(!recoveryReadOnly)restart.DemandNoPending();
        string fingerprint=source(map);
        if(fingerprint.Length!=64||fingerprint.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidDataException("UM-ORIGINAL-STOP-SOURCE-NOT-ACTIVE");
        var state=inspect(map);
        if(!state.Running||state.ConfirmedStopped||
           state.Identity is not {Pid:>0} identity||
           (map.StartWithAsaApi!=(state.LoaderIdentity is {Pid:>0}))||
           (state.LoaderIdentity!=null&&state.LoaderIdentity.Pid==identity.Pid)||
           !portOwner(map.RconPort,identity.Pid))
            throw new InvalidOperationException("UM-ORIGINAL-STOP-PID-RCON-NOT-OWNED");
        if(recoveryReadOnly)
        {
            // This bypass is ONLY for read-only proof during an explicitly
            // approved paired-journal review, never for Stop/Restart/RCON.
            // Still require the ORIGINAL live PID, full Store session and
            // RCON port ownership proof above; an unexpected restarted PID
            // must NOT be made eligible for the old-process recovery.
            var oldRestart=restart.Observe();
            if(oldRestart!=null&&oldRestart.Phase is not
                (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed)&&
               (oldRestart.MapId!=mapId||oldRestart.OldPid!=identity.Pid||
                oldRestart.NewPid!=0||oldRestart.Phase is not
                    (AsaRestartPhase.NeedsOperator or AsaRestartPhase.Stopping)))
                throw new InvalidOperationException(
                    "UM-RECOVERY-RESTART-OLD-PID-OR-PHASE-MISMATCH");
        }
        return new(map,identity,fingerprint);
    }

    internal static string OriginalSource(LocalMap map) =>
        ReadSource(map,Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData),"Kova"));

    internal static string OriginalSource(ClusterService cluster,LocalMap map) =>
        ReadSource(map,LegacyStoreV1PinnedSource.Resolve(cluster.Store.Root,
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),"Kova"),map));

    private static string ReadSource(LocalMap map,string profile)
    {
        var proof=LegacyStoreV1LiveReadGate.InspectOriginalActive(map,profile);
        if(!proof.CompletedOriginalSourceGuard||!proof.BothPeersDone||
           !proof.SharedSessionMatches||proof.ActiveMaps!=10||proof.DonePeers!=2||
           !proof.LocalMapBindingMatches||!proof.NoMutationMarker||
           !proof.NoPendingConversion||!proof.HistoricalFilesUnchanged)
            throw new InvalidOperationException("UM-ORIGINAL-STOP-SOURCE-CHANGED");
        return proof.SourceStateSha256;
    }

    /// <summary>
    /// A source fingerprint fixed at PREVIEW time must remain unchanged
    /// through preflight, native lease, authenticated RCON dispatch and
    /// verification. Exposed delegates are only native test seams, not UI
    /// or LAN parameters.
    /// </summary>
    internal static async Task<AsaGracefulStopReceipt> StopAsync(
        ClusterService cluster,Guid mapId,int approvedPid,
        DateTimeOffset approvedStart,string approvedSource,
        TimeSpan maximum,CancellationToken cancellationToken=default,
        Func<LocalMap,ProcessCheck>? inspect=null,
        Func<LocalMap,ProcessCheck>? stopped=null,
        Func<LocalMap,string>? source=null,
        Func<int,int,bool>? portOwner=null,
        Func<LocalMap,ProcessIdentity,Action,CancellationToken,Task<bool>>? execute=null,
        Action<string>? stage=null,Guid? approvedRestartId=null)
    {
        if(mapId==Guid.Empty||approvedPid<=0||approvedStart==default||
           string.IsNullOrWhiteSpace(approvedSource)||
           maximum<=TimeSpan.Zero||maximum>TimeSpan.FromMinutes(3))
            throw new InvalidDataException("UM-ORIGINAL-STOP-APPROVAL");
        inspect??=(map=>LegacyStoreV1ProcessObserver.Observe(map,cluster.Config.LocalMaps));
        stopped??=(map=>LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(
            map,cluster.Config.LocalMaps));
        source??=(map=>OriginalSource(cluster,map));
        portOwner??=RconPortOwnership.IsOwnedListener;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maximum);
        var ct=deadline.Token;
        var initial=Require(cluster,mapId,inspect,source,portOwner,approvedRestartId);
        if(initial.Process.Pid!=approvedPid||
           initial.Process.StartedUtc!=approvedStart||
           !string.Equals(initial.SourceSha,approvedSource,StringComparison.Ordinal))
            throw new InvalidDataException("UM-ORIGINAL-STOP-SOURCE-OR-PID-CHANGED");
        using var lease=cluster.Operations.Acquire(initial.Map.InstallationPath,
            UntamedOperationKind.Launch);
        var locked=Require(cluster,mapId,inspect,source,portOwner,approvedRestartId);
        if(!ReferenceEquals(locked.Map,initial.Map)||
           !Same(locked.Process,initial.Process)||
           locked.SourceSha!=initial.SourceSha)
            throw new InvalidOperationException("UM-ORIGINAL-STOP-LEASE-PREFLIGHT-CHANGED");
        void Verify()
        {
            ct.ThrowIfCancellationRequested();
            var now=Require(cluster,mapId,inspect,source,portOwner,approvedRestartId);
            if(!ReferenceEquals(now.Map,locked.Map)||
               !Same(now.Process,locked.Process)||
               now.SourceSha!=locked.SourceSha)
                throw new InvalidOperationException("UM-ORIGINAL-STOP-OWNER-CHANGED");
        }
        Verify();
        // Absence of a real ASA log/world witness is a PRE-MUTATION error:
        // do not strand a durable NEEDS_OPERATOR journal if we have sent no
        // RCON byte. Mutating operation state is marked only after witness
        // acquisition and immediately before first command.
        var witness=execute==null?
            AsaWorldSaveEvidence.Begin(locked.Map,locked.Process)
                ??throw new InvalidOperationException("UM-ORIGINAL-STOP-SAVE-WITNESS-MISSING"):
            null;
        lease.MarkMutationStarted();
        bool preExitObserved=false,worldMetadataObserved=false;
        if(execute!=null)
            preExitObserved=await execute(locked.Map,locked.Process,Verify,ct).WaitAsync(ct);
        else
        {
            var evidence=witness!;
            string password=SecureStore.Unprotect(locked.Map.RconPasswordProtected);
            Verify();
            stage?.Invoke("SaveWorld elküldése az eredeti helyi Store-v1 ASA-nak");
            await RconProtocol.DispatchOwnedSaveWorldAsync(locked.Map.RconPort,
                password,Verify,ct);
            var observed=await AsaWorldSaveEvidence.WaitAsync(
                evidence,()=>{
                    try{Verify();return true;}catch{return false;}
                },TimeSpan.FromSeconds(90),ct,allowWorldMetadata:true);
            if(!observed.CompletionObserved&&!observed.WorldMetadataObserved)
                throw new InvalidOperationException(
                    "UM-ORIGINAL-STOP-SAVE-NOT-OBSERVED:"+observed.Stage);
            preExitObserved=observed.CompletionObserved;
            worldMetadataObserved=observed.WorldMetadataObserved;
            stage?.Invoke(preExitObserved
                ?"Új ASA SaveComplete napló és stabil világfájl igazolva"
                :"Új stabil világfájl megfigyelve; ASA SaveComplete napló nem igazolt");
            Verify();
            await RconProtocol.DispatchDoExitAsync(locked.Map.RconPort,
                password,Verify,ct);
            stage?.Invoke("DoExit elküldve; a régi Windows ASA PID kilépését ellenőrzöm");
        }
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var check=stopped(locked.Map);
            if(check.ConfirmedStopped&&!check.Running&&check.Identity==null&&
               check.LoaderIdentity==null)
                break;
            // If a different process has taken over the same old map
            // before the old PID is gone, do not claim completed shutdown.
            var live=inspect(locked.Map);
            if(live.Running&&live.Identity is {Pid:>0} current&&
               !Same(current,locked.Process))
                throw new InvalidOperationException("UM-ORIGINAL-STOP-REPLACEMENT-ASA-PID");
            await Task.Delay(300,ct);
        }
        stage?.Invoke("Eredeti ASA-folyamat kilépése Windowsból igazolt");
        lease.Complete();
        return new(mapId,approvedPid,true,false,true,DateTimeOffset.UtcNow,
            preExitObserved?
              "ORIGINAL_ACTIVE_STORE_PREEXIT_SAVE_EVIDENCE_AND_DOEXIT_PID_STOPPED":
              "ORIGINAL_ACTIVE_STORE_PREEXIT_WORLD_METADATA_LOG_UNVERIFIED_DOEXIT_PID_STOPPED")
        {
            PreExitSaveLogAndWorldObserved=preExitObserved,
            PreExitWorldMetadataObserved=worldMetadataObserved
        };
    }

    // Separate, real SaveWorld operation on an existing ACTIVE/DONE Store map.
    // It uses the SAME owner/source/port guards and installation journal as
    // StopAsync, but deliberately sends NO DoExit and never touches Store data.
    internal sealed record SaveOnlyReceipt(Guid MapId,int Pid,
        bool SaveCompleteObserved,DateTimeOffset ObservedUtc,string Evidence)
    {
        public bool DoExitSent=>false;
        public bool FsyncCertified=>false;
        public bool GameClientRejoinVerified=>false;
    }

    internal static async Task<SaveOnlyReceipt> SaveWorldOnlyAsync(
        ClusterService cluster,Guid mapId,int approvedPid,
        DateTimeOffset approvedStart,string approvedSource,
        TimeSpan maximum,CancellationToken cancellationToken=default,
        Func<LocalMap,ProcessCheck>? inspect=null,
        Func<LocalMap,string>? source=null,
        Func<int,int,bool>? portOwner=null,
        Func<LocalMap,ProcessIdentity,Action,CancellationToken,Task<bool>>? execute=null,
        Action<string>? stage=null)
    {
        if(mapId==Guid.Empty||approvedPid<=0||approvedStart==default||
           string.IsNullOrWhiteSpace(approvedSource)||
           maximum<=TimeSpan.Zero||maximum>TimeSpan.FromMinutes(3))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-APPROVAL");
        inspect??=(map=>LegacyStoreV1ProcessObserver.Observe(map,cluster.Config.LocalMaps));
        source??=(map=>OriginalSource(cluster,map));
        portOwner??=RconPortOwnership.IsOwnedListener;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maximum);
        var ct=deadline.Token;
        var initial=Require(cluster,mapId,inspect,source,portOwner);
        if(initial.Process.Pid!=approvedPid||
           initial.Process.StartedUtc!=approvedStart||
           !string.Equals(initial.SourceSha,approvedSource,StringComparison.Ordinal))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-OWNER-CHANGED");
        using var lease=cluster.Operations.Acquire(initial.Map.InstallationPath,
            UntamedOperationKind.Backup);
        var locked=Require(cluster,mapId,inspect,source,portOwner);
        if(!ReferenceEquals(locked.Map,initial.Map)||
           !Same(locked.Process,initial.Process)||
           locked.SourceSha!=initial.SourceSha)
            throw new InvalidOperationException("UM-ORIGINAL-SAVE-LEASE-OWNER-CHANGED");
        void Verify()
        {
            ct.ThrowIfCancellationRequested();
            var now=Require(cluster,mapId,inspect,source,portOwner);
            if(!ReferenceEquals(now.Map,locked.Map)||
               !Same(now.Process,locked.Process)||
               now.SourceSha!=locked.SourceSha)
                throw new InvalidOperationException("UM-ORIGINAL-SAVE-OWNER-CHANGED");
        }
        Verify();
        // Witness must exist before ANY mutating RCON byte. A missing log or
        // world witness does not strand the durable journal after no send.
        var witness=execute==null?
            AsaWorldSaveEvidence.Begin(locked.Map,locked.Process)
                ??throw new InvalidOperationException("UM-ORIGINAL-SAVE-WITNESS-MISSING"):
            null;
        lease.MarkMutationStarted(); // Crash/uncertain dispatch => NeedsOperator
        bool observed;
        if(execute!=null)
            observed=await execute(locked.Map,locked.Process,Verify,ct).WaitAsync(ct);
        else
        {
            string password=SecureStore.Unprotect(locked.Map.RconPasswordProtected);
            Verify();
            stage?.Invoke("Store-v1 SaveWorld küldése az igazolt helyi ASA PID-nek");
            await RconProtocol.DispatchOwnedSaveWorldAsync(locked.Map.RconPort,
                password,Verify,ct);
            var result=await AsaWorldSaveEvidence.WaitAsync(witness!,()=>{
                try{Verify();return true;}catch{return false;}
            },TimeSpan.FromSeconds(90),ct);
            observed=result.CompletionObserved;
        }
        if(!observed)
            throw new InvalidOperationException("UM-ORIGINAL-SAVE-COMPLETION-NOT-OBSERVED");
        Verify();
        stage?.Invoke("Új ASA SaveComplete log és stabil .ark ellenőrizve; a map tovább fut");
        lease.Complete();
        return new(mapId,approvedPid,true,DateTimeOffset.UtcNow,
            "ORIGINAL_ACTIVE_STORE_SAVEWORLD_LOG_AND_STABLE_WORLD_NO_DOEXIT");
    }

    internal static Owned PreviewForUnfinishedRestartReview(
        ClusterService cluster,Guid mapId,
        Func<LocalMap,ProcessCheck>? inspect=null,
        Func<LocalMap,string>? source=null,
        Func<int,int,bool>? portOwner=null)=>
        Require(cluster,mapId,
            inspect??(map=>LegacyStoreV1ProcessObserver.Observe(
                map,cluster.Config.LocalMaps)),
            source??(map=>OriginalSource(cluster,map)),
            portOwner??RconPortOwnership.IsOwnedListener,
            recoveryReadOnly:true);

    internal static Owned Preview(ClusterService cluster,Guid mapId,
        Func<LocalMap,ProcessCheck>? inspect=null,
        Func<LocalMap,string>? source=null,
        Func<int,int,bool>? portOwner=null)=>
        Require(cluster,mapId,inspect??(map=>LegacyStoreV1ProcessObserver.Observe(
                map,cluster.Config.LocalMaps)),
            source??(map=>OriginalSource(cluster,map)),portOwner??RconPortOwnership.IsOwnedListener);
}
