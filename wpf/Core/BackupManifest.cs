using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record BackupEntry(string Path, long Size, string Sha256);
public sealed record BackupManifest(int Version, Guid MapId, string Scope, DateTimeOffset CreatedUtc,
    List<BackupEntry> Files);

// Integrity primitive only: this does not claim a consistent live-world backup or authorize restore.
public static class BackupIntegrity
{
    public static BackupManifest Create(string directory, Guid mapId, string scope)
    {
        if(mapId==Guid.Empty || string.IsNullOrWhiteSpace(scope))
            throw new InvalidDataException("Backup ownership and scope are required");
        return new(1,mapId,scope,DateTimeOffset.UtcNow,Scan(directory));
    }
    public static void Verify(string directory, BackupManifest manifest, Guid mapId)
    {
        // JSON record's Files field can be explicitly null despite C#'s
        // non-nullable declaration. Reject malformed inputs BEFORE scanning
        // large world directories or dereferencing invalid entries.
        if(manifest==null || manifest.Version!=1 || manifest.MapId!=mapId ||
           string.IsNullOrWhiteSpace(manifest.Scope) || manifest.Files==null ||
           manifest.Files.Any(x=>x is null || string.IsNullOrWhiteSpace(x.Path) ||
               x.Size<0 || x.Sha256 is null || x.Sha256.Length!=64 ||
               !x.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Backup manifest identity or members invalid");
        var actual=Scan(directory);
        if(manifest.Files.Count!=actual.Count ||
            !manifest.Files.OrderBy(x=>x.Path,StringComparer.Ordinal).SequenceEqual(actual))
            throw new InvalidDataException("Backup missing, additional or corrupt files");
    }
    public static string Serialize(BackupManifest manifest) => JsonSerializer.Serialize(manifest);
    private static List<BackupEntry> Scan(string directory)
    {
        var result=new List<BackupEntry>();
        var root=new DirectoryInfo(Path.GetFullPath(directory));
        void Visit(DirectoryInfo dir)
        {
            if((dir.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked backup directory rejected");
            foreach(var file in dir.GetFiles())
            {
                if((file.Attributes & FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Linked backup file rejected");
                using var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);
                result.Add(new(Path.GetRelativePath(root.FullName,file.FullName),stream.Length,
                    Convert.ToHexString(SHA256.HashData(stream))));
            }
            foreach(var child in dir.GetDirectories())Visit(child);
        }
        Visit(root);
        return result.OrderBy(x=>x.Path,StringComparer.Ordinal).ToList();
    }
}
