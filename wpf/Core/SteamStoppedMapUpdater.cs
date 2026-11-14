using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

/// <summary>
/// A REAL, opt-in, stopped-server-only SteamCMD update. No RCON, DoExit,
/// forced stop, ASA launch, Store-v1 mutation or remote Manager lease is used.
/// Live servers are never implicitly stopped; only a registered, independently
/// confirmed STOPPED local map may use this operation. On failure the durable
/// local queue and per-installation operation remain NeedsOperator.
/// </summary>
public static class SteamStoppedMapUpdater
{
    public sealed record Receipt(Guid MapId,string InstallRoot,string OldBuild,
        string NewBuild,string BackupDirectory,DateTimeOffset VerifiedUtc,
        bool ProtectedFilesIntact);

    /// <summary>
    /// Shared guard for both the single-map and bulk SteamCMD entry points.
    /// A stopped non-Store map cannot authorize modification of binaries also
    /// claimed by a historical/new Store sibling or an overlapping install.
    /// This checks ALL registered local maps, not only the UI selection.
    /// </summary>
    public static void DemandSafeLocalInstallation(ClusterService cluster,LocalMap map,
        Func<LocalMap,ProcessCheck>? originalStoppedObserver=null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(map);
        if(map.Id==Guid.Empty ||
           !cluster.Config.LocalMaps.Any(x=>ReferenceEquals(x,map)) ||
           string.IsNullOrWhiteSpace(map.InstallationPath) ||
           !Path.IsPathFullyQualified(map.InstallationPath) ||
           map.InstallationPath.StartsWith(@"\\",StringComparison.Ordinal))
            throw new UnauthorizedAccessException("UM-STEAM-LOCAL-INSTALL-REQUIRED");

        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(map.InstallationPath));
        int owners=0;
        foreach(var sibling in cluster.Config.LocalMaps)
        {
            if(string.IsNullOrWhiteSpace(sibling.InstallationPath))
                continue;
            var other=Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sibling.InstallationPath));
            if(!string.Equals(root,other,StringComparison.OrdinalIgnoreCase))
            {
                if(root.StartsWith(other+Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase) ||
                   other.StartsWith(root+Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "UM-STEAM-NESTED-INSTALL-OWNERSHIP");
                continue;
            }
            owners++;
            bool original=sibling.HistoricalStoreProtected;
            // Only the registered original Kova import may opt into native
            // offline Steam maintenance. No other Store profile or synthetic
            // observer can acquire this capability from a React request.
            if(original)
            {
                if(originalStoppedObserver==null||
                   !map.HistoricalStoreProtected||
                   sibling.ObservedReadOnlyImported||
                   sibling.HistoricalLaunchModeNeedsApproval||sibling.Maintenance||
                   string.IsNullOrWhiteSpace(sibling.HistoricalMapKey)||
                   sibling.HistoricalArchiveSha256?.Length!=64||
                   !sibling.HistoricalArchiveSha256.All(Uri.IsHexDigit)||
                   !string.IsNullOrWhiteSpace(sibling.StoreTestRoot)||
                   !string.IsNullOrWhiteSpace(sibling.FreshStoreClusterId)||
                   !string.IsNullOrWhiteSpace(sibling.FreshStoreTransferDirectory)||
                   !string.IsNullOrWhiteSpace(sibling.Arguments)||
                   !string.IsNullOrWhiteSpace(sibling.ApiLoaderArguments))
                    throw new UnauthorizedAccessException(
                        "UM-STEAM-ORIGINAL-STORE-IDENTITY-INVALID");
            }
            else if(FreshStoreEnrollmentPolicy.IsEnrolled(sibling))
            {
                // Newly enrolled Store maps retain their own exact
                // ClusterRegistry membership and transfer file-ID. Updating
                // ONLY the stopped local ASA install is safe after Core
                // independently rechecks those source-pinned invariants.
                // Historical Store-v1 remains on its separate native path.
                if(!ReferenceEquals(sibling,map) ||
                   map.HistoricalStoreProtected ||
                   sibling.ObservedReadOnlyImported || sibling.Maintenance)
                    throw new UnauthorizedAccessException(
                        "UM-STEAM-NEW-STORE-SHARED-OWNER-DENIED");
                FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,sibling);
            }
            else if(map.HistoricalStoreProtected||
               sibling.ObservedReadOnlyImported||sibling.Maintenance||
               !string.IsNullOrWhiteSpace(sibling.HistoricalArchiveSha256)||
               !string.IsNullOrWhiteSpace(sibling.HistoricalMapKey)||
               !string.IsNullOrWhiteSpace(sibling.StoreTestRoot)||
               !string.IsNullOrWhiteSpace(sibling.FreshStoreClusterId)||
               !string.IsNullOrWhiteSpace(sibling.FreshStoreTransferDirectory)||
               KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(sibling))
                throw new UnauthorizedAccessException(
                    "UM-STEAM-SHARED-STORE-OR-RECOVERY-PROTECTED");
            if(KovaCompatibleWorldBackup.InstallationRecoveryRequired(sibling))
                throw new UnauthorizedAccessException(
                    "UM-STEAM-RECOVERY-MUST-COMPLETE-FIRST");
            var current=original?
                originalStoppedObserver!(sibling):cluster.Check(sibling);
            if(current.Running || !current.ConfirmedStopped ||
               current.Identity!=null || current.LoaderIdentity!=null)
                throw new InvalidOperationException(
                    "UM-STEAM-SHARED-INSTALL-NOT-STOPPED");
        }
        if(owners==0)
            throw new InvalidDataException("UM-STEAM-INSTALL-OWNER-MISSING");
    }

    public static Task<Receipt> UpdateAsync(ClusterService cluster,LocalMap map,
        string steamCmdExecutable,long downloadReserveBytes,
        TimeSpan timeout,IProgress<SteamMaintenanceStage>? progress=null,
        CancellationToken ct=default) =>
        RunCore(cluster,map,steamCmdExecutable,downloadReserveBytes,
            timeout,null,progress,false,ct);

    // The trusted native Windows host supplies this independently observed
    // original Kova stopped-family check; no renderer/RCON flag can impersonate
    // it. The existing backup, SteamCMD execution, queue, file hashes and
    // recovery journal remain the same as normal ASA maintenance.
    public static Task<Receipt> UpdateOriginalStoppedAsync(
        ClusterService cluster,LocalMap map,string steamCmdExecutable,
        long downloadReserveBytes,TimeSpan timeout,
        Func<LocalMap,ProcessCheck> originalStoppedObserver,
        IProgress<SteamMaintenanceStage>? progress=null,CancellationToken ct=default,
        Func<LocalMap,ProcessCheck>? originalStoppedObserverUnderLease=null)
    {
        if(map==null||!map.HistoricalStoreProtected||originalStoppedObserver==null)
            throw new UnauthorizedAccessException(
                "UM-STEAM-ORIGINAL-STORE-NATIVE-OBSERVER-REQUIRED");
        return RunCore(cluster,map,steamCmdExecutable,downloadReserveBytes,
            timeout,null,progress,false,ct,originalStoppedObserver,
            originalStoppedObserverUnderLease);
    }

    internal static Task<Receipt> RunFakeFixtureAsync(
        ClusterService cluster,LocalMap map,string placeholderSteam,
        string fakeExecutable,long downloadReserveBytes,TimeSpan timeout,
        IProgress<SteamMaintenanceStage>? progress=null,CancellationToken ct=default)
    {
        var root=Path.GetFullPath(map.InstallationPath);
        string temp=Path.GetFullPath(Path.GetTempPath());
        if(!root.StartsWith(temp,StringComparison.OrdinalIgnoreCase) ||
           !root.Contains("Untamed-Integration-",StringComparison.OrdinalIgnoreCase) ||
           !Path.GetFileName(fakeExecutable).Equals("FakeSteamCMD.exe",
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only isolated synthetic Steam update fixture supported");
        return RunCore(cluster,map,placeholderSteam,downloadReserveBytes,
            timeout,fakeExecutable,progress,false,ct);
    }

    /// <summary>
    /// One-click local maintenance for all selected, CONFIRMED STOPPED ASA
    /// installations. A shared installation is updated only once. Every map
    /// is processed by the same machine-local journaled serial queue; the
    /// next install is never touched until the preceding job passes.
    /// Running or uncertain maps are refused, NOT stopped automatically.
    /// </summary>
    public static Task<IReadOnlyList<Receipt>> UpdateAllStoppedAsync(
        ClusterService cluster,IReadOnlyList<LocalMap> candidates,
        string steamExecutable,long downloadReserveBytes,TimeSpan timeout,
        IProgress<SteamQueueProgress>? queueProgress=null,
        IProgress<SteamMaintenanceStage>? stageProgress=null,CancellationToken ct=default,
        Func<LocalMap,ProcessCheck>? originalStoppedObserver=null,
        Func<LocalMap,ProcessCheck>? originalStoppedObserverUnderLease=null) =>
        BatchCore(cluster,candidates,steamExecutable,downloadReserveBytes,
            timeout,null,queueProgress,stageProgress,ct,
            originalStoppedObserver,originalStoppedObserverUnderLease);

    internal static Task<IReadOnlyList<Receipt>> RunAllFakeFixtureAsync(
        ClusterService cluster,IReadOnlyList<LocalMap> candidates,
        string placeholderSteam,string fakeExecutable,long downloadReserveBytes,
        TimeSpan timeout,CancellationToken ct=default)
    {
        if(candidates==null||candidates.Any(m=>m==null ||
           !m.InstallationPath.Contains("Untamed-Integration-",StringComparison.OrdinalIgnoreCase))||
           !Path.GetFileName(fakeExecutable).Equals("FakeSteamCMD.exe",StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Batch fixture must use isolated temp ASA roots");
        return BatchCore(cluster,candidates,placeholderSteam,downloadReserveBytes,
            timeout,fakeExecutable,null,null,ct);
    }

    private static async Task<IReadOnlyList<Receipt>> BatchCore(
        ClusterService cluster,IReadOnlyList<LocalMap> candidates,
        string steamExecutable,long reserve,TimeSpan timeout,
        string? fakeExecutable,IProgress<SteamQueueProgress>? queueProgress,
        IProgress<SteamMaintenanceStage>? stageProgress,CancellationToken ct,
        Func<LocalMap,ProcessCheck>? originalStoppedObserver=null,
        Func<LocalMap,ProcessCheck>? originalStoppedObserverUnderLease=null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(candidates);
        if(candidates.Count==0 || candidates.Count>25 ||
           candidates.Any(m=>m==null || !cluster.Config.LocalMaps.Any(x=>ReferenceEquals(x,m))))
            throw new UnauthorizedAccessException("Bulk update accepts registered LOCAL maps only");
        // Multiple map processes may share one ASA file installation.
        // SteamCMD app_update updates the INSTALLATION, not the map.
        var selected=candidates.GroupBy(m=>Path.GetFullPath(m.InstallationPath)
            .TrimEnd(Path.DirectorySeparatorChar),StringComparer.OrdinalIgnoreCase)
            .Select(g=>g.First()).ToArray();
        // The original Store-v1 map uses the existing offline ASA update
        // engine, but ONLY a trusted native Windows process-family observer
        // may prove it stopped. Never infer control from peer status or a UI flag.
        if(selected.Any(m=>m.HistoricalStoreProtected)&&
           (originalStoppedObserver==null||originalStoppedObserverUnderLease==null))
            throw new UnauthorizedAccessException(
                "UM-STEAM-BATCH-ORIGINAL-STOPPED-OBSERVER-REQUIRED");
        ProcessCheck ObserveStopped(LocalMap participant)=>participant.HistoricalStoreProtected?
            originalStoppedObserver?.Invoke(participant)??
                throw new UnauthorizedAccessException(
                    "UM-STEAM-BATCH-ORIGINAL-STOPPED-OBSERVER-REQUIRED"):
            cluster.Check(participant);
        // Reject protected or mixed-owner installations before writing the
        // durable queue journal, even if some siblings were not selected.
        foreach(var selectedMap in selected)
            DemandSafeLocalInstallation(cluster,selectedMap,originalStoppedObserver);
        var receipts=new List<Receipt>();
        var queue=new SteamSerialMaintenanceQueue(cluster.Store.Root,cluster.Config.LocalHostId);
        await queue.ExecuteAsync(selected,cluster.Config.LocalMaps,ObserveStopped,
            async (map,token)=>{
                var item=await RunCore(cluster,map,steamExecutable,reserve,
                    timeout,fakeExecutable,stageProgress,true,token,
                    originalStoppedObserver,originalStoppedObserverUnderLease);
                receipts.Add(item);
            },queueProgress,ct);
        return receipts;
    }

    private static async Task<Receipt> RunCore(ClusterService cluster,LocalMap map,
        string steamExecutable,long reserve,TimeSpan timeout,string? fakeExecutable,
        IProgress<SteamMaintenanceStage>? progress,bool insideQueue,CancellationToken ct,
        Func<LocalMap,ProcessCheck>? originalStoppedObserver=null,
        Func<LocalMap,ProcessCheck>? originalStoppedObserverUnderLease=null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(map);
        if(map.Id==Guid.Empty ||
           !cluster.Config.LocalMaps.Any(m=>ReferenceEquals(m,map)))
            throw new UnauthorizedAccessException("Only a registered local map can be updated");
        if(timeout<TimeSpan.FromMinutes(1)||timeout>TimeSpan.FromHours(5))
            throw new InvalidDataException("SteamCMD timeout outside safe bounds");
        if(reserve<1024L*1024*1024 || reserve>500L*1024*1024*1024)
            throw new InvalidDataException("SteamCMD download reserve outside safe bounds");
        string root=Path.GetFullPath(map.InstallationPath).TrimEnd(Path.DirectorySeparatorChar);
        // Only a real operation lease acquired by this Core pipeline can
        // select the native observer that ignores its OWN pending journal.
        // Preview, initial guard and the pre-lease backup use the strict gate.
        bool ownsUpdateLease=false;
        Func<LocalMap,ProcessCheck>? CurrentObserver() =>
            ownsUpdateLease?originalStoppedObserverUnderLease:originalStoppedObserver;
        ProcessCheck ObserveStopped(LocalMap participant)=>
            participant.HistoricalStoreProtected?
                CurrentObserver()?.Invoke(participant)??
                    throw new UnauthorizedAccessException(
                        "UM-STEAM-ORIGINAL-STORE-NATIVE-OBSERVER-REQUIRED"):
                cluster.Check(participant);
        DemandSafeLocalInstallation(cluster,map,CurrentObserver());
        new AsaManualRestartJournal(root).DemandNoPending();
        if(root.StartsWith(@"\\",StringComparison.Ordinal)||
           !AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidDataException("ASA server executable is not a verified local installation");

        // All maps using this installation must be locally registered and
        // confirmed stopped, even when only one map card was clicked.
        var group=cluster.Config.LocalMaps.Where(m=>
            !string.IsNullOrWhiteSpace(m.InstallationPath) &&
            string.Equals(Path.GetFullPath(m.InstallationPath)
                .TrimEnd(Path.DirectorySeparatorChar),root,StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if(group.Length==0)throw new InvalidDataException("ASA installation ownership is missing");
        void VerifyStopped()
        {
            // Recheck the entire live local registry at every irreversible
            // boundary; a newly registered Store sibling cannot be hidden
            // behind the initial preflight snapshot.
            DemandSafeLocalInstallation(cluster,map,CurrentObserver());
            foreach(var participant in group)
            {
                var state=ObserveStopped(participant);
                if(state.Running || !state.ConfirmedStopped ||
                   state.Identity!=null || state.LoaderIdentity!=null)
                    throw new InvalidOperationException(
                        "Steam update blocked: one map still runs or cannot be confirmed stopped");
            }
        }
        progress?.Report(SteamMaintenanceStage.Preflight);
        VerifyStopped();
        var plan=SteamUpdateDryRun.Plan(steamExecutable,root,
            cluster.Config.LocalMaps.ToArray(),ObserveStopped,reserve);
        if(!plan.DryRunAllowed || string.IsNullOrWhiteSpace(plan.InstalledBuild))
            throw new InvalidOperationException("Steam update preflight did not verify stopped ASA and its local build");

        // Queue is machine-local and serializes updates on this Windows PC.
        // UK-HOME-01 and UK-HOME-02 have independent OS namespaces.
        Receipt? result=null;
        Func<LocalMap,CancellationToken,Task> work=async (_,token)=>{
                token.ThrowIfCancellationRequested();
                VerifyStopped();
                string backupRoot=Path.Combine(cluster.Store.Root,
                    "verified-pre-steam-update");
                string destination=Path.Combine(backupRoot,map.Id.ToString("N")+"-"+
                    Guid.NewGuid().ToString("N"));
                // A previously used map must have a real world/profile save.
                // This is not a blind file update without a recoverable copy.
                var scope=LocalBackupScope.WorldAndProfiles;
                string ini=Path.Combine(root,"ShooterGame","Saved","Config","WindowsServer");
                if(File.Exists(Path.Combine(ini,"Game.ini"))||
                   File.Exists(Path.Combine(ini,"GameUserSettings.ini")))
                    scope|=LocalBackupScope.Ini;
                string binaryRoot=Path.Combine(root,"ShooterGame","Binaries","Win64");
                string apiDir=Path.Combine(binaryRoot,"ArkApi");
                string disabledDir=Path.Combine(binaryRoot,"UntamedDisabledPlugins");
                bool hasFiles(string directory)=>Directory.Exists(directory)&&
                    Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).Any();
                if(hasFiles(apiDir)||hasFiles(disabledDir)||
                   File.Exists(Path.Combine(binaryRoot,"AsaApiLoader.exe"))||
                   File.Exists(Path.Combine(binaryRoot,"Version.dll"))||
                   File.Exists(Path.Combine(root,"Version.dll")))
                    scope|=LocalBackupScope.Plugins;
                VerifyStopped();
                progress?.Report(SteamMaintenanceStage.Backup);
                // The verified backup is created OUTSIDE the install root.
                // LocalBackupWorkflow hashes source AND destination and fails
                // on changed memberships. Missing world saves reject update.
                var backup=LocalBackupWorkflow.Create(map,ObserveStopped(map),
                    destination,scope,checkCurrent:()=>ObserveStopped(map),
                    limits:BackupResourcePolicy.ForManagerProfile(cluster.Store.Root));
                var manifestFile=Path.Combine(backup.PackageDirectory,"manifest.json");
                if(!File.Exists(manifestFile))
                    throw new InvalidDataException("Verified pre-update backup manifest is missing");
                string backupDigest;
                using(var stream=File.OpenRead(manifestFile))
                    backupDigest=Convert.ToHexString(SHA256.HashData(stream));
                VerifyStopped();

                // Cross-profile, per-installation lock; no remote lease or
                // shared operation ID. Hold it across SteamCMD and file checks.
                using var operation=new UntamedOperationCoordinator().Acquire(root,
                    UntamedOperationKind.Update);
                ownsUpdateLease=true;
                VerifyStopped();
                var before=SteamProtectedFiles.Snapshot(root);
                string oldBuild=AsaSteamBuildCatalog.InstalledBuild(root) ??
                    throw new InvalidDataException("Missing local ASA Steam build");
                if(oldBuild!=plan.InstalledBuild)
                    throw new InvalidDataException("Steam build changed after preflight");
                token.ThrowIfCancellationRequested();
                operation.MarkMutationStarted();
                progress?.Report(SteamMaintenanceStage.SteamCmd);
                await RunSteamAsync(fakeExecutable??steamExecutable,root,
                    timeout,fakeExecutable!=null,VerifyStopped,token);
                VerifyStopped();
                progress?.Report(SteamMaintenanceStage.VerifyBuild);
                var after=SteamProtectedFiles.Snapshot(root);
                if(!SteamProtectedFiles.Identical(before,after))
                    throw new InvalidDataException(
                        "SteamCMD altered protected ASA saves/config/plugin data; STOP, restore only after operator review");
                var next=AsaSteamBuildCatalog.InstalledBuild(root);
                if(next==null || !AsaReadyProbe.IsRegisteredServerBinary(map))
                    throw new InvalidDataException(
                        "SteamCMD completed but the on-disk manifest or ASA EXE is invalid");
                // No Start, RCON or SaveWorld is invoked here.
                VerifyStopped();
                operation.Complete();
                var receipt=new SteamMaintenanceReceipt(map.Id,oldBuild,next,
                    WasRunning:false,Restarted:false,
                    FinishedUtc:DateTimeOffset.UtcNow,BackupDigest:backupDigest);
                // Successful version timestamp is contingent on a fully
                // validated receipt, not only SteamCMD exit code.
                new SteamBuildStatusStore(cluster.Store.Root).RecordVerifiedUpdate(map,receipt);
                progress?.Report(SteamMaintenanceStage.Completed);
                result=new(map.Id,root,oldBuild,next,backup.PackageDirectory,
                    receipt.FinishedUtc,true);
            };
        if(insideQueue)await work(map,ct);
        else
        {
            var queue=new SteamSerialMaintenanceQueue(cluster.Store.Root,cluster.Config.LocalHostId);
            await queue.ExecuteAsync(new[]{map},cluster.Config.LocalMaps,ObserveStopped,
                work,ct:ct);
        }
        return result ?? throw new InvalidOperationException("No verified Steam update receipt was committed");
    }

    // Source-compatible, bounded SteamCMD first-run retry. A failed network,
    // disk or SteamCMD process is NEVER retried blindly. Synthetic/native CI
    // fixtures never auto-retry, and no retry is a second operator job.
    internal static bool ShouldRetryMissingConfiguration(
        int attempt,int exitCode,string output,bool synthetic) =>
        !synthetic&&attempt is >=0 and <2&&exitCode!=0&&
        SteamFreshInstall.IsTransientMissingSteamConfiguration(output);

    private static async Task RunSteamAsync(string exe,string install,
        TimeSpan timeout,bool synthetic,Action verifyStopped,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verifyStopped);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        // Reuse the working original SteamCMD bootstrap implementation. A
        // newly installed steamcmd.exe may self-update and exit before it can
        // process app_update; its first +quit happens under the same approved
        // local Steam maintenance lease and never starts/stops an ASA server.
        if(!synthetic)
        {
            verifyStopped();
            await SteamFreshInstall.BootstrapSteamCmdAsync(exe,null,deadline.Token);
            verifyStopped();
            _=SteamCmdPreflight.Inspect(exe);
        }
        static async Task<string> Capture(StreamReader reader,CancellationToken token)
        {
            var output=new StringBuilder();
            var buffer=new char[4096];
            while(true)
            {
                int read=await reader.ReadAsync(buffer.AsMemory(),token);
                if(read==0)break;
                if(output.Length+read>2_000_000)
                    throw new InvalidDataException("SteamCMD log exceeds safe bound");
                output.Append(buffer,0,read);
            }
            return output.ToString();
        }
        for(int attempt=0;attempt<3;attempt++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            // No subsequent SteamCMD process can start unless the full local
            // ASA/Loader process family and its shared-install siblings are
            // independently verified STOPPED again.
            verifyStopped();
            var psi=new ProcessStartInfo(exe)
            {
                UseShellExecute=false,CreateNoWindow=true,
                WorkingDirectory=Path.GetDirectoryName(exe)!,
                RedirectStandardOutput=true,RedirectStandardError=true
            };
            foreach(var arg in new[]{"+force_install_dir",install,"+login","anonymous",
                "+app_update","2430930","validate","+quit"})
                psi.ArgumentList.Add(arg);
            foreach(string key in new[]{"STEAM_USERNAME","STEAM_PASSWORD","MYSQL_PWD",
                "MARIADB_PWD","UNTAMED_RCON_PASSWORD"})
                psi.Environment.Remove(key);
            if(synthetic)psi.Environment["FAKE_STEAM_REFRESH_BUILD"]="1";
            using var child=Process.Start(psi) ??
                throw new IOException("SteamCMD child could not be launched");
            var stdout=Capture(child.StandardOutput,deadline.Token);
            var stderr=Capture(child.StandardError,deadline.Token);
            try
            {
                await child.WaitForExitAsync(deadline.Token);
                string message=await stdout;
                string error=await stderr;
                if(child.ExitCode==0 &&
                   message.Contains("Success! App '2430930' fully installed.",
                       StringComparison.OrdinalIgnoreCase))
                    return;
                if(ShouldRetryMissingConfiguration(attempt,child.ExitCode,
                    message+"\n"+error,synthetic))
                {
                    // Valve's exact initial Missing configuration response
                    // may be transient. At most two retries with bounded
                    // backoff, inside the existing ONE manual update operation.
                    verifyStopped();
                    await Task.Delay(TimeSpan.FromSeconds(3*(attempt+1)),
                        deadline.Token);
                    continue;
                }
                throw new InvalidDataException(
                    "SteamCMD did not acknowledge a complete ASA update");
            }
            catch
            {
                if(!child.HasExited)
                {
                    try{child.Kill(entireProcessTree:true);}catch{}
                    try{await child.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5));}catch{}
                }
                throw;
            }
        }
        throw new InvalidDataException("SteamCMD initial configuration retry exhausted");
    }
}
