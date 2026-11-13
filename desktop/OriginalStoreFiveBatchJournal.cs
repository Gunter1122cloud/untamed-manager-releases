using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedDesktop;

/// <summary>
/// Durable Manager-profile orchestration receipt for five existing Core world
/// backups. It is NOT a Store writer, another backup engine, a Legacy metadata
/// file or a transfer archive. No ASA/Legacy path is ever accepted from WebView.
/// </summary>
internal static class OriginalStoreFiveBatchJournal
{
    internal sealed record Point(Guid MapId,string MapName,string PointId,
        int Files,long Bytes);
    internal sealed record Snapshot(Guid JobId,string State,string Step,
        DateTimeOffset ChangedUtc,string FourFilesSha,string LaunchProfileSha,
        Point[] Points,long TotalBytes);
    private sealed record Envelope(int Schema,Snapshot Snapshot,string Sha256);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private const string Name="original-store-five-backup-v1.json";
    private const int Limit=64*1024;

    private static string Folder(string managerProfile)
    {
        if(string.IsNullOrWhiteSpace(managerProfile) ||
           !Path.IsPathFullyQualified(managerProfile) ||
           managerProfile.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-LOCAL-MANAGER-ONLY");
        string root=Path.GetFullPath(managerProfile);
        string path=Path.Combine(root,"backup-batch-journals");
        if(Directory.Exists(root) &&
           (File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-LINKED-ROOT");
        if(Directory.Exists(path) &&
           (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-LINKED-FOLDER");
        return path;
    }
    private static string Digest(Snapshot snapshot)=>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(snapshot,Json))));
    private static void Validate(Snapshot snapshot)
    {
        if(snapshot==null||snapshot.JobId==Guid.Empty||
           snapshot.State is not ("queued" or "running" or "completed" or "needsOperator")||
           snapshot.Step is null||snapshot.Step.Length is <1 or >120||
           snapshot.FourFilesSha is null||snapshot.FourFilesSha.Length!=64||
           snapshot.LaunchProfileSha is null||snapshot.LaunchProfileSha.Length!=64||
           !snapshot.FourFilesSha.All(Uri.IsHexDigit)||
           !snapshot.LaunchProfileSha.All(Uri.IsHexDigit)||
           snapshot.Points==null||snapshot.Points.Length>5||
           snapshot.TotalBytes<0||
           snapshot.Points.Any(p=>p==null||p.MapId==Guid.Empty||
               p.MapName is null||p.MapName.Length is <1 or >160||
               !Guid.TryParse(p.PointId,out var parsed)||parsed==Guid.Empty||
               p.Files<=0||p.Bytes<0)||
           snapshot.Points.Select(p=>p.MapId).Distinct().Count()!=snapshot.Points.Length||
           (snapshot.State=="completed"&&snapshot.Points.Length!=5))
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-INVALID");
        try
        {
            if(snapshot.Points.Sum(p=>p.Bytes)!=snapshot.TotalBytes)
                throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-TOTAL-MISMATCH");
        }
        catch(OverflowException)
        {
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-TOTAL-OVERFLOW");
        }
    }
    // A held OS file handle is an owner-specific cross-process lease.
    // It is NOT a Legacy Store writer lock. Windows releases it after an
    // unexpected Manager termination without deleting any journal bytes.
    internal static FileStream AcquireBatchLease(string managerProfile)
    {
        string folder=Folder(managerProfile);
        Directory.CreateDirectory(folder);
        if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-LINKED-FOLDER");
        string file=Path.Combine(folder,"original-store-five-batch.lease");
        if(File.Exists(file)&&
           (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-LINKED-LEASE");
        try
        {
            return new FileStream(file,FileMode.OpenOrCreate,FileAccess.ReadWrite,
                FileShare.None,4096,FileOptions.WriteThrough);
        }
        catch(IOException)
        {
            throw new IOException("UM-ORIGINAL-BATCH-JOB-LEASE-BUSY");
        }
    }
    internal static Snapshot? Read(string managerProfile)
    {
        string folder=Folder(managerProfile);
        string file=Path.Combine(folder,Name);
        if(!File.Exists(file))return null;
        if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||
           new FileInfo(file).Length is <10 or >Limit)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-INVALID");
        using var source=new FileStream(file,FileMode.Open,FileAccess.Read,
            FileShare.Read,4096,FileOptions.SequentialScan);
        using var doc=JsonDocument.Parse(source);
        Envelope envelope=doc.RootElement.Deserialize<Envelope>(Json)??
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-INVALID");
        if(envelope.Schema!=1||envelope.Snapshot==null||
           envelope.Sha256?.Length!=64||!envelope.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-INVALID");
        Validate(envelope.Snapshot);
        if(!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(envelope.Sha256),
            Convert.FromHexString(Digest(envelope.Snapshot))))
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-HASH-MISMATCH");
        return envelope.Snapshot;
    }
    internal static void Publish(string managerProfile,Snapshot snapshot)
    {
        Validate(snapshot);
        string folder=Folder(managerProfile);
        Directory.CreateDirectory(folder);
        // A concurrent Manager cannot silently follow a symlink introduced
        // after initial validation.
        if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-LINKED-FOLDER");
        string path=Path.Combine(folder,Name);
        if(File.Exists(path) &&
           (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-LINKED-FILE");
        byte[] data=JsonSerializer.SerializeToUtf8Bytes(
            new Envelope(1,snapshot,Digest(snapshot)),Json);
        if(data.Length>Limit)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-OVERFLOW");
        string staged=Path.Combine(folder,".original-five-"+Guid.NewGuid().ToString("N")+".pending");
        try
        {
            using(var target=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,
                FileShare.None,4096,FileOptions.WriteThrough))
            {
                target.Write(data);
                target.Flush(flushToDisk:true);
            }
            File.Move(staged,path,true);
        }
        finally
        {
            // Only OUR new staging filename is cleaned up. Never delete an
            // original Store file or an older verified backup.
            if(File.Exists(staged))File.Delete(staged);
        }
    }
    // Repair is an explicit, short-lived operator action. It only quarantines
    // corrupted MANAGER-local orchestration bytes, never a Legacy or ASA file.
    internal static string RawDigest(string managerProfile)
    {
        string file=Path.Combine(Folder(managerProfile),Name);
        if(!File.Exists(file)||
           (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||
           new FileInfo(file).Length is <1 or >Limit)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-UNSAFE");
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    }
    internal static void QuarantineInvalid(string managerProfile,string digest)
    {
        string folder=Folder(managerProfile),source=Path.Combine(folder,Name);
        if(!string.Equals(RawDigest(managerProfile),digest,StringComparison.Ordinal))
            throw new IOException("UM-ORIGINAL-BATCH-JOURNAL-CHANGED");
        // A valid journal is not eligible for the corruption repair route.
        try
        {
            Read(managerProfile);
            throw new InvalidOperationException("UM-ORIGINAL-BATCH-JOURNAL-NOT-CORRUPT");
        }
        catch(InvalidDataException) { /* Expected invalid integrity payload. */ }
        catch(JsonException) { /* Expected invalid JSON payload. */ }
        string history=Path.Combine(folder,"history");
        Directory.CreateDirectory(history);
        if((File.GetAttributes(history)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-LINKED-HISTORY");
        string target=Path.Combine(history,"quarantine-"+digest+".json");
        if(File.Exists(target))throw new IOException("UM-ORIGINAL-BATCH-JOURNAL-ALREADY-QUARANTINED");
        if(!string.Equals(RawDigest(managerProfile),digest,StringComparison.Ordinal))
            throw new IOException("UM-ORIGINAL-BATCH-JOURNAL-CHANGED");
        File.Move(source,target);
    }
    internal static void ArchivePrevious(string managerProfile)
    {
        string folder=Folder(managerProfile);
        string source=Path.Combine(folder,Name);
        if(!File.Exists(source))return;
        Snapshot old=Read(managerProfile) ??
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-MISSING");
        string history=Path.Combine(folder,"history");
        Directory.CreateDirectory(history);
        if((File.GetAttributes(history)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-BATCH-JOURNAL-LINKED-HISTORY");
        string destination=Path.Combine(history,old.JobId.ToString("N")+".json");
        if(File.Exists(destination))
            throw new IOException("UM-ORIGINAL-BATCH-JOURNAL-ALREADY-ARCHIVED");
        File.Move(source,destination);
    }
}
