using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

public enum AsaRestartPhase { Stopping, Starting, Completed, NeedsOperator, OperatorReviewed, Recovered }

/// <summary>
/// Machine-wide, installation-keyed journal for manually approved ASA Restart.
/// Survives Manager/UI exit, requires operator review after an interrupted
/// Stop->Start sequence, and never replays operations on its own.
/// Does not contain passwords, character IDs or full filesystem paths.
/// </summary>
public sealed record AsaRestartRecord(int Schema,Guid Id,Guid MapId,string Resource,
    int OldPid,int NewPid,AsaRestartPhase Phase,DateTimeOffset ChangedUtc,
    Guid? ScheduledRunId=null,int StartupAttempt=1);

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
        // File.Exists quietly returns false for directories and ACL errors.
        // Only an explicitly missing receipt can permit a new Restart.
        FileAttributes attrs;
        try { attrs=File.GetAttributes(path); }
        catch(FileNotFoundException) { return null; }
        catch(DirectoryNotFoundException) { return null; }
        var file=new FileInfo(path);
        if((attrs&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0||
           file.Length is <=0 or >8192)
            throw new InvalidDataException("UM-ASA-RESTART-JOURNAL-INVALID");
        var snapshot=JsonSerializer.Deserialize<AsaRestartRecord>(
            File.ReadAllText(path),Json);
        if(snapshot==null||snapshot.Schema!=1||snapshot.Id==Guid.Empty||
           snapshot.MapId==Guid.Empty||snapshot.Resource!=key||
           !Enum.IsDefined(snapshot.Phase)||
           snapshot.OldPid<=0||snapshot.NewPid<0||snapshot.StartupAttempt is <1 or >4||
           snapshot.ScheduledRunId==Guid.Empty)
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

    public AsaRestartRecord Begin(Guid mapId,int oldPid)=>BeginCore(mapId,oldPid,null);

    internal AsaRestartRecord BeginScheduled(Guid mapId,int oldPid,Guid runId)
    {
        if(runId==Guid.Empty)throw new InvalidDataException("UM-RESTART-SCHEDULE-ID");
        return BeginCore(mapId,oldPid,runId);
    }

    private AsaRestartRecord BeginCore(Guid mapId,int oldPid,Guid? runId)
    {
        if(mapId==Guid.Empty||oldPid<=0)
            throw new InvalidDataException("UM-ASA-RESTART-JOURNAL-MAP");
        return Exclusive(()=>{
            var prior=Observe();
            if(prior!=null&&prior.Phase is not (AsaRestartPhase.Completed or
                AsaRestartPhase.OperatorReviewed or AsaRestartPhase.Recovered))
                throw new IOException("UM-ASA-RESTART-JOURNAL-IN-PROGRESS");
            var record=new AsaRestartRecord(1,Guid.NewGuid(),mapId,key,
                oldPid,0,AsaRestartPhase.Stopping,DateTimeOffset.UtcNow,runId,1);
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
                (AsaRestartPhase.Completed or AsaRestartPhase.NeedsOperator or
                 AsaRestartPhase.OperatorReviewed or AsaRestartPhase.Recovered) ||
               (next==AsaRestartPhase.Starting &&
                   current.Phase!=AsaRestartPhase.Stopping)||
               (next==AsaRestartPhase.Completed &&
                   (current.Phase!=AsaRestartPhase.Starting||newPid<=0)))
                throw new InvalidOperationException("UM-ASA-RESTART-JOURNAL-OUT-OF-ORDER");
            var nextRecord=current with {
                Phase=next,
                NewPid=next==AsaRestartPhase.NeedsOperator&&newPid==0?
                    current.NewPid:newPid,
                ChangedUtc=DateTimeOffset.UtcNow
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

    /// <summary>
    /// Record the new ASA child PID BEFORE certifying readiness. Interrupted
    /// startup must retain which attempted child has to be proven gone.
    /// Never marks Restart Completed or grants permission to start again.
    /// </summary>
    public AsaRestartRecord RecordNewAsaPid(Guid expectedId,int newPid)
    {
        if(expectedId==Guid.Empty||newPid<=0)
            throw new InvalidDataException("UM-RESTART-NEW-PID");
        return Exclusive(()=>{
            var old=Observe()??throw new InvalidDataException(
                "UM-RESTART-NEW-PID-JOURNAL-MISSING");
            if(old.Id!=expectedId||old.Phase!=AsaRestartPhase.Starting||
               old.NewPid!=0||newPid==old.OldPid)
                throw new InvalidOperationException(
                    "UM-RESTART-NEW-PID-ALREADY-CLAIMED");
            var recorded=old with{NewPid=newPid,ChangedUtc=DateTimeOffset.UtcNow};
            Write(recorded);
            return recorded;
        });
    }

    /// <summary>
    /// Audited operator review after BOTH old and attempted new ASA/Loader
    /// families have independently been proven STOPPED and the RCON listener
    /// absent. Unlike the running-original review this accepts a previously
    /// recorded new PID, including a failed API-child startup. It does NOT
    /// dispatch a restart and cannot be used by an automatic retry loop.
    /// </summary>
    internal AsaRestartRecord ReviewOriginalStoppedFamily(
        Guid expectedMap,Guid expectedId,int oldPid,int attemptedNewPid)
    {
        if(expectedMap==Guid.Empty||expectedId==Guid.Empty||oldPid<=0||
           attemptedNewPid<0||attemptedNewPid==oldPid)
            throw new InvalidDataException("UM-RESTART-STOPPED-REVIEW-IDENTITY");
        return Exclusive(()=>{
            var old=Observe()??throw new InvalidDataException(
                "UM-RESTART-STOPPED-REVIEW-MISSING");
            if(old.MapId!=expectedMap||old.Id!=expectedId||
               old.OldPid!=oldPid||old.NewPid!=attemptedNewPid)
                throw new InvalidOperationException(
                    "UM-RESTART-STOPPED-REVIEW-ATTEMPT-CHANGED");
            string audit=Path.Combine(folder,key+"-"+old.Id.ToString("N")+
                ".operator-reviewed.json");
            if(old.Phase==AsaRestartPhase.OperatorReviewed)
            {
                if(!File.Exists(audit)||
                   (File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("UM-RESTART-STOPPED-REVIEW-AUDIT-MISSING");
                var archived=JsonSerializer.Deserialize<AsaRestartRecord>(
                    File.ReadAllBytes(audit),Json);
                if(archived==null||archived.Id!=old.Id||
                   archived.MapId!=old.MapId||archived.Resource!=old.Resource||
                   archived.OldPid!=old.OldPid||
                   archived.NewPid!=attemptedNewPid||
                   archived.Phase is not (AsaRestartPhase.Stopping or
                       AsaRestartPhase.Starting or AsaRestartPhase.NeedsOperator))
                    throw new InvalidDataException("UM-RESTART-STOPPED-REVIEW-AUDIT-MISMATCH");
                return old;
            }
            if(old.Phase is not (AsaRestartPhase.Stopping or
                AsaRestartPhase.Starting or AsaRestartPhase.NeedsOperator))
                throw new UnauthorizedAccessException(
                    "UM-RESTART-STOPPED-REVIEW-PHASE");
            if(DateTimeOffset.UtcNow-old.ChangedUtc<TimeSpan.FromMinutes(3))
                throw new InvalidOperationException("UM-RESTART-STOPPED-REVIEW-TOO-RECENT");
            byte[] original=File.ReadAllBytes(path);
            if(File.Exists(audit))
            {
                if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
                   !CryptographicOperations.FixedTimeEquals(
                       SHA256.HashData(File.ReadAllBytes(audit)),
                       SHA256.HashData(original)))
                    throw new InvalidDataException(
                        "UM-RESTART-STOPPED-REVIEW-AUDIT-MISMATCH");
            }
            else
            {
                using var file=new FileStream(audit,FileMode.CreateNew,
                    FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
                file.Write(original);
                file.Flush(true);
            }
            var reviewed=old with{Phase=AsaRestartPhase.OperatorReviewed,
                ChangedUtc=DateTimeOffset.UtcNow};
            Write(reviewed);
            return reviewed;
        });
    }

    // Called only by the active native scheduler with the installation
    // operation lease held. Never resume an interrupted old Start token.
    internal AsaRestartRecord ClaimScheduledRetry(Guid id,Guid runId,int failedAttempt,
        Func<bool> verifyStopped)
    {
        ArgumentNullException.ThrowIfNull(verifyStopped);
        return Exclusive(()=>{
            var old=Observe()??throw new InvalidDataException("UM-RESTART-RETRY-JOURNAL-MISSING");
            if(runId==Guid.Empty||old.Id!=id||old.ScheduledRunId!=runId||
               old.Phase!=AsaRestartPhase.Starting||old.NewPid<=0||
               old.StartupAttempt!=failedAttempt||failedAttempt is <1 or >=4)
                throw new InvalidOperationException("UM-RESTART-RETRY-CLAIM-INVALID");
            if(!verifyStopped())throw new InvalidOperationException("UM-RESTART-RETRY-FAMILY-OR-PORT-BUSY");
            string audit=Path.Combine(folder,key+"-"+id.ToString("N")+
                "-startup-"+failedAttempt+".failed.json");
            using(var output=new FileStream(audit,FileMode.CreateNew,FileAccess.Write,
                FileShare.None,4096,FileOptions.WriteThrough))
            {
                output.Write(File.ReadAllBytes(path));output.Flush(true);
            }
            if(!verifyStopped())throw new InvalidOperationException("UM-RESTART-RETRY-FAMILY-OR-PORT-BUSY");
            var next=old with{NewPid=0,StartupAttempt=failedAttempt+1,
                ChangedUtc=DateTimeOffset.UtcNow};
            Write(next);return next;
        });
    }

    /// <summary>
    /// YARK crash-recovery port: after an unexpected ASA exit, preserve the
    /// failed attempt and reset the same restart transaction for the next
    /// bounded retry. The caller must prove the complete ASA/Loader family
    /// stopped before the next Process.Start is permitted.
    /// </summary>
    internal AsaRestartRecord ClaimAutomaticRetry(Guid id,int failedAttempt,
        Func<bool> verifyStopped)
    {
        ArgumentNullException.ThrowIfNull(verifyStopped);
        return Exclusive(()=>{
            var old=Observe()??throw new InvalidDataException("UM-RESTART-RETRY-JOURNAL-MISSING");
            if(old.Id!=id||old.Phase!=AsaRestartPhase.Starting||old.NewPid<=0||
               old.StartupAttempt!=failedAttempt||failedAttempt is <1 or >=3)
                throw new InvalidOperationException("UM-RESTART-AUTO-RETRY-CLAIM-INVALID");
            if(!verifyStopped())throw new InvalidOperationException("UM-RESTART-AUTO-RETRY-FAMILY-RUNNING");
            string audit=Path.Combine(folder,key+"-"+id.ToString("N")+
                "-startup-"+failedAttempt+".auto-retry-failed.json");
            byte[] bytes=File.ReadAllBytes(path);
            if(File.Exists(audit))
            {
                if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
                   !CryptographicOperations.FixedTimeEquals(
                       SHA256.HashData(File.ReadAllBytes(audit)),SHA256.HashData(bytes)))
                    throw new InvalidDataException("UM-RESTART-AUTO-RETRY-ARCHIVE-MISMATCH");
            }
            else
            {
                using var output=new FileStream(audit,FileMode.CreateNew,FileAccess.Write,
                    FileShare.None,4096,FileOptions.WriteThrough);
                output.Write(bytes);output.Flush(true);
            }
            if(!verifyStopped())throw new InvalidOperationException("UM-RESTART-AUTO-RETRY-FAMILY-CHANGED");
            var next=old with{NewPid=0,StartupAttempt=failedAttempt+1,
                ChangedUtc=DateTimeOffset.UtcNow};
            Write(next);return next;
        });
    }

    /// <summary>
    /// Automatically archives a failed restart after its worker has exited.
    /// A live Stopping/Starting transaction is retained for its normal
    /// ten-minute readiness window; NeedsOperator is a terminal failure,
    /// not a request for the user to repair a journal.
    /// </summary>
    public AsaRestartRecord? AutoRecoverPending()
    {
        return Exclusive(()=>{
            var current=Observe();
            if(current==null||current.Phase is (AsaRestartPhase.Completed or
                AsaRestartPhase.OperatorReviewed or AsaRestartPhase.Recovered))
                return current;
            bool failed=current.Phase==AsaRestartPhase.NeedsOperator;
            bool abandoned=current.Phase is (AsaRestartPhase.Stopping or AsaRestartPhase.Starting) &&
                DateTimeOffset.UtcNow-current.ChangedUtc>=TimeSpan.FromMinutes(15);
            if(!failed&&!abandoned)return current;
            byte[] bytes=File.ReadAllBytes(path);
            string audit=Path.Combine(folder,key+"-"+current.Id.ToString("N")+
                ".auto-recovered.json");
            if(File.Exists(audit))
            {
                if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
                   !CryptographicOperations.FixedTimeEquals(
                       SHA256.HashData(File.ReadAllBytes(audit)),SHA256.HashData(bytes)))
                    throw new InvalidDataException("UM-RESTART-AUTO-RECOVERY-ARCHIVE-MISMATCH");
            }
            else
            {
                using var output=new FileStream(audit,FileMode.CreateNew,FileAccess.Write,
                    FileShare.None,4096,FileOptions.WriteThrough);
                output.Write(bytes);output.Flush(true);
            }
            var recovered=current with{Phase=AsaRestartPhase.Recovered,
                ChangedUtc=DateTimeOffset.UtcNow};
            Write(recovered);
            return recovered;
        });
    }

    public void DemandNoPending()
    {
        var current=AutoRecoverPending();
        if(current!=null&&current.Phase is not (AsaRestartPhase.Completed or
            AsaRestartPhase.OperatorReviewed or AsaRestartPhase.Recovered))
            throw new InvalidOperationException("UM-ASA-RESTART-IN-PROGRESS");
    }

    public void DemandAuthorizedStop(Guid? restartId)
    {
        var current=Observe();
        // An explicit restart authorization is valid ONLY while its exact
        // durable record is present in the expected in-progress phase.
        // Missing/completed journal files cannot authorize a stale token.
        if(restartId==null &&
           (current==null||current.Phase is (AsaRestartPhase.Completed or
               AsaRestartPhase.OperatorReviewed or AsaRestartPhase.Recovered)))return;
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
               (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed or
                AsaRestartPhase.Recovered)))return;
        if(restartId==null||current==null||current.Id!=restartId.Value||
           current.Phase!=AsaRestartPhase.Starting||current.NewPid!=0)
            throw new InvalidOperationException(
                "UM-ASA-RESTART-JOURNAL-START-UNAUTHORIZED");
    }
}
