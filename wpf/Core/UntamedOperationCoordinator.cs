using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

public enum UntamedOperationKind { Launch, Configuration, PluginChange, Backup, Restore, Update, EmergencyKill }
public enum UntamedOperationPhase { Prepared, Mutating, Completed, CancelledBeforeMutation, NeedsOperator, OperatorReviewed }
public sealed record UntamedOperationRecord(int Schema,string Resource,Guid Operation,
    UntamedOperationKind Kind,UntamedOperationPhase Phase,DateTimeOffset UpdatedUtc);

// One machine-wide named semaphore per installation, independent of map/profile ID.
// Semaphores are not thread-affine: a lease can span await safely. Persistent
// pending state is NEVER cleared because of a timeout, PID exit, or GUI restart.
// Live Store/SQL/ASA permissions remain the responsibility of capability gates.
public sealed class UntamedOperationCoordinator
{
    private readonly string stateRoot;
    private static readonly AsyncLocal<string?> IsolatedTestRoot=new();
    public static string DefaultRoot=>IsolatedTestRoot.Value??Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.CommonApplicationData),"UntamedManager","Operations-v1");
    public UntamedOperationCoordinator(string? stateRoot=null)
    {
        this.stateRoot=LocalPath(stateRoot??DefaultRoot);
    }
    // Test-only scope, visible to the isolated Windows test assembly through
    // InternalsVisibleTo. Every default coordinator created within the async
    // test flow writes its journal beneath the test's temporary root instead
    // of machine-wide ProgramData. The shipping UI has no bridge for this.
    internal static IDisposable UseIsolatedRootForTests(string root)
    {
        string? prior=IsolatedTestRoot.Value;
        IsolatedTestRoot.Value=LocalPath(root);
        return new TestRootScope(prior);
    }
    private sealed class TestRootScope(string? prior):IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)==0)IsolatedTestRoot.Value=prior;
        }
    }
    public static string ResourceKey(string installation)
    {
        string path=LocalPath(installation).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        // Reject 8.3/reparse aliases rather than permitting a second resource key.
        if(path.Contains('~'))throw new InvalidDataException("UNTAMED_PATH_ALIAS");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
    }
    private static string LocalPath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
           path.StartsWith(@"\\")||path.StartsWith("//",StringComparison.Ordinal))
            throw new InvalidDataException("UNTAMED_LOCAL_PATH_REQUIRED");
        if(path.Length<3||!char.IsAsciiLetter(path[0])||path[1]!=':'||
           path[2] is not ('\\' or '/')||path[3..].Contains(':'))
            throw new InvalidDataException("UNTAMED_LOCAL_PATH_REQUIRED");
        // Windows GetFullPath may normalize away a trailing-dot component.
        // Reject its original spelling before deriving the shared resource key.
        foreach(string part in path[3..].Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries))
            if(part.EndsWith('.')||part.EndsWith(' '))throw new InvalidDataException("UNTAMED_PATH_ALIAS");
        string full=Path.GetFullPath(path);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UNTAMED_FIXED_DISK_REQUIRED");
        foreach(string part in full[Path.GetPathRoot(full)!.Length..].Split(
            new[]{Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar},StringSplitOptions.RemoveEmptyEntries))
            if(part.EndsWith('.')||part.EndsWith(' '))throw new InvalidDataException("UNTAMED_PATH_ALIAS");
        for(var node=new DirectoryInfo(full);node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UNTAMED_LINKED_PATH");
        return full;
    }
    public Lease Acquire(string installation,UntamedOperationKind kind)
    {
        if(!Enum.IsDefined(kind))throw new InvalidDataException("UNTAMED_OPERATION_KIND");
        string key=ResourceKey(installation);
        var semaphore=new Semaphore(1,1,"Global\\Untamed-Operation-v1-"+key);
        bool entered=false;
        try
        {
            entered=semaphore.WaitOne(0);
            if(!entered)throw new IOException("UNTAMED_OPERATION_BUSY");
            Directory.CreateDirectory(LocalPath(stateRoot));
            string journal=Path.Combine(stateRoot,key+".json");
            var old=Read(journal,key);
            if(old!=null&&old.Phase is not (UntamedOperationPhase.Completed or UntamedOperationPhase.CancelledBeforeMutation or UntamedOperationPhase.OperatorReviewed))
                throw new IOException("UNTAMED_OPERATION_NEEDS_OPERATOR");
            var record=new UntamedOperationRecord(1,key,Guid.NewGuid(),kind,
                UntamedOperationPhase.Prepared,DateTimeOffset.UtcNow);
            Write(journal,record);
            return new Lease(semaphore,journal,record);
        }
        catch
        {
            if(entered)semaphore.Release();semaphore.Dispose();throw;
        }
    }
    public UntamedOperationRecord? Observe(string installation)
    {
        string key=ResourceKey(installation);
        return Read(Path.Combine(stateRoot,key+".json"),key);
    }
    /// <summary>
    /// Explicit local recovery of one interrupted Backup/SaveWorld operation.
    /// The caller supplies fresh original Store-source and stopped-process
    /// verification plus the Manager-local SaveComplete review. This method
    /// serializes BOTH journal reviews against normal mutation with the exact
    /// same machine-wide installation semaphore as Acquire().
    ///
    /// No timeout can free an unknown mutation: only a deliberate operator
    /// approval with pinned original operation ID can write OperatorReviewed.
    /// The old operation journal is preserved byte-for-byte in a durable
    /// per-operation audit file. No Store lock, session or ASA file is changed.
    /// </summary>
    internal void ReviewStoppedBackup(string installation,Guid? expectedOperation,
        Func<bool> verifyOriginalAndStopped,Action reviewLocalReceipt)
    {
        ArgumentNullException.ThrowIfNull(verifyOriginalAndStopped);
        ArgumentNullException.ThrowIfNull(reviewLocalReceipt);
        string key=ResourceKey(installation);
        using var semaphore=new Semaphore(1,1,"Global\\Untamed-Operation-v1-"+key);
        if(!semaphore.WaitOne(0))
            throw new IOException("UNTAMED_OPERATION_REVIEW_BUSY");
        try
        {
            Directory.CreateDirectory(LocalPath(stateRoot));
            string journal=Path.Combine(stateRoot,key+".json");
            var old=Read(journal,key);
            if(old?.Operation!=expectedOperation)
                throw new InvalidOperationException("UNTAMED_OPERATION_REVIEW_ID_CHANGED");
            if(old!=null&&old.Phase is
                (UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or
                 UntamedOperationPhase.NeedsOperator)&&
               old.Kind!=UntamedOperationKind.Backup)
                throw new UnauthorizedAccessException("UNTAMED_OPERATION_REVIEW_NOT_BACKUP");
            if(!verifyOriginalAndStopped())
                throw new UnauthorizedAccessException("UNTAMED_OPERATION_REVIEW_SOURCE_OR_STOP_UNVERIFIED");
            reviewLocalReceipt();
            if(old!=null&&old.Phase is
                (UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or
                 UntamedOperationPhase.NeedsOperator))
            {
                string audit=Path.Combine(stateRoot,key+"-"+old.Operation.ToString("N")+
                    ".operator-reviewed.json");
                byte[] bytes=File.ReadAllBytes(journal);
                if(File.Exists(audit))
                {
                    if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
                       !CryptographicOperations.FixedTimeEquals(
                           SHA256.HashData(File.ReadAllBytes(audit)),SHA256.HashData(bytes)))
                        throw new InvalidDataException("UNTAMED_OPERATION_REVIEW_AUDIT_MISMATCH");
                }
                else
                {
                    using var stream=new FileStream(audit,FileMode.CreateNew,
                        FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk:true);
                }
                Write(journal,old with{
                    Phase=UntamedOperationPhase.OperatorReviewed,
                    UpdatedUtc=DateTimeOffset.UtcNow});
            }
        }
        finally{semaphore.Release();}
    }

    /// <summary>
    /// Explicit recovery for an OLD, uncertain original Store-v1 Launch/Stop
    /// attempt when the EXACT original ASA server is still running. This
    /// does NOT certify the old operation's success and never resends DoExit,
    /// SaveWorld, DinoWipe or Start. The native bridge must independently pin
    /// and recheck the PID + start time, RCON listener, original ACTIVE/DONE
    /// session and DPAPI-backed map profile before it supplies the verifier.
    ///
    /// A durable copy of the original journal is preserved; the journal is
    /// transitioned to OperatorReviewed only under the SAME cross-process
    /// installation semaphore used by Start/Stop/Admin/SteamCMD.
    /// </summary>
    internal void ReviewRunningOriginalLaunch(string installation,
        Guid expectedOperation,Func<bool> verifyOriginalRunning)
    {
        ArgumentNullException.ThrowIfNull(verifyOriginalRunning);
        if(expectedOperation==Guid.Empty)
            throw new InvalidDataException("UM-RECOVERY-OPERATION-ID");
        string key=ResourceKey(installation);
        using var semaphore=new Semaphore(1,1,"Global\\Untamed-Operation-v1-"+key);
        if(!semaphore.WaitOne(0))
            throw new IOException("UM-RECOVERY-OPERATION-STILL-OWNED");
        try
        {
            Directory.CreateDirectory(LocalPath(stateRoot));
            string journal=Path.Combine(stateRoot,key+".json");
            var old=Read(journal,key);
            if(old==null||old.Operation!=expectedOperation)
                throw new InvalidOperationException("UM-RECOVERY-JOURNAL-CHANGED");
            // NEVER review an in-flight mutation, failed Backup/Restore, or
            // an EmergencyKill through the running-Launch recovery route.
            if(old.Kind!=UntamedOperationKind.Launch||
               old.Phase!=UntamedOperationPhase.NeedsOperator)
                throw new UnauthorizedAccessException("UM-RECOVERY-LAUNCH-NEEDS-OPERATOR-ONLY");
            // A failed DoExit/Restart can still be in flight for a while.
            // No time-based auto-clear: age merely qualifies a MANUAL review.
            if(DateTimeOffset.UtcNow-old.UpdatedUtc<TimeSpan.FromMinutes(3))
                throw new InvalidOperationException("UM-RECOVERY-OPERATION-TOO-RECENT");
            if(!verifyOriginalRunning())
                throw new UnauthorizedAccessException("UM-RECOVERY-RUNNING-SOURCE-UNVERIFIED");
            string audit=Path.Combine(stateRoot,key+"-"+old.Operation.ToString("N")+
                ".operator-reviewed.json");
            byte[] bytes=File.ReadAllBytes(journal);
            if(File.Exists(audit))
            {
                if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
                   !CryptographicOperations.FixedTimeEquals(
                       SHA256.HashData(File.ReadAllBytes(audit)),SHA256.HashData(bytes)))
                    throw new InvalidDataException("UM-RECOVERY-AUDIT-MISMATCH");
            }
            else
            {
                using var stream=new FileStream(audit,FileMode.CreateNew,
                    FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
                stream.Write(bytes);
                stream.Flush(flushToDisk:true);
            }
            // Do not change the record if the physical source changed between
            // the two independent fresh observations under the same lease.
            if(!verifyOriginalRunning())
                throw new UnauthorizedAccessException("UM-RECOVERY-SOURCE-CHANGED");
            Write(journal,old with {
                Phase=UntamedOperationPhase.OperatorReviewed,
                UpdatedUtc=DateTimeOffset.UtcNow
            });
        }
        finally{semaphore.Release();}
    }

    public Batch AcquireMany(IEnumerable<string> installations,UntamedOperationKind kind)
    {
        var leases=new List<Lease>();
        try
        {
            foreach(var root in installations.GroupBy(ResourceKey).OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>g.First()))
                leases.Add(Acquire(root,kind));
            return new Batch(leases);
        }
        catch(Exception acquisitionFailure)
        {
            // A failed journal write during cleanup must not strand the other
            // OS leases or hide why the batch could not be acquired.
            try{new Batch(leases).Dispose();}
            catch(Exception cleanupFailure)
            {throw new AggregateException("UNTAMED_BATCH_ACQUIRE_AND_CLEANUP_FAILED",acquisitionFailure,cleanupFailure);}
            throw;
        }
    }
    public sealed class Batch : IDisposable
    {
        private readonly List<Lease> leases;
        internal Batch(List<Lease> leases){this.leases=leases;}
        public void MarkMutationStarted(){foreach(var lease in leases)lease.MarkMutationStarted();}
        public void Complete(){foreach(var lease in leases)lease.Complete();}
        public void Dispose()
        {
            Exception? failure=null;
            foreach(var lease in leases.AsEnumerable().Reverse())
                try{lease.Dispose();}catch(Exception ex){failure??=ex;}
            if(failure!=null)throw new IOException("UNTAMED_BATCH_JOURNAL_FAILED",failure);
        }
    }
    private static UntamedOperationRecord? Read(string path,string key)
    {
        if(!File.Exists(path))return null;
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||new FileInfo(path).Length is <2 or >16384)
            throw new InvalidDataException("UNTAMED_OPERATION_JOURNAL_INVALID");
        try
        {
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var record=JsonSerializer.Deserialize<UntamedOperationRecord>(file);
            if(record==null||record.Schema!=1||record.Resource!=key||record.Operation==Guid.Empty||
               !Enum.IsDefined(record.Phase)||!Enum.IsDefined(record.Kind))
                throw new InvalidDataException("UNTAMED_OPERATION_JOURNAL_INVALID");
            return record;
        }
        catch(JsonException){throw new InvalidDataException("UNTAMED_OPERATION_JOURNAL_INVALID");}
    }
    private static void Write(string path,UntamedOperationRecord record)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,record);stream.Flush(true);}
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public sealed class Lease : IDisposable
    {
        private readonly Semaphore semaphore;
        private readonly string journal;
        private UntamedOperationRecord record;
        private bool disposed,completed;
        internal Lease(Semaphore semaphore,string journal,UntamedOperationRecord record)
        {this.semaphore=semaphore;this.journal=journal;this.record=record;}
        public void MarkMutationStarted()
        {
            if(disposed||completed||record.Phase!=UntamedOperationPhase.Prepared)
                throw new InvalidOperationException("UNTAMED_OPERATION_PHASE");
            var next=record with {Phase=UntamedOperationPhase.Mutating,UpdatedUtc=DateTimeOffset.UtcNow};
            Write(journal,next);record=next;
        }
        public void Complete()
        {
            if(disposed||completed)throw new InvalidOperationException("UNTAMED_OPERATION_PHASE");
            var next=record with {Phase=UntamedOperationPhase.Completed,UpdatedUtc=DateTimeOffset.UtcNow};
            Write(journal,next);record=next;completed=true;
        }
        public void Dispose()
        {
            if(disposed)return;
            disposed=true;
            try
            {
                if(!completed)Write(journal,record with {
                    Phase=record.Phase==UntamedOperationPhase.Prepared?
                        UntamedOperationPhase.CancelledBeforeMutation:UntamedOperationPhase.NeedsOperator,
                    UpdatedUtc=DateTimeOffset.UtcNow});
            }
            finally{semaphore.Release();semaphore.Dispose();}
        }
    }
}
