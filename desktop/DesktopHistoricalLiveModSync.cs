using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

// Reconcile ONLY the local Manager's next-launch mod metadata, not the
// running game. No network/RCON dispatch, process mutation or Store writes.
// Never run in the UI smoke-test host or across a manager update handoff.
internal sealed class DesktopHistoricalLiveModSync : IDisposable
{
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim hostGate;
    private readonly Func<bool> frozen,enterLane;
    private readonly Action leaveLane;
    private readonly CancellationTokenSource closing=new();
    private int started,disposed;

    internal DesktopHistoricalLiveModSync(ClusterService cluster,SemaphoreSlim hostGate,
        Func<bool> frozen,Func<bool> enterLane,Action leaveLane)
    {
        this.cluster=cluster;this.hostGate=hostGate;this.frozen=frozen;
        this.enterLane=enterLane;this.leaveLane=leaveLane;
    }

    private bool Eligible(LocalMap map)=>map.HistoricalStoreProtected&&
        !map.ObservedReadOnlyImported&&!map.Maintenance&&!map.ModChangesPending&&
        !map.HistoricalLaunchModeNeedsApproval;

    private bool Pending(LocalMap map)
    {
        var receipt=LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id);
        return receipt?.Phase is "prepared" or "needsOperator";
    }

    private void Reconcile(LocalMap map)
    {
        // Observe before acquiring the global lane, but trust nothing from
        // this sample for writes; it is only a cheap no-change filter.
        if(!Eligible(map)||frozen()||closing.IsCancellationRequested)return;
        var first=LegacyStoreV1ProcessObserver.Observe(map,cluster.Config.LocalMaps);
        if(!first.Running||first.Identity==null||first.LiveModOrder==null||
           first.LivePassiveModOrder==null)return;
        var saved=ModStateController.Read(map);
        if(saved.EnabledOrder.SequenceEqual(first.LiveModOrder)&&
           saved.Passive.SequenceEqual(first.LivePassiveModOrder))return;
        if(!hostGate.Wait(0))return;
        try
        {
            if(frozen()||closing.IsCancellationRequested||!enterLane())return;
            try
            {
                if(!Eligible(map)||!cluster.Config.LocalMaps.Contains(map)||
                   !LegacyStoreV1PinnedSource.HasApprovedLocalSource(cluster.Store.Root,map)||
                   Pending(map))return;
                using var lease=cluster.Operations.AcquireMany(
                    new[]{cluster.Store.Root,map.InstallationPath},UntamedOperationKind.Configuration);
                if(frozen()||closing.IsCancellationRequested||!Eligible(map)||Pending(map))
                {lease.Complete();return;}
                var owner=LegacyStoreV1GracefulStop.Preview(cluster,map.Id);
                // Source proof, exact current ASA PID/time, local RCON
                // listener ownership, restart journal AND Loader ancestry
                // are all revalidated here. Preview sends no RCON bytes.
                if(!ReferenceEquals(owner.Map,map)||owner.Process.Pid!=first.Identity.Pid||
                   owner.Process.StartedUtc!=first.Identity.StartedUtc)
                {lease.Complete();return;}
                var actual=LegacyStoreV1ProcessObserver.CaptureVerifiedModOrder(cluster,map,owner.Process);
                if(!Eligible(map)||Pending(map)||frozen()||closing.IsCancellationRequested)
                {lease.Complete();return;}
                HistoricalLiveModSync.WriteVerifiedBaseline(map,actual,cluster.Save,lease.MarkMutationStarted);
                lease.Complete();
            }
            finally{leaveLane();}
        }
        finally{hostGate.Release();}
    }

    internal void Start()
    {
        if(Interlocked.Exchange(ref started,1)!=0||Volatile.Read(ref disposed)!=0)return;
        var token=closing.Token;
        _=Task.Run(async()=>{
            try
            {
                using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
                do
                {
                    foreach(var map in cluster.Config.LocalMaps.ToArray())
                    {
                        if(token.IsCancellationRequested)break;
                        try{Reconcile(map);}
                        catch(Exception){/* Unknown/busy/failed metadata remains unchanged; no server action. */}
                    }
                }while(await timer.WaitForNextTickAsync(token));
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){}
            catch(Exception){/* Fail closed: background metadata reconciliation stops. */}
        },token);
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)==0)closing.Cancel();
        // No synchronous wait on WMI while closing; token prevents new work.
    }
}
