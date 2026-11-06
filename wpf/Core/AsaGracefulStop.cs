using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public sealed record AsaGracefulStopReceipt(Guid MapId,int Pid,
    bool ConfirmedStopped,bool SaveCompleteProven,bool UsedDoExit,
    DateTimeOffset CompletedUtc,string Evidence);

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
        Func<int,int,bool>? listenerOwner=null)
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
        Verify();
        lease.MarkMutationStarted();
        if(sendCommands!=null)
            await sendCommands(map,expected,ct).WaitAsync(ct);
        else
        {
            string password=SecureStore.Unprotect(map.RconPasswordProtected);
            if(string.IsNullOrWhiteSpace(password))
                throw new InvalidDataException("Local RCON credential unavailable");
            await RconProtocol.DispatchOwnedSaveWorldAsync(
                map.RconPort,password,Verify,ct);
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
            "REAL_LOCAL_RCON_DOEXIT_PROCESS_EXIT_NOT_INDEPENDENT_SAVECOMPLETE");
    }
}
