using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// The only autonomous operations in Obsidian are locally owned READ-ONLY
/// inspections. The durable Core runner claims each due slot before dispatch,
/// skips missed slots and never replays a task after Manager restart.
/// No RCON, SteamCMD app_update, ASA lifecycle, Store writer, SQL, or peer writes.
/// Legacy SteamCheck schedules are strictly inert under the manual-only
/// SteamCMD policy; no background version lookup is allowed.
/// </summary>
public sealed class DesktopRecurringReadOnlySupervisor : IDisposable
{
    private readonly ClusterService cluster;
    private readonly RecurringReadOnlyRunner journal;
    private readonly Func<LocalMap, ProcessCheck> inspect;
    private readonly CancellationTokenSource cancel=new();
    private Task? loop;
    private int started;
    private int disposed;
    private string state="not-started";
    public string RuntimeState => Volatile.Read(ref state);

    public DesktopRecurringReadOnlySupervisor(ClusterService cluster,
        Func<LocalMap,ProcessCheck>? processInspector=null,
        Func<LocalMap,CancellationToken,Task<AsaSteamBuildCheck>>? steamPublished=null)
    {
        this.cluster=cluster??throw new ArgumentNullException(nameof(cluster));
        // Profile-local journal, not inside an ASA installation, shared Store
        // or external MariaDB directory. The Core validates fixed disk/link.
        journal=new RecurringReadOnlyRunner(cluster.Store.Root);
        inspect=processInspector??cluster.ObserveLocalProcessesReadOnly;
        // Keep the legacy injectable constructor contract, but never retain
        // or invoke an unattended Steam metadata callback.
        _=steamPublished;
    }

    public RecurringJobReceipt[] Recent(int count=30)=>journal.Recent(count);
    public IReadOnlyDictionary<Guid,DateTimeOffset> DueDates()=>journal.DueDates();

    public RecurringJobReceipt[] TickOnce(DateTimeOffset now)
    {
        if(Volatile.Read(ref disposed)!=0)
            throw new ObjectDisposedException(nameof(DesktopRecurringReadOnlySupervisor));
        var schedules=cluster.SnapshotRecurringSchedules()
            .Where(x=>x.Kind!=OperationalTaskKind.SteamCheck).ToArray();
        if(schedules.Length==0)return Array.Empty<RecurringJobReceipt>();
        var maps=cluster.Config.LocalMaps.ToDictionary(m=>m.Id);
        return journal.Tick(schedules,maps.Keys.ToHashSet(),now,
            job=>!maps.TryGetValue(job.Map,out var map)||map.Maintenance,
            job=>{
                if(!maps.TryGetValue(job.Map,out var map))
                    throw new UnauthorizedAccessException("Read-only schedule map is not locally owned");
                if(map.Maintenance)
                    throw new InvalidOperationException("Maintenance disables automatic checks");
                switch(job.Kind)
                {
                    case OperationalTaskKind.Observation:
                    {
                        var state=inspect(map);
                        if(!state.Running&&!state.ConfirmedStopped)
                            throw new InvalidDataException("No verified local process observation");
                        break;
                    }
                    case OperationalTaskKind.ModCheck:
                        _=ModStateController.Read(map); // configured mods, NOT live loaded mods
                        break;
                    case OperationalTaskKind.ApiCheck:
                        if(!AsaApiInspector.Inspect(map.InstallationPath,inspect(map)).Complete)
                            throw new InvalidDataException("ASA API file integrity not confirmed");
                        break;
                    case OperationalTaskKind.SteamCheck:
                        // Defense in depth if the projection filter changes:
                        // never call SteamCMD from the recurring supervisor.
                        throw new InvalidOperationException("UM-STEAM-CHECK-MANUAL-ONLY");
                    default:
                        throw new UnauthorizedAccessException("Autonomous lifecycle operation forbidden");
                }
            });
    }

    public void Start()
    {
        if(Volatile.Read(ref disposed)!=0)
            throw new ObjectDisposedException(nameof(DesktopRecurringReadOnlySupervisor));
        if(Interlocked.Exchange(ref started,1)!=0)return;
        Volatile.Write(ref state,"active");
        loop=Task.Run(async()=>{
            using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                do
                {
                    try
                    {
                        // All disk scans run on the background pool, never the
                        // WinForms UI thread. Errors stay visible via status;
                        // no automatic retries of claimed operations.
                        TickOnce(DateTimeOffset.UtcNow);
                        Volatile.Write(ref state,"active");
                    }
                    catch(Exception)
                    {
                        Volatile.Write(ref state,"journal-or-inspection-error");
                    }
                }
                while(await timer.WaitForNextTickAsync(cancel.Token));
            }
            catch(OperationCanceledException){/* Normal window exit */ }
            finally{Volatile.Write(ref state,"stopped");}
        });
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        cancel.Cancel();
        // Do not synchronously wait on a running local scan while closing the
        // Manager. A WinForms exit must never terminate an independent ASA.
        _=loop;
    }
}
