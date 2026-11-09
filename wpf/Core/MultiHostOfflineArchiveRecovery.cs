using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace UntamedManager.Core;

/// <summary>
/// Operator-reviewed recovery of the EXISTING v1 archive's .incomplete
/// checkpoint. Never rewrites a verified object, never deletes a former stage
/// and never resumes an unverified partial copy. After retaining the entire
/// interrupted stage, the normal v1 archive engine makes a fresh attempt.
/// </summary>
public static class MultiHostOfflineArchiveRecovery
{
    public sealed record Review(string Target,string Stage,string Catalog,
        Guid BatchId,int VerifiedFiles,int Components,string JournalSha256,
        string CatalogSha256);
    private const string Prefix="Untamed-MultiHost-";
    private const string Suffix=".incomplete";
    private static string Sha(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    private static bool Within(string parent,string child)=>
        string.Equals(parent,child,StringComparison.OrdinalIgnoreCase)||
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+
            Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);

    public static Review Inspect(string incompleteDirectory,string catalogFile,
        MultiHostBackupOwnershipSnapshot ownership,
        IReadOnlyCollection<string>? protectedRoots=null)
    {
        if(string.IsNullOrWhiteSpace(incompleteDirectory)||
           !Path.IsPathFullyQualified(incompleteDirectory))
            throw new InvalidDataException("UM-MULTIHOST-RECOVERY-STAGE-REQUIRED");
        string stage=Path.GetFullPath(incompleteDirectory).TrimEnd(
            Path.DirectorySeparatorChar);
        string name=Path.GetFileName(stage);
        if(!name.StartsWith(Prefix,StringComparison.Ordinal)||
           !name.EndsWith(Suffix,StringComparison.Ordinal)||
           name.Length<Prefix.Length+Suffix.Length+34)
            throw new InvalidDataException("UM-MULTIHOST-RECOVERY-STAGE-NAME");
        string target=stage[..^Suffix.Length];
        if(!Directory.Exists(stage)||Directory.Exists(target)||File.Exists(target))
            throw new InvalidDataException("UM-MULTIHOST-RECOVERY-STAGE-NOT-INTERRUPTED");
        var status=MultiHostClusterBackupArchive.InspectInterrupted(target);
        if(!status.NeedsOperator||!status.BatchId.HasValue)
            throw new InvalidDataException("UM-MULTIHOST-RECOVERY-JOURNAL-NOT-VERIFIED");
        string expectedSuffix=status.BatchId.Value.ToString("N");
        if(!Path.GetFileName(target).EndsWith("-"+expectedSuffix,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-MULTIHOST-RECOVERY-BATCH-NAME-CHANGED");
        string journal=Path.Combine(stage,"copy-journal-v1.json");
        string canonicalCatalog=Path.GetFullPath(catalogFile);
        string sourceRoot=Path.GetDirectoryName(canonicalCatalog)!;
        if(Within(sourceRoot,target)||Within(target,sourceRoot)||
           Within(sourceRoot,stage)||Within(stage,sourceRoot))
            throw new InvalidDataException("UM-MULTIHOST-RECOVERY-SOURCE-OVERLAP");
        if(protectedRoots!=null)
        {
            foreach(var raw in protectedRoots)
            {
                if(string.IsNullOrWhiteSpace(raw))continue;
                string blocked=Path.GetFullPath(raw);
                if(Within(blocked,stage)||Within(stage,blocked)||
                   Within(blocked,target)||Within(target,blocked))
                    throw new UnauthorizedAccessException(
                        "UM-MULTIHOST-RECOVERY-PRODUCTION-ROOT");
            }
        }
        var inspected=MultiHostClusterBackupArchive.InspectCatalog(
            canonicalCatalog,ownership,protectedRoots);
        if(inspected.BatchId!=status.BatchId.Value)
            throw new InvalidDataException("UM-MULTIHOST-RECOVERY-CATALOG-BATCH-MISMATCH");
        if(status.VerifiedFiles>inspected.Components)
            throw new InvalidDataException("UM-MULTIHOST-RECOVERY-IMPOSSIBLE-COUNT");
        return new(target,stage,canonicalCatalog,status.BatchId.Value,
            status.VerifiedFiles,inspected.Components,Sha(journal),Sha(canonicalCatalog));
    }

    public static FileStream AcquireLease(string verifiedTarget)
    {
        // InspectInterrupted validates FixedLocal target, including the
        // on-disk fixed-drive and reparse-path requirements. No recovery
        // stage may be touched without that initial validation.
        string target=Path.GetFullPath(verifiedTarget);
        string file=target+".recovery-lease";
        if(File.Exists(file)&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-MULTIHOST-RECOVERY-LINKED-LEASE");
        try
        {
            return new FileStream(file,FileMode.OpenOrCreate,FileAccess.ReadWrite,
                FileShare.None,4096,FileOptions.WriteThrough);
        }
        catch(IOException)
        {
            throw new IOException("UM-MULTIHOST-RECOVERY-LEASE-BUSY");
        }
    }

    public static string RetainInterrupted(Review approved,
        MultiHostBackupOwnershipSnapshot ownership,
        IReadOnlyCollection<string>? protectedRoots=null)
    {
        var now=Inspect(approved.Stage,approved.Catalog,ownership,protectedRoots);
        if(now.Target!=approved.Target||now.BatchId!=approved.BatchId||
           now.VerifiedFiles!=approved.VerifiedFiles||
           now.Components!=approved.Components||
           now.JournalSha256!=approved.JournalSha256||
           now.CatalogSha256!=approved.CatalogSha256)
            throw new IOException("UM-MULTIHOST-RECOVERY-CHANGED-SINCE-APPROVAL");
        string retained=approved.Target+".retained-"+Guid.NewGuid().ToString("N");
        if(Directory.Exists(retained)||File.Exists(retained))
            throw new IOException("UM-MULTIHOST-RECOVERY-RETAINED-ALREADY-EXISTS");
        // A file-system directory rename does not destroy the verified partial
        // archive or its checkpoint. It remains available for later inspection.
        Directory.Move(approved.Stage,retained);
        return Path.GetFileName(retained);
    }
}
