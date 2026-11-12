using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

[Flags]
public enum LocalBackupScope { None=0, WorldAndProfiles=1, Ini=2, Plugins=4 }

// File-integrity-only backup of explicitly owned local maps. Never accesses Kova Store,
// cluster shares or SQL, and never claims live ASA save consistency.
public sealed record LocalBackupResult(string PackageDirectory, BackupManifest Manifest,
    string Consistency="File integrity verified; ASA application consistency NOT established");

// Read-only calculation: "Ready" means the selected stopped-map FILE COPY
// has valid inputs and enough disk, NOT that ASA save/SQL/Store is consistent.
public sealed record LocalBackupPreflight(Guid MapId,LocalBackupScope Scope,
    string Destination,int Files,long SelectedBytes,long FreeBytes,long ReserveBytes,
    string Notice="Only stopped-map file-copy readiness; no ASA/SQL/Store consistency proof");


public static class LocalBackupWorkflow
{
    // Per-map offline file copy limits. This is not the larger, separately
    // bounded multi-host archive. Reject oversized sources before any staging
    // directory is created, never silently omit a component.
    public const int MaxSelectedFiles = 100_000;
    public const long MaxSelectedBytes = 2L * 1024 * 1024 * 1024 * 1024;

    public static void ValidateFileBudget(int count,long bytes)
    {
        if(count<1 || count>MaxSelectedFiles)
            throw new InvalidDataException("UM-BACKUP-FILE-LIMIT");
        if(bytes<0 || bytes>MaxSelectedBytes)
            throw new InvalidDataException("UM-BACKUP-SIZE-LIMIT");
    }
    public static LocalBackupPreflight Preview(LocalMap map,ProcessCheck check,
        string destination,LocalBackupScope scope)
    {
        if(map==null||map.Id==Guid.Empty)
            throw new InvalidDataException("Map identity required");
        RequireStopped(check);
        if(scope==LocalBackupScope.None ||
           (scope & ~(LocalBackupScope.WorldAndProfiles|LocalBackupScope.Ini|LocalBackupScope.Plugins))!=0)
            throw new InvalidDataException("Unsupported backup scope");
        string root=FixedLocalPath(map.InstallationPath);
        string target=FixedLocalPath(destination);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);
        if(IsInside(root,target)||IsInside(target,root)||
           string.Equals(root,target,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup destination and map directory overlap");
        string staging=FixedLocalPath(target+".incomplete");
        if(Directory.Exists(target)||Directory.Exists(staging)||
           File.Exists(target)||File.Exists(staging))
            throw new IOException("Backup target or interrupted backup already exists");
        var paths=SelectFiles(root,scope);
        ValidateCriticalSaveLengths(paths,UntamedWorldBackup.HasLiveOrHistoricalStore(map),
            scope.HasFlag(LocalBackupScope.WorldAndProfiles));
        long bytes=0;
        foreach(var entry in paths)
        {
            bytes=checked(bytes+new FileInfo(entry.Value).Length);
            ValidateFileBudget(paths.Count,bytes);
        }
        long reserve=Math.Max(64L*1024*1024,bytes/10);
        long free=new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
        if(bytes>free||reserve>free-bytes)
            throw new IOException("Insufficient free disk space for backup and safety reserve");
        return new(map.Id,scope,target,paths.Count,bytes,free,reserve);
    }

    private static void RequireStopped(ProcessCheck? state)
    {
        if(state==null||!state.ConfirmedStopped||state.Running||
           state.Identity!=null||state.LoaderIdentity!=null)
            throw new InvalidOperationException(
                "UM-BACKUP-CONFIRMED-STOPPED-REQUIRED");
    }

    public static LocalBackupResult Create(LocalMap map,ProcessCheck check,string destination,
        LocalBackupScope scope,Action<string>? progress=null,
        Func<ProcessCheck>? checkCurrent=null)
    {
        // The same immutable preflight gates the actual file operation.
        // The content and membership are verified again during the copy.
        Preview(map,check,destination,scope);
        if(map==null||map.Id==Guid.Empty)throw new InvalidDataException("Map identity required");
        RequireStopped(check);
        // Legacy callers retain the original initial proof. Every production
        // SteamCMD/desktop caller passes a FRESH OS process observer, so a
        // process starting during file copy blocks backup publication.
        void Recheck()=>RequireStopped(checkCurrent?.Invoke()??check);
        if(scope==LocalBackupScope.None || (scope & ~(LocalBackupScope.WorldAndProfiles|LocalBackupScope.Ini|LocalBackupScope.Plugins))!=0)
            throw new InvalidDataException("Unsupported backup scope");
        var root=FixedLocalPath(map.InstallationPath);
        using var operation=new UntamedOperationCoordinator().Acquire(root,UntamedOperationKind.Backup);
        Recheck();
        var target=FixedLocalPath(destination);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);
        if(IsInside(root,target)||IsInside(target,root)||string.Equals(root,target,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup destination and map directory overlap");
        string staging=target+".incomplete";
        FixedLocalPath(staging);
        if(Directory.Exists(target)||Directory.Exists(staging)||File.Exists(target)||File.Exists(staging))
            throw new IOException("Target or interrupted backup already exists");
        var paths=SelectFiles(root,scope);
        ValidateCriticalSaveLengths(paths,UntamedWorldBackup.HasLiveOrHistoricalStore(map),
            scope.HasFlag(LocalBackupScope.WorldAndProfiles));
        // Fail before staging if this fixed local disk cannot hold the selected files.
        // A reserve is intentional: File hashes and the manifest do not prove ASA consistency.
        long totalBytes=0;
        foreach(var entry in paths)
        {
            totalBytes=checked(totalBytes+new FileInfo(entry.Value).Length);
            ValidateFileBudget(paths.Count,totalBytes);
        }
        long reserve=Math.Max(64L*1024*1024,totalBytes/10);
        long free=new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
        if(totalBytes>free || reserve>free-totalBytes)
            throw new IOException("Insufficient free disk space for backup and safety reserve");
        string data=Path.Combine(staging,"data");
        Directory.CreateDirectory(data);
        foreach(var entry in paths)
        {
            progress?.Invoke(entry.Key);
            string copy=Path.Combine(data,entry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            using(var input=new FileStream(entry.Value,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=new FileStream(copy,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { input.CopyTo(output);output.Flush(true); }
        }
        // Check source bytes AND membership. A new character/profile/plugin
        // file must not silently disappear from a supposedly complete scope.
        Recheck();
        var manifest=BackupIntegrity.Create(data,map.Id,
            scope+"; stopped-map file copy; ASA and shared Store consistency NOT established");
        if(manifest.Files.Count!=paths.Count)throw new InvalidDataException("Staged backup file count differs");
        foreach(var file in manifest.Files)
        {
            if(!paths.TryGetValue(file.Path,out var original))
                throw new InvalidDataException("Unexpected staged file");
            using var stream=new FileStream(original,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(stream.Length!=file.Size ||
               !System.Security.Cryptography.SHA256.HashData(stream).AsSpan().SequenceEqual(Convert.FromHexString(file.Sha256)))
                throw new InvalidDataException("Source changed while backing up");
        }
        var finalPaths=SelectFiles(root,scope);
        if(!paths.Keys.SequenceEqual(finalPaths.Keys,StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Source file set changed while backing up");
        BackupIntegrity.Verify(data,manifest,map.Id);
        Recheck();
        File.WriteAllText(Path.Combine(staging,"manifest.json"),BackupIntegrity.Serialize(manifest));
        File.WriteAllText(Path.Combine(staging,"complete"),"1");
        Recheck();
        // Only the atomic publication can create a successful package.
        // A process-state race before this point must not strand the map
        // in a misleading Backup/NeedsOperator journal.
        operation.MarkMutationStarted();
        Directory.Move(staging,target);
        TestBackupPackage.Verify(target,map.Id);
        operation.Complete();
        return new(target,manifest);
    }

    // The generic local backup is used before SteamCMD as well as by the
    // Backup Center. A 0-byte ASA world is never a recoverable copy, and a
    // 0-byte loose profile/tribe must not be advertised as a healthy
    // non-Store backup. Original Store-v1 conversions can legitimately
    // retain empty loose profiles: copy their exact bytes, never repair them.
    private static void ValidateCriticalSaveLengths(
        IReadOnlyDictionary<string,string> files,bool historicalStore,bool requireWorld)
    {
        bool foundWorld=false;
        foreach(var (relative,full) in files)
        {
            var parts=relative.Split(new[]{
                Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar
            },StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length<4||
               !parts[0].Equals("ShooterGame",StringComparison.OrdinalIgnoreCase)||
               !parts[1].Equals("Saved",StringComparison.OrdinalIgnoreCase)||
               !(parts[2].StartsWith("SavedArks",StringComparison.OrdinalIgnoreCase)||
                 parts[2].Equals("SaveGames",StringComparison.OrdinalIgnoreCase)))
                continue;
            string name=Path.GetFileName(relative);
            bool world=name.EndsWith(".ark",StringComparison.OrdinalIgnoreCase);
            bool loose=name.EndsWith(".arkprofile",StringComparison.OrdinalIgnoreCase)||
                       name.EndsWith(".arktribe",StringComparison.OrdinalIgnoreCase);
            if((world||loose&&!historicalStore)&&new FileInfo(full).Length==0)
                throw new InvalidDataException("UM-BACKUP-ZERO-BYTE-CRITICAL-SAVE");
            if(world)foundWorld=true;
        }
        // SaveGames/SaveArks may contain only logs or stale loose profiles.
        // Never approve a pre-update "world" backup with no .ark anywhere.
        if(requireWorld&&!foundWorld)
            throw new InvalidDataException("UM-BACKUP-VALID-WORLD-MISSING");
    }

    private static SortedDictionary<string,string> SelectFiles(string root,LocalBackupScope scope)
    {
        var paths=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string saved=Path.Combine(root,"ShooterGame","Saved");
        if(scope.HasFlag(LocalBackupScope.WorldAndProfiles))
        {
            if(Directory.Exists(saved))
                foreach(var dir in new DirectoryInfo(saved).GetDirectories())
                    if(dir.Name.StartsWith("SavedArks",StringComparison.OrdinalIgnoreCase) ||
                       dir.Name.Equals("SaveGames",StringComparison.OrdinalIgnoreCase))
                        CollectDirectory(root,dir.FullName,paths);
            if(!paths.Any())throw new InvalidDataException("No SavedArks/SaveGames files found; refused empty world backup");
        }
        if(scope.HasFlag(LocalBackupScope.Ini))
        {
            string conf=Path.Combine(saved,"Config","WindowsServer");
            int before=paths.Count;
            foreach(var name in new[]{"Game.ini","GameUserSettings.ini"})
            {
                string file=Path.Combine(conf,name);
                if(File.Exists(file))AddFile(root,file,paths);
            }
            if(before==paths.Count)throw new InvalidDataException("Selected INI files do not exist");
        }
        if(scope.HasFlag(LocalBackupScope.Plugins))
        {
            int before=paths.Count;
            // Full ASA API/plugin stack must be recoverable after SteamCMD,
            // not merely ArkApi/Plugins: the loader, API Core DLL, disabled
            // plugin directory and both Version.dll injection locations matter.
            string bin=Path.Combine(root,"ShooterGame","Binaries","Win64");
            string api=Path.Combine(bin,"ArkApi");
            if(Directory.Exists(api))CollectDirectory(root,api,paths);
            string disabled=Path.Combine(bin,"UntamedDisabledPlugins");
            if(Directory.Exists(disabled))CollectDirectory(root,disabled,paths);
            foreach(string file in new[]{
                Path.Combine(bin,"AsaApiLoader.exe"),
                Path.Combine(bin,"Version.dll"),
                Path.Combine(root,"Version.dll")
            })
                if(File.Exists(file))AddFile(root,file,paths);
            if(before==paths.Count)
                throw new InvalidDataException("Selected plugin/API runtime data not found");
        }
        if(paths.Count==0)throw new InvalidDataException("No supported files selected");
        return paths;
    }

    private static void CollectDirectory(string root,string directory,IDictionary<string,string> result)
    {
        var node=new DirectoryInfo(directory);
        if((node.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked directory rejected");
        foreach(var file in node.GetFiles())AddFile(root,file.FullName,result);
        foreach(var child in node.GetDirectories())CollectDirectory(root,child.FullName,result);
    }
    private static void AddFile(string root,string file,IDictionary<string,string> result)
    {
        var info=new FileInfo(file);
        if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked file rejected");
        var full=Path.GetFullPath(file);
        if(!IsInside(root,full))throw new InvalidDataException("File escapes map root");
        // A lexical prefix is insufficient: ShooterGame, Saved, Config or any
        // intermediate parent could be a junction/symlink to external data.
        // Reject them before counting, hashing or copying the file.
        var parent=Path.GetDirectoryName(full);
        while(parent!=null && IsInside(root,parent))
        {
            var node=new DirectoryInfo(parent);
            if((node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked backup source directory rejected");
            parent=Path.GetDirectoryName(parent);
        }
        var relative=Path.GetRelativePath(root,full);
        if(info.Length>MaxSelectedBytes)
            throw new InvalidDataException("UM-BACKUP-SIZE-LIMIT");
        if(result.Count>=MaxSelectedFiles)
            throw new InvalidDataException("UM-BACKUP-FILE-LIMIT");
        if(!result.TryAdd(relative,full))throw new InvalidDataException("Duplicate backup path");
    }
    private static bool IsInside(string root,string candidate)=>
        candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase);
    private static string FixedLocalPath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\"))
            throw new InvalidDataException("Absolute local fixed-disk path required");
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Local fixed drive required");
        for(var current=new DirectoryInfo(full);current!=null;current=current.Parent)
            if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked backup root rejected");
        return full;
    }
}

