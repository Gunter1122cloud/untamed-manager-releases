using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

// Isolated test-copy workflow. Restore is intentionally absent.
public static class TestBackupPackage
{
    // These manifests originate in operator-selected OFFLINE copies. Keep
    // JSON deserialization bounded even if a damaged/malicious archive is
    // presented to the native folder picker. No player bytes are modified.
    private const long MaxManifestBytes=16L*1024*1024;

    public static void Create(string source, string destination, Guid owner, string scope)
    {
        source=LocalPath(source); destination=LocalPath(destination);
        if(destination.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
            source.Equals(destination,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup paths overlap");
        string staging=destination+".incomplete";
        if(Directory.Exists(destination) || Directory.Exists(staging))
            throw new IOException("Backup destination or interrupted staging already exists");
        var manifest=BackupIntegrity.Create(source,owner,scope);
        string manifestJson=BackupIntegrity.Serialize(manifest);
        if(Encoding.UTF8.GetByteCount(manifestJson)>MaxManifestBytes)
            throw new InvalidDataException("Offline test package manifest exceeds size limit");
        Directory.CreateDirectory(Path.Combine(staging,"data"));
        foreach(var file in manifest.Files)
        {
            string target=Path.Combine(staging,"data",file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source,file.Path),target,false);
        }
        BackupIntegrity.Verify(source,manifest,owner);
        BackupIntegrity.Verify(Path.Combine(staging,"data"),manifest,owner);
        File.WriteAllText(Path.Combine(staging,"manifest.json"),manifestJson);
        File.WriteAllText(Path.Combine(staging,"complete"),"1");
        Directory.Move(staging,destination);
    }
    public static BackupManifest Verify(string directory, Guid owner)
    {
        directory=LocalPath(directory);
        if(directory.EndsWith(".incomplete",StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(directory,"complete")))
            throw new InvalidDataException("Backup was interrupted or never committed");
        string manifestPath=Path.Combine(directory,"manifest.json");
        var info=new FileInfo(manifestPath);
        if(!info.Exists||info.Length is <2 or >MaxManifestBytes||
           (info.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Offline package manifest missing, linked or oversized");
        using var input=new FileStream(manifestPath,FileMode.Open,FileAccess.Read,
            FileShare.Read,65536,FileOptions.SequentialScan);
        // Race-safe size guard for an input changed between FileInfo and open.
        if(input.Length is <2 or >MaxManifestBytes)
            throw new InvalidDataException("Offline package manifest size changed");
        var manifest=JsonSerializer.Deserialize<BackupManifest>(input)
            ?? throw new InvalidDataException("Missing manifest");
        BackupIntegrity.Verify(Path.Combine(directory,"data"),manifest,owner);
        return manifest;
    }
    private static string LocalPath(string path)
    {
        if(!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit local test path required");
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Fixed local test disk required");
        for(var dir=new DirectoryInfo(full);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked backup path rejected");
        return full;
    }
}
