using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Persistent LOCAL MANAGER receipt for a guarded historical SaveWorld-only
/// operation. Separate from the original Kova ACTIVE/DONE, locks, transfer and
/// world files; this does not create a second backup or writer protocol.
/// 'completed' only means a new ASA SaveComplete log + stable .ark was
/// observed for an exact local PID/start/source guard. No fsync, single-writer
/// quorum, client rejoin or production restore authority is implied.
/// </summary>
public sealed record LegacyStoreV1SaveEvidence(
    int SchemaVersion, Guid OperationId, Guid MapId, int Pid,
    DateTimeOffset PidStartedUtc, string OriginalSourceSha256,
    string Phase, DateTimeOffset BeganUtc, DateTimeOffset? SaveObservedUtc)
{
    public bool LocalSaveCompleteObserved => Phase=="completed" &&
        SaveObservedUtc.HasValue;
    // Same .ark file changed after dispatch, but the ASA log pair is absent.
    // This does NOT certify the RCON command caused the file update.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LocalWorldMetadataObserved => Phase=="metadataObserved" &&
        SaveObservedUtc.HasValue;
    public bool SharedStoreWriterCertified=>false;
    public bool BothHostsSaveCompleteCertified=>false;
    public bool GameClientRejoinVerified=>false;
    public bool ProductionRestoreAllowed=>false;
    public bool FsyncCertified=>false;
}
public static class LegacyStoreV1SaveEvidenceJournal
{
    private const int Schema=1;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Sealed(string Digest,LegacyStoreV1SaveEvidence Evidence);

    // A local operation journal must have exactly one writer across all
    // Manager PROCESSES (not merely the WebView bridge's in-process semaphore).
    // This is not an ownership/lease claim on the ORIGINAL Kova Store share.
    private sealed class JournalWriteLease : IDisposable
    {
        private readonly System.Threading.Mutex gate;
        public JournalWriteLease(System.Threading.Mutex mutex) => gate=mutex;
        public void Dispose()
        {
            try { gate.ReleaseMutex(); }
            finally { gate.Dispose(); }
        }
    }

    private static JournalWriteLease AcquireWriteLease(string file)
    {
        // Full canonical journal pathname scopes the lock to one local
        // Manager profile + one map, including across Windows sessions.
        string key=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(file).ToUpperInvariant())));
        var gate=new System.Threading.Mutex(false,
            @"Global\Obsidian-StoreV1-SaveEvidence-"+key);
        try
        {
            try
            {
                if(!gate.WaitOne(0))
                    throw new IOException("UM-ORIGINAL-SAVE-JOURNAL-WRITER-BUSY");
            }
            catch(System.Threading.AbandonedMutexException)
            {
                // Windows released a crashed process's OS mutex. The durable
                // prepared/.next journal is still authoritative, never
                // silently recovered or removed.
            }
            return new JournalWriteLease(gate);
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    private static bool Hash(string? value)=>value is {Length:64} &&
        value.All(Uri.IsHexDigit);

    private static string PathFor(string managerProfileRoot,Guid mapId)
    {
        if(mapId==Guid.Empty)throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-MAP");
        string root=VaultReadSnapshot.LocalPath(managerProfileRoot,true);
        string folder=System.IO.Path.Combine(root,"historical-save-evidence-v1");
        // This is a dedicated Manager-owned local folder, not any original
        // Store path. Directory.Exists silently returns false on access
        // denial or for an unexpected file/reparse point at the directory
        // slot. Neither is evidence that a previously prepared receipt is
        // absent: only an actually missing directory is allowed.
        var folderAttributes=AttributesIfPresent(folder);
        if(folderAttributes.HasValue&&
           (folderAttributes.Value&
             (FileAttributes.Directory|FileAttributes.ReparsePoint))!=
             FileAttributes.Directory)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-LINKED");
        return System.IO.Path.Combine(folder,mapId.ToString("N")+".json");
    }

    private static string Digest(LegacyStoreV1SaveEvidence evidence)=>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            evidence,Json)));

    private static void Validate(LegacyStoreV1SaveEvidence value)
    {
        if(value.SchemaVersion!=Schema||value.OperationId==Guid.Empty||
           value.MapId==Guid.Empty||value.Pid<=0||value.PidStartedUtc==default||
           value.BeganUtc==default||!Hash(value.OriginalSourceSha256)||
           value.Phase is not ("prepared" or "completed" or "metadataObserved" or
             "notDispatched" or "needsOperator" or "reviewed")||
           ((value.Phase is "completed" or "metadataObserved")&&
             (!value.SaveObservedUtc.HasValue||
              value.SaveObservedUtc.Value<value.BeganUtc))||
           (value.Phase is not ("completed" or "metadataObserved")&&
             value.SaveObservedUtc.HasValue))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-SCHEMA");
    }

    private static FileAttributes? AttributesIfPresent(string path)
    {
        // File.Exists/Directory.Exists silently return false on access
        // errors. That is not safe for a SaveWorld intent which gates
        // later RCON/Stop/Restart; only an explicitly missing file or
        // parent may mean "no pending receipt".
        try{return File.GetAttributes(path);}
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
    }

    private static LegacyStoreV1SaveEvidence? Load(string file)
    {
        // A crash or filesystem tamper may leave either a file OR a
        // directory at the atomic .next slot. Neither may be interpreted as
        // an absent receipt while a new Stop/Restart/RCON mutation proceeds.
        if(AttributesIfPresent(file+".next").HasValue)
            throw new IOException("UM-ORIGINAL-SAVE-JOURNAL-INTERRUPTED");
        var attrs=AttributesIfPresent(file);
        if(!attrs.HasValue)return null;
        if((attrs.Value&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0||
           new FileInfo(file).Length is <64 or >4096)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-UNSAFE");
        var envelope=JsonSerializer.Deserialize<Sealed>(File.ReadAllText(file),Json)
            ??throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-EMPTY");
        if(envelope.Evidence==null||!Hash(envelope.Digest)||
           !string.Equals(envelope.Digest,Digest(envelope.Evidence),
                StringComparison.Ordinal))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-TAMPERED");
        Validate(envelope.Evidence);
        return envelope.Evidence;
    }

    public static LegacyStoreV1SaveEvidence? Inspect(
        string managerProfileRoot,Guid mapId)
    {
        string file=PathFor(managerProfileRoot,mapId);
        var data=Load(file);
        if(data!=null&&data.MapId!=mapId)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-OTHER-MAP");
        return data;
    }

    /// <summary>
    /// Operator-facing READ-ONLY diagnosis. An interrupted, tampered or
    /// inaccessible receipt must be visible as an explicit blocker instead
    /// of crashing the Stop/Restart/SaveWorld recovery screen. This helper
    /// NEVER changes journal bytes, marks an operation reviewed or grants
    /// permission to issue an RCON command. Mutating preflights continue
    /// to use the strict, exception-throwing Inspect() entry point.
    /// </summary>
    public static (LegacyStoreV1SaveEvidence? Evidence,bool Unreadable)
        InspectReadOnly(string managerProfileRoot,Guid mapId)
    {
        try{return(Inspect(managerProfileRoot,mapId),false);}
        catch(Exception ex) when(ex is IOException or InvalidDataException or
            UnauthorizedAccessException or JsonException)
        {
            return(null,true);
        }
    }

    // P1 operator-only, read-only diagnosis. No broken/partial receipt is
    // reinterpreted as "missing", no operation identity is guessed, and no
    // file is repaired, renamed, deleted, quarantined or RCON-dispatched.
    // The original bytes remain untouched; only their bounded digest is
    // returned, never credentials, paths or JSON content.
    public sealed record ReadOnlyDiagnosis(string State,string ReasonCode,
        long? ByteLength,string? Sha256,bool OriginalBytesPreserved);

    public static ReadOnlyDiagnosis DiagnoseReadOnly(string managerProfileRoot,
        Guid mapId)
    {
        string? file=null;
        long? size=null;
        string? sha=null;
        try
        {
            file=PathFor(managerProfileRoot,mapId);
            if(AttributesIfPresent(file+".next").HasValue)
                return new("interrupted","UM-ORIGINAL-SAVE-JOURNAL-INTERRUPTED",
                    null,null,true);
            var attrs=AttributesIfPresent(file);
            if(!attrs.HasValue)
                return new("missing","UM-ORIGINAL-SAVE-JOURNAL-NOT-PRESENT",
                    null,null,true);
            if((attrs.Value&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)
                return new("unsafe","UM-ORIGINAL-SAVE-JOURNAL-UNSAFE",
                    null,null,true);
            var info=new FileInfo(file);
            size=info.Length;
            // Only hash canonical Manager-local small receipts, never large,
            // potentially mislocated data or UNC/Store roots.
            if(size is >=0 and <=4096)
            {
                using var stream=new FileStream(file,FileMode.Open,
                    FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                sha=Convert.ToHexString(SHA256.HashData(stream));
            }
            Inspect(managerProfileRoot,mapId);
            return new("valid","UM-ORIGINAL-SAVE-JOURNAL-VALID",
                size,sha,true);
        }
        catch(UnauthorizedAccessException)
        {
            return new("inaccessible","UM-ORIGINAL-SAVE-JOURNAL-ACCESS-DENIED",
                size,sha,true);
        }
        catch(System.Text.Json.JsonException)
        {
            return new("corrupt","UM-ORIGINAL-SAVE-JOURNAL-MALFORMED",
                size,sha,true);
        }
        catch(InvalidDataException ex)
        {
            // Only self-authored stable codes can reach the UI.
            string code=ex.Message.StartsWith("UM-ORIGINAL-SAVE-JOURNAL-",
                StringComparison.Ordinal)?ex.Message:
                "UM-ORIGINAL-SAVE-JOURNAL-INVALID";
            if(code.Length>90||code.Any(char.IsWhiteSpace))
                code="UM-ORIGINAL-SAVE-JOURNAL-INVALID";
            return new(code.Contains("OTHER-MAP",StringComparison.Ordinal)?
                "otherMap":"corrupt",code,size,sha,true);
        }
        catch(IOException)
        {
            // Sharing violations and transient I/O errors must not be
            // mislabeled as irrecoverably corrupt JSON.
            return new("ioUnavailable","UM-ORIGINAL-SAVE-JOURNAL-IO-UNAVAILABLE",
                size,sha,true);
        }
    }

    public sealed record ForensicExport(Guid MapId,string FileName,
        string SourceSlot,string FailureCode,long Bytes,string Sha256,
        bool OriginalUntouched,bool SendsRcon,bool GrantsRecovery);

    // Explicit operator export of an UNREADABLE Manager-local SaveEvidence
    // receipt. The identity of a broken record is never invented: the map
    // comes from the native owner's LocalMap profile, not from corrupted JSON.
    // Only a byte-identical copy is made under another Manager-local folder.
    // This NEVER clears its unknown outcome or enables a new ASA operation.
    public static ForensicExport ExportUnreadable(string managerProfileRoot,
        Guid mapId,bool operatorApproved)
    {
        if(!operatorApproved)
            throw new UnauthorizedAccessException(
                "UM-ORIGINAL-SAVE-FORENSIC-APPROVAL-REQUIRED");
        string file=PathFor(managerProfileRoot,mapId);
        var diagnosis=DiagnoseReadOnly(managerProfileRoot,mapId);
        if(diagnosis.State is "missing" or "valid")
            throw new InvalidOperationException(
                "UM-ORIGINAL-SAVE-FORENSIC-UNREADABLE-ONLY");
        string pending=file+".next";
        bool next=AttributesIfPresent(pending).HasValue;
        string src=next?pending:file;
        var attrs=AttributesIfPresent(src);
        if(!attrs.HasValue)
            throw new IOException("UM-ORIGINAL-SAVE-FORENSIC-SOURCE-MISSING");
        if((attrs.Value&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-FORENSIC-LINKED");
        // No large/archive/Store payloads are copied. A receipt and .next
        // are normally below 4 KiB, with 64 KiB maximum for forensic work.
        using var input=new FileStream(src,FileMode.Open,FileAccess.Read,
            FileShare.ReadWrite|FileShare.Delete,4096,FileOptions.SequentialScan);
        if(input.Length is <1 or >65536)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-FORENSIC-SIZE");
        byte[] original=new byte[checked((int)input.Length)];
        input.ReadExactly(original);
        if(input.Length!=original.Length)
            throw new IOException("UM-ORIGINAL-SAVE-FORENSIC-SOURCE-CHANGED");
        string sha=Convert.ToHexString(SHA256.HashData(original));
        string root=VaultReadSnapshot.LocalPath(managerProfileRoot,true);
        string directory=Path.Combine(root,"historical-save-evidence-forensic-v1");
        var parent=AttributesIfPresent(directory);
        if(parent.HasValue&&(parent.Value&(FileAttributes.Directory|
            FileAttributes.ReparsePoint))!=FileAttributes.Directory)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-FORENSIC-ROOT-UNSAFE");
        Directory.CreateDirectory(directory);
        string name=mapId.ToString("N")+"-"+Guid.NewGuid().ToString("N")+
            (next?".next.bin":".json.bin");
        string target=Path.Combine(directory,name);
        using(var output=new FileStream(target,FileMode.CreateNew,
            FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        {
            output.Write(original);
            output.Flush(true);
        }
        // Verify the explicitly-created local artifact before reporting it.
        if(!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(File.ReadAllBytes(target)),SHA256.HashData(original)))
            throw new IOException("UM-ORIGINAL-SAVE-FORENSIC-COPY-FAILED");
        return new(mapId,name,next?"next":"current",
            diagnosis.ReasonCode,original.Length,sha,true,false,false);
    }

    private static void Publish(string file,LegacyStoreV1SaveEvidence evidence)
    {
        Validate(evidence);
        string dir=System.IO.Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(dir);
        if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-LINKED");
        string next=file+".next";
        if(File.Exists(next)||Directory.Exists(next))
            throw new IOException("UM-ORIGINAL-SAVE-JOURNAL-INTERRUPTED");
        string data=JsonSerializer.Serialize(new Sealed(Digest(evidence),evidence),Json);
        byte[] bytes=Encoding.UTF8.GetBytes(data);
        using(var stream=new FileStream(next,FileMode.CreateNew,FileAccess.Write,
            FileShare.None,4096,FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk:true);
        }
        File.Move(next,file,overwrite:true);
    }

    private static void ArchivePreviousBeforeNewAttempt(
        string file,LegacyStoreV1SaveEvidence prior)
    {
        // Preserve the completed/reviewed receipt byte-for-byte BEFORE the
        // latest map-scoped receipt is replaced by the next approved save.
        // The original source/Store/ASA paths are never touched.
        if(prior.Phase is not ("completed" or "metadataObserved" or
            "notDispatched" or "reviewed"))
            throw new InvalidOperationException(
                "UM-ORIGINAL-SAVE-JOURNAL-NEEDS-REVIEW");
        string archiveFolder=Path.Combine(Path.GetDirectoryName(file)!,"history");
        var before=AttributesIfPresent(archiveFolder);
        if(before.HasValue&&(before.Value&(FileAttributes.ReparsePoint|
            FileAttributes.Directory))!=FileAttributes.Directory)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-HISTORY-UNSAFE");
        Directory.CreateDirectory(archiveFolder);
        if((File.GetAttributes(archiveFolder)&
            (FileAttributes.ReparsePoint|FileAttributes.Directory))!=
            FileAttributes.Directory)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-HISTORY-UNSAFE");
        string archived=Path.Combine(archiveFolder,prior.MapId.ToString("N")+
            "-"+prior.OperationId.ToString("N")+"-"+prior.Phase+".json");
        // Another process cannot replace the file via the Manager APIs:
        // the map-scoped cross-process journal write lease is already held.
        byte[] original=File.ReadAllBytes(file);
        if(original.Length is <64 or >4096)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-HISTORY-UNSAFE");
        // Revalidate the exact sealed evidence being archived, rather than
        // trusting metadata captured before a filesystem race.
        var now=Load(file);
        if(now==null||now!=prior)
            throw new InvalidOperationException(
                "UM-ORIGINAL-SAVE-HISTORY-RECEIPT-CHANGED");
        var attrs=AttributesIfPresent(archived);
        if(attrs.HasValue)
        {
            if((attrs.Value&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0||
               new FileInfo(archived).Length is <64 or >4096||
               !CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(File.ReadAllBytes(archived)),
                   SHA256.HashData(original)))
                throw new InvalidDataException(
                    "UM-ORIGINAL-SAVE-HISTORY-ARCHIVE-MISMATCH");
            return;
        }
        // Creating a new immutable history slot is never an overwrite.
        // An interrupted write leaves a mismatching slot that FAILS CLOSED
        // on the next attempt; it cannot silently erase old evidence.
        using var writer=new FileStream(archived,FileMode.CreateNew,
            FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
        writer.Write(original);
        writer.Flush(flushToDisk:true);
    }

    public static LegacyStoreV1SaveEvidence Begin(string managerProfileRoot,
        Guid operationId,Guid mapId,int pid,DateTimeOffset startedUtc,
        string originalSourceSha256)
    {
        if(operationId==Guid.Empty||mapId==Guid.Empty||pid<=0||
           startedUtc==default||!Hash(originalSourceSha256))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-APPROVAL");
        string file=PathFor(managerProfileRoot,mapId);
        using var writer=AcquireWriteLease(file);
        var prior=Load(file);
        if(prior!=null&&prior.Phase is not ("completed" or "metadataObserved" or
            "notDispatched" or "reviewed"))
            throw new InvalidOperationException("UM-ORIGINAL-SAVE-JOURNAL-NEEDS-REVIEW");
        if(prior!=null)ArchivePreviousBeforeNewAttempt(file,prior);
        var evidence=new LegacyStoreV1SaveEvidence(Schema,operationId,mapId,pid,
            startedUtc,originalSourceSha256,"prepared",DateTimeOffset.UtcNow,null);
        Publish(file,evidence);
        return evidence;
    }

    private static LegacyStoreV1SaveEvidence RequireAttempt(
        string file,Guid operationId,Guid mapId,int pid,
        DateTimeOffset startedUtc,string originalSha)
    {
        var current=Load(file)??throw new InvalidDataException(
            "UM-ORIGINAL-SAVE-JOURNAL-MISSING");
        if(current.OperationId!=operationId||current.MapId!=mapId||
           current.Pid!=pid||current.PidStartedUtc!=startedUtc||
           !string.Equals(current.OriginalSourceSha256,originalSha,
               StringComparison.Ordinal)||current.Phase!="prepared")
            throw new InvalidOperationException("UM-ORIGINAL-SAVE-JOURNAL-ATTEMPT-CHANGED");
        return current;
    }

    public static LegacyStoreV1SaveEvidence Complete(string managerProfileRoot,
        Guid operationId,Guid mapId,int pid,DateTimeOffset startedUtc,
        string originalSha,DateTimeOffset observedUtc,bool saveCompleteObserved,
        bool doExitSent)
    {
        string file=PathFor(managerProfileRoot,mapId);
        using var writer=AcquireWriteLease(file);
        var current=RequireAttempt(file,operationId,mapId,pid,startedUtc,originalSha);
        if(!saveCompleteObserved||doExitSent||observedUtc<current.BeganUtc||
           observedUtc>DateTimeOffset.UtcNow.AddMinutes(1))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-RECEIPT-INVALID");
        var next=current with{Phase="completed",SaveObservedUtc=observedUtc};
        Publish(file,next);
        return next;
    }

    /// <summary>
    /// A restricted, post-dispatch local .ark metadata witness. It never
    /// asserts SaveComplete, an fsync, shared Store ownership, or restore.
    /// The caller must have independently observed the same owned ASA PID and
    /// that its RCON SaveWorld dispatch returned without uncertainty.
    /// </summary>
    public static LegacyStoreV1SaveEvidence CompleteWorldMetadata(
        string managerProfileRoot,Guid operationId,Guid mapId,int pid,
        DateTimeOffset startedUtc,string originalSha,DateTimeOffset observedUtc,
        bool worldMetadataObserved,bool doExitSent)
    {
        string file=PathFor(managerProfileRoot,mapId);
        using var writer=AcquireWriteLease(file);
        var current=RequireAttempt(file,operationId,mapId,pid,startedUtc,originalSha);
        if(!worldMetadataObserved||doExitSent||observedUtc<current.BeganUtc||
           observedUtc>DateTimeOffset.UtcNow.AddMinutes(1))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-METADATA-RECEIPT-INVALID");
        var next=current with{Phase="metadataObserved",SaveObservedUtc=observedUtc};
        Publish(file,next);
        return next;
    }

    /// <summary>
    /// Explicitly reviews a crashed/uncertain LOCAL SaveWorld receipt. The
    /// original pending evidence is durably archived, never deleted, before
    /// publishing a distinct 'reviewed' phase. The native host must supply a
    /// callback that freshly proves stopped ASA and unchanged original Store
    /// source while this map's cross-process receipt mutex is owned.
    /// No original Kova metadata, transfer files or shared locks are changed.
    /// </summary>
    internal static LegacyStoreV1SaveEvidence ReviewUncertain(
        string managerProfileRoot,Guid mapId,Guid expectedOperationId,
        string expectedOriginalSourceSha,Func<bool> verifyStoppedSource)
    {
        ArgumentNullException.ThrowIfNull(verifyStoppedSource);
        if(mapId==Guid.Empty||expectedOperationId==Guid.Empty||
           !Hash(expectedOriginalSourceSha))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-REVIEW-IDENTITY");
        string file=PathFor(managerProfileRoot,mapId);
        using var lease=AcquireWriteLease(file);
        var current=Load(file)??throw new InvalidDataException(
            "UM-ORIGINAL-SAVE-REVIEW-MISSING");
        if(current.MapId!=mapId||current.OperationId!=expectedOperationId||
           !string.Equals(current.OriginalSourceSha256,expectedOriginalSourceSha,
               StringComparison.Ordinal))
            throw new InvalidOperationException("UM-ORIGINAL-SAVE-REVIEW-SOURCE-CHANGED");
        if(current.Phase=="reviewed")return current; // idempotent after interrupted multi-journal review
        if(current.Phase is not ("prepared" or "needsOperator"))
            throw new InvalidOperationException("UM-ORIGINAL-SAVE-REVIEW-NOT-UNCERTAIN");
        if(!verifyStoppedSource())
            throw new UnauthorizedAccessException("UM-ORIGINAL-SAVE-REVIEW-STOP-OR-SOURCE-UNVERIFIED");

        // Archive the signed, original sealed envelope byte-for-byte before
        // the reviewed replacement. A retry accepts only the exact same
        // archive bytes, never overwrites a different prior review.
        string archiveFolder=Path.Combine(Path.GetDirectoryName(file)!,"reviewed");
        if(Directory.Exists(archiveFolder)&&
           (File.GetAttributes(archiveFolder)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-REVIEW-LINKED");
        Directory.CreateDirectory(archiveFolder);
        if((File.GetAttributes(archiveFolder)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ORIGINAL-SAVE-REVIEW-LINKED");
        string archived=Path.Combine(archiveFolder,
            mapId.ToString("N")+"-"+expectedOperationId.ToString("N")+".json");
        byte[] original=File.ReadAllBytes(file);
        if(File.Exists(archived))
        {
            if((File.GetAttributes(archived)&FileAttributes.ReparsePoint)!=0||
               !CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(File.ReadAllBytes(archived)),
                   SHA256.HashData(original)))
                throw new InvalidDataException("UM-ORIGINAL-SAVE-REVIEW-ARCHIVE-MISMATCH");
        }
        else
        {
            using var stream=new FileStream(archived,FileMode.CreateNew,
                FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
            stream.Write(original);
            stream.Flush(flushToDisk:true);
        }
        var reviewed=current with{Phase="reviewed"};
        Publish(file,reviewed);
        return reviewed;
    }

    /// <summary>
    /// ONLY the pre-dispatch native worker may close a prepared SaveWorld
    /// intent this way. A cancelled attempt never means SaveComplete and
    /// remains archived byte-for-byte before a separately requested retry.
    /// The caller must have proven no mutating RCON bytes were attempted.
    /// </summary>
    public static LegacyStoreV1SaveEvidence MarkNotDispatched(
        string managerProfileRoot,Guid operationId,Guid mapId,int pid,
        DateTimeOffset startedUtc,string originalSha)
    {
        string file=PathFor(managerProfileRoot,mapId);
        using var writer=AcquireWriteLease(file);
        var current=RequireAttempt(file,operationId,mapId,pid,startedUtc,originalSha);
        var result=current with{Phase="notDispatched"};
        Publish(file,result);
        return result;
    }

    public static void MarkUncertain(string managerProfileRoot,Guid operationId,
        Guid mapId,int pid,DateTimeOffset startedUtc,string originalSha)
    {
        string file=PathFor(managerProfileRoot,mapId);
        using var writer=AcquireWriteLease(file);
        var current=RequireAttempt(file,operationId,mapId,pid,startedUtc,originalSha);
        Publish(file,current with{Phase="needsOperator"});
    }
}
