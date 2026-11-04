using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

public sealed record PluginStateItem(string Name,bool Enabled,bool Exists,bool RuntimeLoaded,
    string Information);
public sealed record PluginToggleResult(Guid Map,string Plugin,bool Enabled,
    string Destination,string BackupPackage,bool RequiresRestart);

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
        return a.Concat(d).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)
            .Select(name=>new PluginStateItem(name,a.Contains(name,StringComparer.OrdinalIgnoreCase),
                true,false,"Disk state only; actual API runtime load is unverified"))
            .ToArray();
    }

    public static PluginToggleResult Toggle(LocalMap map,ProcessCheck state,
        string plugin,bool enabled,string profileRoot)
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

