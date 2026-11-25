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
    Prepared, MapInProgress, Completed, NeedsOperator, AbandonedBetweenMaps
}
public sealed record SteamQueueSnapshot(Guid QueueId,SteamQueuePhase Phase,
    string HostId,int NextIndex,Guid[] MapIds,Guid? ActiveMap,
    DateTimeOffset ChangedUtc,string Summary);
public sealed record SteamQueueProgress(int Position,int Total,string MapName,string Stage)
{
    public SteamStoppedMapUpdater.Receipt? VerifiedReceipt {get;init;}
}

/// <summary>
/// A machine-local, cross-profile named semaphore and durable operation journal.
/// Each Windows Manager handles only its own registered maps. The named OS
/// semaphore is shared by local processes, NOT by the other physical computer.
/// No LAN connection, peer state or cluster-wide lease is required.
/// Never replay a dispatched update. A checkpoint without an active map
/// can be archived under the host lease. Uncertain mutations stay protected.
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
        _=UntamedOperationCoordinator.ResourceKey(folder);
        FileAttributes attributes;
        try{attributes=File.GetAttributes(Journal);}
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
        if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0||
            new FileInfo(Journal).Length is <=0 or >65536)
            throw new InvalidDataException("Steam queue journal is invalid");
        var status=JsonSerializer.Deserialize<SteamQueueSnapshot>(
            File.ReadAllText(Journal));
        if(status==null||status.QueueId==Guid.Empty||status.HostId!=hostId||
           !Enum.IsDefined(status.Phase)||status.MapIds==null||status.MapIds.Length is <1 or >25||
           status.MapIds.Any(id=>id==Guid.Empty)||status.MapIds.Distinct().Count()!=status.MapIds.Length||
           status.NextIndex<0||status.NextIndex>status.MapIds.Length||
           (status.ActiveMap.HasValue&&(status.NextIndex>=status.MapIds.Length||
                status.MapIds[status.NextIndex]!=status.ActiveMap.Value))||
           (status.Phase==SteamQueuePhase.MapInProgress&&!status.ActiveMap.HasValue)||
           (status.Phase is (SteamQueuePhase.Prepared or SteamQueuePhase.Completed or SteamQueuePhase.AbandonedBetweenMaps)&&status.ActiveMap.HasValue))
            throw new InvalidDataException("Steam queue journal identity mismatch");
        return status;
    }
    public SteamQueueSnapshot? Observe()=>Read();
    // Projection of persistent uncertainty, NOT a concurrency check.
    // Actual Steam work still needs the machine-wide lease.
    internal static bool BlocksNewWork(SteamQueueSnapshot? s)=>s!=null&&
        (s.Phase==SteamQueuePhase.MapInProgress||s.Phase==SteamQueuePhase.NeedsOperator&&s.ActiveMap.HasValue);

    private void ArchiveIdleCheckpoint(SteamQueueSnapshot previous)
    {
        if(previous.ActiveMap.HasValue||previous.Phase is not (SteamQueuePhase.Prepared or SteamQueuePhase.NeedsOperator))
            throw new InvalidOperationException("UM-STEAM-QUEUE-CHECKPOINT-ACTIVE");
        byte[] original=File.ReadAllBytes(Journal);
        string archive=Path.Combine(folder,"queue-"+previous.QueueId.ToString("N")+".abandoned.json");
        try
        {
            using var file=new FileStream(archive,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
            file.Write(original);file.Flush(true);
        }
        catch(IOException) when(File.Exists(archive))
        {
            if((File.GetAttributes(archive)&FileAttributes.ReparsePoint)!=0||
                new FileInfo(archive).Length!=original.Length||!File.ReadAllBytes(archive).SequenceEqual(original))throw;
        }
        Write(previous with{Phase=SteamQueuePhase.AbandonedBetweenMaps,ChangedUtc=DateTimeOffset.UtcNow,
            Summary="Previous queue closed between maps. No active map dispatched. No replay."});
    }
    internal bool TryCloseIdleCheckpoint()
    {
        try
        {
            using var lease=SteamHostLease.Acquire();
            var current=Read();
            if(current==null||current.Phase is not (SteamQueuePhase.Prepared or SteamQueuePhase.NeedsOperator)||
                current.ActiveMap.HasValue)return false;
            SteamStoppedMapUpdater.DemandNoConcurrentSteamCmd();
            ArchiveIdleCheckpoint(current);return true;
        }
        catch(IOException){return false;}
    }
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
        using var hostLease=SteamHostLease.Acquire();
        {
            var previous=Read();
            if(BlocksNewWork(previous))
                throw new IOException("UM-STEAM-QUEUE-ACTIVE-MAP-OUTCOME-UNKNOWN");
            if(previous?.Phase is SteamQueuePhase.Prepared or SteamQueuePhase.NeedsOperator)
            {
                SteamStoppedMapUpdater.DemandNoConcurrentSteamCmd();
                ArchiveIdleCheckpoint(previous);
            }
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
                    new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
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
                bool uncertain=status.ActiveMap.HasValue;
                status=status with {Phase=uncertain?SteamQueuePhase.NeedsOperator:SteamQueuePhase.AbandonedBetweenMaps,
                    ChangedUtc=DateTimeOffset.UtcNow,
                    Summary=(uncertain?"Active map outcome unknown (":"Queue stopped before next dispatch (")+
                        ex.GetType().Name+")"};
                Write(status);
                throw;
            }
        }

    }
}
