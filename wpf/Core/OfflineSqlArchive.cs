using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace UntamedManager.Core;

// Offline .sql archive only; does not connect to MariaDB or invoke mysqldump/mysql.
// Explicit user-selected already-exported files are read and copied to a new
// locally isolated Manager archive with SHA-256 and interruption detection.
public sealed record OfflineSqlArchiveResult(string Database,string Package,
    long Bytes,string Sha256,bool Verified,string Scope);
public static class OfflineSqlArchive
{
    public static readonly string[] SupportedDatabases={"arkshop","arkpermissions","lethalquests"};
    public static bool IsSupported(string name) =>
        SupportedDatabases.Contains(name,StringComparer.OrdinalIgnoreCase);

    public static OfflineSqlArchiveResult ImportFixture(
        string profileRoot,string sqlFile,string database,Guid localOwner)
    {
        if(localOwner==Guid.Empty || !IsSupported(database))
            throw new InvalidDataException("Explicit local host and supported database required");
        string profile=Fixed(profileRoot),source=Fixed(sqlFile);
        if(!File.Exists(source) || !source.EndsWith(".sql",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select an existing offline .sql export");
        if(source.Contains(VaultProtocol.ProtectedNameToken,StringComparison.OrdinalIgnoreCase) ||
           source.Contains("Store",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Untamed Cluster Vault paths are not SQL fixture sources");
        if(!string.Equals(Path.GetFileNameWithoutExtension(source),database,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Offline SQL fixture filename must match selected database");
        var sourceInfo=new FileInfo(source);
        if(sourceInfo.Length==0 || sourceInfo.Length>512L*1024*1024)
            throw new InvalidDataException("Unsupported SQL fixture size");
        string archiveRoot=Path.Combine(profile,"offline-sql-archives");
        if(source.StartsWith(archiveRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
           profile.StartsWith(Path.GetDirectoryName(source)!+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQL source and archive profile overlap");
        var drive=new DriveInfo(Path.GetPathRoot(profile)!);
        if(drive.AvailableFreeSpace<sourceInfo.Length+64L*1024*1024)
            throw new IOException("Insufficient free disk space for SQL fixture archive");
        string id=Guid.NewGuid().ToString("N"),package=Path.Combine(archiveRoot,database+"-"+id);
        string staging=Path.Combine(archiveRoot,".fixture-stage-"+id);
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(staging);
        try
        {
            string isolated=Path.Combine(staging,database+".sql");
            File.Copy(source,isolated,false);
            TestBackupPackage.Create(staging,package,localOwner,
                "Offline SQL fixture only: "+database+"; not a verified live MariaDB snapshot");
            var verified=Verify(package,database,localOwner);
            using var sourceStream=File.OpenRead(source);
            string sourceHash=Convert.ToHexString(SHA256.HashData(sourceStream));
            if(verified.Sha256!=sourceHash)throw new IOException("SQL source changed during archive");
            return verified;
        }
        finally
        {
            if(Directory.Exists(staging))Directory.Delete(staging,true);
        }
    }
    public static OfflineSqlArchiveResult Verify(string package,string database,Guid owner)
    {
        if(!IsSupported(database) || owner==Guid.Empty)
            throw new InvalidDataException("Unsupported SQL archive ownership");
        string root=Fixed(package);
        var manifest=TestBackupPackage.Verify(root,owner);
        if(manifest.Files.Count!=1 ||
           !manifest.Scope.StartsWith("Offline SQL fixture only: "+database+";",
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQL archive scope is not verified");
        var file=manifest.Files.Single();
        if(file.Path!=database+".sql" || file.Size==0)
            throw new InvalidDataException("SQL archive content does not match its database identity");
        return new(database,root,file.Size,file.Sha256,true,
            "Offline SQL fixture hash only – live consistency and import unproven");
    }
    public static string SimulateRestore(string package,string database,Guid owner)
    {
        var verified=Verify(package,database,owner);
        // Only a freshly allocated temp sandbox, never a database or production directory.
        string sandbox=Path.Combine(Path.GetTempPath(),
            "Untamed-OfflineSql-Restore-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        string output=Path.Combine(sandbox,database+".sql");
        try
        {
            File.Copy(Path.Combine(verified.Package,"data",database+".sql"),output,false);
            using var stream=File.OpenRead(output);
            if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(verified.Sha256,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Restored SQL test file hash mismatch");
            return output;
        }
        catch
        {
            try{Directory.Delete(sandbox,true);}catch{}
            throw;
        }
    }
    private static string Fixed(string path)
    {
        if(string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
           path.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit local SQL test path required");
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Offline SQL fixture requires a fixed disk");
        for(var dir=new DirectoryInfo(full);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked or redirected SQL fixture path rejected");
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked SQL fixture rejected");
        return full;
    }
}

