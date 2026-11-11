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
/// Read-only compatibility view for retired automatic SteamCMD schedules.
/// No background timer, SteamCMD update callback, or automatic mutation path
/// exists. Old journal data is preserved for rollback/audit purposes.
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
     private readonly ClusterService cluster;
    private readonly string path,mutexName;
    private int disposed;
    private string runtimeState="not-started";
    public string RuntimeState=>Volatile.Read(ref runtimeState);

    public DesktopScheduledStoppedSteamSupervisor(ClusterService source,
        Func<LocalMap,CancellationToken,Task<SteamStoppedMapUpdater.Receipt>>? isolatedUpdater=null)
    {
        cluster=source??throw new ArgumentNullException(nameof(source));
        // The optional callback is accepted for compatibility with existing
        // isolated test callers, but is intentionally never retained/invoked.
        _=isolatedUpdater;
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

    public ScheduledStoppedSteamUpdate[] Snapshot()=>Locked(()=>
        Read().Schedules.Select(s=>s with {
            Enabled=false,LastStatus="manual-only-retired"
        }).ToArray());

    public ScheduledStoppedSteamUpdate Configure(Guid mapId,int intervalMinutes,
        DateTimeOffset? now=null)
    {
        // Explicit user policy: never create a new scheduled update.
        // Existing JSON records are left intact for recoverability, but
        // Snapshot/Tick/Start ignore their historical Enabled flag.
        throw new InvalidOperationException("UM-STEAM-SCHEDULE-MANUAL-ONLY");
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

    public Task<ScheduledStoppedSteamUpdate[]> TickOnceAsync(
        DateTimeOffset now,CancellationToken token=default)
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        token.ThrowIfCancellationRequested();
        // Historical due claims MUST NOT execute, even if a caller explicitly
        // invokes TickOnceAsync or a saved schedule still says Enabled=true.
        return Task.FromResult(Snapshot());
    }

    public void Start()
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        // Deliberately NO timer and NO scheduled SteamCMD work.
        // Only a separately approved manual native update may mutate ASA.
        Volatile.Write(ref runtimeState,"manual-only");
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        // No scheduled task or update process exists to cancel.
    }
}
