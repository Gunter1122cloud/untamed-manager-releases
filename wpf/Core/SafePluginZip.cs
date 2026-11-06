using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

// Strict, fail-closed DLL/PDB-only ZIP updater. Existing configuration, saves, database files
// and licenses are copied unchanged. Full prior plugin is backed up OUTSIDE the Plugins tree.
public sealed record PluginZipInfo(string PluginName,int BinaryCount,int SkippedNonBinaryCount);
public sealed record PluginInstallResult(string PluginName,string Target,string? PreviousBackup,int BinaryCount);
public sealed record PluginZipPreview(string PluginName,int BinaryCount,int SkippedFiles,
    bool PreviousPluginExists,bool Ready,string Reason);
public sealed record PluginRollbackPreview(string PluginName,string BackupFolder,
    int PreviousBinaryCount,bool Allowed,string Reason);
public sealed record PluginRollbackResult(string PluginName,string Target,
    string PreRollbackBackup,int RestoredBinaryCount);
public static class SafePluginZip
{
    private const long MaxPackageBytes=256L*1024*1024;
    private const long MaxFileBytes=100L*1024*1024;
    private static readonly Regex SafePart=new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$",RegexOptions.Compiled);

    // Windows normalizes trailing dots and reserves device names even with extensions.
    // Reject those paths before extraction, not after a partial plugin replacement.
    private static bool SafeWindowsPart(string part)
    {
        if(!SafePart.IsMatch(part) || part.EndsWith(".",StringComparison.Ordinal))return false;
        string stem=part.Split('.')[0];
        if(new[]{"CON","PRN","AUX","NUL"}.Contains(stem,StringComparer.OrdinalIgnoreCase))
            return false;
        if(stem.Length==4 && stem[3]>='1' && stem[3]<='9' &&
           (stem.StartsWith("COM",StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("LPT",StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    public static PluginZipInfo Inspect(string zipPath)
    {
        using var archive=Open(zipPath);
        return Validate(archive).info;
    }
    // Preflight only. This never creates directories or writes a plugin or backup.
    // Installation revalidates every invariant independently at commit time.
    public static PluginZipPreview Preview(LocalMap map,ProcessCheck check,string zipPath,string backupRoot)
    {
        if(map==null || map.Id==Guid.Empty)throw new InvalidDataException("Map identity required");
        var install=FixedLocal(map.InstallationPath);
        if(!Directory.Exists(install))throw new DirectoryNotFoundException(install);
        var backup=FixedLocal(backupRoot);
        if(Inside(install,backup)||Inside(backup,install)||
           install.Equals(backup,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin backup folder must be outside map installation");
        using var zip=Open(zipPath);
        var (info,_)=Validate(zip);
        var target=Path.Combine(install,"ShooterGame","Binaries","Win64","ArkApi","Plugins",info.PluginName);
        CheckAncestors(target);
        bool previous=Directory.Exists(target);
        if(previous)CheckTree(target);
        bool ready=check.ConfirmedStopped && !check.Running;
        return new(info.PluginName,info.BinaryCount,info.SkippedNonBinaryCount,
            previous,ready,ready?"Confirmed stopped; preview only":"Server is running or uncertain; update blocked");
    }
    internal static PluginInstallResult Install(LocalMap map,ProcessCheck check,string zipPath,string backupRoot)
        =>InstallCore(map,check,zipPath,backupRoot,null);

    public static PluginInstallResult InstallGuarded(ClusterService cluster,Guid mapId,string zipPath,string backupRoot)
    {
        var (map,observe)=GuardedObserver(cluster,mapId);
        return InstallCore(map,observe(),zipPath,backupRoot,observe);
    }

    private static (LocalMap Map,Func<ProcessCheck> Observe) GuardedObserver(ClusterService cluster,Guid mapId)
    {
        var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId&&mapId!=Guid.Empty)??
            throw new UnauthorizedAccessException("Only a registered local plugin target is allowed");
        string install=FixedLocal(map.InstallationPath);
        ProcessCheck InspectGroup()
        {
            if(!cluster.Config.LocalMaps.Contains(map)||FixedLocal(map.InstallationPath)!=install)
                throw new UnauthorizedAccessException("Plugin map ownership changed");
            ProcessCheck? selected=null;
            foreach(var local in cluster.Config.LocalMaps.Where(x=>string.Equals(
                FixedLocal(x.InstallationPath),install,StringComparison.OrdinalIgnoreCase)))
            {
                var state=cluster.Check(local);
                if(local.Maintenance||state.Running||!state.ConfirmedStopped||
                   state.Identity!=null||state.LoaderIdentity!=null)
                    throw new InvalidOperationException("All maps sharing this plugin installation must remain stopped");
                if(local.Id==mapId)selected=state;
            }
            return selected??throw new UnauthorizedAccessException("Plugin map no longer registered");
        }
        return(map,InspectGroup);
    }

    private static PluginInstallResult InstallCore(LocalMap map,ProcessCheck check,string zipPath,
        string backupRoot,Func<ProcessCheck>? observe)
    {
        if(map==null||map.Id==Guid.Empty)throw new InvalidDataException("Map identity required");
        if(!check.ConfirmedStopped||check.Running)
            throw new InvalidOperationException("Plugin update requires confirmed stopped local server");
        using var operation=new UntamedOperationCoordinator().Acquire(map.InstallationPath,UntamedOperationKind.PluginChange);
        observe?.Invoke();
        string install=FixedLocal(map.InstallationPath);
        if(!Directory.Exists(install))throw new DirectoryNotFoundException(install);
        string archives=FixedLocal(backupRoot);
        if(Inside(install,archives)||Inside(archives,install)||string.Equals(install,archives,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin backup folder must be outside map installation");
        using var zip=Open(zipPath);
        var (info,files)=Validate(zip);
        string plugins=Path.Combine(install,"ShooterGame","Binaries","Win64","ArkApi","Plugins");
        CheckAncestors(plugins);
        string target=Path.Combine(plugins,info.PluginName);
        if(Directory.Exists(target))CheckTree(target);
        string token=Guid.NewGuid().ToString("N");
        string staging=Path.Combine(plugins,".untamed-stage-"+token);
        string displaced=Path.Combine(plugins,".untamed-old-"+token);
        string? backup=null;
        DirectoryUpdateTransaction? snapshot=null;
        bool moved=false, installed=false;
        operation.MarkMutationStarted();
        Directory.CreateDirectory(plugins);
        try
        {
            if(Directory.Exists(target))
            {
                backup=Path.Combine(archives,map.Id.ToString("N"),info.PluginName,
                    DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")+"-"+token);
                CheckAncestors(backup);
                CopyTree(target,backup);
                var copyManifest=BackupIntegrity.Create(target,map.Id,"Pre-plugin-update complete plugin data");
                BackupIntegrity.Verify(backup,copyManifest,map.Id);
                // A separate manifest binds the full pre-update plugin tree to
                // the local map identity for a later explicit rollback.
                File.WriteAllText(backup+".untamed-manifest.json",BackupIntegrity.Serialize(copyManifest));
                var preparedSnapshot=new DirectoryUpdateTransaction(backup+".transaction");
                preparedSnapshot.Prepare(target);snapshot=preparedSnapshot;
                CopyTree(target,staging);
            }
            else Directory.CreateDirectory(staging);
            foreach(var entry in files)
            {
                string relative=entry.FullName[(info.PluginName.Length+1)..];
                if(!relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) &&
                   !relative.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase))continue;
                string to=Path.Combine(staging,relative.Replace('/',Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                using var input=entry.Open();
                using var output=new FileStream(to,FileMode.Create,FileAccess.Write,FileShare.None);
                input.CopyTo(output);
                output.Flush(true);
                if(output.Length!=entry.Length)throw new InvalidDataException("ZIP extraction size mismatch");
            }
            CheckTree(staging);
            observe?.Invoke();
            if(Directory.Exists(target)){Directory.Move(target,displaced);moved=true;}
            Directory.Move(staging,target);installed=true;
            observe?.Invoke();
            if(moved&&Directory.Exists(displaced))Directory.Delete(displaced,true);
            snapshot?.Commit();
            operation.Complete();
            return new(info.PluginName,target,backup,info.BinaryCount);
        }
        catch
        {
            // A process starting after preflight must not have its loaded tree
            // replaced by rollback. Preserve all snapshots and journal instead.
            observe?.Invoke();
            if(snapshot!=null)
            {
                snapshot.Rollback();
                if(moved&&Directory.Exists(displaced))Directory.Delete(displaced,true);
            }
            else if(moved&&Directory.Exists(displaced))
            {
                if(Directory.Exists(target))Directory.Delete(target,true);
                Directory.Move(displaced,target);
            }
            else if(installed&&Directory.Exists(target))Directory.Delete(target,true);
            if(Directory.Exists(staging))Directory.Delete(staging,true);
            throw;
        }
    }

    // Rollback explicitly restores only DLL/PDB from the SHA-verified, previously
    // captured plugin backup. Current JSON, licenses, databases and saves are
    // retained in the target and independently backed up before the swap.
    public static PluginRollbackPreview PreviewRollback(LocalMap map,ProcessCheck check,
        string pluginName,string previousBackup,string backupRoot)
    {
        if(map==null || map.Id==Guid.Empty)throw new InvalidDataException("Map identity required");
        if(!SafeWindowsPart(pluginName) || pluginName.Contains(VaultProtocol.ProtectedNameToken,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Protected or unsafe plugin name");
        string install=FixedLocal(map.InstallationPath);
        string backups=FixedLocal(backupRoot);
        if(Inside(install,backups) || Inside(backups,install) ||
           install.Equals(backups,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup and installation must not overlap");
        string allowedRoot=Path.Combine(backups,map.Id.ToString("N"),pluginName);
        string previous=FixedLocal(previousBackup);
        if(!Inside(allowedRoot,previous) || !Directory.Exists(previous))
            throw new UnauthorizedAccessException("Plugin backup is not owned by this map/plugin");
        CheckTree(previous);
        string manifestPath=previous+".untamed-manifest.json";
        CheckAncestors(manifestPath);
        if(!File.Exists(manifestPath) ||
           (File.GetAttributes(manifestPath)&FileAttributes.ReparsePoint)!=0 ||
           new FileInfo(manifestPath).Length>2_000_000)
            throw new InvalidDataException("Previous plugin backup manifest unavailable");
        var manifest=System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(
            File.ReadAllText(manifestPath))??throw new InvalidDataException("Invalid rollback manifest");
        if(manifest.Scope!="Pre-plugin-update complete plugin data")
            throw new InvalidDataException("Unexpected rollback backup scope");
        BackupIntegrity.Verify(previous,manifest,map.Id);
        int binaryCount=manifest.Files.Count(x=>
            x.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) ||
            x.Path.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase));
        if(binaryCount==0 || !manifest.Files.Any(x=>x.Path.Equals(pluginName+".dll",
            StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Rollback backup missing plugin root DLL");
        string target=Path.Combine(install,"ShooterGame","Binaries","Win64","ArkApi",
            "Plugins",pluginName);
        CheckAncestors(target);
        if(!Directory.Exists(target))throw new DirectoryNotFoundException(target);
        CheckTree(target);
        bool ready=check.ConfirmedStopped && !check.Running && !map.Maintenance;
        return new(pluginName,previous,binaryCount,ready,
            ready?"Previous DLL/PDB hashes verified; current config/database/license preserved":
                "Server is running, uncertain or in maintenance");
    }

    public static PluginRollbackResult RollbackGuarded(ClusterService cluster,Guid mapId,
        string pluginName,string previousBackup,string backupRoot)
    {
        var (map,observe)=GuardedObserver(cluster,mapId);
        return RollbackCore(map,observe(),pluginName,previousBackup,backupRoot,observe);
    }

    internal static PluginRollbackResult Rollback(LocalMap map,ProcessCheck check,
        string pluginName,string previousBackup,string backupRoot)
        =>RollbackCore(map,check,pluginName,previousBackup,backupRoot,null);

    private static PluginRollbackResult RollbackCore(LocalMap map,ProcessCheck check,
        string pluginName,string previousBackup,string backupRoot,Func<ProcessCheck>? observe)
    {
        var preview=PreviewRollback(map,check,pluginName,previousBackup,backupRoot);
        if(!preview.Allowed)throw new InvalidOperationException(preview.Reason);
        using var operation=new UntamedOperationCoordinator().Acquire(map.InstallationPath,UntamedOperationKind.Restore);
        observe?.Invoke();
        string install=FixedLocal(map.InstallationPath);
        string backupRootChecked=FixedLocal(backupRoot);
        string plugins=Path.Combine(install,"ShooterGame","Binaries","Win64","ArkApi","Plugins");
        string target=Path.Combine(plugins,pluginName);
        string token=Guid.NewGuid().ToString("N");
        string staging=Path.Combine(plugins,".untamed-rollback-stage-"+token);
        string displaced=Path.Combine(plugins,".untamed-rollback-old-"+token);
        string preimage=Path.Combine(backupRootChecked,map.Id.ToString("N"),
            pluginName,"prerollback-"+token);
        bool moved=false;
        DirectoryUpdateTransaction? snapshot=null;
        operation.MarkMutationStarted();
        try
        {
            // Another complete, verified copy protects current binaries AND configs.
            CopyTree(target,preimage);
            var premanifest=BackupIntegrity.Create(target,map.Id,
                "Pre-plugin-rollback full protection");
            BackupIntegrity.Verify(preimage,premanifest,map.Id);
            File.WriteAllText(preimage+".untamed-manifest.json",
                BackupIntegrity.Serialize(premanifest));
            var preparedSnapshot=new DirectoryUpdateTransaction(preimage+".transaction");
            preparedSnapshot.Prepare(target);snapshot=preparedSnapshot;
            CopyTree(target,staging);
            foreach(var file in Directory.EnumerateFiles(staging,"*",SearchOption.AllDirectories))
                if(file.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) ||
                   file.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            var prior=System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(
                File.ReadAllText(previousBackup+".untamed-manifest.json"))!;
            BackupIntegrity.Verify(previousBackup,prior,map.Id);
            foreach(var binary in prior.Files.Where(x=>
                x.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) ||
                x.Path.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase)))
            {
                string destination=Path.GetFullPath(Path.Combine(staging,binary.Path));
                if(!Inside(staging,destination))
                    throw new InvalidDataException("Backup binary escapes staging");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(previousBackup,binary.Path),destination,false);
                using var stream=File.OpenRead(destination);
                if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))!=
                    binary.Sha256)throw new InvalidDataException("Restored plugin hash mismatch");
            }
            CheckTree(staging);
            observe?.Invoke();
            Directory.Move(target,displaced);
            moved=true;
            Directory.Move(staging,target);
            observe?.Invoke();
            // If cleanup fails, the displaced directory remains safely recoverable.
            try{Directory.Delete(displaced,true);}catch{}
            snapshot.Commit();
            operation.Complete();
            return new(pluginName,target,preimage,preview.PreviousBinaryCount);
        }
        catch
        {
            observe?.Invoke();
            if(snapshot!=null)
            {
                snapshot.Rollback();
                if(moved&&Directory.Exists(displaced))Directory.Delete(displaced,true);
            }
            else if(moved && Directory.Exists(displaced))
            {
                if(Directory.Exists(target))
                    Directory.Move(target,target+".failed-"+Guid.NewGuid().ToString("N"));
                Directory.Move(displaced,target);
            }
            if(Directory.Exists(staging))
                try{Directory.Delete(staging,true);}catch{}
            throw;
        }
    }

    private static ZipArchive Open(string path)
    {
        if(!File.Exists(path))throw new FileNotFoundException("Plugin ZIP missing",path);
        var source=new FileInfo(path);
        if(source.Length>MaxPackageBytes||(source.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("ZIP too large or linked");
        return ZipFile.OpenRead(path);
    }
    private static (PluginZipInfo info,List<ZipArchiveEntry> files) Validate(ZipArchive archive)
    {
        if(archive.Entries.Count==0||archive.Entries.Count>1000)
            throw new InvalidDataException("Empty or excessive ZIP entry count");
        string? plugin=null;long sum=0;int binaries=0,ignored=0;
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files=new List<ZipArchiveEntry>();
        foreach(var entry in archive.Entries)
        {
            string name=entry.FullName;
            if(string.IsNullOrWhiteSpace(name)||name.StartsWith('/')||name.Contains('\\')||name.Contains(':')||
               name.Contains('\0'))throw new InvalidDataException("Unsafe ZIP path");
            var pieces=name.TrimEnd('/').Split('/');
            if(pieces.Length==0||(!name.EndsWith('/')&&pieces.Length<2)||pieces.Any(x=>x=="."||x==".."||!SafeWindowsPart(x)))
                throw new InvalidDataException("ZIP must contain a named plugin folder with safe paths");
            if(plugin==null)plugin=pieces[0];
            else if(!string.Equals(plugin,pieces[0],StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Multiple plugin roots not allowed");
            int type=(entry.ExternalAttributes>>16)&0xF000;
            if(type==0xA000 || ((FileAttributes)entry.ExternalAttributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked ZIP entry rejected");
            if(name.EndsWith('/'))continue;
            if(!seen.Add(name))throw new InvalidDataException("Duplicate ZIP entry");
            if(entry.Length>MaxFileBytes || entry.Length<0 || sum>MaxPackageBytes-entry.Length)
                throw new InvalidDataException("Expanded ZIP exceeds safety limit");
            sum+=entry.Length;
            if(entry.Length>1024*1024 && entry.CompressedLength>0 && entry.Length/entry.CompressedLength>200)
                throw new InvalidDataException("Excessive compression ratio");
            bool binary=name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase);
            if(binary){if(name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))binaries++;}else ignored++;
            files.Add(entry);
        }
        if(plugin!=null && plugin.Contains(VaultProtocol.ProtectedNameToken,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Untamed Cluster Vault plugin updates are protected");
        if(binaries==0 || !files.Any(x=>x.FullName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&
                                          x.FullName.Split('/').Length==2))
            throw new InvalidDataException("Plugin ZIP needs at least one root DLL");
        return (new(plugin!,binaries,ignored),files);
    }
    private static bool Inside(string parent,string child)=>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                         StringComparison.OrdinalIgnoreCase);
    private static string FixedLocal(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\"))
            throw new InvalidDataException("Fixed local path required");
        var full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Local fixed disk required");
        CheckAncestors(full);return full;
    }
    private static void CheckAncestors(string directory)
    {
        for(var current=new DirectoryInfo(Path.GetFullPath(directory));current!=null;current=current.Parent)
            if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Junction/link path rejected");
    }
    private static void CheckTree(string directory)
    {
        var root=new DirectoryInfo(directory);
        if((root.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked plugin directory");
        foreach(var file in root.GetFiles())
            if((file.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked plugin file");
        foreach(var sub in root.GetDirectories())CheckTree(sub.FullName);
    }
    private static void CopyTree(string source,string target)
    {
        CheckTree(source);
        Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(source))
            File.Copy(file,Path.Combine(target,Path.GetFileName(file)));
        foreach(var sub in Directory.EnumerateDirectories(source))
            CopyTree(sub,Path.Combine(target,Path.GetFileName(sub)));
    }
}

