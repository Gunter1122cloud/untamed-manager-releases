using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public sealed record AsaGracefulStopReceipt(Guid MapId,int Pid,
    bool ConfirmedStopped,bool SaveCompleteProven,bool UsedDoExit,
    DateTimeOffset CompletedUtc,string Evidence)
{
    // Log marker + changed/stable .ark were witnessed BEFORE DoExit.
    // They do not prove ASA's final DoExit save was fsynced, nor that a
    // character/tribe can actually rejoin after a restore.
    public bool PreExitSaveLogAndWorldObserved {get;init;}
    public bool GameClientRejoinVerified=>false;
}

/// <summary>
/// Strictly local, non-Store, first-run ASA graceful stop. SaveWorld is sent
/// before DoExit, and DoExit itself asks ASA to save and shut down. An RCON
/// acknowledgement or process exit is not a proof of independently verified
/// disk flush; the receipt deliberately never claims SaveComplete.
/// No Kill, retries, synthetic witness or implicit restart are permitted.
/// </summary>
public static class AsaGracefulStop
{
    private static bool Same(ProcessIdentity? a,ProcessIdentity? b)=>
        a!=null && b!=null && a.Pid==b.Pid &&
        a.StartedUtc==b.StartedUtc &&
        string.Equals(a.ExecutablePath,b.ExecutablePath,
            StringComparison.OrdinalIgnoreCase) &&
        a.Arguments==b.Arguments;

    public static async Task<AsaGracefulStopReceipt> StopAsync(
        ClusterService cluster,Guid mapId,ProcessIdentity expected,
        TimeSpan timeout,CancellationToken cancellationToken=default,
        Func<LocalMap,ProcessIdentity,CancellationToken,Task>? sendCommands=null,
        Func<int,int,bool>? listenerOwner=null,
        Guid? approvedRestartId=null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(expected);
        if(mapId==Guid.Empty||timeout<=TimeSpan.Zero||
           timeout>TimeSpan.FromMinutes(3))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId)
            ??throw new UnauthorizedAccessException("Local ASA map is not registered");
        if(KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map)||map.Maintenance||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           (map.Arguments+" "+map.ApiLoaderArguments).Contains("store",
               StringComparison.OrdinalIgnoreCase)||
           !AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidOperationException("Historical or unverified map cannot stop");
        if(map.RconHost!="127.0.0.1"||map.RconPort is <1024 or >65535||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected))
            throw new InvalidDataException("Local RCON is required for graceful stop");
        FreshAsaInstallationSafety.Demand(cluster,map);
        var restartJournal=new AsaManualRestartJournal(map.InstallationPath);
        restartJournal.DemandAuthorizedStop(approvedRestartId);
        var owns=listenerOwner??RconPortOwnership.IsOwnedListener;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct=deadline.Token;
        void Verify()
        {
            ct.ThrowIfCancellationRequested();
            var check=cluster.Check(map);
            if(!check.Running||check.ConfirmedStopped||
               !Same(check.Identity,expected)||
               !owns(map.RconPort,expected.Pid))
                throw new InvalidOperationException("ASA PID or RCON listener identity changed");
        }
        var status=cluster.Check(map);
        if(!ReleaseCapabilityGate.Check(ManagerOperation.RealAsaStop,true,
            map.Maintenance,status.ConfirmedStopped,status.Running,
            verifiedFreshInstall:true,explicitOperatorApproval:true).Allowed)
            throw new InvalidOperationException("Graceful local stop is not authorized");
        Verify();
        using var lease=cluster.Operations.Acquire(map.InstallationPath,
            UntamedOperationKind.Launch);
        FreshAsaInstallationSafety.Demand(cluster,map);
        restartJournal.DemandAuthorizedStop(approvedRestartId);
        if(!AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidOperationException("UM-ASA-STOP-LOCAL-BINARY-CHANGED");
        Verify();
        lease.MarkMutationStarted();
        bool preExitEvidence=false;
        if(sendCommands!=null)
            await sendCommands(map,expected,ct).WaitAsync(ct);
        else
        {
            string password=SecureStore.Unprotect(map.RconPasswordProtected);
            if(string.IsNullOrWhiteSpace(password))
                throw new InvalidDataException("Local RCON credential unavailable");
            // Capture the current log byte offset BEFORE irreversible RCON
            // dispatch. Old autosave lines must not certify this SaveWorld.
            var witness=AsaWorldSaveEvidence.Begin(map,expected) ??
                throw new InvalidOperationException(
                    "UM-ASA-STOP-LOG-WITNESS-MISSING");
            await RconProtocol.DispatchOwnedSaveWorldAsync(
                map.RconPort,password,Verify,ct);
            var observed=await AsaWorldSaveEvidence.WaitAsync(
                witness,()=>{
                    try{Verify();return true;}
                    catch{return false;}
                },TimeSpan.FromSeconds(90),ct);
            if(!observed.CompletionObserved)
                throw new InvalidOperationException(
                    "UM-ASA-STOP-SAVE-NOT-OBSERVED_NO_DOEXIT");
            preExitEvidence=true;
            Verify();
            await RconProtocol.DispatchDoExitAsync(
                map.RconPort,password,Verify,ct);
        }
        // DoExit normally closes ASA after its own world save. The process
        // must vanish before declaring a successful graceful stop.
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var check=cluster.Check(map);
            if(check.ConfirmedStopped && !check.Running &&
               check.Identity==null&&check.LoaderIdentity==null)
                break;
            if(check.Running && !Same(check.Identity,expected))
                throw new InvalidOperationException(
                    "Unexpected replacement ASA process during shutdown");
            await Task.Delay(300,ct);
        }
        lease.Complete();
        return new(map.Id,expected.Pid,true,false,true,DateTimeOffset.UtcNow,
            preExitEvidence?
              "REAL_LOCAL_PREEXIT_SAVE_LOG_AND_WORLD_VERIFIED_FINAL_EXIT_SAVE_UNVERIFIED":
              "ISOLATED_COMMAND_DISPATCH_AND_PROCESS_EXIT_NO_INDEPENDENT_SAVE_WITNESS")
        {
            PreExitSaveLogAndWorldObserved=preExitEvidence
        };
    }
}
