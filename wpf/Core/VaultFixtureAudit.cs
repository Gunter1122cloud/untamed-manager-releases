using System;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// Does NOT parse, migrate or implement any KovaCluster Store format.
// This deliberately accepts only explicit TEMP synthetic copies of the Store,
// not home-host shares or current cluster/character save directories.
public sealed record VaultFixtureReport(string Source,Guid TestOwner,int Files,long Bytes,
    bool FormatCompatible,string Warning);
public static class VaultFixtureAudit
{
    public static VaultFixtureReport Inspect(string testRoot,Guid testOwner)
    {
        if(testOwner==Guid.Empty || string.IsNullOrWhiteSpace(testRoot) ||
           !Path.IsPathFullyQualified(testRoot)||testRoot.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit local synthetic Untamed Cluster Vault test copy required");
        string full=Path.GetFullPath(testRoot).TrimEnd(Path.DirectorySeparatorChar);
        string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            +Path.DirectorySeparatorChar;
        if(!full.StartsWith(temp+"Untamed-Vault-Fixture-",
            StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only synthetic Untamed Cluster Vault fixture below TEMP is permitted");
        if(!Directory.Exists(full))throw new DirectoryNotFoundException(full);
        var manifest=BackupIntegrity.Create(full,testOwner,
            "Synthetic Untamed Cluster Vault file hash audit only; compatibility NOT proven");
        if(manifest.Files.Count==0 || manifest.Files.Count>5000)
            throw new InvalidDataException("Synthetic Untamed Cluster Vault fixture count invalid");
        if(manifest.Files.Any(x=>x.Path.Contains("..") || x.Size<0))
            throw new InvalidDataException("Synthetic Store fixture contains unsafe files");
        return new(full,testOwner,manifest.Files.Count,manifest.Files.Sum(x=>x.Size),
            false,"Exact file bytes can be verified; Untamed Cluster Vault schema and 1:1 compatibility remain unverified");
    }
    public static string Snapshot(string fixtureRoot,Guid testOwner,string destination)
    {
        Inspect(fixtureRoot,testOwner);
        TestBackupPackage.Create(fixtureRoot,destination,testOwner,
            "Synthetic Untamed Cluster Vault fixture byte-preserving copy; NO schema conversion");
        var package=TestBackupPackage.Verify(destination,testOwner);
        if(package.Files.Count==0)throw new InvalidDataException("Empty synthetic Untamed Cluster Vault snapshot");
        return destination;
    }
}

