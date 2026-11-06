using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UntamedManager.Core;

// T07 import bridge: a user-prepared 37-file manifest on the third isolated
// Windows machine can now drive the real verified offline archive copier.
// This JSON belongs to Untamed; it does not modify legacy Store-v1 files.
public sealed record ClusterBatchCatalogItem(ClusterBackupComponent Component,
    string RelativeSource);
public sealed record ClusterBatchCatalog(int Schema,Guid BatchId,
    TransferMapOwner[] Maps,ClusterBatchCatalogItem[] Items,
    bool LiveConsistencyProven,string Scope);

public static class ClusterBatchCatalogImport
{
    private const string Scope="UNTAMED-OFFLINE-37-CATALOG-V1";

    public static ClusterBatchArchiveManifest BuildFromCatalog(string inputFile,string destination)
    {
        if(string.IsNullOrWhiteSpace(inputFile)||!Path.IsPathFullyQualified(inputFile)||
           inputFile.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("Local catalog file path is required");
        string source=Path.GetFullPath(inputFile);
        if(!source.EndsWith(".json",StringComparison.OrdinalIgnoreCase) ||
           !File.Exists(source) ||
           (File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0 ||
           new FileInfo(source).Length is < 32 or > 1_000_000)
            throw new InvalidDataException("Missing, linked or oversized offline catalog");
        string root=Path.GetDirectoryName(source)!;
        UntamedOperationCoordinator.ResourceKey(root);
        ClusterBatchCatalog catalog;
        try
        {
            using var stream=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
            catalog=JsonSerializer.Deserialize<ClusterBatchCatalog>(stream) ??
                throw new InvalidDataException("Empty offline catalog");
        }
        catch(JsonException){throw new InvalidDataException("Invalid offline catalog JSON");}

        if(catalog.Schema!=1||catalog.BatchId==Guid.Empty||catalog.LiveConsistencyProven||
           catalog.Scope!=Scope||catalog.Maps==null||catalog.Items==null||
           catalog.Items.Length!=37||
           catalog.Items.Any(item=>item==null||item.Component==null) ||
           !ClusterBackupInventory.Verify(catalog.BatchId,catalog.Maps,
               catalog.Items.Select(x=>x.Component).ToArray()).Complete)
            throw new InvalidDataException("Unverified offline batch catalog membership");

        var sources=catalog.Items.Select(item=>
        {
            string relative=item.RelativeSource;
            if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||
               relative.Contains(':')||
               relative.Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries)
                   .Any(p=>p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Contains('~')))
                throw new InvalidDataException("Unsafe catalog-relative source path");
            string full=Path.GetFullPath(Path.Combine(root,relative));
            if(!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+
                Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
               !File.Exists(full)||(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Catalog source is missing, linked or outside its directory");
            // This rejects junctions and drive aliases before the file is opened.
            UntamedOperationCoordinator.ResourceKey(full);
            return new ClusterBatchSource(item.Component,full);
        }).ToArray();
        return ClusterBatchArchive.BuildOffline(catalog.BatchId,catalog.Maps,sources,destination);
    }

    // Produces a self-contained explicit catalog on a separate operator-owned
    // test disk; this is a helper for creation, never an automatic Store import.
    public static string CatalogScope => Scope;
}
