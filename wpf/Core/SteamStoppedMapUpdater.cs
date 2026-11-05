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

    public static Task<Receipt> UpdateAsync(ClusterService cluster,LocalMap map,
        string steamCmdExecutable,long downloadReserveBytes,
        TimeSpan timeout,IProgress<SteamMaintenanceStage>? progress=null,
        CancellationToken ct=default) =>
        RunCore(cluster,map,steamCmdExecutable,downloadReserveBytes,
            timeout,null,progress,false,ct);

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
        IProgress<SteamMaintenanceStage>? stageProgress=null,CancellationToken ct=default) =>
        BatchCore(cluster,candidates,steamExecutable,downloadReserveBytes,
            timeout,null,queueProgress,stageProgress,ct);

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
        IProgress<SteamMaintenanceStage>? stageProgress,CancellationToken ct)
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
        var receipts=new List<Receipt>();
        var queue=new SteamSerialMaintenanceQueue(cluster.Store.Root,cluster.Config.LocalHostId);
        await queue.ExecuteAsync(selected,cluster.Config.LocalMaps,cluster.Check,
            async (map,token)=>{
                var item=await RunCore(cluster,map,steamExecutable,reserve,
                    timeout,fakeExecutable,stageProgress,true,token);
                receipts.Add(item);
            },queueProgress,ct);
        return receipts;
    }

    private static async Task<Receipt> RunCore(ClusterService cluster,LocalMap map,
        string steamExecutable,long reserve,TimeSpan timeout,string? fakeExecutable,
        IProgress<SteamMaintenanceStage>? progress,bool insideQueue,CancellationToken ct)
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
            foreach(var participant in group)
            {
                var state=cluster.Check(participant);
                if(state.Running || !state.ConfirmedStopped ||
                   state.Identity!=null || state.LoaderIdentity!=null)
                    throw new InvalidOperationException(
                        "Steam update blocked: one map still runs or cannot be confirmed stopped");
            }
        }
        progress?.Report(SteamMaintenanceStage.Preflight);
        VerifyStopped();
        var plan=SteamUpdateDryRun.Plan(steamExecutable,root,
            cluster.Config.LocalMaps.ToArray(),cluster.Check,reserve);
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
                string plugin=Path.Combine(root,"ShooterGame","Binaries","Win64",
                    "ArkApi","Plugins");
                if(Directory.Exists(plugin)&&
                   Directory.EnumerateFiles(plugin,"*",SearchOption.AllDirectories).Any())
                    scope|=LocalBackupScope.Plugins;
                VerifyStopped();
                progress?.Report(SteamMaintenanceStage.Backup);
                // The verified backup is created OUTSIDE the install root.
                // LocalBackupWorkflow hashes source AND destination and fails
                // on changed memberships. Missing world saves reject update.
                var backup=LocalBackupWorkflow.Create(map,cluster.Check(map),
                    destination,scope);
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
                    timeout,fakeExecutable!=null,token);
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
            await queue.ExecuteAsync(new[]{map},cluster.Config.LocalMaps,cluster.Check,
                work,ct:ct);
        }
        return result ?? throw new InvalidOperationException("No verified Steam update receipt was committed");
    }

    private static async Task RunSteamAsync(string exe,string install,
        TimeSpan timeout,bool synthetic,CancellationToken ct)
    {
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
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var child=Process.Start(psi) ??
            throw new IOException("SteamCMD child could not be launched");
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
        var stdout=Capture(child.StandardOutput,deadline.Token);
        var stderr=Capture(child.StandardError,deadline.Token);
        try
        {
            await child.WaitForExitAsync(deadline.Token);
            var message=await stdout;
            _=await stderr;
            if(child.ExitCode!=0 ||
               !message.Contains("Success! App '2430930' fully installed.",
                   StringComparison.Ordinal))
                throw new InvalidDataException("SteamCMD did not acknowledge a complete ASA update");
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
}
