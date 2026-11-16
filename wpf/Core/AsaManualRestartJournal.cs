using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

public enum AsaRestartPhase { Stopping, Starting, Completed, NeedsOperator, OperatorReviewed }

/// <summary>
/// Machine-wide, installation-keyed journal for manually approved ASA Restart.
/// Survives Manager/UI exit, requires operator review after an interrupted
/// Stop->Start sequence, and never replays operations on its own.
/// Does not contain passwords, character IDs or full filesystem paths.
/// </summary>
public sealed record AsaRestartRecord(int Schema,Guid Id,Guid MapId,string Resource,
    int OldPid,int NewPid,AsaRestartPhase Phase,DateTimeOffset ChangedUtc);

public sealed class AsaManualRestartJournal
{
    private readonly string key,folder,path;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public AsaManualRestartJournal(string installation,string? journalRoot=null)
    {
        key=UntamedOperationCoordinator.ResourceKey(installation);
        folder=journalRoot??Path.Combine(
            UntamedOperationCoordinator.DefaultRoot,"Restart-v1");
        if(!Path.IsPathFullyQualified(folder)||folder.StartsWith(@"\\",
            StringComparison.Ordinal))
            throw new InvalidDataException("UM-ASA-RESTART-JOURNAL-ROOT");
        folder=Path.GetFullPath(folder);
        path=Path.Combine(folder,key+".json");
    }

    private void ValidateFolder()
    {
        for(DirectoryInfo? node=new(folder);node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-ASA-RESTART-JOURNAL-LINK");
    }

    public AsaRestartRecord? Observe()
    {
        ValidateFolder();
        if(!File.Exists(path))return null;
        var file=new FileInfo(path);
        if(file.Length is <=0 or >8192||
           (file.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ASA-RESTART-JOURNAL-INVALID");
        var snapshot=JsonSerializer.Deserialize<AsaRestartRecord>(
            File.ReadAllText(path),Json);
        if(snapshot==null||snapshot.Schema!=1||snapshot.Id==Guid.Empty||
           snapshot.MapId==Guid.Empty||snapshot.Resource!=key||
           !Enum.IsDefined(snapshot.Phase)||
           snapshot.OldPid<=0||snapshot.NewPid<0)
            throw new InvalidDataException("UM-ASA-RESTART-JOURNAL-IDENTITY");
        return snapshot;
    }

    private void Write(AsaRestartRecord record)
    {
        ValidateFolder();
        Directory.CreateDirectory(folder);
        ValidateFolder();
        string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(tmp,FileMode.CreateNew,
                FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(stream,record,Json);
                stream.Flush(true);
            }
            File.Move(tmp,path,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }

    private T Exclusive<T>(Func<T> action)
    {
        using var mutex=new Semaphore(1,1,
            @"Global\Untamed-AsaRestart-v1-"+key);
        if(!mutex.WaitOne(0))
            throw new IOException("UM-ASA-RESTART-JOURNAL-BUSY");
        try{return action();}
        finally{mutex.Release();}
    }

    public AsaRestartRecord Begin(Guid mapId,int oldPid)
    {
        if(mapId==Guid.Empty||oldPid<=0)
            throw new InvalidDataException("UM-ASA-RESTART-JOURNAL-MAP");
        return Exclusive(()=>{
            var prior=Observe();
            if(prior!=null&&prior.Phase is not (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed))
                throw new IOException("UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR");
            var record=new AsaRestartRecord(1,Guid.NewGuid(),mapId,key,
                oldPid,0,AsaRestartPhase.Stopping,DateTimeOffset.UtcNow);
            Write(record);
            return record;
        });
    }

    public AsaRestartRecord Change(Guid id,AsaRestartPhase next,int newPid=0)
    {
        if(id==Guid.Empty || !Enum.IsDefined(next) ||
           next is not (AsaRestartPhase.Starting or
               AsaRestartPhase.Completed or AsaRestartPhase.NeedsOperator))
            throw new InvalidDataException("UM-ASA-RESTART-JOURNAL-TRANSITION");
        return Exclusive(()=>{
            var current=Observe()??throw new InvalidDataException(
                "UM-ASA-RESTART-JOURNAL-MISSING");
            if(current.Id!=id||current.Phase is
                (AsaRestartPhase.Completed or AsaRestartPhase.NeedsOperator or AsaRestartPhase.OperatorReviewed) ||
               (next==AsaRestartPhase.Starting &&
                   current.Phase!=AsaRestartPhase.Stopping)||
               (next==AsaRestartPhase.Completed &&
                   (current.Phase!=AsaRestartPhase.Starting||newPid<=0)))
                throw new InvalidOperationException("UM-ASA-RESTART-JOURNAL-OUT-OF-ORDER");
            var nextRecord=current with {
                Phase=next,NewPid=newPid,ChangedUtc=DateTimeOffset.UtcNow
            };
            Write(nextRecord);
            return nextRecord;
        });
    }

    /// <summary>
    /// Under the caller's installation-wide operation semaphore, review a
    /// genuinely interrupted original Restart whose OLD process still runs.
    /// Restart is NOT marked Completed. Crash recovery is explicit: if this
    /// journal was reviewed first but the paired Launch journal remains
    /// pending, a fresh owner proof can resume the SECOND half safely.
    /// This method NEVER executes an ASA/RCON command.
    /// </summary>
    internal AsaRestartRecord ReviewOriginalOldPid(Guid expectedMap,
        Guid expectedId,int currentOldPid)
    {
        if(expectedMap==Guid.Empty||expectedId==Guid.Empty||currentOldPid<=0)
            throw new InvalidDataException("UM-RESTART-REVIEW-APPROVAL-INVALID");
        return Exclusive(()=>{
            var old=Observe()??throw new InvalidDataException(
                "UM-RESTART-REVIEW-JOURNAL-MISSING");
            if(old.MapId!=expectedMap||old.Id!=expectedId||
               old.OldPid!=currentOldPid||old.NewPid!=0)
                throw new InvalidOperationException(
                    "UM-RESTART-REVIEW-OLD-PID-OR-ID-CHANGED");
            string audit=Path.Combine(folder,key+"-"+old.Id.ToString("N")+
                ".operator-reviewed.json");
            if(old.Phase==AsaRestartPhase.OperatorReviewed)
            {
                if(!File.Exists(audit)||
                   (File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("UM-RESTART-REVIEW-AUDIT-MISSING");
                var preserved=JsonSerializer.Deserialize<AsaRestartRecord>(
                    File.ReadAllBytes(audit),Json);
                if(preserved==null||preserved.Id!=old.Id||
                   preserved.MapId!=old.MapId||preserved.Resource!=old.Resource||
                   preserved.OldPid!=old.OldPid||preserved.NewPid!=0||
                   preserved.Phase is not (AsaRestartPhase.Stopping or
                       AsaRestartPhase.NeedsOperator))
                    throw new InvalidDataException("UM-RESTART-REVIEW-AUDIT-MISMATCH");
                return old;
            }
            if(old.Phase is not (AsaRestartPhase.NeedsOperator or
                AsaRestartPhase.Stopping))
                throw new UnauthorizedAccessException(
                    "UM-RESTART-REVIEW-OLD-PROCESS-NOT-PENDING");
            if(DateTimeOffset.UtcNow-old.ChangedUtc<TimeSpan.FromMinutes(3))
                throw new InvalidOperationException("UM-RESTART-REVIEW-TOO-RECENT");
            byte[] original=File.ReadAllBytes(path);
            if(File.Exists(audit))
            {
                if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
                   !CryptographicOperations.FixedTimeEquals(
                       SHA256.HashData(File.ReadAllBytes(audit)),
                       SHA256.HashData(original)))
                    throw new InvalidDataException("UM-RESTART-REVIEW-AUDIT-MISMATCH");
            }
            else
            {
                using var fs=new FileStream(audit,FileMode.CreateNew,
                    FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
                fs.Write(original);
                fs.Flush(flushToDisk:true);
            }
            var reviewed=old with{
                Phase=AsaRestartPhase.OperatorReviewed,
                ChangedUtc=DateTimeOffset.UtcNow
            };
            Write(reviewed);
            return reviewed;
        });
    }

    public void DemandNoPending()
    {
        var current=Observe();
        if(current!=null&&current.Phase is not (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed))
            throw new InvalidOperationException(
                "UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR");
    }

    public void DemandAuthorizedStop(Guid? restartId)
    {
        var current=Observe();
        // An explicit restart authorization is valid ONLY while its exact
        // durable record is present in the expected in-progress phase.
        // Missing/completed journal files cannot authorize a stale token.
        if(restartId==null &&
           (current==null||current.Phase is (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed)))return;
        if(restartId==null||current==null||current.Id!=restartId.Value||
           current.Phase!=AsaRestartPhase.Stopping)
            throw new InvalidOperationException(
                "UM-ASA-RESTART-JOURNAL-STOP-UNAUTHORIZED");
    }

    public void DemandAuthorizedStart(Guid? restartId)
    {
        var current=Observe();
        // An explicit restart authorization is valid ONLY while its exact
        // durable record is present in the expected in-progress phase.
        // Missing, completed and explicitly audited OperatorReviewed
        // records do not block a NEW independently approved normal Start.
        // OperatorReviewed never authorizes resuming the OLD restart token.
        if(restartId==null &&
           (current==null||current.Phase is
               (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed)))return;
        if(restartId==null||current==null||current.Id!=restartId.Value||
           current.Phase!=AsaRestartPhase.Starting)
            throw new InvalidOperationException(
                "UM-ASA-RESTART-JOURNAL-START-UNAUTHORIZED");
    }
}
