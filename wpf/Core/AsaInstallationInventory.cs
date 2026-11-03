using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

// Read-only grouped installation preflight. No SteamCMD, DoExit, SaveWorld, restore,
// Store, SQL or network operation is performed here.
public sealed record AsaInstallationInfo(string InstallationRoot,IReadOnlyList<string> LocalMaps,
    bool ServerExecutablePresent,string? InstalledSteamBuild,bool UpdateBlocked,string Reason);

public static class AsaInstallationInventory
{
    private static readonly Regex BuildId=new(
        @"(?m)^\s*""buildid""\s*""(?<id>[0-9]+)""\s*$",RegexOptions.Compiled);

    public static IReadOnlyList<AsaInstallationInfo> Inspect(
        IEnumerable<LocalMap> ownedMaps,Func<LocalMap,ProcessCheck> inspect)
    {
        if(ownedMaps==null)throw new ArgumentNullException(nameof(ownedMaps));
        if(inspect==null)throw new ArgumentNullException(nameof(inspect));
        var maps=ownedMaps.ToArray();
        var unique=new HashSet<Guid>();
        foreach(var map in maps)
            if(map==null||map.Id==Guid.Empty||!unique.Add(map.Id))
                throw new InvalidDataException("Map identities must be unique and local");
        var groups=maps.GroupBy(m=>FixedLocal(m.InstallationPath),
            StringComparer.OrdinalIgnoreCase);
        var results=new List<AsaInstallationInfo>();
        foreach(var group in groups)
        {
            string root=group.Key;
            string exe=Path.Combine(root,"ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
            RejectLinkedAncestors(exe);
            bool present=File.Exists(exe);
            if(present && (File.GetAttributes(exe)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked ASA server executable rejected");

            string? build=null;
            string manifest=Path.Combine(root,"steamapps","appmanifest_2430930.acf");
            RejectLinkedAncestors(manifest);
            if(File.Exists(manifest))
            {
                var file=new FileInfo(manifest);
                if((file.Attributes & FileAttributes.ReparsePoint)!=0 || file.Length>1024*1024)
                    throw new InvalidDataException("Unsafe Steam appmanifest file");
                var match=BuildId.Match(File.ReadAllText(manifest));
                if(match.Success)build=match.Groups["id"].Value;
            }
            bool blocked=!present || build==null;
            string reason=!present?"ASA executable not found":build==null?
                "Steam build ID unverified":"Update remains disabled until live ASA save/stop is validated";
            foreach(var map in group)
            {
                var state=inspect(map);
                if(!state.ConfirmedStopped || state.Running)
                {
                    blocked=true;
                    reason="A local map is running or has uncertain process state";
                }
            }
            // This preflight NEVER unlocks an update on its own.
            results.Add(new(root,group.Select(m=>m.Name).ToArray(),present,build,
                true,reason));
        }
        return results;
    }

    private static string FixedLocal(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path) ||
           path.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit fixed local installation root required");
        var full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Only fixed local installation roots are supported");
        RejectLinkedAncestors(full);
        return full;
    }
    private static void RejectLinkedAncestors(string path)
    {
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
            dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Installation path uses junction or symlink");
    }
}
