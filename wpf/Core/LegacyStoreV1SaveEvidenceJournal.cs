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

    private static bool Hash(string? value)=>value is {Length:64} &&
        value.All(Uri.IsHexDigit);

    private static string PathFor(string managerProfileRoot,Guid mapId)
    {
        if(mapId==Guid.Empty)throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-MAP");
        string root=VaultReadSnapshot.LocalPath(managerProfileRoot,true);
        string folder=System.IO.Path.Combine(root,"historical-save-evidence-v1");
        // This is a dedicated Manager-owned local folder, not any Kova path.
        if(Directory.Exists(folder)&&
           (File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
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
           value.Phase is not ("prepared" or "completed" or "needsOperator")||
           (value.Phase=="completed"&&
             (!value.SaveObservedUtc.HasValue||
              value.SaveObservedUtc.Value<value.BeganUtc))||
           (value.Phase!="completed"&&value.SaveObservedUtc.HasValue))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-SCHEMA");
    }

    private static LegacyStoreV1SaveEvidence? Load(string file)
    {
        if(File.Exists(file+".next"))
            throw new IOException("UM-ORIGINAL-SAVE-JOURNAL-INTERRUPTED");
        if(!File.Exists(file))return null;
        if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||
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

    public static LegacyStoreV1SaveEvidence Begin(string managerProfileRoot,
        Guid operationId,Guid mapId,int pid,DateTimeOffset startedUtc,
        string originalSourceSha256)
    {
        if(operationId==Guid.Empty||mapId==Guid.Empty||pid<=0||
           startedUtc==default||!Hash(originalSourceSha256))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-APPROVAL");
        string file=PathFor(managerProfileRoot,mapId);
        var prior=Load(file);
        if(prior!=null&&prior.Phase!="completed")
            throw new InvalidOperationException("UM-ORIGINAL-SAVE-JOURNAL-NEEDS-REVIEW");
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
        var current=RequireAttempt(file,operationId,mapId,pid,startedUtc,originalSha);
        if(!saveCompleteObserved||doExitSent||observedUtc<current.BeganUtc||
           observedUtc>DateTimeOffset.UtcNow.AddMinutes(1))
            throw new InvalidDataException("UM-ORIGINAL-SAVE-JOURNAL-RECEIPT-INVALID");
        var next=current with{Phase="completed",SaveObservedUtc=observedUtc};
        Publish(file,next);
        return next;
    }

    public static void MarkUncertain(string managerProfileRoot,Guid operationId,
        Guid mapId,int pid,DateTimeOffset startedUtc,string originalSha)
    {
        string file=PathFor(managerProfileRoot,mapId);
        var current=RequireAttempt(file,operationId,mapId,pid,startedUtc,originalSha);
        Publish(file,current with{Phase="needsOperator"});
    }
}
