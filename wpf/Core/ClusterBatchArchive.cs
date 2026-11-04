using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

// T07: a real *offline* 37-file copy/publish operation on a fixed Windows disk.
// Inputs are independently prepared test/cold-copy components, never live ASA
// saves, shared Store writers or production database connections.
public sealed record ClusterBatchSource(ClusterBackupComponent Component,string SourceFile);
public sealed record ClusterBatchArchiveManifest(int Schema,Guid BatchId,DateTimeOffset CreatedUtc,
    TransferMapOwner[] Maps,ClusterBackupComponent[] Components,
    bool LiveAsaSaveConfirmed,bool StoreWriterOwnershipConfirmed,
    bool SqlTransactionBoundaryConfirmed,string Scope)
{
    public bool ProductionRestoreCertified => false;
}

public static class ClusterBatchArchive
{
    private const int MemberCount=37;
    private const string ManifestName="cluster-batch-v1.json";
    private const string ManifestHashName="cluster-batch-v1.sha256";
    private const string CompleteName="complete";
    private const string ObjectsName="objects";
    private const string OfflineScope="UNTAMED-OFFLINE-37-v1: independently copied data; not a live ASA/Store/MariaDB consistency checkpoint";

    private static string FixedLocal(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||!Path.IsPathFullyQualified(value)||
           value.StartsWith(@"\\",StringComparison.Ordinal)||
           value.StartsWith("//",StringComparison.Ordinal)||
           value.StartsWith(@"\\?",StringComparison.Ordinal))
            throw new InvalidDataException("Explicit fixed local path required");
        string[] raw=value.Replace('/','\\').Split('\\');
        if(raw.Any(p=>p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')||p.Contains('~'))||
           (value.Length>=3 && value[3..].Contains(':')))
            throw new InvalidDataException("Path alias or alternate stream rejected");
        string full=Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Only fixed local disks allowed");
        for(DirectoryInfo? parent=new DirectoryInfo(Path.GetDirectoryName(full)!);
            parent!=null;parent=parent.Parent)
            if(parent.Exists&&(parent.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked ancestor rejected");
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked source file rejected");
        if(Directory.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked target directory rejected");
        return full;
    }

    private static bool ContainedBy(string path,string directory)
    {
        string prefix=directory.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        return path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path,directory,StringComparison.OrdinalIgnoreCase);
    }

    private static string Digest(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void WriteDurable(string path,string contents)
    {
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false),leaveOpen:true);
        writer.Write(contents);writer.Flush();stream.Flush(true);
    }

    public static ClusterBatchArchiveManifest BuildOffline(Guid batchId,
        IReadOnlyList<TransferMapOwner> maps,IReadOnlyList<ClusterBatchSource> inputs,
        string destination,Action<int>? afterCopiedForTesting=null)
    {
        if(maps==null||inputs==null)throw new ArgumentNullException(nameof(inputs));
        var sources=inputs.ToArray();
        var components=sources.Select(x=>x?.Component ??
            throw new InvalidDataException("Null backup member")).ToArray();
        if(!ClusterBackupInventory.Verify(batchId,maps,components).Complete)
            throw new InvalidDataException("37-component offline backup membership invalid");

        string target=FixedLocal(destination),incomplete=FixedLocal(target+".incomplete");
        if(Directory.Exists(target)||File.Exists(target)||
           Directory.Exists(incomplete)||File.Exists(incomplete))
            throw new IOException("Existing destination or incomplete batch requires operator review");

        string[] paths=sources.Select(x=>FixedLocal(x.SourceFile)).ToArray();
        if(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=MemberCount)
            throw new InvalidDataException("Different backup components require distinct source files");
        for(int i=0;i<paths.Length;i++)
        {
            string file=paths[i];
            if(!File.Exists(file)||ContainedBy(file,target)||ContainedBy(file,incomplete))
                throw new InvalidDataException("Missing, overlapping or unsafe source file");
            var info=new FileInfo(file);
            if(info.Length!=components[i].Length || Digest(file)!=components[i].Sha256.ToUpperInvariant())
                throw new InvalidDataException("Input length or SHA256 does not match manifest");
        }

        string parent=Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(incomplete);
        string objects=Path.Combine(incomplete,ObjectsName);
        Directory.CreateDirectory(objects);
        // A failure retains .incomplete for operator inspection. No source is
        // modified and no marker or final publication is created on failure.
        for(int i=0;i<paths.Length;i++)
        {
            string output=Path.Combine(objects,i.ToString("D2")+".bin");
            using(var input=new FileStream(paths[i],FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {input.CopyTo(file,1024*1024);file.Flush(true);}
            if(new FileInfo(output).Length!=components[i].Length ||
               Digest(output)!=components[i].Sha256.ToUpperInvariant() ||
               Digest(paths[i])!=components[i].Sha256.ToUpperInvariant())
                throw new InvalidDataException("Backup source changed during copy or output failed verification");
            afterCopiedForTesting?.Invoke(i);
        }

        var manifest=new ClusterBatchArchiveManifest(1,batchId,DateTimeOffset.UtcNow,
            maps.ToArray(),components,false,false,false,OfflineScope);
        string manifestPath=Path.Combine(incomplete,ManifestName);
        WriteDurable(manifestPath,JsonSerializer.Serialize(manifest));
        WriteDurable(Path.Combine(incomplete,ManifestHashName),Digest(manifestPath));
        WriteDurable(Path.Combine(incomplete,CompleteName),batchId.ToString("N"));
        Verify(incomplete);
        Directory.Move(incomplete,target);
        return Verify(target);
    }

    public static ClusterBatchArchiveManifest Verify(string directory)
    {
        string root=FixedLocal(directory);
        if(!Directory.Exists(root))throw new InvalidDataException("Batch directory not found");
        string manifestPath=FixedLocal(Path.Combine(root,ManifestName));
        string hashPath=FixedLocal(Path.Combine(root,ManifestHashName));
        string marker=FixedLocal(Path.Combine(root,CompleteName));
        string objects=FixedLocal(Path.Combine(root,ObjectsName));
        if(!File.Exists(manifestPath)||!File.Exists(hashPath)||!File.Exists(marker)||
           !Directory.Exists(objects)||new FileInfo(manifestPath).Length is < 20 or > 1_000_000||
           new FileInfo(hashPath).Length is < 64 or > 80 ||
           new FileInfo(marker).Length is < 32 or > 40)
            throw new InvalidDataException("Batch incomplete or manifest exceeds limits");
        if(!string.Equals(File.ReadAllText(hashPath).Trim(),Digest(manifestPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Batch index digest mismatch");
        ClusterBatchArchiveManifest report;
        try{report=JsonSerializer.Deserialize<ClusterBatchArchiveManifest>(File.ReadAllText(manifestPath))
             ??throw new InvalidDataException("Missing batch manifest");}
        catch(JsonException){throw new InvalidDataException("Malformed batch manifest");}
        if(report.Schema!=1||report.BatchId==Guid.Empty||
           report.LiveAsaSaveConfirmed||report.StoreWriterOwnershipConfirmed||
           report.SqlTransactionBoundaryConfirmed||report.Scope!=OfflineScope||
           !string.Equals(File.ReadAllText(marker).Trim(),report.BatchId.ToString("N"),
               StringComparison.OrdinalIgnoreCase) ||
           report.Maps==null||report.Components==null||
           !ClusterBackupInventory.Verify(report.BatchId,report.Maps,report.Components).Complete)
            throw new InvalidDataException("Batch metadata incomplete or unsupported");
        string[] expected=Enumerable.Range(0,MemberCount)
            .Select(i=>i.ToString("D2")+".bin").ToArray();
        var actual=Directory.GetFiles(objects).Select(Path.GetFileName).ToArray();
        if(actual.Length!=MemberCount||!actual.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(expected))
            throw new InvalidDataException("Batch data members missing or unexpected");
        if(Directory.GetDirectories(objects).Length!=0 ||
           Directory.GetDirectories(root).Length!=1 ||
           Directory.GetFiles(root).Length!=3)
            throw new InvalidDataException("Unexpected data within sealed batch");
        for(int i=0;i<MemberCount;i++)
        {
            string path=FixedLocal(Path.Combine(objects,expected[i]));
            if(!File.Exists(path)||new FileInfo(path).Length!=report.Components[i].Length||
               Digest(path)!=report.Components[i].Sha256.ToUpperInvariant())
                throw new InvalidDataException("Batch member integrity mismatch");
        }
        return report;
    }
}
