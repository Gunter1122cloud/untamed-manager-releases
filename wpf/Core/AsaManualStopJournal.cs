using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

public enum AsaStopPhase { Pending, Completed, NeedsOperator, OperatorReviewed, Recovered }

/// <summary>
/// Machine-local, installation-identity-bound graceful Stop journal.
/// A committed SaveWorld/DoExit attempt must survive UI and Manager restarts.
/// No command, password, character data, full install path or Store writer is
/// persisted. Recovery never sends RCON or starts/stops an ASA process.
/// </summary>
public sealed record AsaManualStopRecord(int Schema,Guid Id,Guid MapId,
    string Resource,int OldPid,DateTimeOffset OldStartedUtc,
    AsaStopPhase Phase,DateTimeOffset CreatedUtc,DateTimeOffset ChangedUtc);

public sealed class AsaManualStopJournal
{
    private readonly string key,folder,path;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public AsaManualStopJournal(string installation,string? journalRoot=null)
    {
        key=UntamedOperationCoordinator.ResourceKey(installation);
        folder=journalRoot??Path.Combine(
            UntamedOperationCoordinator.DefaultRoot,"GracefulStop-v1");
        if(!Path.IsPathFullyQualified(folder)||folder.StartsWith(@"\\",
            StringComparison.Ordinal))
            throw new InvalidDataException("UM-ASA-STOP-JOURNAL-ROOT");
        folder=Path.GetFullPath(folder);
        path=Path.Combine(folder,key+".json");
    }

    private void ValidateFolder()
    {
        for(DirectoryInfo? node=new(folder);node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-ASA-STOP-JOURNAL-LINK");
        if(Directory.Exists(path))
            throw new InvalidDataException("UM-ASA-STOP-JOURNAL-INVALID");
    }

    public AsaManualStopRecord? Observe()
    {
        ValidateFolder();
        if(!File.Exists(path))return null;
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ASA-STOP-JOURNAL-INVALID");
        // Read a single immutable snapshot. Allow the writer to replace the
        // directory entry while status polling still holds the old handle.
        // Never permit in-place writes through this reader.
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.Read|FileShare.Delete);
        long length=stream.Length;
        if(length is <=0 or >8192)
            throw new InvalidDataException("UM-ASA-STOP-JOURNAL-INVALID");
        byte[] bytes=new byte[(int)length];
        stream.ReadExactly(bytes);
        var record=JsonSerializer.Deserialize<AsaManualStopRecord>(bytes,Json);
        if(record==null||record.Schema!=1||record.Id==Guid.Empty||
           record.MapId==Guid.Empty||record.Resource!=key||
           record.OldPid<=0||record.OldStartedUtc==default||
           record.CreatedUtc==default||record.ChangedUtc==default||
           !Enum.IsDefined(record.Phase))
            throw new InvalidDataException("UM-ASA-STOP-JOURNAL-IDENTITY");
        return record;
    }

    private void Write(AsaManualStopRecord record)
    {
        ValidateFolder();
        Directory.CreateDirectory(folder);
        ValidateFolder();
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,
                FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(stream,record,Json);
                stream.Flush(true);
            }
            // Windows may temporarily deny replacement while a legacy status
            // reader, antivirus or indexer holds the destination open.
            // Retry ONLY publication of the already flushed journal bytes,
            // under the same per-installation exclusive gate. No RCON, no
            // second operation claim and no successful receipt before commit.
            for(int attempt=0;;attempt++)
            {
                try{File.Move(temp,path,true);break;}
                catch(Exception ex) when(attempt<20&&
                    (ex is IOException or UnauthorizedAccessException)&&
                    ((ex.HResult&0xffff) is 5 or 32 or 33))
                {
                    Thread.Sleep(50);
                    ValidateFolder();
                }
            }
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    private T Exclusive<T>(Func<T> action)
    {
        using var mutex=new Semaphore(1,1,
            @"Global\Untamed-AsaStop-v1-"+key);
        if(!mutex.WaitOne(0))
            throw new IOException("UM-ASA-STOP-JOURNAL-BUSY");
        try{return action();}
        finally{mutex.Release();}
    }

    // An OperatorReviewed flag is NOT proof by itself: before releasing
    // future Start/Stop/Restart require the pinned, immutable original
    // journal bytes to still exist and match the recorded operation.
    private void DemandReviewedAudit(AsaManualStopRecord reviewed)
    {
        if(reviewed.Phase!=AsaStopPhase.OperatorReviewed)return;
        ValidateFolder();
        string audit=Path.Combine(folder,key+"-"+
            reviewed.Id.ToString("N")+".operator-reviewed.json");
        if(!File.Exists(audit))
            throw new InvalidDataException(
                "UM-ASA-STOP-REVIEW-AUDIT-MISSING");
        var file=new FileInfo(audit);
        if(file.Length is <=0 or >8192 ||
           (file.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException(
                "UM-ASA-STOP-REVIEW-AUDIT-MISMATCH");
        byte[] original=File.ReadAllBytes(audit);
        if(original.Length is <=0 or >8192)
            throw new InvalidDataException(
                "UM-ASA-STOP-REVIEW-AUDIT-MISMATCH");
        AsaManualStopRecord? evidence;
        try{evidence=JsonSerializer.Deserialize<AsaManualStopRecord>(
            original,Json);}
        catch(JsonException){
            throw new InvalidDataException(
                "UM-ASA-STOP-REVIEW-AUDIT-MISMATCH");
        }
        if(evidence==null||evidence.Schema!=1||
           evidence.Phase is not
               (AsaStopPhase.Pending or AsaStopPhase.NeedsOperator)||
           evidence.Id!=reviewed.Id||evidence.MapId!=reviewed.MapId||
           evidence.Resource!=key||evidence.OldPid!=reviewed.OldPid||
           evidence.OldStartedUtc!=reviewed.OldStartedUtc||
           evidence.CreatedUtc!=reviewed.CreatedUtc||
           evidence.ChangedUtc<evidence.CreatedUtc||
           evidence.ChangedUtc>reviewed.ChangedUtc)
            throw new InvalidDataException(
                "UM-ASA-STOP-REVIEW-AUDIT-MISMATCH");
    }

    public AsaManualStopRecord Begin(Guid mapId,int oldPid,
        DateTimeOffset oldStartedUtc)
    {
        if(mapId==Guid.Empty||oldPid<=0||oldStartedUtc==default)
            throw new InvalidDataException("UM-ASA-STOP-JOURNAL-IDENTITY");
        return Exclusive(()=>{
            var existing=Observe();
            if(existing!=null&&existing.Phase is
                (AsaStopPhase.Pending or AsaStopPhase.NeedsOperator))
                throw new IOException("UM-ASA-STOP-JOURNAL-IN-PROGRESS");
            if(existing!=null)DemandReviewedAudit(existing);
            var now=DateTimeOffset.UtcNow;
            var record=new AsaManualStopRecord(1,Guid.NewGuid(),mapId,
                key,oldPid,oldStartedUtc,AsaStopPhase.Pending,now,now);
            Write(record);
            return record;
        });
    }

    public AsaManualStopRecord Finish(Guid id,AsaStopPhase next)
    {
        if(id==Guid.Empty||next is not
            (AsaStopPhase.Completed or AsaStopPhase.NeedsOperator))
            throw new InvalidDataException("UM-ASA-STOP-JOURNAL-PHASE");
        return Exclusive(()=>{
            var current=Observe()??throw new InvalidDataException(
                "UM-ASA-STOP-JOURNAL-MISSING");
            if(current.Id!=id||current.Phase!=AsaStopPhase.Pending)
                throw new InvalidOperationException(
                    "UM-ASA-STOP-JOURNAL-OUT-OF-ORDER");
            var changed=current with{Phase=next,ChangedUtc=DateTimeOffset.UtcNow};
            Write(changed);
            return changed;
        });
    }

    public AsaManualStopRecord? AutoRecoverPending(Guid mapId)
    {
        if(mapId==Guid.Empty)throw new InvalidDataException("UM-ASA-STOP-JOURNAL-MAP");
        return Exclusive(()=>{
            var current=Observe();
            if(current==null)return null;
            if(current.MapId!=mapId)
                throw new InvalidDataException("UM-ASA-STOP-JOURNAL-MAP-MISMATCH");
            if(current.Phase is not (AsaStopPhase.Pending or AsaStopPhase.NeedsOperator))
                return current;
            bool failed=current.Phase==AsaStopPhase.NeedsOperator;
            bool abandoned=current.Phase==AsaStopPhase.Pending&&
                DateTimeOffset.UtcNow-current.ChangedUtc>=TimeSpan.FromMinutes(5);
            if(!failed&&!abandoned)return current;
            byte[] bytes=File.ReadAllBytes(path);
            string audit=Path.Combine(folder,key+"-"+current.Id.ToString("N")+
                ".auto-recovered.json");
            if(File.Exists(audit))
            {
                if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
                   !CryptographicOperations.FixedTimeEquals(
                       SHA256.HashData(File.ReadAllBytes(audit)),SHA256.HashData(bytes)))
                    throw new InvalidDataException("UM-ASA-STOP-AUTO-RECOVERY-ARCHIVE-MISMATCH");
            }
            else
            {
                using var output=new FileStream(audit,FileMode.CreateNew,FileAccess.Write,
                    FileShare.None,4096,FileOptions.WriteThrough);
                output.Write(bytes);output.Flush(true);
            }
            var recovered=current with{Phase=AsaStopPhase.Recovered,
                ChangedUtc=DateTimeOffset.UtcNow};
            Write(recovered);return recovered;
        });
    }

    public void DemandNoPending(Guid mapId)
    {
        var current=AutoRecoverPending(mapId);
        if(current?.Phase is AsaStopPhase.Pending or AsaStopPhase.NeedsOperator)
            throw new InvalidOperationException("UM-ASA-STOP-IN-PROGRESS");
        if(current!=null)DemandReviewedAudit(current);
    }

    /// <summary>
    /// Explicit manual audit only after the original ASA process is
    /// physically confirmed stopped. The caller MUST check Windows process
    /// ownership, no in-memory running Stop, and conscious typed consent.
    /// This method cannot send commands, retry DoExit or write game data.
    /// </summary>
    public AsaManualStopRecord ReviewStopped(Guid mapId,Guid id)
    {
        if(mapId==Guid.Empty||id==Guid.Empty)
            throw new InvalidDataException("UM-ASA-STOP-REVIEW-IDENTITY");
        return Exclusive(()=>{
            var current=Observe()??throw new InvalidDataException(
                "UM-ASA-STOP-JOURNAL-MISSING");
            if(current.Id!=id||current.MapId!=mapId||
               current.Phase is not
                   (AsaStopPhase.Pending or AsaStopPhase.NeedsOperator))
                throw new InvalidOperationException(
                    "UM-ASA-STOP-REVIEW-NOT-PENDING");
            // Preserve the EXACT pre-review journal bytes in an immutable,
            // installation- and operation-keyed audit file. A crash after
            // creating the audit but before updating the journal can resume
            // only if the existing audit still hashes to the same source.
            // Never overwrite or delete an audit, even after an error.
            byte[] original=File.ReadAllBytes(path);
            string audit=Path.Combine(folder,key+"-"+
                current.Id.ToString("N")+".operator-reviewed.json");
            if(File.Exists(audit))
            {
                var info=new FileInfo(audit);
                if(info.Length is <=0 or >8192 ||
                   (info.Attributes&FileAttributes.ReparsePoint)!=0 ||
                   !CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(File.ReadAllBytes(audit)),
                        SHA256.HashData(original)))
                    throw new InvalidDataException(
                        "UM-ASA-STOP-REVIEW-AUDIT-MISMATCH");
            }
            else
            {
                using var stream=new FileStream(audit,FileMode.CreateNew,
                    FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
                stream.Write(original);
                stream.Flush(flushToDisk:true);
            }
            var changed=current with{
                Phase=AsaStopPhase.OperatorReviewed,
                ChangedUtc=DateTimeOffset.UtcNow
            };
            Write(changed);
            return changed;
        });
    }
}
