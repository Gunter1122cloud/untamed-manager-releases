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
    public static PluginInstallResult Install(LocalMap map,ProcessCheck check,string zipPath,string backupRoot)
    {
        if(map==null||map.Id==Guid.Empty)throw new InvalidDataException("Map identity required");
        if(!check.ConfirmedStopped||check.Running)
            throw new InvalidOperationException("Plugin update requires confirmed stopped local server");
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
        bool moved=false, installed=false;
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
            if(Directory.Exists(target)){Directory.Move(target,displaced);moved=true;}
            Directory.Move(staging,target);installed=true;
            if(moved&&Directory.Exists(displaced))Directory.Delete(displaced,true);
            return new(info.PluginName,target,backup,info.BinaryCount);
        }
        catch
        {
            if(moved&&Directory.Exists(displaced))
            {
                if(Directory.Exists(target))Directory.Delete(target,true);
                Directory.Move(displaced,target);
            }
            else if(installed&&Directory.Exists(target))Directory.Delete(target,true);
            if(Directory.Exists(staging))Directory.Delete(staging,true);
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
        if(plugin!=null && plugin.Contains("Kova",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("KovaCluster and Kova-related plugin updates are protected");
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
