using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

// This planner never runs steamcmd.exe, downloads mod files, or changes ASA.
// One shared installation = one update plan, regardless of how many local maps use it.
public sealed record SteamUpdatePreview(string InstallRoot,string[] Maps,string SteamCmdSha256,
    string? InstalledBuild,long EstimatedDownloadBytes,long AvailableBytes,bool DryRunAllowed,
    string Reason);
public sealed record SteamDryRunReceipt(Guid Id,DateTimeOffset Utc,string InstallRoot,
    string[] Maps,string State,string Description);

public static class SteamUpdateDryRun
{
    public static SteamUpdatePreview Plan(string steamCmdPath,string installRoot,
        LocalMap[] ownedMaps,Func<LocalMap,ProcessCheck> check,long estimatedDownloadBytes)
    {
        if(ownedMaps==null||check==null)throw new ArgumentNullException(nameof(ownedMaps));
        if(estimatedDownloadBytes<=0 || estimatedDownloadBytes>500L*1024*1024*1024)
            throw new InvalidDataException("Require explicit bounded update size estimate");
        var steam=SteamCmdPreflight.Inspect(steamCmdPath);
        if(ownedMaps.Length==0 || ownedMaps.Any(x=>x==null||x.Id==Guid.Empty) ||
            ownedMaps.Select(x=>x.Id).Distinct().Count()!=ownedMaps.Length)
            throw new InvalidDataException("Local map ownership must be complete and unique");
        string full=Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        if(!Path.IsPathFullyQualified(installRoot)||installRoot.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit local shared installation required");
        var selected=ownedMaps.Where(x=>string.Equals(
            Path.GetFullPath(x.InstallationPath).TrimEnd(Path.DirectorySeparatorChar),
            full,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(selected.Length==0)throw new UnauthorizedAccessException("Installation has no registered local maps");
        var groups=AsaInstallationInventory.Inspect(selected,check);
        if(groups.Count!=1)throw new InvalidDataException("Cannot plan cross-installation update");
        var info=groups.Single();
        long free=new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace;
        bool stopped=selected.All(x=>{var state=check(x);
            return state.ConfirmedStopped&&!state.Running && !x.Maintenance;});
        bool ready=stopped && info.ServerExecutablePresent &&
            free>=estimatedDownloadBytes+Math.Max(256L*1024*1024,estimatedDownloadBytes/4);
        return new(full,selected.Select(x=>x.Name).ToArray(),steam.Sha256,
            info.InstalledSteamBuild,estimatedDownloadBytes,free,ready,
            !stopped?"A map is running, uncertain or under maintenance":
            !info.ServerExecutablePresent?"Local ASA installation is incomplete":
            !ready?"Insufficient disk space for dry-run reserve":
            "Dry-run plan only; no live Steam update or SaveWorld/Stop certification");
    }

    public static SteamDryRunReceipt Simulate(SteamUpdatePreview plan,string profileRoot)
    {
        if(plan==null||!plan.DryRunAllowed)
            throw new InvalidOperationException("Dry-run plan must pass preflight");
        if(string.IsNullOrWhiteSpace(profileRoot) ||
            !Path.IsPathFullyQualified(profileRoot)||profileRoot.StartsWith(@"\\"))
            throw new InvalidDataException("Manager profile must be explicit and local");
        string full=Path.GetFullPath(profileRoot);
        for(var dir=new DirectoryInfo(full);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked dry-run profile path rejected");
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Dry-run requires local fixed disk");
        string folder=Path.Combine(full,"steamcmd-test-logs");
        if(plan.InstallRoot.StartsWith(folder+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Install root overlaps test logs");
        Directory.CreateDirectory(folder);
        var receipt=new SteamDryRunReceipt(Guid.NewGuid(),DateTimeOffset.UtcNow,
            plan.InstallRoot,plan.Maps,"dry-run-complete",
            "NO steamcmd.exe executed, NO app files touched, NO restart; installed build="+
            (plan.InstalledBuild??"unknown"));
        string path=Path.Combine(folder,receipt.Id.ToString("N")+".json");
        using(var fs=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(fs,receipt);fs.Flush(true);}
        return receipt;
    }
}
