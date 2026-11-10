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
/// An explicitly scheduled SteamCheck may run SteamCMD app_info_print only
/// and persist the comparison to the Manager-local status journal.
/// </summary>
public sealed class DesktopRecurringReadOnlySupervisor : IDisposable
{
    private readonly ClusterService cluster;
    private readonly RecurringReadOnlyRunner journal;
    private readonly Func<LocalMap, ProcessCheck> inspect;
    private readonly Func<LocalMap,CancellationToken,Task<AsaSteamBuildCheck>> publishedSteam;
    private readonly SteamBuildStatusStore steamStatus;
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
        steamStatus=new SteamBuildStatusStore(cluster.Store.Root);
        publishedSteam=steamPublished??((map,ct)=>AsaSteamBuildCatalog.CheckAsync(
            cluster.Config.SteamCmdExecutable,map.InstallationPath,"public",
            TimeSpan.FromSeconds(85),ct));
    }

    public RecurringJobReceipt[] Recent(int count=30)=>journal.Recent(count);
    public IReadOnlyDictionary<Guid,DateTimeOffset> DueDates()=>journal.DueDates();

    public RecurringJobReceipt[] TickOnce(DateTimeOffset now)
    {
        if(Volatile.Read(ref disposed)!=0)
            throw new ObjectDisposedException(nameof(DesktopRecurringReadOnlySupervisor));
        var schedules=cluster.SnapshotRecurringSchedules();
        if(schedules.Length==0)return Array.Empty<RecurringJobReceipt>();
        var maps=cluster.Config.LocalMaps.ToDictionary(m=>m.Id);
        // Multiple due maps using a single ASA installation share one
        // authoritative Steam appinfo query in this tick (not an update).
        // Cache the Task (including a failed result) so two due maps on
        // one installation never start two appinfo processes in a tick.
        var publicCache=new Dictionary<string,Task<AsaSteamBuildCheck>>(
            StringComparer.OrdinalIgnoreCase);
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
                    {
                        // A due SteamCheck now compares *published* and installed
                        // build IDs. The former local-manifest-only check could
                        // never report whether an ASA update was available.
                        var pending=new SteamSerialMaintenanceQueue(
                            cluster.Store.Root,cluster.Config.LocalHostId).Observe();
                        if(pending!=null && pending.Phase is
                           (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress or
                            SteamQueuePhase.NeedsOperator))
                            throw new InvalidOperationException(
                                "UM-STEAM-READONLY-CHECK-BLOCKED-BY-UPDATE");
                        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
                        string root=Path.TrimEndingDirectorySeparator(
                            Path.GetFullPath(map.InstallationPath));
                        string? installed=AsaSteamBuildCatalog.InstalledBuild(root);
                        if(string.IsNullOrWhiteSpace(installed))
                            throw new InvalidDataException("Installed Steam Build ID unknown");
                        if(!publicCache.TryGetValue(root,out var query))
                        {
                            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(
                                cancel.Token);
                            deadline.CancelAfter(TimeSpan.FromSeconds(90));
                            try
                            { query=publishedSteam(map,deadline.Token); }
                            catch(Exception ex)
                            { query=Task.FromException<AsaSteamBuildCheck>(ex); }
                            query??=Task.FromException<AsaSteamBuildCheck>(
                                new InvalidDataException("Missing Steam metadata task"));
                            publicCache.Add(root,query);
                            // The query is fully awaited before disposing the
                            // linked cancellation source.
                            var checkedReport=query.GetAwaiter().GetResult();
                            ValidateAndPersist(root,installed,map,checkedReport);
                            break;
                        }
                        var report=query.GetAwaiter().GetResult();
                        ValidateAndPersist(root,installed,map,report);
                        break;
                    }
                    default:
                        throw new UnauthorizedAccessException("Autonomous lifecycle operation forbidden");
                }
            });
    }

    private void ValidateAndPersist(string root,string installed,LocalMap map,
        AsaSteamBuildCheck report)
    {
                        if(report==null || report.Branch!="public" ||
                           !string.Equals(root,Path.TrimEndingDirectorySeparator(
                               Path.GetFullPath(report.InstallationRoot)),
                               StringComparison.OrdinalIgnoreCase) ||
                           !string.Equals(installed,report.InstalledBuild,
                               StringComparison.Ordinal) ||
                           report.State is not (AsaSteamBuildState.Current or
                               AsaSteamBuildState.UpdateAvailable) ||
                           report.CheckedUtc>DateTimeOffset.UtcNow.AddMinutes(1) ||
                           DateTimeOffset.UtcNow-report.CheckedUtc>TimeSpan.FromMinutes(10))
                            throw new InvalidDataException(
                                "UM-STEAM-READONLY-PUBLISHED-BUILD-UNVERIFIED");
                        steamStatus.SaveCheck(map,report);
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
