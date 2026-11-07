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
        Func<int,int,bool> portOwner)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(portOwner);
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId)
            ??throw new UnauthorizedAccessException("UM-ORIGINAL-STOP-OWNED-MAP-REQUIRED");
        if(!map.HistoricalStoreProtected||map.Maintenance||map.StartWithAsaApi||
           !string.IsNullOrWhiteSpace(map.Arguments)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           map.RconHost!="127.0.0.1"||map.RconPort is <1024 or >65535||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected)||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||
           !AsaRconConfigurationEvidence.Matches(map)||
           KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
            throw new InvalidOperationException("UM-ORIGINAL-STOP-CONFIGURATION");
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        string fingerprint=source(map);
        if(fingerprint.Length!=64||fingerprint.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidDataException("UM-ORIGINAL-STOP-SOURCE-NOT-ACTIVE");
        var state=inspect(map);
        if(!state.Running||state.ConfirmedStopped||
           state.Identity is not {Pid:>0} identity||
           !portOwner(map.RconPort,identity.Pid))
            throw new InvalidOperationException("UM-ORIGINAL-STOP-PID-RCON-NOT-OWNED");
        return new(map,identity,fingerprint);
    }

    private static string OriginalSource(LocalMap map)
    {
        var proof=LegacyStoreV1LiveReadGate.InspectOriginalActive(map,
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),"Kova"));
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
        Action<string>? stage=null)
    {
        if(mapId==Guid.Empty||approvedPid<=0||approvedStart==default||
           string.IsNullOrWhiteSpace(approvedSource)||
           maximum<=TimeSpan.Zero||maximum>TimeSpan.FromMinutes(3))
            throw new InvalidDataException("UM-ORIGINAL-STOP-APPROVAL");
        inspect??=LegacyStoreV1ProcessObserver.Observe;
        stopped??=LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart;
        source??=OriginalSource;
        portOwner??=RconPortOwnership.IsOwnedListener;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maximum);
        var ct=deadline.Token;
        var initial=Require(cluster,mapId,inspect,source,portOwner);
        if(initial.Process.Pid!=approvedPid||
           initial.Process.StartedUtc!=approvedStart||
           !string.Equals(initial.SourceSha,approvedSource,StringComparison.Ordinal))
            throw new InvalidDataException("UM-ORIGINAL-STOP-SOURCE-OR-PID-CHANGED");
        using var lease=cluster.Operations.Acquire(initial.Map.InstallationPath,
            UntamedOperationKind.Launch);
        var locked=Require(cluster,mapId,inspect,source,portOwner);
        if(!ReferenceEquals(locked.Map,initial.Map)||
           !Same(locked.Process,initial.Process)||
           locked.SourceSha!=initial.SourceSha)
            throw new InvalidOperationException("UM-ORIGINAL-STOP-LEASE-PREFLIGHT-CHANGED");
        void Verify()
        {
            ct.ThrowIfCancellationRequested();
            var now=Require(cluster,mapId,inspect,source,portOwner);
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
        bool preExitObserved=false;
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
                },TimeSpan.FromSeconds(90),ct);
            if(!observed.CompletionObserved)
                throw new InvalidOperationException("UM-ORIGINAL-STOP-SAVE-NOT-OBSERVED");
            preExitObserved=true;
            stage?.Invoke("Új ASA SaveComplete log + stabil világfájl igazolva");
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
            "ORIGINAL_ACTIVE_STORE_PREEXIT_SAVE_EVIDENCE_AND_DOEXIT_PID_STOPPED")
        {
            PreExitSaveLogAndWorldObserved=preExitObserved
        };
    }

    internal static Owned Preview(ClusterService cluster,Guid mapId,
        Func<LocalMap,ProcessCheck>? inspect=null,
        Func<LocalMap,string>? source=null,
        Func<int,int,bool>? portOwner=null)=>
        Require(cluster,mapId,inspect??LegacyStoreV1ProcessObserver.Observe,
            source??OriginalSource,portOwner??RconPortOwnership.IsOwnedListener);
}
