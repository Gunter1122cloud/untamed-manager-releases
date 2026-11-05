using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public enum SteamMaintenanceStage
{
    Preflight, NotifyPlayers, SaveWorld, Backup, StopVerified,
    SteamCmd, VerifyBuild, Restart, Readiness, Completed
}
public sealed record SteamMaintenanceEvidence(Guid MapId,string OldBuild,
    string NewBuild,string BackupDigest,bool ProtectedFilesIntact,
    DateTimeOffset CompletedUtc);
public sealed record SteamMaintenanceReceipt(Guid MapId,string OldBuild,
    string NewBuild,bool WasRunning,bool Restarted,
    DateTimeOffset FinishedUtc,string BackupDigest);

/// <summary>
/// Plug-in boundary for an independently certified ASA adapter. Its production
/// implementation must correlate real ASA SaveComplete and post-flush evidence,
/// verify a stopped-map backup, protect the entire installation and report an
/// authoritative Steam build/EXE after app_update. A fake adapter is permitted
/// only through the explicit isolated test entry below.
/// </summary>
public interface ISteamVerifiedMaintenanceAdapter : ILocalServerSession
{
    LocalMap Map { get; }
    Task<string> CreateVerifiedBackup(CancellationToken ct);
    Task<SteamMaintenanceEvidence> UpdateAndVerifySteamAsync(CancellationToken ct);
    Task<AsaReadinessSnapshot> ProbeProductionReadiness(CancellationToken ct);
}

public static class SteamVerifiedMaintenancePipeline
{
    public static Task<SteamMaintenanceReceipt> ExecuteAsync(
        ISteamVerifiedMaintenanceAdapter adapter,bool restartPreviouslyRunning,
        TimeSpan shutdownTimeout,TimeSpan readinessTimeout,
        IProgress<SteamMaintenanceStage>? progress=null,CancellationToken ct=default)
    {
        // Critically, successful FakeAsa/SteamCMD lab tests never unlock this.
        var allowed=ReleaseCapabilityGate.Check(ManagerOperation.LiveSteamUpdate,
            adapter!=null&&adapter.Map.Id!=Guid.Empty);
        if(!allowed.Allowed)throw new InvalidOperationException(
            "Production SaveComplete/Stop/Store and cluster update safety not certified");
        return ExecuteCoreAsync(adapter,restartPreviouslyRunning,
            shutdownTimeout,readinessTimeout,progress,ct);
    }

    internal static Task<SteamMaintenanceReceipt> RunSyntheticFixtureAsync(
        ISteamVerifiedMaintenanceAdapter adapter,bool restartPreviouslyRunning,
        TimeSpan shutdownTimeout,TimeSpan readinessTimeout,
        IProgress<SteamMaintenanceStage>? progress=null,CancellationToken ct=default)
    {
        if(adapter==null || adapter.Map.Id==Guid.Empty ||
           !adapter.Map.InstallationPath.StartsWith(
               Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase) ||
           !adapter.Map.InstallationPath.Contains("Untamed-Integration-",
               StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only an isolated FakeAsa update pipeline may run");
        return ExecuteCoreAsync(adapter,restartPreviouslyRunning,
            shutdownTimeout,readinessTimeout,progress,ct);
    }
    private static async Task<SteamMaintenanceReceipt> ExecuteCoreAsync(
        ISteamVerifiedMaintenanceAdapter adapter,bool restartPreviouslyRunning,
        TimeSpan shutdownTimeout,TimeSpan readinessTimeout,
        IProgress<SteamMaintenanceStage>? progress,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if(shutdownTimeout<=TimeSpan.Zero||shutdownTimeout>TimeSpan.FromMinutes(10)||
           readinessTimeout<=TimeSpan.Zero||readinessTimeout>TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        var map=adapter.Map;
        ct.ThrowIfCancellationRequested();
        progress?.Report(SteamMaintenanceStage.Preflight);
        var initial=adapter.Check();
        if(initial.Running && (initial.Identity==null||initial.ConfirmedStopped))
            throw new InvalidOperationException("Running ASA process is not independently identified");
        if(!initial.Running && (!initial.ConfirmedStopped||
            initial.Identity!=null||initial.LoaderIdentity!=null))
            throw new InvalidOperationException("Uncertain stopped state: Steam update blocked");
        bool wasRunning=initial.Running;
        string? oldBuild=AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath);
        if(string.IsNullOrWhiteSpace(oldBuild))
            throw new InvalidDataException("Local Steam build ID is not verified");
        if(wasRunning)
        {
            progress?.Report(SteamMaintenanceStage.NotifyPlayers);
            // SafeLifecycle sends a built-in ServerChat warning BEFORE SaveWorld.
            // It refuses DoExit unless its adapter proves SaveCompleted().
            progress?.Report(SteamMaintenanceStage.SaveWorld);
            await SafeLifecycle.Stop(adapter,false,shutdownTimeout,ct);
        }
        ct.ThrowIfCancellationRequested();
        var stopped=adapter.Check();
        if(stopped.Running||!stopped.ConfirmedStopped||
           stopped.Identity!=null||stopped.LoaderIdentity!=null)
            throw new InvalidOperationException("ASA termination was not verified; update blocked");
        progress?.Report(SteamMaintenanceStage.StopVerified);
        progress?.Report(SteamMaintenanceStage.Backup);
        string backup=await adapter.CreateVerifiedBackup(ct);
        if(backup.Length!=64 || !backup.All(Uri.IsHexDigit))
            throw new InvalidDataException("Backup receipt is not a verified SHA-256 manifest");
        ct.ThrowIfCancellationRequested();
        progress?.Report(SteamMaintenanceStage.SteamCmd);
        var evidence=await adapter.UpdateAndVerifySteamAsync(ct);
        if(evidence.MapId!=map.Id || evidence.OldBuild!=oldBuild||
           evidence.NewBuild.Length is <1 or >20 ||
           !evidence.NewBuild.All(char.IsAsciiDigit) ||
           evidence.BackupDigest!=backup || !evidence.ProtectedFilesIntact ||
           evidence.CompletedUtc>DateTimeOffset.UtcNow.AddSeconds(3))
            throw new InvalidDataException("SteamCMD/build/protected data evidence is incomplete");
        progress?.Report(SteamMaintenanceStage.VerifyBuild);
        string? onDisk=AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath);
        if(onDisk!=evidence.NewBuild ||
           !LocalAsaInstallLocator.Probe(map.InstallationPath).HasServerExecutable)
            throw new InvalidDataException("On-disk Steam manifest/build or ASA server executable mismatch");
        // A previously stopped map always remains stopped regardless of the
        // user's restart preference. There is no retry-on-start-failure loop.
        bool restart=wasRunning&&restartPreviouslyRunning;
        if(restart)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(SteamMaintenanceStage.Restart);
            adapter.Start();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(readinessTimeout);
            progress?.Report(SteamMaintenanceStage.Readiness);
            bool ready=false;
            while(!ready)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var current=adapter.Check();
                if(!current.Running || current.ConfirmedStopped || current.Identity==null)
                    throw new InvalidOperationException("Restarted ASA process exited or cannot be verified");
                var observed=await adapter.ProbeProductionReadiness(deadline.Token);
                ready=observed.MapId==map.Id&&observed.ProductionReady &&
                    observed.ObservedUtc>=evidence.CompletedUtc;
                if(!ready)await Task.Delay(250,deadline.Token);
            }
        }
        else
        {
            var final=adapter.Check();
            if(final.Running||!final.ConfirmedStopped)
                throw new InvalidOperationException("A stopped map did not remain stopped");
        }
        progress?.Report(SteamMaintenanceStage.Completed);
        return new(map.Id,oldBuild,evidence.NewBuild,wasRunning,restart,
            DateTimeOffset.UtcNow,backup);
    }
}
