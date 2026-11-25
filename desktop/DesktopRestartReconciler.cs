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
            if(map.Maintenance)return false;
            var general=cluster.Operations.Observe(map.InstallationPath);
            bool paired=general?.Phase is UntamedOperationPhase.Prepared or
                UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator;
            if(paired&&(general!.Kind!=UntamedOperationKind.Launch||
                DateTimeOffset.UtcNow-general.UpdatedUtc<TimeSpan.FromMinutes(3)))return false;
            var journal=new AsaManualRestartJournal(map.InstallationPath);
            var record=journal.Observe();
            if(record==null||record.MapId!=map.Id||
                (record.Phase!=AsaRestartPhase.NeedsOperator&&
                 !(paired&&record.Phase==AsaRestartPhase.OperatorReviewed))||
                record.NewPid==record.OldPid||
                (record.Phase!=AsaRestartPhase.OperatorReviewed&&
                 DateTimeOffset.UtcNow-record.ChangedUtc<TimeSpan.FromMinutes(3)))
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
                    JsonSerializer.Serialize(map)!=profile||
                    cluster.Operations.Observe(map.InstallationPath)!=general)return false;
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
            if(paired)
                cluster.Operations.ReviewStoppedOriginalPaired(map.InstallationPath,general!.Operation,
                    map.Id,record.Id,record.OldPid,Verify,record.NewPid,replacement:true);
            else
                cluster.Operations.ReviewReplacementOriginalRestartOnly(map.InstallationPath,map.Id,
                    record.Id,record.OldPid,record.NewPid,Verify);
            return true;
        }
        catch(Exception ex) when(IsRecoverable(ex))
        {
            // Leave the unresolved receipt intact on failed preflight. The
            // existing journal diagnostics still expose its actual blocker.
            return false;
        }
    }

    // Original owner still running: no new PID was recorded, and the exact
    // current family and source are re-proven under the installation lease.
    // Old v1 journals lack an old start epoch; refuse a PID born after the
    // abandoned claim. The result is an archived unknown, not a success.
    internal static bool TryReconcileOldOwner(ClusterService cluster,LocalMap map,
        Func<AsaRestartRecord,Witness> observe)
    {
        try
        {
            if(map.Maintenance)return false;
            var journal=new AsaManualRestartJournal(map.InstallationPath);
            var record=journal.Observe();
            var general=cluster.Operations.Observe(map.InstallationPath);
            bool paired=general?.Phase is (UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator);
            if(paired&&(general!.Kind!=UntamedOperationKind.Launch||
                DateTimeOffset.UtcNow-general.UpdatedUtc<TimeSpan.FromMinutes(3)))return false;
            if(record==null||record.MapId!=map.Id||record.NewPid!=0||
                (record.Phase is not (AsaRestartPhase.Stopping or AsaRestartPhase.NeedsOperator)&&
                 !(paired&&record.Phase==AsaRestartPhase.OperatorReviewed))||
                (record.Phase!=AsaRestartPhase.OperatorReviewed&&
                 DateTimeOffset.UtcNow-record.ChangedUtc<TimeSpan.FromMinutes(3)))return false;
            string profile=JsonSerializer.Serialize(map);
            bool ClearOtherReceipts()
            {
                new AsaManualStopJournal(map.InstallationPath).DemandNoPending(map.Id);
                return !map.HistoricalStoreProtected||
                    LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id)?.Phase is not ("prepared" or "needsOperator");
            }
            if(!ClearOtherReceipts())return false;
            bool ValidOld(Witness w)=>w.Asa.Pid==record.OldPid&&w.Asa.StartedUtc.Year>=2020&&
                w.Asa.StartedUtc<record.ChangedUtc&&!string.IsNullOrWhiteSpace(w.Asa.ExecutablePath)&&
                !string.IsNullOrWhiteSpace(w.Source)&&map.StartWithAsaApi==(w.Loader!=null)&&
                (w.Loader==null||w.Loader.Pid>0&&w.Loader.Pid!=w.Asa.Pid&&w.Loader.StartedUtc<=w.Asa.StartedUtc&&
                    !string.IsNullOrWhiteSpace(w.Loader.ExecutablePath));
            var first=observe(record);if(!ValidOld(first))return false;
            bool Verify()
            {
                var fresh=journal.Observe();
                if(fresh==null||!ReferenceEquals(cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==map.Id),map)||
                   JsonSerializer.Serialize(map)!=profile||cluster.Operations.Observe(map.InstallationPath)!=general||
                   !ClearOtherReceipts())return false;
                if(fresh!=record&&(fresh.Phase!=AsaRestartPhase.OperatorReviewed||
                    fresh!=(record with{Phase=AsaRestartPhase.OperatorReviewed,ChangedUtc=fresh.ChangedUtc})))return false;
                var current=observe(fresh);
                return ValidOld(current)&&current.Source==first.Source&&Same(first.Asa,current.Asa)&&Same(first.Loader,current.Loader);
            }
            if(paired)cluster.Operations.ReviewRunningOriginalLaunch(map.InstallationPath,general!.Operation,
                Verify,map.Id,record.Id,record.OldPid);
            else cluster.Operations.ReviewRunningOriginalRestartOnly(map.InstallationPath,map.Id,record.Id,record.OldPid,Verify);
            return true;
        }
        catch(Exception ex) when(IsRecoverable(ex)){return false;}
    }

    private static bool IsRecoverable(Exception ex)=>ex is IOException or InvalidDataException or
        UnauthorizedAccessException or InvalidOperationException or ArgumentException or
        JsonException or NotSupportedException;

    // Inspired by phase-specific job recovery: the stopped family is a
    // verified runtime state, not a successful historical stop or restart.
    // Only restart-only and paired Launch receipts are supported here.
    // No start is issued; any later Start must be a new explicit or already
    // configured scheduled operation through the regular native gates.
    internal static bool TryReconcileStopped(ClusterService cluster,LocalMap map,
        Func<AsaRestartRecord,string?> observeStopped)
    {
        try
        {
            // AutoStart is a preference, not a live launch. The installation lease
            // and repeated stopped-family proof prevent races; recovery never starts ASA.
            if(map.Maintenance)return false;
            var journal=new AsaManualRestartJournal(map.InstallationPath);
            var record=journal.Observe();
            var general=cluster.Operations.Observe(map.InstallationPath);
            bool paired=general?.Phase is UntamedOperationPhase.Prepared or
                UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator;
            if(paired&&general!.Kind!=UntamedOperationKind.Launch)return false;
            if(record==null||record.MapId!=map.Id||record.NewPid==record.OldPid||
                (record.Phase is not (AsaRestartPhase.NeedsOperator or AsaRestartPhase.Stopping or AsaRestartPhase.Starting)&&
                 !(paired&&record.Phase==AsaRestartPhase.OperatorReviewed))||
                (record.Phase!=AsaRestartPhase.OperatorReviewed&&
                 DateTimeOffset.UtcNow-record.ChangedUtc<TimeSpan.FromMinutes(3)))return false;
            var stopJournal=new AsaManualStopJournal(map.InstallationPath);
            var stop=stopJournal.Observe();
            bool pendingStop=stop?.Phase is AsaStopPhase.Pending or AsaStopPhase.NeedsOperator;
            if(pendingStop)
            {
                if(stop!.MapId!=map.Id||stop.OldPid!=record.OldPid||
                    stop.Resource!=UntamedOperationCoordinator.ResourceKey(map.InstallationPath)||
                    DateTimeOffset.UtcNow-stop.ChangedUtc<TimeSpan.FromMinutes(3))return false;
            }
            else stopJournal.DemandNoPending(map.Id);
            bool SaveClear()=>!map.HistoricalStoreProtected||
                LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id)?.Phase is not
                    ("prepared" or "needsOperator");
            if(!SaveClear())return false;
            string profile=JsonSerializer.Serialize(map);
            string? source=observeStopped(record);
            if(string.IsNullOrWhiteSpace(source))return false;
            bool Verify()
            {
                var current=journal.Observe();
                if(current==null||!ReferenceEquals(cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==map.Id),map)||
                   JsonSerializer.Serialize(map)!=profile||cluster.Operations.Observe(map.InstallationPath)!=general)
                    return false;
                if(current!=record&&(current.Phase!=AsaRestartPhase.OperatorReviewed||
                    current!=(record with{Phase=AsaRestartPhase.OperatorReviewed,ChangedUtc=current.ChangedUtc})))
                    return false;
                var liveStop=stopJournal.Observe();
                if(pendingStop)
                {
                    if(liveStop!=stop)
                    {
                        if(liveStop==null||liveStop!=(stop! with{Phase=AsaStopPhase.OperatorReviewed,
                            ChangedUtc=liveStop.ChangedUtc}))return false;
                        stopJournal.DemandNoPending(map.Id);
                    }
                }
                else
                {
                    if(liveStop!=stop)return false;
                    stopJournal.DemandNoPending(map.Id);
                }
                return SaveClear()&&observeStopped(current)==source;
            }
            Action? secondary=pendingStop?()=>{
                if(stopJournal.Observe()!=stop||!Verify())
                    throw new InvalidOperationException("UM-RECOVERY-PAIRED-STOP-CHANGED");
                stopJournal.ReviewStopped(map.Id,stop!.Id);
            }:null;
            if(paired)
                cluster.Operations.ReviewStoppedOriginalPaired(map.InstallationPath,general!.Operation,
                    map.Id,record.Id,record.OldPid,Verify,record.NewPid,reviewSecondary:secondary);
            else
                cluster.Operations.ReviewStoppedOriginalRestartOnly(map.InstallationPath,map.Id,
                    record.Id,record.OldPid,record.NewPid,Verify,secondary);
            return true;
        }
        catch(Exception ex) when(IsRecoverable(ex)){return false;}
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
                // Local Steam queue housekeeping uses its own machine-wide
                // Steam lease and does not change any map installation.
                try{new SteamSerialMaintenanceQueue(cluster.Store.Root,cluster.Config.LocalHostId)
                    .TryIsolateAbandonedActive(cluster.Config.LocalMaps);}
                catch(Exception ex){System.Diagnostics.Trace.TraceWarning("Steam queue reconciliation: "+ex.GetType().Name);}
                foreach(var map in cluster.Config.LocalMaps.ToArray())
                {
                    if(paused()||ct.IsCancellationRequested)break;
                    try
                    {
                        if(!map.Maintenance&&!map.ObservedReadOnlyImported)
                        {
                            // Only an unowned Prepared journal is known to
                            // have no mutation. Secondary receipts are checked
                            // independently by their native reconciliation.
                            cluster.Operations.TryCancelAbandonedPrepared(map.InstallationPath);
                            reconcile(map);
                        }
                    }catch(Exception ex)
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
