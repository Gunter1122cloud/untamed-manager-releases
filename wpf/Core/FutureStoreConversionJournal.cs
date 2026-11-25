using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

/// <summary>
/// Durable LOCAL conversion attempt journal. This is not a Store transaction
/// executor: no ASA, world, RCON, remote UNC or Store source is ever touched.
/// The journal exists so a later native converter cannot replay an uncertain
/// one-shot -converttostore run after a crash. An interrupted .next is
/// intentionally never cleaned up or silently retried.
/// </summary>
public sealed record FutureStoreConversionEntry(
    int Schema,Guid OperationId,Guid MapId,string ClusterId,
    string OriginalSourceSha256,string Phase,
    string BackupManifestSha256,string ConvertedStoreSha256,
    string PlannedAppendSha256,string ResultingSourceSha256,
    DateTimeOffset CreatedUtc,DateTimeOffset UpdatedUtc)
{
    public bool StoreWriterCertified=>false;
    public bool CharacterTravelCertified=>false;
    public bool AutoRetryAllowed=>false;
}
public static class FutureStoreConversionJournal
{
    private const int Schema=1;
    private sealed record Envelope(string Sha256,FutureStoreConversionEntry Entry);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static bool Hash(string? s)=>s?.Length==64&&s.All(Uri.IsHexDigit);
    private static string PathFor(string profile,Guid id)
    {
        if(id==Guid.Empty)throw new InvalidDataException(
            "UM-FUTURE-CONVERSION-MAP-ID");
        string safe=VaultReadSnapshot.LocalPath(profile,true);
        return Path.Combine(safe,"future-store-conversions",id.ToString("N")+".json");
    }
    private static string Digest(FutureStoreConversionEntry entry)=>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            entry,Json)));
    private static void Validate(FutureStoreConversionEntry entry)
    {
        if(entry.Schema!=Schema||entry.OperationId==Guid.Empty||
           entry.MapId==Guid.Empty||entry.ClusterId.Length is <1 or >64||
           !entry.ClusterId.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-')||
           !Hash(entry.OriginalSourceSha256)||
           entry.CreatedUtc==default||entry.UpdatedUtc<entry.CreatedUtc||
           entry.UpdatedUtc>DateTimeOffset.UtcNow.AddMinutes(5)||
           entry.Phase is not ("prepared" or "backupVerified" or
               "conversionAttempted" or "conversionVerified" or
               "appendStaged" or "appendCommitted" or "needsOperator" or
               "cancelledBeforeConversion")||
           (entry.Phase is "backupVerified" or "conversionAttempted" or
               "conversionVerified" or "appendStaged" or "appendCommitted"
               && !Hash(entry.BackupManifestSha256))||
           (entry.Phase is "conversionVerified" or "appendStaged" or
               "appendCommitted" && !Hash(entry.ConvertedStoreSha256))||
           (entry.Phase is "appendStaged" or "appendCommitted"
               && !Hash(entry.PlannedAppendSha256))||
           (entry.Phase=="appendCommitted"&&!Hash(entry.ResultingSourceSha256)))
            throw new InvalidDataException("UM-FUTURE-CONVERSION-JOURNAL-SCHEMA");
    }
    private static FutureStoreConversionEntry? ReadCore(string file)
    {
        if(File.Exists(file+".next"))
            throw new IOException("UM-FUTURE-CONVERSION-JOURNAL-INTERRUPTED");
        if(!File.Exists(file))return null;
        if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-FUTURE-CONVERSION-JOURNAL-LINK");
        byte[] raw=File.ReadAllBytes(file);
        if(raw.Length is <16 or >16384)
            throw new InvalidDataException("UM-FUTURE-CONVERSION-JOURNAL-SIZE");
        var value=JsonSerializer.Deserialize<Envelope>(raw,Json) ??
            throw new InvalidDataException("UM-FUTURE-CONVERSION-JOURNAL-EMPTY");
        Validate(value.Entry);
        if(value.Sha256!=Digest(value.Entry))
            throw new InvalidDataException("UM-FUTURE-CONVERSION-JOURNAL-TAMPERED");
        return value.Entry;
    }
    public static FutureStoreConversionEntry? Inspect(string profile,Guid id)=>
        ReadCore(PathFor(profile,id));

    private static void Save(string file,FutureStoreConversionEntry value,
        bool initial)
    {
        Validate(value);
        string folder=Path.GetDirectoryName(file)!;
        if(Directory.Exists(folder)&&
           (File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-FUTURE-CONVERSION-JOURNAL-LINK");
        Directory.CreateDirectory(folder);
        if(File.Exists(file+".next"))
            throw new IOException("UM-FUTURE-CONVERSION-JOURNAL-INTERRUPTED");
        if(initial&&File.Exists(file))
            throw new InvalidOperationException("UM-FUTURE-CONVERSION-JOURNAL-ALREADY-EXISTS");
        if(!initial&&!File.Exists(file))
            throw new InvalidOperationException("UM-FUTURE-CONVERSION-JOURNAL-MISSING");
        string staging=file+".next";
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(
            new Envelope(Digest(value),value),Json);
        using(var stream=new FileStream(staging,FileMode.CreateNew,
            FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        {stream.Write(bytes);stream.Flush(true);}
        if(initial)File.Move(staging,file);
        else File.Replace(staging,file,file+".previous",true);
    }
    private static IDisposable Lock(string path)
    {
        string key=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        var mutex=new Mutex(false,@"Global\Untamed-FutureStoreConversion-"+key);
        bool entered=false;
        try
        {
            try{entered=mutex.WaitOne(0);}
            catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("UM-FUTURE-CONVERSION-JOURNAL-BUSY");
            return new Owned(mutex);
        }
        catch{if(entered)mutex.ReleaseMutex();mutex.Dispose();throw;}
    }
    private sealed class Owned : IDisposable
    {
        private readonly Mutex m;
        public Owned(Mutex value)=>m=value;
        public void Dispose(){m.ReleaseMutex();m.Dispose();}
    }
    public static FutureStoreConversionEntry Begin(string profile,Guid mapId,
        string clusterId,string sourceSha256)
    {
        string file=PathFor(profile,mapId);
        using var gate=Lock(file);
        if(ReadCore(file)!=null)
            throw new InvalidOperationException("UM-FUTURE-CONVERSION-JOURNAL-ALREADY-EXISTS");
        var now=DateTimeOffset.UtcNow;
        var entry=new FutureStoreConversionEntry(Schema,Guid.NewGuid(),mapId,
            clusterId,sourceSha256,"prepared","","","","",now,now);
        Save(file,entry,true);
        return entry;
    }

    // External witnesses MUST be independently checked by the future
    // native converter before recording any receipt. These hash strings
    // do not certify ASA SaveWorld, Store conversion, or safe peer commit.
    public static FutureStoreConversionEntry Advance(string profile,Guid mapId,
        Guid operationId,string expectedPhase,string nextPhase,string evidenceSha256="")
    {
        string file=PathFor(profile,mapId);
        using var gate=Lock(file);
        var prior=ReadCore(file)??throw new InvalidOperationException(
            "UM-FUTURE-CONVERSION-JOURNAL-MISSING");
        if(prior.MapId!=mapId||prior.OperationId!=operationId||
           prior.Phase!=expectedPhase)
            throw new InvalidOperationException("UM-FUTURE-CONVERSION-JOURNAL-STATE-CHANGED");
        bool transition=(expectedPhase,nextPhase) switch
        {
            ("prepared","backupVerified")=>true,
            ("backupVerified","conversionAttempted")=>true,
            ("conversionAttempted","conversionVerified")=>true,
            ("conversionVerified","appendStaged")=>true,
            ("appendStaged","appendCommitted")=>true,
            ("prepared","cancelledBeforeConversion")=>true,
            ("backupVerified","cancelledBeforeConversion")=>true,
            ("prepared","needsOperator")=>true,
            ("backupVerified","needsOperator")=>true,
            ("conversionAttempted","needsOperator")=>true,
            ("conversionVerified","needsOperator")=>true,
            ("appendStaged","needsOperator")=>true,
            _=>false
        };
        if(!transition)throw new InvalidOperationException(
            "UM-FUTURE-CONVERSION-JOURNAL-TRANSITION-DENIED");
        if(nextPhase is "backupVerified" or "conversionVerified" or
           "appendStaged" or "appendCommitted" && !Hash(evidenceSha256))
            throw new InvalidDataException("UM-FUTURE-CONVERSION-EVIDENCE-MISSING");
        var updated=prior with{
            Phase=nextPhase,UpdatedUtc=DateTimeOffset.UtcNow,
            BackupManifestSha256=nextPhase=="backupVerified"?
                evidenceSha256:prior.BackupManifestSha256,
            ConvertedStoreSha256=nextPhase=="conversionVerified"?
                evidenceSha256:prior.ConvertedStoreSha256,
            PlannedAppendSha256=nextPhase=="appendStaged"?
                evidenceSha256:prior.PlannedAppendSha256,
            ResultingSourceSha256=nextPhase=="appendCommitted"?
                evidenceSha256:prior.ResultingSourceSha256
        };
        Save(file,updated,false);
        return updated;
    }
}
