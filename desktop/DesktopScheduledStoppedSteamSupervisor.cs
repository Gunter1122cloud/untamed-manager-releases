using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit opt-in SteamCMD maintenance for independently confirmed STOPPED local
/// non-Store ASA installs. Public SteamCheck evidence is required before any
/// update. The existing Core updater owns the backup, queue, locks and SteamCMD
/// mutation; this supervisor only owns durable no-replay schedule claims.
/// Running servers are NEVER stopped/restarted from this scheduler.
/// </summary>
public sealed record ScheduledStoppedSteamUpdate(
    Guid MapId,int IntervalMinutes,string Fingerprint,bool Enabled,
    DateTimeOffset NextDueUtc,DateTimeOffset? LastAttemptUtc,string LastStatus,
    string? OldBuild,string? NewBuild);

public sealed class DesktopScheduledStoppedSteamSupervisor : IDisposable
{
    private sealed class State
    {
        public List<ScheduledStoppedSteamUpdate> Schedules {get;set;}=new();
    }

    private const int MaxJournalBytes=4_000_000;
    private const long Reserve=10L*1024*1024*1024;
    private readonly ClusterService cluster;
    private readonly string path,mutexName;
    private readonly Func<LocalMap,CancellationToken,Task<SteamStoppedMapUpdater.Receipt>> update;
    private readonly SemaphoreSlim tickGate=new(1,1);
    private readonly CancellationTokenSource closing=new();
    private Task? loop;
    private int started,disposed;
    private string runtimeState="not-started";
    public string RuntimeState=>Volatile.Read(ref runtimeState);

    public DesktopScheduledStoppedSteamSupervisor(ClusterService source,
        Func<LocalMap,CancellationToken,Task<SteamStoppedMapUpdater.Receipt>>? isolatedUpdater=null)
    {
        cluster=source??throw new ArgumentNullException(nameof(source));
        update=isolatedUpdater??((map,ct)=>SteamStoppedMapUpdater.UpdateAsync(
            cluster,map,cluster.Config.SteamCmdExecutable,Reserve,
            TimeSpan.FromHours(2),ct:ct));
        string root=cluster.Store.Root;
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-SCHEDULED-STEAM-LOCAL-ROOT");
        root=Path.GetFullPath(root);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-SCHEDULED-STEAM-FIXED-DISK");
        for(DirectoryInfo? node=new(root);node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-SCHEDULED-STEAM-ROOT-LINK");
        Directory.CreateDirectory(root);
        path=Path.Combine(root,"scheduled-stopped-steam-v1.json");
        mutexName="Local\\Untamed-StoppedSteam-"+
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                path.ToUpperInvariant())));
        Locked(()=>{_ = Read();return true;});
    }

    private T Locked<T>(Func<T> work)
    {
        using var mutex=new Mutex(false,mutexName);
        bool entered=false;
        try
        {
            try{entered=mutex.WaitOne(TimeSpan.FromSeconds(5));}
            catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("UM-SCHEDULED-STEAM-JOURNAL-LOCK");
            return work();
        }
        finally{if(entered)mutex.ReleaseMutex();}
    }

    private State Read()
    {
        if(!File.Exists(path))return new State();
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-SCHEDULED-STEAM-JOURNAL-LINK");
        var file=new FileInfo(path);
        if(file.Length is <2 or >MaxJournalBytes)
            throw new InvalidDataException("UM-SCHEDULED-STEAM-JOURNAL-SIZE");
        byte[] raw=File.ReadAllBytes(path);
        if(raw.Length!=file.Length)
            throw new InvalidDataException("UM-SCHEDULED-STEAM-JOURNAL-CHANGED");
        var data=JsonSerializer.Deserialize<State>(raw) ??
            throw new InvalidDataException("UM-SCHEDULED-STEAM-JOURNAL-BROKEN");
        if(data.Schedules==null||data.Schedules.Count>4096||
           data.Schedules.Any(s=>s==null||s.MapId==Guid.Empty||
               s.IntervalMinutes is <60 or >10080||
               s.Fingerprint==null||s.Fingerprint.Length!=64||
               !s.Fingerprint.All(Uri.IsHexDigit)||
               s.LastStatus==null||s.LastStatus.Length>96||
               (s.OldBuild!=null&&s.OldBuild.Length>20)||
               (s.NewBuild!=null&&s.NewBuild.Length>20))||
           data.Schedules.Select(s=>s.MapId).Distinct().Count()!=data.Schedules.Count)
            throw new InvalidDataException("UM-SCHEDULED-STEAM-JOURNAL-INVALID");
        return data;
    }

    private void Write(State data)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,
                FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(file,data);
                if(file.Length>MaxJournalBytes)
                    throw new InvalidDataException("UM-SCHEDULED-STEAM-JOURNAL-LIMIT");
                file.Flush(true);
            }
            if(File.Exists(path) &&
               (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-SCHEDULED-STEAM-JOURNAL-LINK");
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

    private LocalMap Owned(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(
        x=>x.Id==id)??throw new UnauthorizedAccessException(
            "UM-SCHEDULED-STEAM-MAP-NOT-LOCAL");

    private static string Fingerprint(LocalMap map,string steam)
    {
        string source=map.Id.ToString("D")+"\n"+
            Path.GetFullPath(map.InstallationPath).ToUpperInvariant()+"\n"+
            Path.GetFullPath(map.ExecutablePath).ToUpperInvariant()+"\n"+
            (map.Arguments??"")+"\n"+(map.ApiLoaderArguments??"")+"\n"+
            (map.FreshStoreClusterId??"")+"\n"+(map.HistoricalMapKey??"")+"\n"+
            steam.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private bool PendingQueue()
    {
        var queue=new SteamSerialMaintenanceQueue(cluster.Store.Root,
            cluster.Config.LocalHostId).Observe();
        return queue!=null&&queue.Phase is
            (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress or
             SteamQueuePhase.NeedsOperator);
    }

    private void DemandEligible(LocalMap map)
    {
        SteamStoppedMapUpdater.DemandSafeLocalInstallation(cluster,map);
        if(PendingQueue())throw new InvalidOperationException(
            "UM-SCHEDULED-STEAM-QUEUE-PENDING");
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        if(!AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidDataException("UM-SCHEDULED-STEAM-ASA-BINARY");
    }

    public ScheduledStoppedSteamUpdate[] Snapshot()=>Locked(()=>
        Read().Schedules.ToArray());

    public ScheduledStoppedSteamUpdate Configure(Guid mapId,int intervalMinutes,
        DateTimeOffset? now=null)
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        if(intervalMinutes is <60 or >10080)
            throw new InvalidDataException("UM-SCHEDULED-STEAM-INTERVAL");
        var map=Owned(mapId);
        DemandEligible(map);
        var fingerprint=Fingerprint(map,cluster.Config.SteamCmdExecutable);
        DateTimeOffset next=(now??DateTimeOffset.UtcNow).ToUniversalTime()
            .AddMinutes(intervalMinutes);
        return Locked(()=>{
            var state=Read();
            var item=new ScheduledStoppedSteamUpdate(mapId,intervalMinutes,
                fingerprint,true,next,null,"scheduled",null,null);
            int index=state.Schedules.FindIndex(x=>x.MapId==mapId);
            if(index>=0)state.Schedules[index]=item;
            else state.Schedules.Add(item);
            Write(state);return item;
        });
    }

    public ScheduledStoppedSteamUpdate Disable(Guid mapId)
    {
        _=Owned(mapId);
        return Locked(()=>{
            var state=Read();
            int index=state.Schedules.FindIndex(x=>x.MapId==mapId);
            if(index<0)throw new InvalidDataException("UM-SCHEDULED-STEAM-MISSING");
            var next=state.Schedules[index] with {
                Enabled=false,LastStatus="disabled-by-operator"
            };
            state.Schedules[index]=next;Write(state);return next;
        });
    }

    private bool Claimed(ScheduledStoppedSteamUpdate expected,DateTimeOffset at)
        =>Locked(()=>Read().Schedules.Any(s=>s.Enabled&&s.MapId==expected.MapId&&
            s.Fingerprint==expected.Fingerprint&&s.LastAttemptUtc==at&&
            s.LastStatus=="claimed-no-auto-retry"));

    public async Task<ScheduledStoppedSteamUpdate[]> TickOnceAsync(
        DateTimeOffset now,CancellationToken token=default)
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        if(!await tickGate.WaitAsync(0,token))return Snapshot();
        try
        {
            now=now.ToUniversalTime();
            var due=Locked(()=>Read().Schedules
                .Where(s=>s.Enabled&&s.NextDueUtc<=now&&
                    s.LastStatus!="claimed-no-auto-retry"&&
                    s.LastStatus!="needs-operator")
                .OrderBy(s=>s.NextDueUtc).ToArray());
            foreach(var observed in due)
            {
                token.ThrowIfCancellationRequested();
                bool missed=now-observed.NextDueUtc>TimeSpan.FromMinutes(5);
                bool claimed=Locked(()=>{
                    var state=Read();
                    int index=state.Schedules.FindIndex(s=>s.MapId==observed.MapId);
                    if(index<0)return false;
                    var item=state.Schedules[index];
                    if(!item.Enabled||item.NextDueUtc!=observed.NextDueUtc||
                        item.Fingerprint!=observed.Fingerprint||
                        item.LastStatus is "claimed-no-auto-retry" or "needs-operator")
                        return false;
                    state.Schedules[index]=item with {
                        NextDueUtc=now.AddMinutes(item.IntervalMinutes),
                        LastAttemptUtc=now,
                        LastStatus=missed?"missed-no-catchup":"claimed-no-auto-retry",
                        OldBuild=null,NewBuild=null
                    };
                    Write(state);return !missed;
                });
                if(!claimed)continue;
                string result="no-verified-new-build";
                string? oldBuild=null,newBuild=null;
                bool mutationEntered=false;
                try
                {
                    var map=Owned(observed.MapId);
                    if(Fingerprint(map,cluster.Config.SteamCmdExecutable)!=observed.Fingerprint)
                        throw new InvalidDataException("UM-SCHEDULED-STEAM-PROFILE-CHANGED");
                    DemandEligible(map);
                    if(!Claimed(observed,now))
                        throw new UnauthorizedAccessException("UM-SCHEDULED-STEAM-CLAIM-REVOKED");
                    var state=new SteamBuildStatusStore(cluster.Store.Root).Read(map);
                    var installed=AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath);
                    bool fresh=state.LastCheckedUtc.HasValue&&
                        state.LastCheckedUtc.Value<=DateTimeOffset.UtcNow.AddMinutes(1)&&
                        DateTimeOffset.UtcNow-state.LastCheckedUtc.Value<=TimeSpan.FromHours(2);
                    if(!fresh || state.Branch!="public" ||
                       state.State!=AsaSteamBuildState.UpdateAvailable||
                       string.IsNullOrWhiteSpace(installed)||
                       installed!=state.LocalBuild||
                       string.IsNullOrWhiteSpace(state.AvailableBuild)||
                       !state.AvailableBuild.All(char.IsAsciiDigit))
                    {
                        result="no-fresh-authoritative-update";
                    }
                    else
                    {
                        oldBuild=installed;
                        // Core rechecks ALL sibling processes, Store metadata,
                        // disk protection, backup and machine-wide update lock.
                        mutationEntered=true;
                        var receipt=await update(map,token);
                        if(receipt.MapId!=map.Id||receipt.OldBuild!=oldBuild||
                           !receipt.ProtectedFilesIntact||
                           receipt.NewBuild!=AsaSteamBuildCatalog.InstalledBuild(
                               map.InstallationPath)||
                           !Directory.Exists(receipt.BackupDirectory))
                            throw new InvalidDataException(
                                "UM-SCHEDULED-STEAM-RECEIPT-INVALID");
                        newBuild=receipt.NewBuild;
                        result="verified-stopped-install-updated";
                    }
                }
                catch(OperationCanceledException) when(!mutationEntered)
                {
                    result="cancelled-before-mutation";
                }
                catch(Exception)
                {
                    // Unknown failure after update dispatch requires operator
                    // review, never another autonomous attempt.
                    result="needs-operator";
                }
                Locked(()=>{
                    var state=Read();
                    int index=state.Schedules.FindIndex(s=>s.MapId==observed.MapId);
                    if(index>=0 && state.Schedules[index].LastAttemptUtc==now &&
                       state.Schedules[index].LastStatus=="claimed-no-auto-retry")
                    {
                        var item=state.Schedules[index];
                        state.Schedules[index]=item with {
                            Enabled=result!="needs-operator",
                            LastStatus=result,OldBuild=oldBuild,NewBuild=newBuild
                        };
                        Write(state);
                    }
                    return true;
                });
            }
            return Snapshot();
        }
        finally{tickGate.Release();}
    }

    public void Start()
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        if(Interlocked.Exchange(ref started,1)!=0)return;
        Volatile.Write(ref runtimeState,"active");
        loop=Task.Run(async()=>{
            using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                do
                {
                    try{await TickOnceAsync(DateTimeOffset.UtcNow,closing.Token);
                        Volatile.Write(ref runtimeState,"active");}
                    catch(Exception){Volatile.Write(ref runtimeState,"journal-or-update-error");}
                }while(await timer.WaitForNextTickAsync(closing.Token));
            }
            catch(OperationCanceledException){ }
            finally{Volatile.Write(ref runtimeState,"stopped");}
        });
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        closing.Cancel();
        // Do not kill SteamCMD or ASA. The Core/journal requires operator
        // recovery if the Manager exits during the actual update.
        _=loop;
    }
}
