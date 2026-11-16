using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

public enum AsaStopPhase { Pending, Completed, NeedsOperator, OperatorReviewed }

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
        var file=new FileInfo(path);
        if(file.Length is <=0 or >8192||
           (file.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ASA-STOP-JOURNAL-INVALID");
        var record=JsonSerializer.Deserialize<AsaManualStopRecord>(
            File.ReadAllBytes(path),Json);
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
            File.Move(temp,path,true);
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

    public AsaManualStopRecord Begin(Guid mapId,int oldPid,
        DateTimeOffset oldStartedUtc)
    {
        if(mapId==Guid.Empty||oldPid<=0||oldStartedUtc==default)
            throw new InvalidDataException("UM-ASA-STOP-JOURNAL-IDENTITY");
        return Exclusive(()=>{
            var existing=Observe();
            if(existing!=null&&existing.Phase is
                (AsaStopPhase.Pending or AsaStopPhase.NeedsOperator))
                throw new IOException("UM-ASA-STOP-JOURNAL-NEEDS-OPERATOR");
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
            var changed=current with{
                Phase=AsaStopPhase.OperatorReviewed,
                ChangedUtc=DateTimeOffset.UtcNow
            };
            Write(changed);
            return changed;
        });
    }
}
