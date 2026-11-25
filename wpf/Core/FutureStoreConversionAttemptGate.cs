using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace UntamedManager.Core;

/// <summary>
/// Native, stopped-world gate before any future one-shot -converttostore
/// Process.Start. The only writes are to the LOCAL Manager journal: this
/// does not start ASA, issue RCON, edit the world, UNC or Store metadata.
/// A conversion executor MUST independently recheck ownership, peer
/// quiescence, backup bytes and the durable reservation at launch time.
/// </summary>
public sealed record FutureStoreConversionReservation(
    Guid OperationId,Guid MapId,string OriginalSourceSha256,
    string BackupManifestSha256,DateTimeOffset ReservedUtc)
{
    public bool AsaStarted=>false;
    public bool WorldConverted=>false;
    public bool StoreWriterAuthorized=>false;
    public bool AutomaticRetryAllowed=>false;
}

public static class FutureStoreConversionAttemptGate
{
    private static void Deny(string code)=>throw new InvalidDataException(
        "UM-FUTURE-CONVERSION-"+code);

    private static bool Hash(string? value)=>value is {Length:64} &&
        value.All(Uri.IsHexDigit);

    private static string Root(LocalMap map)
    {
        if(map.Id==Guid.Empty||string.IsNullOrWhiteSpace(map.InstallationPath)||
           !Path.IsPathFullyQualified(map.InstallationPath)||
           map.InstallationPath.StartsWith(@"\\",StringComparison.Ordinal))
            Deny("MAP-ROOT");
        return FutureStoreInstallationPath.Canonical(map.InstallationPath);
    }

    private static void ValidateOwner(LocalMap map,
        IReadOnlyCollection<LocalMap> registeredMaps)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(registeredMaps);
        string root=Root(map);
        if(registeredMaps.Count(x=>x.Id==map.Id)==1 &&
           !registeredMaps.Contains(map) &&
           registeredMaps.Any(x=>x.Id==map.Id))
            Deny("MAP-INSTANCE-CHANGED");
        if(registeredMaps.Count(x=>x.Id==map.Id)!=1||
           registeredMaps.Any(x=>x.Id!=map.Id&&
               !string.IsNullOrWhiteSpace(x.InstallationPath)&&
               FutureStoreInstallationPath.Overlaps(x.InstallationPath,root))||
           map.HistoricalStoreProtected||
           !string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)||
           !string.IsNullOrWhiteSpace(map.FreshStoreClusterId)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           map.Maintenance||map.AutoStart||
           map.GamePort is <1024 or >65535||
           map.QueryPort is <1024 or >65535||
           map.RconPort is <1024 or >65535||
           map.GamePort==map.QueryPort||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.Arguments))
            Deny("MAP-NOT-NEW-OR-UNSAFE");
        LocalMapPortPolicy.RequireNoConflicts(map,registeredMaps);
        // Prevent unrelated or arbitrary command-line switches from being
        // carried into the future conversion launch.
        _=FreshStoreEnrollmentPolicy.ValidateBaseArguments(map,map.Arguments);
        _=AsaSaveLayoutResolver.MapToken(map);
    }

    private static void Stopped(ProcessCheck? value)
    {
        if(value==null||value.Running||!value.ConfirmedStopped||
           value.Identity!=null||value.LoaderIdentity!=null)
            throw new InvalidOperationException(
                "UM-FUTURE-CONVERSION-CONFIRMED-STOPPED-REQUIRED");
    }

    private static FutureStoreBackupProof RequireExactBackup(LocalMap map,
        string package,Func<ProcessCheck> freshProcessCheck)
    {
        Stopped(freshProcessCheck());
        var proof=FutureStoreBackupVerifier.Verify(map,
            freshProcessCheck(),package,freshProcessCheck);
        // The package integrity check proves the stored copy; prove that the
        // source bytes and INVENTORY are still identical before reserving an
        // irreversible operation. A new .arkprofile or changed INI invalidates
        // the old backup even if the manifest itself remains valid.
        string root=Root(map);
        var manifest=TestBackupPackage.Verify(proof.PackageDirectory,map.Id);
        var witnessed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in manifest.Files)
        {
            string relative=file.Path.Replace(Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
            string full=Path.GetFullPath(Path.Combine(root,relative));
            if(!full.StartsWith(root+Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)||
               !File.Exists(full)||
               (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
                Deny("WORLD-SOURCE-MISSING-OR-LINK");
            for(var dir=new DirectoryInfo(Path.GetDirectoryName(full)!);
                dir!=null && dir.FullName.Length>=root.Length;dir=dir.Parent)
            {
                if((dir.Attributes&FileAttributes.ReparsePoint)!=0)
                    Deny("WORLD-SOURCE-DIRECTORY-LINK");
                if(dir.FullName.Equals(root,StringComparison.OrdinalIgnoreCase))
                    break;
            }
            using var stream=new FileStream(full,FileMode.Open,FileAccess.Read,
                FileShare.Read,65536,FileOptions.SequentialScan);
            if(stream.Length!=file.Size||
               !Convert.ToHexString(SHA256.HashData(stream))
                   .Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))
                Deny("WORLD-SOURCE-CHANGED");
            witnessed.Add(relative);
        }
        // WorldAndProfiles selects ALL SavedArks*/SaveGames directories,
        // not merely the .ark file. A newly created profile or tribe cannot
        // slip into the pending conversion after the backup was captured.
        string saved=Path.Combine(root,"ShooterGame","Saved");
        if(!Directory.Exists(saved))Deny("WORLD-SAVED-DIRECTORY-MISSING");
        foreach(var folder in new DirectoryInfo(saved).GetDirectories()
            .Where(x=>x.Name.StartsWith("SavedArks",StringComparison.OrdinalIgnoreCase)||
                      x.Name.Equals("SaveGames",StringComparison.OrdinalIgnoreCase)))
        {
            void Visit(DirectoryInfo directory)
            {
                if((directory.Attributes&FileAttributes.ReparsePoint)!=0)
                    Deny("WORLD-DIRECTORY-LINK");
                foreach(var file in directory.GetFiles())
                {
                    if((file.Attributes&FileAttributes.ReparsePoint)!=0||
                       !witnessed.Contains(Path.GetRelativePath(root,file.FullName)))
                        Deny("WORLD-INVENTORY-CHANGED");
                }
                foreach(var child in directory.GetDirectories())Visit(child);
            }
            Visit(folder);
        }
        Stopped(freshProcessCheck());
        return proof;
    }

    /// <summary>
    /// Records independently verified backup manifest bytes in the existing
    /// durable intent. This DOES NOT claim game-side SaveComplete.
    /// </summary>
    public static FutureStoreConversionEntry RecordVerifiedBackup(
        string managerProfileRoot,LocalMap map,
        IReadOnlyCollection<LocalMap> registeredMaps,string package,
        string expectedSourceSha256,Func<ProcessCheck> freshProcessCheck)
    {
        ArgumentNullException.ThrowIfNull(freshProcessCheck);
        ValidateOwner(map,registeredMaps);
        if(!Hash(expectedSourceSha256))Deny("SOURCE-HASH");
        var intent=FutureStoreConversionJournal.Inspect(managerProfileRoot,map.Id);
        if(intent==null||intent.Phase!="prepared"||
           intent.ClusterId!=VaultProtocol.ClusterId||
           intent.OriginalSourceSha256!=expectedSourceSha256)
            throw new InvalidOperationException("UM-FUTURE-CONVERSION-INTENT-CHANGED");
        var backup=RequireExactBackup(map,package,freshProcessCheck);
        if(backup.MapId!=map.Id)Deny("BACKUP-OWNER-CHANGED");
        return FutureStoreConversionJournal.Advance(managerProfileRoot,map.Id,
            intent.OperationId,"prepared","backupVerified",backup.ManifestSha256);
    }

    /// <summary>
    /// Permanently consumes backupVerified before a future process launch.
    /// Once reserved, an interrupted conversion MUST NOT automatically retry.
    /// This call is NOT an executable launch authorization by itself.
    /// </summary>
    public static FutureStoreConversionReservation ReserveOneShot(
        string managerProfileRoot,LocalMap map,
        IReadOnlyCollection<LocalMap> registeredMaps,string package,
        string expectedSourceSha256,Func<ProcessCheck> freshProcessCheck)
    {
        ArgumentNullException.ThrowIfNull(freshProcessCheck);
        ValidateOwner(map,registeredMaps);
        if(!Hash(expectedSourceSha256))Deny("SOURCE-HASH");
        var intent=FutureStoreConversionJournal.Inspect(managerProfileRoot,map.Id);
        if(intent==null||intent.Phase!="backupVerified"||
           intent.ClusterId!=VaultProtocol.ClusterId||
           intent.OriginalSourceSha256!=expectedSourceSha256)
            throw new InvalidOperationException("UM-FUTURE-CONVERSION-BACKUP-NOT-READY");
        var backup=RequireExactBackup(map,package,freshProcessCheck);
        if(backup.MapId!=map.Id||
           !backup.ManifestSha256.Equals(intent.BackupManifestSha256,
                StringComparison.Ordinal))
            Deny("BACKUP-MANIFEST-CHANGED");
        Stopped(freshProcessCheck());
        var reserved=FutureStoreConversionJournal.Advance(managerProfileRoot,
            map.Id,intent.OperationId,"backupVerified","conversionAttempted");
        return new(reserved.OperationId,map.Id,reserved.OriginalSourceSha256,
            reserved.BackupManifestSha256,reserved.UpdatedUtc);
    }
}
