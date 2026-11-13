using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

// Real SHA-verified ARCHIVE COPYING from ten previously produced, stopped-map
// offline backup packages + three previously exported offline SQL packages.
// This is *not* a two-host live/consistent backup, and NEVER touches Kova Store.
// Can run on a separate Windows test PC after the 13 inputs have been copied there.
public sealed record OfflineMapBackupInput(string Host,Guid Map,string Name,string Package);
public sealed record OfflineSqlBackupInput(string Database,Guid Owner,string Package);
public sealed record OfflineClusterBackupRequest(
    OfflineMapBackupInput[] Maps,OfflineSqlBackupInput[] Sql);
public sealed record OfflineClusterBackupMember(string Host,Guid Owner,string Name,
    string RelativeDirectory,string ManifestSha256,int FileCount,long DataBytes);
public sealed record OfflineClusterBackupManifest(
    int Schema,Guid Batch,DateTimeOffset CreatedUtc,
    OfflineClusterBackupMember[] Items,bool RealAsaConsistencyProven,
    [property:System.Text.Json.Serialization.JsonPropertyName("KovaStoreIncluded")]
    bool VaultStoreIncluded,bool MariaDbSnapshotConsistencyProven,string Disclaimer);
public static class OfflineClusterBackup
{
    // Four-folder Windows wizard: select previously copied, independently
    // verified packages without editing JSON, credentials or Kova Store files.
    // Folder convention: HP/<five package dirs>, RYZEN/<five package dirs>,
    // SQL/arkshop, SQL/arkpermissions, SQL/lethalquests.
    public static OfflineClusterBackupManifest BuildFromFolders(
        string hpRoot,string ryzenRoot,string sqlRoot,string destination)
    {
        string hp=Fixed(hpRoot),ryzen=Fixed(ryzenRoot),sql=Fixed(sqlRoot);
        if(new[]{hp,ryzen,sql}.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=3)
            throw new InvalidDataException("HP, Ryzen and SQL source roots must be distinct");
        foreach(var x in new[]{hp,ryzen,sql})
            if(!Directory.Exists(x))
                throw new DirectoryNotFoundException("Missing offline source folder: "+x);
        OfflineMapBackupInput[] ScanHost(string directory,string role)
        {
            var list=Directory.GetDirectories(directory);
            if(list.Length!=5)throw new InvalidDataException(role+" requires exactly five copied map backups");
            return list.Select(package=>
            {
                string name=Path.GetFileName(package);
                if((File.GetAttributes(package)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Linked offline map backup folder");
                var meta=ReadManifest(package);
                TestBackupPackage.Verify(package,meta.MapId);
                return new OfflineMapBackupInput(role,meta.MapId,name,package);
            }).ToArray();
        }
        var mapInputs=ScanHost(hp,"HP").Concat(ScanHost(ryzen,"RYZEN")).ToArray();
        var sqlInputs=OfflineSqlArchive.SupportedDatabases.Select(name=>
        {
            string package=Path.Combine(sql,name);
            if(!Directory.Exists(package))
                throw new DirectoryNotFoundException("Missing SQL package: "+name);
            var meta=ReadManifest(package);
            OfflineSqlArchive.Verify(package,name,meta.MapId);
            return new OfflineSqlBackupInput(name,meta.MapId,package);
        }).ToArray();
        return Build(new(mapInputs,sqlInputs),destination);
    }
    private static BackupManifest ReadManifest(string package)
    {
        string path=Path.Combine(Fixed(package),"manifest.json");
        if(!File.Exists(path)||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0 ||
           new FileInfo(path).Length>5_000_000)
            throw new InvalidDataException("Missing or oversized offline package manifest");
        var info=JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path))
            ??throw new InvalidDataException("Unreadable offline package manifest");
        if(info.Version!=1 ||info.MapId==Guid.Empty ||info.Files==null)
            throw new InvalidDataException("Invalid offline package manifest identity");
        return info;
    }

    private const string Warning="Complete 10-map/3-SQL OFFLINE FILE INTEGRITY COPY. "+
        "Not a consistent live ASA snapshot, not a Untamed Cluster Vault backup and not an SQL transaction.";
    public static OfflineClusterBackupManifest Build(OfflineClusterBackupRequest request,
        string destination)
    {
        if(request==null)throw new ArgumentNullException(nameof(request));
        var maps=request.Maps??throw new InvalidDataException("Map package list missing");
        var sql=request.Sql??throw new InvalidDataException("SQL package list missing");
        ValidateInputs(maps,sql);
        string target=Fixed(destination);
        string staging=target+".incomplete";
        if(Directory.Exists(target)||Directory.Exists(staging)||
           File.Exists(target)||File.Exists(staging))
            throw new IOException("Existing destination or incomplete backup requires operator review");
        var sources=maps.Select(m=>Fixed(m.Package)).Concat(sql.Select(s=>Fixed(s.Package)))
            .ToArray();
        if(sources.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=13)
            throw new InvalidDataException("Duplicate offline input backup packages");
        foreach(var source in sources)
        {
            if(!Directory.Exists(source)||Inside(source,target)||Inside(target,source)||
               Inside(source,staging)||Inside(staging,source))
                throw new InvalidDataException("Invalid source/target overlap or missing package");
        }
        long required=0;
        for(int i=0;i<maps.Length;i++)
        {
            var manifest=TestBackupPackage.Verify(sources[i],maps[i].Map);
            if(manifest.Files.Count==0)throw new InvalidDataException("Empty map backup package");
            required=checked(required+manifest.Files.Sum(e=>e.Size));
        }
        for(int i=0;i<sql.Length;i++)
        {
            var proof=OfflineSqlArchive.Verify(sources[maps.Length+i],sql[i].Database,sql[i].Owner);
            required=checked(required+proof.Bytes);
        }
        var root=Path.GetPathRoot(target)!;
        var free=new DriveInfo(root).AvailableFreeSpace;
        long reserve=Math.Max(64L*1024*1024,required/10);
        if(required>free || reserve>free-required)
            throw new IOException("Insufficient fixed local disk capacity for offline cluster copy");
        var entries=new List<OfflineClusterBackupMember>();
        Directory.CreateDirectory(staging);
        try
        {
            for(int i=0;i<maps.Length;i++)
            {
                var item=maps[i];string rel=Path.Combine(item.Host.ToLowerInvariant(),item.Map.ToString("N"));
                string copied=Path.Combine(staging,rel);
                CopyArchive(sources[i],copied);
                var verified=TestBackupPackage.Verify(copied,item.Map);
                var original=TestBackupPackage.Verify(sources[i],item.Map);
                CheckEquivalent(original,verified);
                entries.Add(new(item.Host,item.Map,item.Name,rel,
                    Sha(Path.Combine(copied,"manifest.json")),verified.Files.Count,
                    verified.Files.Sum(x=>x.Size)));
            }
            for(int i=0;i<sql.Length;i++)
            {
                var item=sql[i];string rel=Path.Combine("sql",item.Database);
                string copied=Path.Combine(staging,rel);
                CopyArchive(sources[maps.Length+i],copied);
                var verified=OfflineSqlArchive.Verify(copied,item.Database,item.Owner);
                var original=OfflineSqlArchive.Verify(sources[maps.Length+i],item.Database,item.Owner);
                if(original.Sha256!=verified.Sha256 || original.Bytes!=verified.Bytes)
                    throw new InvalidDataException("Offline SQL copy differs from source");
                entries.Add(new("SQL",item.Owner,item.Database,rel,
                    Sha(Path.Combine(copied,"manifest.json")),1,verified.Bytes));
            }
            var output=new OfflineClusterBackupManifest(1,Guid.NewGuid(),
                DateTimeOffset.UtcNow,entries.ToArray(),false,false,false,Warning);
            string journal=Path.Combine(staging,"cluster-index.json");
            using(var file=new FileStream(journal,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(file,output);file.Flush(true);}
            File.WriteAllText(Path.Combine(staging,"cluster-index.sha256"),Sha(journal));
            File.WriteAllText(Path.Combine(staging,"complete"),"1");
            Verify(staging);
            Directory.Move(staging,target);
            return Verify(target);
        }
        catch
        {
            // An incomplete staging remains for forensic review. It is NEVER
            // promoted to a successful backup and none of the sources are moved.
            throw;
        }
    }

    public static OfflineClusterBackupManifest Verify(string package)
    {
        string root=Fixed(package);
        string manifestFile=Path.Combine(root,"cluster-index.json");
        if(!Directory.Exists(root)||!File.Exists(Path.Combine(root,"complete"))||
           !File.Exists(manifestFile)||!File.Exists(Path.Combine(root,"cluster-index.sha256")) ||
           !string.Equals(File.ReadAllText(Path.Combine(root,"cluster-index.sha256")).Trim(),
               Sha(manifestFile),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cluster archive incomplete or index hash mismatch");
        if(new FileInfo(manifestFile).Length>1_000_000)
            throw new InvalidDataException("Excessive offline cluster index");
        var report=JsonSerializer.Deserialize<OfflineClusterBackupManifest>(
            File.ReadAllText(manifestFile))??throw new InvalidDataException("Broken archive index");
        if(report.Schema!=1||report.Batch==Guid.Empty ||report.Items==null||
           report.Items.Length!=13 ||report.RealAsaConsistencyProven||
           report.VaultStoreIncluded||report.MariaDbSnapshotConsistencyProven ||
           report.Disclaimer!=Warning)
            throw new InvalidDataException("Offline archive must not claim certified live backup");
        var maps=report.Items.Where(e=>e.Host is "HP" or "RYZEN").ToArray();
        var sql=report.Items.Where(e=>e.Host=="SQL").ToArray();
        if(maps.Length!=10 ||maps.Count(x=>x.Host=="HP")!=5||
           maps.Count(x=>x.Host=="RYZEN")!=5 ||
           maps.Select(x=>x.Owner).Distinct().Count()!=10||
           maps.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=10||
           sql.Length!=3 ||sql.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=3 ||
           sql.Any(x=>!OfflineSqlArchive.IsSupported(x.Name)))
            throw new InvalidDataException("Offline cluster archive missing required host/map/SQL members");
        foreach(var item in report.Items)
        {
            if(item.Owner==Guid.Empty||item.FileCount<=0||item.DataBytes<=0||
               string.IsNullOrWhiteSpace(item.RelativeDirectory)||
               Path.IsPathFullyQualified(item.RelativeDirectory)||
               item.RelativeDirectory.Contains("..",StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe archive member identity");
            string member=Path.GetFullPath(Path.Combine(root,item.RelativeDirectory));
            if(!Inside(root,member))
                throw new InvalidDataException("Archive member escapes package root");
            if(Sha(Path.Combine(member,"manifest.json"))!=item.ManifestSha256)
                throw new InvalidDataException("Archive member manifest changed");
            if(item.Host=="SQL")
            {
                var sqlProof=OfflineSqlArchive.Verify(member,item.Name,item.Owner);
                if(sqlProof.Bytes!=item.DataBytes ||item.FileCount!=1)
                    throw new InvalidDataException("Archive SQL file count/size mismatch");
            }
            else
            {
                var proof=TestBackupPackage.Verify(member,item.Owner);
                if(proof.Files.Count!=item.FileCount ||
                   proof.Files.Sum(x=>x.Size)!=item.DataBytes)
                    throw new InvalidDataException("Archive map files/bytes mismatch");
            }
        }
        return report;
    }

    private static void ValidateInputs(OfflineMapBackupInput[] maps,OfflineSqlBackupInput[] sql)
    {
        if(maps.Length!=10||sql.Length!=3||
           maps.Count(m=>m.Host=="HP")!=5 || maps.Count(m=>m.Host=="RYZEN")!=5||
           maps.Any(m=>m.Map==Guid.Empty||string.IsNullOrWhiteSpace(m.Name))||
           maps.Select(m=>m.Map).Distinct().Count()!=10||
           maps.Select(m=>m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=10||
           sql.Any(s=>s.Owner==Guid.Empty||!OfflineSqlArchive.IsSupported(s.Database))||
           sql.Select(s=>s.Database).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=3)
            throw new InvalidDataException("Exactly 5 HP, 5 Ryzen and 3 distinct supported SQL packages required");
    }
    private static void CopyArchive(string source,string target)
    {
        Directory.CreateDirectory(Path.Combine(target,"data"));
        string manifest=Path.Combine(source,"manifest.json");
        if(!File.Exists(manifest)||!File.Exists(Path.Combine(source,"complete")))
            throw new InvalidDataException("Offline package missing manifest or completion marker");
        var meta=JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifest))
            ??throw new InvalidDataException("Invalid source package manifest");
        if(meta.Files.Count is < 1 or > 50000)
            throw new InvalidDataException("Unsupported offline package file count");
        foreach(var item in meta.Files)
        {
            string from=Path.GetFullPath(Path.Combine(source,"data",item.Path));
            string to=Path.GetFullPath(Path.Combine(target,"data",item.Path));
            if(!Inside(Path.Combine(source,"data"),from)||
               !Inside(Path.Combine(target,"data"),to))
                throw new InvalidDataException("Offline package path traversal");
            if((File.GetAttributes(from)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked offline archive source rejected");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from,to,false);
        }
        File.Copy(manifest,Path.Combine(target,"manifest.json"),false);
        File.WriteAllText(Path.Combine(target,"complete"),"1");
    }
    private static void CheckEquivalent(BackupManifest a,BackupManifest b)
    {
        if(a.MapId!=b.MapId||a.Scope!=b.Scope||a.Files.Count!=b.Files.Count||
           !a.Files.SequenceEqual(b.Files))
            throw new InvalidDataException("Offline package source differs from staged copy");
    }
    private static string Sha(string file)
    {
        if(!File.Exists(file)||(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Missing/linked offline archive index file");
        using var input=File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private static string Fixed(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
           path.StartsWith(@"\\"))
            throw new InvalidDataException("Only explicit local archive paths allowed");
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Archive requires local fixed disk");
        for(var dir=new DirectoryInfo(full);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked archive directory rejected");
        return full;
    }
    private static bool Inside(string root,string child)=>
        child.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

