using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public enum SteamQueuePhase
{
    Prepared, MapInProgress, Completed, NeedsOperator
}
public sealed record SteamQueueSnapshot(Guid QueueId,SteamQueuePhase Phase,
    string HostId,int NextIndex,Guid[] MapIds,Guid? ActiveMap,
    DateTimeOffset ChangedUtc,string Summary);
public sealed record SteamQueueProgress(int Position,int Total,string MapName,string Stage);

/// <summary>
/// A host-wide, cross-profile OS mutex and durable operation journal. It is
/// deliberately NOT a claim of cluster-wide coordination: remote mutation is
/// forbidden until an authenticated, shared two-node lease is implemented.
/// Never auto-resume any interrupted or failed update. One failure blocks all
/// following maps, with no unbounded retries or concurrent Steam downloads.
/// </summary>
public sealed class SteamSerialMaintenanceQueue
{
    private readonly string folder,hostId;
    public SteamSerialMaintenanceQueue(string root,string hostIdentity)
    {
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\",StringComparison.Ordinal)||
           string.IsNullOrWhiteSpace(hostIdentity)||hostIdentity.Length>128)
            throw new InvalidDataException("Valid local Manager profile and host required");
        folder=Path.Combine(Path.GetFullPath(root),"steam-maintenance-queue");
        hostId=hostIdentity;
    }
    private string Journal=>Path.Combine(folder,"queue-v1.json");
    private SteamQueueSnapshot? Read()
    {
        if(!File.Exists(Journal))return null;
        if((File.GetAttributes(Journal)&FileAttributes.ReparsePoint)!=0||
            new FileInfo(Journal).Length is <=0 or >65536)
            throw new InvalidDataException("Steam queue journal is invalid");
        var status=JsonSerializer.Deserialize<SteamQueueSnapshot>(
            File.ReadAllText(Journal));
        if(status==null||status.QueueId==Guid.Empty||status.HostId!=hostId||
           !Enum.IsDefined(status.Phase)||status.MapIds.Length>25)
            throw new InvalidDataException("Steam queue journal identity mismatch");
        return status;
    }
    public SteamQueueSnapshot? Observe()=>Read();
    private void Write(SteamQueueSnapshot snapshot)
    {
        Directory.CreateDirectory(folder);
        if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked maintenance queue refused");
        string temporary=Journal+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,snapshot);stream.Flush(true);}
            File.Move(temporary,Journal,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public async Task<SteamQueueSnapshot> ExecuteAsync(
        IReadOnlyList<LocalMap> selected,IReadOnlyList<LocalMap> registeredLocal,
        Func<LocalMap,ProcessCheck> inspect,
        Func<LocalMap,CancellationToken,Task> applyVerifiedMaintenance,
        IProgress<SteamQueueProgress>? progress=null,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(registeredLocal);
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(applyVerifiedMaintenance);
        if(selected.Count==0||selected.Count>25||
           selected.Any(x=>x==null||x.Id==Guid.Empty)||
           selected.Select(x=>x.Id).Distinct().Count()!=selected.Count||
           selected.Any(x=>!registeredLocal.Any(r=>ReferenceEquals(r,x))))
            throw new InvalidDataException("Queue must contain unique registered local map objects");
        using var semaphore=new Semaphore(1,1,@"Global\Untamed-SteamQueue-Host-v1");
        bool acquired=false;
        try
        {
            acquired=semaphore.WaitOne(0);
            if(!acquired)throw new IOException("Another Manager already owns the local Steam queue");
            var previous=Read();
            if(previous!=null && previous.Phase is
               (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress or SteamQueuePhase.NeedsOperator))
                throw new IOException("Interrupted Steam queue requires human review before another operation");
            var status=new SteamQueueSnapshot(Guid.NewGuid(),SteamQueuePhase.Prepared,
                hostId,0,selected.Select(x=>x.Id).ToArray(),null,
                DateTimeOffset.UtcNow,"Awaiting first map");
            Write(status);
            try
            {
                for(int i=0;i<selected.Count;i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var map=selected[i];
                    // Check all other maps sharing the same installation; a
                    // running sibling can use the same ASA binaries as this map.
                    string install=Path.GetFullPath(map.InstallationPath).TrimEnd(
                        Path.DirectorySeparatorChar);
                    var siblings=registeredLocal.Where(x=>x.Id!=map.Id &&
                        !string.IsNullOrWhiteSpace(x.InstallationPath) &&
                        string.Equals(Path.GetFullPath(x.InstallationPath).TrimEnd(
                            Path.DirectorySeparatorChar),install,StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(siblings.Any(x=>{
                        var state=inspect(x);
                        return state.Running || !state.ConfirmedStopped ||
                            state.Identity!=null || state.LoaderIdentity!=null;
                    }))
                        throw new InvalidOperationException(
                            "Shared-install update blocked: another local map still uses the executable");
                    progress?.Report(new(i+1,selected.Count,map.Name,"Előellenőrzés"));
                    // Persist the operation boundary BEFORE invoking any
                    // handler; an unclean process exit is never auto-retried.
                    status=status with {Phase=SteamQueuePhase.MapInProgress,
                        NextIndex=i,ActiveMap=map.Id,ChangedUtc=DateTimeOffset.UtcNow,
                        Summary="Map maintenance began: "+map.Name};
                    Write(status);
                    await applyVerifiedMaintenance(map,ct);
                    progress?.Report(new(i+1,selected.Count,map.Name,"Sikeresen befejezve"));
                    status=status with {Phase=SteamQueuePhase.Prepared,
                        NextIndex=i+1,ActiveMap=null,ChangedUtc=DateTimeOffset.UtcNow,
                        Summary="Map maintenance verified: "+map.Name};
                    Write(status);
                }
                status=status with {Phase=SteamQueuePhase.Completed,
                    ActiveMap=null,ChangedUtc=DateTimeOffset.UtcNow,
                    Summary="All local jobs completed and verified"};
                Write(status);
                return status;
            }
            catch(Exception ex)
            {
                status=status with {Phase=SteamQueuePhase.NeedsOperator,
                    ChangedUtc=DateTimeOffset.UtcNow,
                    Summary="Queue halted; operator intervention required ("+
                        ex.GetType().Name+")"};
                Write(status);
                throw;
            }
        }
        finally{if(acquired)semaphore.Release();}
    }
}
