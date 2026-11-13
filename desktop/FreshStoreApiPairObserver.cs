using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// A bounded follow-up to an OPERATOR-approved Loader launch for fresh ASA
/// and newly enrolled Store maps, including
/// manual Restart. Read-only physical process observation may persist a verified
/// local Loader/ASA identity pair for safe orphan-child reattach after UI restart.
/// No new Start, RCON command, Store transfer, session or backup writes.
/// </summary>
internal static class FreshStoreApiPairObserver
{
    internal static bool MatchesVerifiedPair(ProcessCheck observed,
        ProcessIdentity launchedLoader,string expectedAsaExecutable,
        string expectedAsaArguments,int rconPort,Func<int,int,bool> ownsRcon)
    {
        if(observed==null||launchedLoader==null||
           !observed.Running||observed.ConfirmedStopped||
           observed.Identity is not {Pid:>0} child||
           observed.LoaderIdentity is not {Pid:>0} parent||
           rconPort is <1024 or >65535||
           parent.Pid!=launchedLoader.Pid||
           parent.StartedUtc!=launchedLoader.StartedUtc||
           parent.StartedUtc>child.StartedUtc||
           parent.Pid==child.Pid||
           !string.Equals(parent.ExecutablePath,launchedLoader.ExecutablePath,
               StringComparison.OrdinalIgnoreCase)||
           parent.Arguments!=launchedLoader.Arguments||
           string.IsNullOrWhiteSpace(expectedAsaExecutable)||
           !string.Equals(child.ExecutablePath,expectedAsaExecutable,
               StringComparison.OrdinalIgnoreCase)||
           child.Arguments!=expectedAsaArguments||
           string.Equals(parent.ExecutablePath,child.ExecutablePath,
               StringComparison.OrdinalIgnoreCase))
            return false;
        return ownsRcon(rconPort,child.Pid);
    }

    internal static void Begin(ClusterService cluster,Guid mapId,
        ProcessIdentity launchedLoader,CancellationToken cancellation)
    {
        if(cluster==null||mapId==Guid.Empty||launchedLoader.Pid<=0||
           launchedLoader.StartedUtc==default)
            return;
        _=Task.Run(async()=>{
            try
            {
                var until=DateTimeOffset.UtcNow.AddMinutes(3);
                while(!cancellation.IsCancellationRequested&&DateTimeOffset.UtcNow<until)
                {
                    var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
                    if(map==null||map.HistoricalStoreProtected||
                       map.ObservedReadOnlyImported||map.Maintenance||
                       !map.StartWithAsaApi)
                        return;
                    // Both newly enrolled Store and ordinary fresh ASA are
                    // permitted. Never extend this to historical/foreign maps.
                    if(FreshStoreEnrollmentPolicy.IsEnrolled(map))
                        FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);
                    else
                        FreshAsaInstallationSafety.Demand(cluster,map);
                    var observed=cluster.ObserveLocalProcessesReadOnly(map);
                    if(MatchesVerifiedPair(observed,launchedLoader,
                        map.ExecutablePath,map.Arguments,map.RconPort,
                        RconPortOwnership.IsOwnedListener))
                    {
                        // RecordObservedIdentity obtains the SAME installation
                        // lease as Start/Restart and rechecks physical process
                        // identity there. A partial/unknown pair never writes.
                        cluster.RecordObservedIdentity(mapId,observed);
                        return;
                    }
                    await Task.Delay(850,cancellation);
                }
            }
            catch(Exception)
            {
                // Strictly fail closed; no retries or fabricated runtime proof.
            }
        },cancellation);
    }
}
