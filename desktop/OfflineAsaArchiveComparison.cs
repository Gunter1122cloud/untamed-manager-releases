using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Compares two individually SEALED, local, offline ten-map cluster archives.
/// No ASA process, shared transfer directory, Store session or writer is opened.
/// File names in shared transfer data never leave this process.
/// </summary>
public static class OfflineAsaArchiveComparison
{
    public sealed record MapDelta(string Host,Guid MapId,string Name,
        int FilesAdded,int FilesRemoved,int FilesModified,
        bool WorldChanged,int WorldFilesBefore,int WorldFilesAfter);
    public sealed record Report(Guid BeforeBatch,Guid AfterBatch,int Maps,
        int TransferFilesBefore,int TransferFilesAfter,
        int TransferAdded,int TransferRemoved,int TransferModified,
        int ZeroByteTransferFilesPreserved,
        int ConfigurationAdded,int ConfigurationRemoved,int ConfigurationModified,
        bool ConfigurationCoverageChanged,bool AnyChanges,
        bool BothArchivesSha256Verified,bool LiveConsistencyCertified,
        bool StoreWriterCertified,bool CharacterTravelVerified,
        bool ProductionRestoreAllowed,MapDelta[] Results,string Evidence);

    private sealed record Changes(int Added,int Removed,int Modified)
    {
        public bool Any => Added+Removed+Modified>0;
    }
    private static readonly StringComparer Names=StringComparer.OrdinalIgnoreCase;
    private const string Evidence="TWO_SEALED_OFFLINE_ARCHIVE_FILE_DIFFERENCES_ONLY";

    private static Dictionary<string,(long Size,string Sha)> Index<T>(
        IEnumerable<T> files,Func<T,string> path,Func<T,long> size,Func<T,string> hash)
    {
        var index=new Dictionary<string,(long Size,string Sha)>(Names);
        foreach(var f in files)
        {
            string name=path(f);
            if(string.IsNullOrWhiteSpace(name) ||
               !index.TryAdd(name,(size(f),hash(f))))
                throw new InvalidDataException("Duplicate or empty archive member identity");
        }
        return index;
    }
    private static Changes Diff(
        Dictionary<string,(long Size,string Sha)> before,
        Dictionary<string,(long Size,string Sha)> after)
    {
        int added=0,removed=0,modified=0;
        foreach(var (name,old) in before)
        {
            if(!after.TryGetValue(name,out var now))removed++;
            else if(now.Size!=old.Size || !Names.Equals(now.Sha,old.Sha))modified++;
        }
        foreach(string name in after.Keys)
            if(!before.ContainsKey(name))added++;
        return new(added,removed,modified);
    }
    private static BackupManifest Manifest(string archive,AsaOfflineMap map)
    {
        string root=Path.GetFullPath(archive).TrimEnd(Path.DirectorySeparatorChar);
        string hostFolder=Path.GetFullPath(Path.Combine(root,map.PackageRelativePath));
        if(!hostFolder.StartsWith(root+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe archive map location");
        string path=Path.Combine(hostFolder,"manifest.json");
        var bytes=File.ReadAllBytes(path);
        if(bytes.Length>12_000_000 ||
           !Convert.ToHexString(SHA256.HashData(bytes))
             .Equals(map.ManifestSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive map manifest changed during comparison");
        var manifest=JsonSerializer.Deserialize<BackupManifest>(bytes)
            ??throw new InvalidDataException("Unreadable archive map manifest");
        if(manifest.MapId!=map.Map || manifest.Files==null)
            throw new InvalidDataException("Archive map ownership changed");
        return manifest;
    }
    private static Dictionary<string,(long Size,string Sha)> Maps(BackupManifest m)=>
        Index(m.Files,x=>x.Path,x=>x.Size,x=>x.Sha256);
    private static Dictionary<string,(long Size,string Sha)> Worlds(BackupManifest m)=>
        Index(m.Files.Where(x=>x.Path.EndsWith(".ark",StringComparison.OrdinalIgnoreCase)),
            x=>x.Path,x=>x.Size,x=>x.Sha256);

    public static Report Compare(string beforeArchive,string afterArchive)
    {
        if(string.IsNullOrWhiteSpace(beforeArchive)||
           string.IsNullOrWhiteSpace(afterArchive)||
           Path.GetFullPath(beforeArchive).Equals(Path.GetFullPath(afterArchive),
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Two distinct local offline archives required");

        // Full SHA-256 verification includes ten world packages, the complete
        // copied tribute/transfer tree and optional config trees on both hosts.
        var before=OfflineAsaClusterArchive.Verify(beforeArchive);
        var after=OfflineAsaClusterArchive.Verify(afterArchive);
        if(before.Batch==after.Batch || after.CreatedUtc<before.CreatedUtc)
            throw new InvalidDataException("The second archive must be a newer distinct batch");

        var beforeMaps=before.Maps.ToDictionary(x=>x.Map);
        var afterMaps=after.Maps.ToDictionary(x=>x.Map);
        if(beforeMaps.Count!=10 || afterMaps.Count!=10 ||
           !beforeMaps.Keys.ToHashSet().SetEquals(afterMaps.Keys) ||
           beforeMaps.Any(x=>x.Value.Host!=afterMaps[x.Key].Host))
            throw new InvalidDataException("Both archives must contain identical ten-map host ownership");

        var results=new List<MapDelta>();
        foreach(var item in beforeMaps.Values.OrderBy(x=>x.Host,Names).ThenBy(x=>x.Name,Names))
        {
            var other=afterMaps[item.Map];
            var oldManifest=Manifest(beforeArchive,item);
            var newManifest=Manifest(afterArchive,other);
            var changes=Diff(Maps(oldManifest),Maps(newManifest));
            var oldWorlds=Worlds(oldManifest);
            var newWorlds=Worlds(newManifest);
            results.Add(new(item.Host,item.Map,other.Name,changes.Added,
                changes.Removed,changes.Modified,Diff(oldWorlds,newWorlds).Any,
                oldWorlds.Count,newWorlds.Count));
        }

        var oldTransfer=Index(before.Transfer,x=>x.RelativePath,x=>x.Bytes,x=>x.Sha256);
        var newTransfer=Index(after.Transfer,x=>x.RelativePath,x=>x.Bytes,x=>x.Sha256);
        var transfer=Diff(oldTransfer,newTransfer);
        int unchangedEmpty=oldTransfer.Count(x=>x.Value.Size==0 &&
            newTransfer.TryGetValue(x.Key,out var next) && next.Size==0 &&
            Names.Equals(x.Value.Sha,next.Sha));

        var configs=Diff(
            Index(before.Config??Array.Empty<AsaOfflineConfig>(),
                x=>x.RelativePath,x=>x.Bytes,x=>x.Sha256),
            Index(after.Config??Array.Empty<AsaOfflineConfig>(),
                x=>x.RelativePath,x=>x.Bytes,x=>x.Sha256));
        bool coverage=(before.Config is {Length:>0})!=(after.Config is {Length:>0});
        return new(before.Batch,after.Batch,results.Count,
            before.Transfer.Length,after.Transfer.Length,
            transfer.Added,transfer.Removed,transfer.Modified,unchangedEmpty,
            configs.Added,configs.Removed,configs.Modified,coverage,
            transfer.Any||configs.Any||results.Any(x=>x.FilesAdded+x.FilesRemoved+
                x.FilesModified>0),true,false,false,false,false,
            results.ToArray(),Evidence);
    }
}
