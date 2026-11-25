using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

// Reconcile an abandoned terminal Restart-v1 against the independently
// identified *current* local owner. This is journal bookkeeping, not replay.
// The old failure remains archived; no success is invented and no ASA, RCON,
// Steam, INI, world or shared Store operation is performed.
internal static class DesktopRestartReconciler
{
    internal sealed record Witness(ProcessIdentity Asa,ProcessIdentity? Loader,string Source);
    private static bool Same(ProcessIdentity? a,ProcessIdentity? b)=>
        a==null?b==null:b!=null&&a.Pid==b.Pid&&a.StartedUtc==b.StartedUtc&&
        string.Equals(a.ExecutablePath,b.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&
        string.Equals(a.Arguments,b.Arguments,StringComparison.Ordinal);
    private static bool Valid(Witness w,LocalMap map,AsaRestartRecord record)
    {
        var asa=w.Asa;var loader=w.Loader;
        var now=DateTimeOffset.UtcNow;
        return asa.Pid>0&&asa.Pid!=record.OldPid&&asa.StartedUtc.Year>=2020&&
            asa.StartedUtc<=now&& !string.IsNullOrWhiteSpace(asa.ExecutablePath)&&
            !string.IsNullOrWhiteSpace(w.Source)&&map.StartWithAsaApi==(loader!=null)&&
            (loader==null||(loader.Pid>0&&loader.Pid!=asa.Pid&&loader.Pid!=record.OldPid&&
                loader.StartedUtc.Year>=2020&&loader.StartedUtc<=asa.StartedUtc&&
                !string.IsNullOrWhiteSpace(loader.ExecutablePath)));
    }
    private static bool GeneralClear(ClusterService cluster,LocalMap map)=>
        cluster.Operations.Observe(map.InstallationPath)?.Phase is not
            (UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator);

    // Caller holds the host mutation lane. Coordinator additionally acquires
    // the installation-wide cross-process lease and revalidates each witness.
    internal static bool TryReconcile(ClusterService cluster,LocalMap map,
        Func<AsaRestartRecord,Witness> observe)
    {
        try
        {
            if(map.Maintenance||!GeneralClear(cluster,map))return false;
            var journal=new AsaManualRestartJournal(map.InstallationPath);
            var record=journal.Observe();
            if(record==null||record.MapId!=map.Id||record.Phase!=AsaRestartPhase.NeedsOperator||
                record.NewPid==record.OldPid||DateTimeOffset.UtcNow-record.ChangedUtc<TimeSpan.FromMinutes(3))
                return false;
            new AsaManualStopJournal(map.InstallationPath).DemandNoPending(map.Id);
            // A separate uncertain SaveWorld is not silently cleared.
            if(map.HistoricalStoreProtected)
            {
                var save=LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id);
                if(save?.Phase is "prepared" or "needsOperator")return false;
            }
            string profile=JsonSerializer.Serialize(map);
            var first=observe(record);
            if(!Valid(first,map,record))return false;
            bool Verify()
            {
                var fresh=journal.Observe();
                if(fresh==null||!ReferenceEquals(cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==map.Id),map)||
                    JsonSerializer.Serialize(map)!=profile||!GeneralClear(cluster,map))return false;
                // Allow only the exact original snapshot or our audited terminal
                // transition; never adopt an unrelated new journal.
                if(fresh!=record&&(fresh.Phase!=AsaRestartPhase.OperatorReviewed||
                    fresh!=(record with{Phase=AsaRestartPhase.OperatorReviewed,ChangedUtc=fresh.ChangedUtc})))
                    return false;
                new AsaManualStopJournal(map.InstallationPath).DemandNoPending(map.Id);
                if(map.HistoricalStoreProtected&&
                    LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id)?.Phase is ("prepared" or "needsOperator"))
                    return false;
                var current=observe(fresh);
                return Valid(current,map,record)&&current.Source==first.Source&&
                    Same(current.Asa,first.Asa)&&Same(current.Loader,first.Loader);
            }
            cluster.Operations.ReviewReplacementOriginalRestartOnly(map.InstallationPath,map.Id,
                record.Id,record.OldPid,record.NewPid,Verify);
            return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
        {
            // Leave the unresolved receipt intact on failed preflight. The
            // existing journal diagnostics still expose its actual blocker.
            return false;
        }
    }
}

internal sealed class DesktopRestartReconciliationSupervisor : IDisposable
{
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim hostGate;
    private readonly Func<bool> enter,paused;
    private readonly Action leave;
    private readonly Func<LocalMap,bool> reconcile;
    private readonly CancellationTokenSource cancellation=new();
    private int started;
    public DesktopRestartReconciliationSupervisor(ClusterService cluster,SemaphoreSlim hostGate,
        Func<bool> enter,Action leave,Func<bool> paused,Func<LocalMap,bool> reconcile)
    {
        this.cluster=cluster;this.hostGate=hostGate;this.enter=enter;this.leave=leave;
        this.paused=paused;this.reconcile=reconcile;
    }
    internal async Task TickOnceAsync(CancellationToken ct)
    {
        if(paused()||!await hostGate.WaitAsync(0,ct))return;
        try
        {
            if(paused()||!enter())return;
            try
            {
                foreach(var map in cluster.Config.LocalMaps.ToArray())
                {
                    if(paused()||ct.IsCancellationRequested)break;
                    try{reconcile(map);}catch(Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning("Restart reconciliation {0}: {1}",map.Id,ex.GetType().Name);
                    }
                }
            }
            finally{leave();}
        }
        finally{hostGate.Release();}
    }
    public void Start()
    {
        if(Interlocked.Exchange(ref started,1)!=0)return;
        var ct=cancellation.Token;
        _=Task.Run(async()=>{
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                do
                {
                    try{await TickOnceAsync(ct);}
                    catch(Exception ex) when(ex is not OperationCanceledException)
                    {System.Diagnostics.Trace.TraceWarning("Restart reconciliation scan: {0}",ex.GetType().Name);}
                }while(await timer.WaitForNextTickAsync(ct));
            }
            catch(OperationCanceledException){}
        });
    }
    public void Dispose()=>cancellation.Cancel();
}
