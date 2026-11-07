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
/// Explicitly opted-in, local stopped-world snapshots ONLY. This independent
/// journal is not the read-only inspector scheduler. A due slot is durably
/// claimed before any I/O, so an uncertain/crashed copy is never replayed.
/// No server stop/start, RCON, Store-v1 writer, retention or restore action.
/// </summary>
public sealed record ScheduledStoppedBackup(Guid MapId,int IntervalMinutes,
    string MapFingerprint,bool Enabled,DateTimeOffset NextDueUtc,
    DateTimeOffset? LastAttemptUtc,string LastStatus,string? LastPoint);

public sealed class DesktopScheduledStoppedBackupSupervisor : IDisposable
{
    private sealed class State
    {
        public List<ScheduledStoppedBackup> Schedules { get; set; }=new();
    }
    private const int MaximumJournalBytes=512_000;
    private readonly ClusterService cluster;
    private readonly Func<LocalMap,ProcessCheck> inspect;
    private readonly string path,mutexName;
    private readonly CancellationTokenSource cancel=new();
    private readonly SemaphoreSlim tickGate=new(1,1);
    private Task? loop;
    private int started,disposed;
    private string runtimeState="not-started";
    public string RuntimeState=>Volatile.Read(ref runtimeState);

    public DesktopScheduledStoppedBackupSupervisor(ClusterService source,
        Func<LocalMap,ProcessCheck>? processInspector=null)
    {
        cluster=source??throw new ArgumentNullException(nameof(source));
        inspect=processInspector??(map=>new WindowsProcessInspector().InspectFamily(map));
        string root=cluster.Store.Root;
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-SCHEDULED-BACKUP-LOCAL-ROOT-REQUIRED");
        root=Path.GetFullPath(root);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-SCHEDULED-BACKUP-FIXED-ROOT-REQUIRED");
        for(var d=new DirectoryInfo(root);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-SCHEDULED-BACKUP-LINKED-ROOT");
        Directory.CreateDirectory(root);
        path=Path.Combine(root,"scheduled-stopped-backups-v1.json");
        mutexName="Local\\Untamed-StoppedBackup-"+
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                path.ToUpperInvariant())));
        Locked(()=>{_ = Read();return true;});
    }

    private static string Fingerprint(LocalMap map)
    {
        // Reconfiguration cannot silently redirect an enabled task to a
        // different map/installation or turn it into a Store-v1 launch.
        string source=map.Id.ToString("D")+"\n"+
            Path.GetFullPath(map.InstallationPath).ToUpperInvariant()+"\n"+
            UntamedWorldBackup.MapKey(map)+"\n"+(map.Arguments??"")+"\n"+
            (map.ApiLoaderArguments??"");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private T Locked<T>(Func<T> operation)
    {
        using var mutex=new Mutex(false,mutexName);
        bool entered=false;
        try
        {
            try{entered=mutex.WaitOne(TimeSpan.FromSeconds(5));}
            catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("UM-SCHEDULED-BACKUP-JOURNAL-BUSY");
            return operation();
        }
        finally{if(entered)mutex.ReleaseMutex();}
    }

    private static byte[] ReadBounded(string file)
    {
        FileAttributes attributes;
        try{attributes=File.GetAttributes(file);}
        catch(FileNotFoundException){return Array.Empty<byte>();}
        if((attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-SCHEDULED-BACKUP-LINKED-JOURNAL");
        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,
            FileShare.Read,8192,FileOptions.SequentialScan);
        if(stream.Length is <2 or >MaximumJournalBytes)
            throw new InvalidDataException("UM-SCHEDULED-BACKUP-JOURNAL-SIZE");
        byte[] bytes=new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if(stream.ReadByte()!=-1)
            throw new InvalidDataException("UM-SCHEDULED-BACKUP-JOURNAL-CHANGED");
        return bytes;
    }

    private State Read()
    {
        byte[] bytes=ReadBounded(path);
        if(bytes.Length==0)return new State();
        var state=JsonSerializer.Deserialize<State>(bytes)??
            throw new InvalidDataException("UM-SCHEDULED-BACKUP-JOURNAL-INVALID");
        if(state.Schedules==null || state.Schedules.Count>50 ||
           state.Schedules.Any(s=>s==null||s.MapId==Guid.Empty||
               s.IntervalMinutes is <60 or >10080 ||
               s.MapFingerprint==null||s.MapFingerprint.Length!=64||
               !s.MapFingerprint.All(Uri.IsHexDigit)||
               s.LastStatus==null||s.LastStatus.Length>96||
               (s.LastPoint!=null&&!Guid.TryParse(s.LastPoint,out _)))||
           state.Schedules.Select(s=>s.MapId).Distinct().Count()!=state.Schedules.Count)
            throw new InvalidDataException("UM-SCHEDULED-BACKUP-JOURNAL-INVALID");
        return state;
    }

    private void Write(State state)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,
                FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(stream,state);
                if(stream.Length>MaximumJournalBytes)
                    throw new InvalidDataException("UM-SCHEDULED-BACKUP-JOURNAL-LIMIT");
                stream.Flush(true);
            }
            // File.Exists swallows ACL errors. Probe attributes instead;
            // absent file is the only condition that permits initial create.
            try
            {
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("UM-SCHEDULED-BACKUP-LINKED-JOURNAL");
            }
            catch(FileNotFoundException){ /* New local journal */ }
            File.Move(temp,path,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    private LocalMap Owned(Guid id)
    {
        if(id==Guid.Empty)throw new InvalidDataException("UM-SCHEDULED-BACKUP-MAP-MISSING");
        return cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id)??
            throw new UnauthorizedAccessException("UM-SCHEDULED-BACKUP-NOT-LOCAL");
    }

    public ScheduledStoppedBackup[] Snapshot()=>
        Locked(()=>Read().Schedules.ToArray());

    public ScheduledStoppedBackup Configure(Guid mapId,int intervalMinutes,
        DateTimeOffset? now=null)
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        if(intervalMinutes is <60 or >10080)
            throw new InvalidDataException("UM-SCHEDULED-BACKUP-INTERVAL");
        var map=Owned(mapId);
        if(UntamedWorldBackup.HasLiveOrHistoricalStore(map))
            throw new UnauthorizedAccessException("UM-SCHEDULED-BACKUP-STORE-V1-DENIED");
        AsaSaveLayoutResolver.DemandUniqueOwner(map,cluster.Config.LocalMaps);
        string fingerprint=Fingerprint(map);
        DateTimeOffset next=(now??DateTimeOffset.UtcNow).ToUniversalTime()
            .AddMinutes(intervalMinutes);
        return Locked(()=>{
            var data=Read();
            var entry=new ScheduledStoppedBackup(map.Id,intervalMinutes,
                fingerprint,true,next,null,"scheduled",null);
            int index=data.Schedules.FindIndex(s=>s.MapId==mapId);
            if(index>=0)data.Schedules[index]=entry;
            else
            {
                if(data.Schedules.Count>=50)
                    throw new InvalidDataException("UM-SCHEDULED-BACKUP-TOO-MANY");
                data.Schedules.Add(entry);
            }
            Write(data);
            return entry;
        });
    }

    public ScheduledStoppedBackup Disable(Guid mapId)
    {
        _=Owned(mapId);
        return Locked(()=>{
            var data=Read();
            int index=data.Schedules.FindIndex(s=>s.MapId==mapId);
            if(index<0)throw new InvalidDataException("UM-SCHEDULED-BACKUP-NOT-FOUND");
            var entry=data.Schedules[index] with {Enabled=false,LastStatus="disabled"};
            data.Schedules[index]=entry;Write(data);return entry;
        });
    }

    // Intended to run from a worker, never the WinForms UI thread.
    // Returns the post-attempt state for deterministic isolated Windows tests.
    public ScheduledStoppedBackup[] TickOnce(DateTimeOffset now)
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        if(!tickGate.Wait(0))return Snapshot();
        try
        {
            now=now.ToUniversalTime();
            var due=Locked(()=>Read().Schedules
                .Where(x=>x.Enabled&&x.NextDueUtc<=now)
                .OrderBy(x=>x.NextDueUtc).ToArray());
            foreach(var observed in due)
            {
                // Durable claim BEFORE expensive disk work; no catch-up.
                bool missed=now-observed.NextDueUtc>TimeSpan.FromMinutes(5);
                var claimed=Locked(()=>{
                    var data=Read();
                    int index=data.Schedules.FindIndex(x=>x.MapId==observed.MapId);
                    if(index<0)return false;
                    var current=data.Schedules[index];
                    if(!current.Enabled||current.NextDueUtc!=observed.NextDueUtc||
                       current.MapFingerprint!=observed.MapFingerprint)return false;
                    data.Schedules[index]=current with {
                        NextDueUtc=now.AddMinutes(current.IntervalMinutes),
                        LastAttemptUtc=now,
                        LastStatus=missed?"missed-no-catchup":"claimed-no-auto-retry",
                        LastPoint=null
                    };
                    Write(data);return !missed;
                });
                if(!claimed)continue;
                string result="failed-no-auto-retry";
                string? point=null;
                try
                {
                    var map=Owned(observed.MapId);
                    if(Fingerprint(map)!=observed.MapFingerprint ||
                       UntamedWorldBackup.HasLiveOrHistoricalStore(map))
                        result="config-or-store-blocked";
                    else if(map.Maintenance||
                            UntamedWorldBackup.InstallationRecoveryRequired(map))
                        result="maintenance-or-recovery-blocked";
                    else
                    {
                        AsaSaveLayoutResolver.DemandUniqueOwner(map,cluster.Config.LocalMaps);
                        var state=inspect(map);
                        if(state.Running||!state.ConfirmedStopped||
                           state.Identity!=null||state.LoaderIdentity!=null)
                            result="not-confirmed-stopped";
                        else
                        {
                            // Core re-checks stop state before and after copy.
                            // It only writes a new verified local archive.
                            var saved=UntamedWorldBackup.Create(map,cluster.Store.Root,
                                ()=>inspect(map));
                            point=saved.Id;
                            result="verified-backup-created";
                        }
                    }
                }
                catch(Exception) { result="failed-no-auto-retry"; }
                Locked(()=>{
                    var data=Read();
                    int index=data.Schedules.FindIndex(x=>x.MapId==observed.MapId);
                    if(index>=0&&data.Schedules[index].LastAttemptUtc==now)
                    {
                        data.Schedules[index]=data.Schedules[index] with {
                            LastStatus=result,LastPoint=point
                        };
                        Write(data);
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
                    try{TickOnce(DateTimeOffset.UtcNow);
                        Volatile.Write(ref runtimeState,"active");}
                    catch(Exception){Volatile.Write(ref runtimeState,"journal-or-backup-error");}
                }while(await timer.WaitForNextTickAsync(cancel.Token));
            }
            catch(OperationCanceledException){ /* Manager shut down */ }
            finally{Volatile.Write(ref runtimeState,"stopped");}
        });
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        cancel.Cancel();
        // Never kill an ASA server or delete an in-progress staged archive.
        // A crash leaves an uncommitted package, never a replayable job.
        _=loop;
    }
}
