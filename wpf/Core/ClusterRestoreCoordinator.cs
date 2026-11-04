using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

// T09: disk-backed rehydration of the existing 10-map/3-SQL archive into
// a freshly-created third-machine TEMP sandbox. This never writes a registered
// ASA installation, SQL service, shared Store directory or live cluster.
public sealed record OfflineRestoredMap(string Host,Guid Owner,string Name,
    BackupManifest Manifest);
public sealed record OfflineRestoredSql(string Database,Guid Owner,long Bytes,string Sha256);
public sealed record OfflineRestoreStagingManifest(int Schema,Guid SourceBatch,
    DateTimeOffset CreatedUtc,OfflineRestoredMap[] Maps,OfflineRestoredSql[] Sql,
    bool ProductionRestoreAllowed,string Scope);

public static class ClusterRestoreCoordinator
{
    private const string Scope="Untamed isolated 10-map/3-SQL materialization only; no live restore or Store ownership";
    private const string Index="restore-lab-index.json";
    private const string Marker="complete";
    private static string VerifyLabTarget(string destination)
    {
        if(string.IsNullOrWhiteSpace(destination)||!Path.IsPathFullyQualified(destination)||
           destination.StartsWith(@"\\",StringComparison.Ordinal) ||
           destination.StartsWith("//",StringComparison.Ordinal) ||
           destination.Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries)
               .Any(p=>p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Contains('~')))
            throw new InvalidDataException("Explicit isolated restore lab target required");
        string full=Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if(!string.Equals(Path.GetDirectoryName(full),temp,StringComparison.OrdinalIgnoreCase) ||
           !Path.GetFileName(full).StartsWith("Untamed-Isolated-Restore-",StringComparison.Ordinal) ||
           new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new UnauthorizedAccessException("Restore may only materialize into a new TEMP test sandbox");
        for(var node=new DirectoryInfo(temp);node!=null;node=node.Parent)
            if(node.Exists && (node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked restore ancestor rejected");
        return full;
    }
    private static bool Within(string parent,string child)=>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void CopyChecked(string from,string to,long size,string digest)
    {
        if((File.GetAttributes(from)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked restore source rejected");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using(var input=new FileStream(from,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            if(input.Length!=size)throw new InvalidDataException("Restore source size mismatch");
            input.CopyTo(output,1024*1024);
            output.Flush(true);
        }
        if(new FileInfo(to).Length!=size||
           !string.Equals(Hash(to),digest,StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(Hash(from),digest,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restore source or materialized bytes have changed");
    }
    private static string SafeRelative(string relative)
    {
        if(string.IsNullOrWhiteSpace(relative)||Path.IsPathFullyQualified(relative)||
           relative.Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries)
               .Any(p=>p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Contains(':')))
            throw new InvalidDataException("Unsafe backup-relative path");
        return relative;
    }
    private static void WriteDurable(string path,string content)
    {
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false),1024,leaveOpen:true);
        writer.Write(content);writer.Flush();stream.Flush(true);
    }

    public static OfflineRestoreStagingManifest MaterializeToLab(
        string archive,string destination,CancellationToken cancellationToken=default,
        Action<int>? afterFileCopiedForTesting=null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var verified=OfflineClusterBackup.Verify(archive);
        string target=VerifyLabTarget(destination),incomplete=target+".incomplete";
        if(Directory.Exists(target)||File.Exists(target)||
           Directory.Exists(incomplete)||File.Exists(incomplete))
            throw new IOException("Existing restore target or interrupted staging requires operator review");
        string packageRoot=Path.GetFullPath(archive);
        if(Within(packageRoot,target)||Within(target,packageRoot))
            throw new InvalidDataException("Archive and target may not overlap");
        var maps=new List<OfflineRestoredMap>();
        var sql=new List<OfflineRestoredSql>();
        int copied=0;
        Directory.CreateDirectory(incomplete);
        foreach(var member in verified.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source=Path.GetFullPath(Path.Combine(packageRoot,SafeRelative(member.RelativeDirectory)));
            if(!Within(packageRoot,source))throw new InvalidDataException("Archive member escapes verified root");
            if(member.Host is "HP" or "RYZEN")
            {
                var manifest=TestBackupPackage.Verify(source,member.Owner);
                string ownerRoot=Path.Combine(incomplete,member.Host,member.Owner.ToString("N"));
                foreach(var entry in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string rel=SafeRelative(entry.Path);
                    string from=Path.GetFullPath(Path.Combine(source,"data",rel));
                    string to=Path.GetFullPath(Path.Combine(ownerRoot,rel));
                    if(!Within(Path.Combine(source,"data"),from)||!Within(ownerRoot,to))
                        throw new InvalidDataException("Restore file path escapes source or target");
                    CopyChecked(from,to,entry.Size,entry.Sha256);
                    afterFileCopiedForTesting?.Invoke(++copied);
                }
                BackupIntegrity.Verify(ownerRoot,manifest,member.Owner);
                maps.Add(new(member.Host,member.Owner,member.Name,manifest));
            }
            else if(member.Host=="SQL")
            {
                var proof=OfflineSqlArchive.Verify(source,member.Name,member.Owner);
                string rel=proof.Database+".sql";
                CopyChecked(Path.Combine(source,"data",rel),
                    Path.Combine(incomplete,"SQL",rel),proof.Bytes,proof.Sha256);
                afterFileCopiedForTesting?.Invoke(++copied);
                sql.Add(new(proof.Database,member.Owner,proof.Bytes,proof.Sha256));
            }
            else throw new InvalidDataException("Unknown archive host");
        }
        var staged=new OfflineRestoreStagingManifest(1,verified.Batch,DateTimeOffset.UtcNow,
            maps.ToArray(),sql.ToArray(),false,Scope);
        string index=Path.Combine(incomplete,Index);
        WriteDurable(index,JsonSerializer.Serialize(staged));
        WriteDurable(Path.Combine(incomplete,Marker),verified.Batch.ToString("N"));
        VerifyInternal(incomplete,true);
        Directory.Move(incomplete,target);
        return VerifyLab(target);
    }

    public static OfflineRestoreStagingManifest VerifyLab(string destination)
        => VerifyInternal(destination,false);

    private static OfflineRestoreStagingManifest VerifyInternal(string destination,bool internalStage)
    {
        string path=internalStage && destination.EndsWith(".incomplete",StringComparison.Ordinal)?
            destination[..^".incomplete".Length]:destination;
        string root=VerifyLabTarget(path);
        if(internalStage)
            root=destination;
        else if(destination.EndsWith(".incomplete",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Incomplete restore staging cannot be accepted");
        for(var dir=new DirectoryInfo(root);dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked restore workspace rejected");
        string index=Path.Combine(root,Index),marker=Path.Combine(root,Marker);
        if(!Directory.Exists(root)||!File.Exists(index)||!File.Exists(marker)||
           (File.GetAttributes(index)&FileAttributes.ReparsePoint)!=0||
           new FileInfo(index).Length is <32 or >2_000_000)
            throw new InvalidDataException("Isolated restore is incomplete or index invalid");
        OfflineRestoreStagingManifest report;
        try{report=JsonSerializer.Deserialize<OfflineRestoreStagingManifest>(File.ReadAllText(index))
             ??throw new InvalidDataException("Missing restore index");}
        catch(JsonException){throw new InvalidDataException("Invalid restore index JSON");}
        if(report.Schema!=1||report.SourceBatch==Guid.Empty||report.ProductionRestoreAllowed||
           report.Scope!=Scope||report.Maps==null||report.Sql==null||
           report.Maps.Length!=10||report.Sql.Length!=3||
           report.Maps.Count(m=>m.Host=="HP")!=5||report.Maps.Count(m=>m.Host=="RYZEN")!=5||
           report.Maps.Select(m=>m.Owner).Distinct().Count()!=10||
           report.Sql.Select(s=>s.Database).ToHashSet(StringComparer.Ordinal)
                .SetEquals(OfflineSqlArchive.SupportedDatabases)==false||
           File.ReadAllText(marker).Trim()!=report.SourceBatch.ToString("N"))
            throw new InvalidDataException("Offline restore inventory identity or scope mismatch");
        if(Directory.GetFiles(root).Length!=2||Directory.GetDirectories(root).Length!=3)
            throw new InvalidDataException("Extra or missing isolated restore root content");
        foreach(string host in new[]{"HP","RYZEN"})
            if(!Directory.Exists(Path.Combine(root,host))||
               Directory.GetFiles(Path.Combine(root,host)).Length!=0||
               Directory.GetDirectories(Path.Combine(root,host)).Length!=5)
                throw new InvalidDataException("Unexpected host restore membership");
        string sqlRoot=Path.Combine(root,"SQL");
        if(!Directory.Exists(sqlRoot)||Directory.GetDirectories(sqlRoot).Length!=0||
           Directory.GetFiles(sqlRoot).Length!=3)
            throw new InvalidDataException("Unexpected SQL restore membership");
        foreach(var member in report.Maps)
        {
            if(member.Manifest==null||member.Owner==Guid.Empty)
                throw new InvalidDataException("Missing map restore manifest");
            string folder=Path.Combine(root,member.Host,member.Owner.ToString("N"));
            BackupIntegrity.Verify(folder,member.Manifest,member.Owner);
        }
        foreach(var sql in report.Sql)
        {
            if(!OfflineSqlArchive.IsSupported(sql.Database)||sql.Bytes<=0||
               new FileInfo(Path.Combine(root,"SQL",sql.Database+".sql")).Length!=sql.Bytes||
               Hash(Path.Combine(root,"SQL",sql.Database+".sql"))!=sql.Sha256)
                throw new InvalidDataException("Materialized SQL backup bytes changed");
        }
        return report;
    }
}
