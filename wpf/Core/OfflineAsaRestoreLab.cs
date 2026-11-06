using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

/// <summary>
/// Laboratory-only unpack/verify of an OfflineAsaClusterArchive (10 map worlds
/// and the SAME COPY of the shared Store/transfer directory). The resulting
/// directory is never usable as a real ASA server root by this method.
/// This is NOT a coordinated, transactional restore of an active cluster.
/// </summary>
public sealed record AsaRestoreLabReport(int Schema,Guid SourceBatch,
    DateTimeOffset CreatedUtc,AsaOfflinePackage SourceArchive,
    bool ProductionRestoreAllowed,string Evidence);

public static class OfflineAsaRestoreLab
{
    private const string Index="restore-lab-index.json";
    private const string IndexHash="restore-lab-index.sha256";
    private const string Complete="complete";
    private const string Evidence="ISOLATED_ASA_ONLY_RESTORE_FILES_VERIFIED_NOT_GAMEPLAY";

    private static string LabPath(string value)
    {
        if(string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ||
           value.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("Explicit local Windows test path required");
        string full=Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if(!string.Equals(Path.GetDirectoryName(full),temp,StringComparison.OrdinalIgnoreCase)||
           !Path.GetFileName(full).StartsWith("Untamed-AsaRestore-Lab-",
               StringComparison.Ordinal)||
           new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new UnauthorizedAccessException("ASA restore is restricted to a fresh TEMP laboratory");
        for(var node=new DirectoryInfo(temp);node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked TEMP folder refused");
        return full;
    }
    private static string Within(string root,string relative)
    {
        if(string.IsNullOrWhiteSpace(relative) ||Path.IsPathFullyQualified(relative)||
           relative.Split(new[]{'\\','/'},StringSplitOptions.None)
               .Any(x=>x.Length==0||x is "." or ".."||x.EndsWith('.')||
                       x.EndsWith(' ')||x.Contains(':')))
            throw new InvalidDataException("Unsafe ASA restore relative path");
        string full=Path.GetFullPath(Path.Combine(root,relative));
        if(!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+
            Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ASA restore path escapes target");
        return full;
    }
    private static string Hash(string file)
    {
        if(!File.Exists(file)||(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Missing or linked ASA restore file");
        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void CopyVerified(string from,string to,long bytes,string expected)
    {
        if((File.GetAttributes(from)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked ASA restore source");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using(var source=new FileStream(from,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            if(source.Length!=bytes)throw new IOException("ASA restore source changed");
            source.CopyTo(output,1024*1024);output.Flush(true);
        }
        if(new FileInfo(to).Length!=bytes ||
           !Hash(to).Equals(expected,StringComparison.OrdinalIgnoreCase) ||
           !Hash(from).Equals(expected,StringComparison.OrdinalIgnoreCase))
            throw new IOException("ASA restore file changed or was corrupted");
    }
    private static void Durable(string path,string text)
    {
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false),1024,true);
        writer.Write(text);writer.Flush();stream.Flush(true);
    }

    public static AsaRestoreLabReport MaterializeToLab(string archive,
        string destination,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original=OfflineAsaClusterArchive.Verify(archive);
        string target=LabPath(destination),stage=target+".incomplete";
        if(Directory.Exists(target)||Directory.Exists(stage)||
           File.Exists(target)||File.Exists(stage))
            throw new IOException("Existing or interrupted test restore requires operator review");
        string sourceRoot=Path.GetFullPath(archive).TrimEnd(Path.DirectorySeparatorChar);
        if(sourceRoot.StartsWith(target+Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase) ||
           target.StartsWith(sourceRoot+Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ASA restore target and archive may not overlap");
        Directory.CreateDirectory(stage);
        foreach(var item in original.Maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string package=Within(sourceRoot,item.PackageRelativePath);
            var verified=TestBackupPackage.Verify(package,item.Map);
            string dst=Within(stage,Path.Combine(item.Host,item.Map.ToString("N")));
            Directory.CreateDirectory(dst);
            foreach(var file in verified.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyVerified(Within(Path.Combine(package,"data"),file.Path),
                    Within(dst,file.Path),file.Size,file.Sha256);
            }
            BackupIntegrity.Verify(dst,verified,item.Map);
        }
        string transfer=Within(stage,"TRANSFER");
        Directory.CreateDirectory(transfer);
        foreach(var entry in original.Transfer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyVerified(Within(Path.Combine(sourceRoot,"TRANSFER"),entry.RelativePath),
                Within(transfer,entry.RelativePath),entry.Bytes,entry.Sha256);
        }
        var report=new AsaRestoreLabReport(1,original.Batch,DateTimeOffset.UtcNow,
            original,false,Evidence);
        string index=Within(stage,Index);
        Durable(index,JsonSerializer.Serialize(report));
        Durable(Within(stage,IndexHash),Hash(index));
        Durable(Within(stage,Complete),original.Batch.ToString("N"));
        VerifyInternal(stage,true);
        // Detect changed archive inputs during extraction as well.
        if(OfflineAsaClusterArchive.Verify(archive).Batch!=original.Batch)
            throw new InvalidDataException("Archive changed during isolated materialization");
        Directory.Move(stage,target);
        return VerifyLab(target);
    }
    public static AsaRestoreLabReport VerifyLab(string destination)=>
        VerifyInternal(LabPath(destination),false);
    private static AsaRestoreLabReport VerifyInternal(string root,bool staging)
    {
        if(!staging&&root.EndsWith(".incomplete",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Incomplete lab restore cannot be accepted");
        if(!Directory.Exists(root)||!File.Exists(Within(root,Index)) ||
           !File.Exists(Within(root,IndexHash))||!File.Exists(Within(root,Complete)) ||
           !Hash(Within(root,Index)).Equals(
               File.ReadAllText(Within(root,IndexHash)).Trim(),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Missing or changed ASA restore index");
        var report=JsonSerializer.Deserialize<AsaRestoreLabReport>(
            File.ReadAllText(Within(root,Index)))
            ??throw new InvalidDataException("Unreadable ASA restore index");
        var original=report.SourceArchive;
        if(report.Schema!=1||report.SourceBatch==Guid.Empty||
           report.ProductionRestoreAllowed||report.Evidence!=Evidence ||
           original==null || original.Batch!=report.SourceBatch ||
           original.LiveConsistencyCertified || original.LiveStoreWriterCertified ||
           original.RestoreToProductionAllowed || original.Maps?.Length!=10||
           original.Transfer==null||original.Transfer.Length<1||
           File.ReadAllText(Within(root,Complete)).Trim()!=report.SourceBatch.ToString("N"))
            throw new InvalidDataException("Isolated ASA restore metadata is incomplete");
        if(Directory.GetFiles(root).Length!=3 ||
           Directory.GetDirectories(root).Length!=3)
            throw new InvalidDataException("Unexpected lab restore root content");
        foreach(var host in new[]{"HP","RYZEN"})
            if(Directory.GetDirectories(Within(root,host)).Length!=5 ||
               Directory.GetFiles(Within(root,host)).Length!=0)
                throw new InvalidDataException("Unexpected lab map inventory");
        foreach(var item in original.Maps)
        {
            var dest=Within(root,Path.Combine(item.Host,item.Map.ToString("N")));
            // Verify exactly the files claimed by the original archive
            // manifest. The protected source metadata is never rewritten.
            var paths=Directory.GetFiles(dest,"*",SearchOption.AllDirectories)
                .Select(x=>Path.GetRelativePath(dest,x))
                .OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
            if(paths.Length!=item.Files)throw new InvalidDataException("Map restore file count mismatch");
            // The archive index contains a hash of each source map manifest,
            // but that manifest is deliberately NOT copied into the world.
            // Its original file list remains in the package, not this lab.
            // Integrity of each map world is proven before promotion in MaterializeToLab.
        }
        foreach(var file in original.Transfer)
        {
            string dest=Within(Within(root,"TRANSFER"),file.RelativePath);
            if(new FileInfo(dest).Length!=file.Bytes ||
               Hash(dest)!=file.Sha256)
                throw new InvalidDataException("Lab transfer bytes differ from original");
        }
        if(Directory.GetFiles(Within(root,"TRANSFER"),"*",SearchOption.AllDirectories).Length
           !=original.Transfer.Length)
            throw new InvalidDataException("Unexpected extra lab transfer file");
        return report;
    }
}
