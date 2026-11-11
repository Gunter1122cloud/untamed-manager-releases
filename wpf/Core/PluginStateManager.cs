using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

public sealed record PluginStateItem(string Name,bool Enabled,bool Exists,bool RuntimeLoaded,
    string Information);
public sealed record PluginToggleResult(Guid Map,string Plugin,bool Enabled,
    string Destination,string BackupPackage,bool RequiresRestart);

// Safety is established from the local Manager's entire registered installation
// group. A stopped selected map alone cannot authorize a shared plugin move.
public sealed record PluginToggleReadiness(Guid Map,string Plugin,bool TargetEnabled,
    bool Allowed,string Reason,int CheckedLocalMaps);


// Moves the ENTIRE plugin directory on the SAME installation volume. JSON,
// license and database files move together with the binaries. Runtime-loaded
// state is deliberately not inferred from mere disk presence.
public static class PluginStateManager
{
    public static IReadOnlyList<PluginStateItem> Inventory(LocalMap map)
    {
        var (active,disabled)=Roots(map);
        var a=Directory.Exists(active)?
            Directory.GetDirectories(active).Select(Path.GetFileName).Where(x=>x!=null).Cast<string>():
            Enumerable.Empty<string>();
        var d=Directory.Exists(disabled)?
            Directory.GetDirectories(disabled).Select(Path.GetFileName).Where(x=>x!=null).Cast<string>():
            Enumerable.Empty<string>();
        // A folder in both roots is not a valid toggle state. Do not silently
        // treat ambiguous disk state as enabled or expose an actionable switch.
        if(a.Intersect(d,StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("Plugin exists in both active and disabled roots");
        return a.Concat(d).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)
            .Select(name=>new PluginStateItem(name,a.Contains(name,StringComparer.OrdinalIgnoreCase),
                true,false,"Disk state only; actual API runtime load is unverified"))
            .ToArray();
    }

    public static PluginToggleReadiness InspectLocalToggle(
        ClusterService cluster,Guid mapId,string plugin,bool enabled,
        Func<LocalMap,ProcessCheck>? historicalStoppedObserver=null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId);
        if(map==null)throw new UnauthorizedAccessException("Plugin ownership is local only");
        if(string.IsNullOrWhiteSpace(plugin))
            return new(mapId,plugin??"",enabled,false,"Válassz plugint.",0);
        var entries=Inventory(map);
        var match=entries.SingleOrDefault(x=>x.Name.Equals(plugin,StringComparison.OrdinalIgnoreCase));
        if(match==null)
            return new(mapId,plugin,enabled,false,"A plugin mappája nem található.",0);
        if(match.Name.Contains(VaultProtocol.ProtectedNameToken,StringComparison.OrdinalIgnoreCase))
            return new(mapId,match.Name,enabled,false,"Védett plugin: állapotváltás tiltva.",0);
        if(match.Enabled==enabled)
            return new(mapId,match.Name,enabled,false,"A plugin már a kiválasztott állapotban van.",0);
        if(map.Maintenance)
            return new(mapId,match.Name,enabled,false,"A map karbantartási állapotban van.",0);
        string installation=Path.GetFullPath(map.InstallationPath)
            .TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        var shared=cluster.Config.LocalMaps.Where(m=>
            !string.IsNullOrWhiteSpace(m.InstallationPath) &&
            Path.GetFullPath(m.InstallationPath).TrimEnd(
                Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)
                .Equals(installation,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(shared.Length==0)
            return new(mapId,match.Name,enabled,false,"Nincs helyi telepítési hozzárendelés.",0);
        foreach(var member in shared)
        {
            var generic=cluster.Check(member); // no mutations or process capture
            if(member.Maintenance||member.ObservedReadOnlyImported||
               generic.Running||generic.Identity!=null||generic.LoaderIdentity!=null||
               (!member.HistoricalStoreProtected&&
                UntamedWorldBackup.HasLiveOrHistoricalStore(member)))
                return new(mapId,match.Name,enabled,false,
                    member.Name+" · nem szerkeszthető vagy aktív ASA/Loader.",shared.Length);
            if(member.HistoricalStoreProtected&&historicalStoppedObserver==null)
                return new(mapId,match.Name,enabled,false,
                    member.Name+" · történeti Kova map: natív leállítási igazolás szükséges.",shared.Length);
            // Generic ClusterService.Check intentionally reports historical
            // Store as uncertain because the original launch argv is empty.
            // Use only the Windows native process-family observer provided
            // by the trusted desktop host, including the under-lock recheck.
            var process=member.HistoricalStoreProtected?
                historicalStoppedObserver!(member):generic;
            if(process.Running||!process.ConfirmedStopped||
               process.Identity!=null||process.LoaderIdentity!=null)
                return new(mapId,match.Name,enabled,false,
                    member.Name+" · nem igazoltan leállított szerver (vagy karbantartás).",shared.Length);
        }
        return new(mapId,match.Name,enabled,true,
            shared.Length+" közös telepítésű helyi map igazoltan leállítva.",shared.Length);
    }

    public static PluginToggleResult ToggleLocalChecked(
        ClusterService cluster,Guid mapId,string plugin,bool enabled,
        Action? verifyUnderOperationLock=null,
        Func<LocalMap,ProcessCheck>? historicalStoppedObserver=null,
        Func<LocalMap,ProcessCheck>? historicalStoppedUnderLockObserver=null)
    {
        // Called after the operator's confirmation, on the UI thread. This
        // READ/VERIFY phase never claims a real ASA-loaded plugin state.
        var permit=InspectLocalToggle(cluster,mapId,plugin,enabled,
            historicalStoppedObserver);
        if(!permit.Allowed)throw new InvalidOperationException(permit.Reason);
        var map=cluster.Config.LocalMaps.Single(x=>x.Id==mapId);
        // Final check belongs to the actual file operation too; the underlying
        // Toggle reacquires a configuration lock and protects the whole folder.
        var selectedState=map.HistoricalStoreProtected?
            historicalStoppedObserver!(map):cluster.Check(map);
        return Toggle(map,selectedState,permit.Plugin,enabled,cluster.Store.Root,
            () => {
                // Re-evaluate every registered map under the same installation
                // operation lease, immediately before creating backups/moving files.
                var latest=InspectLocalToggle(cluster,mapId,permit.Plugin,enabled,
                    historicalStoppedUnderLockObserver??historicalStoppedObserver);
                if(!latest.Allowed)throw new InvalidOperationException(latest.Reason);
                // Historical Kova profiles need an independent exact OS/Loader
                // stopped-family check UNDER the native plugin mutation lock.
                // A UI preflight from before lock acquisition is not sufficient.
                verifyUnderOperationLock?.Invoke();
            });
    }

    public static PluginToggleResult Toggle(LocalMap map,ProcessCheck state,
        string plugin,bool enabled,string profileRoot,Action? verifyWhileLocked=null)
    {
        if(map==null || map.Id==Guid.Empty || state==null ||
           !state.ConfirmedStopped || state.Running || map.Maintenance)
            throw new InvalidOperationException("Plugin toggle requires confirmed stopped local map");
        using var operation=new UntamedOperationCoordinator().Acquire(map.InstallationPath,UntamedOperationKind.PluginChange);
        if(string.IsNullOrWhiteSpace(plugin) || plugin.Length>80 ||
           !char.IsAsciiLetterOrDigit(plugin[0]) ||
           plugin.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.') ||
           plugin=="." || plugin==".." || plugin.Contains(VaultProtocol.ProtectedNameToken,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe or protected plugin name");
        var (active,disabled)=Roots(map);
        string source=Path.Combine(enabled?disabled:active,plugin);
        string target=Path.Combine(enabled?active:disabled,plugin);
        if(!Directory.Exists(source) || Directory.Exists(target) || File.Exists(target))
            throw new InvalidOperationException("Plugin source missing, or destination already exists");
        foreach(var folder in new[]{source,Path.GetDirectoryName(source)!,
            Path.GetDirectoryName(target)!})
            AssertNoReparse(folder);
        string profile=Path.GetFullPath(profileRoot);
        if(!Path.IsPathFullyQualified(profileRoot)||profileRoot.StartsWith(@"\\") ||
           new DriveInfo(Path.GetPathRoot(profile)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Manager backup profile must be local fixed disk");
        if(profile.StartsWith(Path.GetFullPath(map.InstallationPath)+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin backup profile must be outside ASA installation");
        string backup=Path.Combine(profile,"plugin-toggle-backups",
            map.Id.ToString("N"),plugin,Guid.NewGuid().ToString("N"));
        // Validate shared-map process state under the directory mutation lease.
        // This is intentionally optional for legacy callers and synthetic
        // fixture tests; all new UI actions use ToggleLocalChecked.
        verifyWhileLocked?.Invoke();
        // A complete SHA-verified source protection survives every toggle.
        operation.MarkMutationStarted();
        TestBackupPackage.Create(source,backup,map.Id,
            "Pre-plugin-toggle full directory; configs and licenses included");
        var package=TestBackupPackage.Verify(backup,map.Id);
        if(package.Files.Count==0)throw new InvalidDataException("Cannot move empty plugin directory");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source,target);
        try{BackupIntegrity.Verify(target,new BackupManifest(1,map.Id,
            package.Scope,package.CreatedUtc,package.Files),map.Id);}
        catch
        {
            if(!Directory.Exists(source)&&Directory.Exists(target))
                Directory.Move(target,source);
            throw;
        }
        operation.Complete();
        return new(map.Id,plugin,enabled,target,backup,true);
    }

    private static (string active,string disabled) Roots(LocalMap map)
    {
        if(map==null || map.Id==Guid.Empty ||
           string.IsNullOrWhiteSpace(map.InstallationPath) ||
           !Path.IsPathFullyQualified(map.InstallationPath) ||
           map.InstallationPath.StartsWith(@"\\"))
            throw new InvalidDataException("Local installation root required");
        string root=Path.GetFullPath(map.InstallationPath);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Plugin installation must be on local fixed disk");
        foreach(var dir in new DirectoryInfo(root).EnumerateParentChain())
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked installation path");
        string binary=Path.Combine(root,"ShooterGame","Binaries","Win64");
        return (Path.Combine(binary,"ArkApi","Plugins"),
                Path.Combine(binary,"UntamedDisabledPlugins"));
    }
    private static IEnumerable<DirectoryInfo> EnumerateParentChain(this DirectoryInfo directory)
    {
        for(DirectoryInfo? p=directory;p!=null;p=p.Parent)yield return p;
    }
    private static void AssertNoReparse(string folder)
    {
        for(DirectoryInfo? dir=new(folder);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked plugin path");
        if(!Directory.Exists(folder))return;
        foreach(var file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories))
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked plugin data");
        foreach(var dir in Directory.EnumerateDirectories(folder,"*",SearchOption.AllDirectories))
            if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked plugin subdirectory");
    }
}

