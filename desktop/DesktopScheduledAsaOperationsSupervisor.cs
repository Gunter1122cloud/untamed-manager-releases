using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

// Only locally owned, explicitly opted-in ASA installations, including pinned Store-v1. The native
// runner calls the existing PID-bound RCON and Restart bridges, never a second
// implementation of SaveWorld / DoExit / Start. The journal claims a slot
// BEFORE any RCON bytes; crashes, missed slots and failed jobs never replay.
public sealed record ScheduledAsaOperation(
    Guid MapId,string Kind,string Frequency,int IntervalMinutes,
    int MinuteOfDay,int DaysMask,string Fingerprint,bool Enabled,
    DateTimeOffset NextDueUtc,DateTimeOffset? LastClaimedUtc,
    string LastStatus,string LastEvidence,bool PostRestartWipe=false,int WipeDelaySeconds=0,
    string TimeZoneId="Europe/Budapest",int[]? DayMinutes=null,DateTimeOffset? OnceAtUtc=null,Guid Revision=default);

// Bounded, persisted execution evidence. A slot is recorded BEFORE dispatch;
// after a crash an unfinished claim remains visible and is never auto-replayed.
public sealed record ScheduledAsaExecution(
    Guid RunId,Guid MapId,string Kind,DateTimeOffset ScheduledUtc,
    DateTimeOffset ClaimedUtc,DateTimeOffset? FinishedUtc,string Status,string Evidence,
    bool TerminalPublicationPending=false);

public sealed class DesktopScheduledAsaOperationsSupervisor : IDisposable
{
    private sealed record ScheduleReviewApproval(Guid Token,Guid MapId,
        string Kind,Guid RunId,string JournalSha256,DateTimeOffset CreatedUtc,
        bool CanKeepFutureRestart);
    private readonly Dictionary<Guid,ScheduleReviewApproval> reviewTokens=new();
    private readonly TimeSpan minimumReviewAge;
    private static bool UnknownSlot(string status)=>status is
        "claimed-no-auto-retry" or "interrupted-needs-operator" or
        "failed-needs-operator-no-retry";
    private sealed class OwnedSlot : IDisposable
    {
        private readonly Semaphore gate;
        public OwnedSlot(Semaphore held){gate=held;}
        public void Dispose(){try{gate.Release();}finally{gate.Dispose();}}
    }
    private OwnedSlot AcquireOwnedSlot(Guid mapId,string kind)
    {
        // Held across runner execution AND the terminal claim write.
        // A second Manager cannot review a live/paused dispatch even if
        // the prior claim has exceeded its ordinary timeout.
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            folder.ToUpperInvariant()+"|"+mapId.ToString("N")+"|"+kind)));
        var gate=new Semaphore(1,1,"Global\\Untamed-AsaScheduleSlot-"+key);
        if(!gate.WaitOne(0))
        {
            gate.Dispose();
            throw new IOException("UM-ASA-SCHEDULE-SLOT-OTHER-WORKER");
        }
        return new OwnedSlot(gate);
    }
    private sealed class State
    {
        public State() { }
        public List<ScheduledAsaOperation> Jobs {get;set;}=new();
        public List<ScheduledAsaExecution> History {get;set;}=new();
    }
    private const int MaxHistory=4096;
    private const int MaxJournalBytes=4_000_000;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ClusterService cluster;
    private readonly DiscordOperationEvents eventStream;
    internal Func<Guid,bool>? AutomaticRecoveryPending {get;set;}
    private readonly DesktopRconReadOnlyBridge rcon;
    private readonly DesktopRconAdminBridge freshWipeAdmin;
    private readonly DesktopRconAdminBridge countdownAdmin;
    private readonly bool nativeCountdown;
    private readonly DesktopFreshAsaRestartBridge restart;
    private readonly DesktopHistoricalGracefulStopBridge? historicalStop;
    private readonly DesktopHistoricalRestartBridge? historicalRestart;
    private readonly DesktopHistoricalRconAdminBridge? historicalAdmin;
    private readonly Func<Guid,string,CancellationToken,Task<string>> runner;
    private readonly Func<LocalMap,bool> safeMap;
    private readonly string folder,path,mutexName;
    private readonly SemaphoreSlim tickGate=new(1,1);
    private readonly SemaphoreSlim dispatchLane=new(1,1);
    // Accessed only under tickGate. One active worker per registered map,
    // across every schedule kind and every retry delay.
    private readonly Dictionary<Guid,Task> activeMapWorkers=new();
    private readonly DiscordMapReservation discordScope=new();
    internal bool TryReserveDiscord(Guid[] ids)=>Volatile.Read(ref updateFrozen)==0&&discordScope.TryReserve(ids);
    internal void ReleaseDiscord()=>discordScope.Release();
    private int updateFrozen;
    // Host calls only under the native updater dispatch gate. No active
    // scheduled copy or lifecycle may be interrupted to update the Manager.
    internal bool TryFreezeForManagerUpdate()
    {
        if(!tickGate.Wait(0))return false;
        if(discordScope.HasReservation||activeMapWorkers.Values.Any(task=>!task.IsCompleted))
        {tickGate.Release();return false;}
        Volatile.Write(ref updateFrozen,1);
        return true;
    }
    internal void ReleaseManagerUpdateFreeze()
    {
        if(Interlocked.Exchange(ref updateFrozen,0)==1)tickGate.Release();
    }
    private readonly CancellationTokenSource cancel=new();
    private Task? loop;
    private int started,disposed;
    private string runtimeState="not-started";
    public string RuntimeState=>Volatile.Read(ref runtimeState);

    public DesktopScheduledAsaOperationsSupervisor(ClusterService cluster,
        DesktopRconReadOnlyBridge rcon,DesktopFreshAsaRestartBridge restart,
        Func<Guid,string,CancellationToken,Task<string>>? isolatedRunner=null,
        Func<LocalMap,bool>? isolatedPreflight=null,
        TimeSpan? isolatedMinimumReviewAge=null,
        DesktopHistoricalGracefulStopBridge? historicalStop=null,
        DesktopHistoricalRestartBridge? historicalRestart=null,
        DesktopHistoricalRconAdminBridge? historicalAdmin=null)
    {
        minimumReviewAge=isolatedMinimumReviewAge??TimeSpan.FromMinutes(5);
        this.cluster=cluster??throw new ArgumentNullException(nameof(cluster));
        eventStream=new DiscordOperationEvents(cluster);
        this.rcon=rcon??throw new ArgumentNullException(nameof(rcon));
        freshWipeAdmin=new DesktopRconAdminBridge(cluster);
        countdownAdmin=new DesktopRconAdminBridge(cluster);
        nativeCountdown=isolatedRunner==null&&isolatedPreflight==null;
        this.restart=restart??throw new ArgumentNullException(nameof(restart));
        this.historicalStop=historicalStop;
        this.historicalRestart=historicalRestart;
        this.historicalAdmin=historicalAdmin;
        var root=cluster.Store.Root;
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-ASA-SCHEDULE-LOCAL-PROFILE");
        folder=Path.GetFullPath(root);
        if(new DriveInfo(Path.GetPathRoot(folder)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-ASA-SCHEDULE-FIXED-DISK");
        path=Path.Combine(folder,"native-asa-schedules-v1.json");
        CheckLinks();
        var key=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..28];
        mutexName=@"Global\Untamed-NativeAsaSchedules-"+key;
        runner=isolatedRunner??RunNativeAsync;
        // The production host never supplies either isolated fixture hook.
        safeMap=isolatedPreflight??(map=>
            FreshAsaInstallationSafety.IsProvenFresh(cluster,map) &&
            !map.Maintenance && !KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map) &&
            !KovaCompatibleWorldBackup.InstallationRecoveryRequired(map) &&
            AsaReadyProbe.IsRegisteredServerBinary(map) &&
            map.RconHost=="127.0.0.1" && map.RconPort is >=1024 and <=65535 &&
            !string.IsNullOrWhiteSpace(map.RconPasswordProtected) &&
            AsaRconConfigurationEvidence.Matches(map));
    }

    private bool HistoricalEligible(LocalMap map)
    {
        // Existing manual bridge revalidates source, PID, RCON, lease and
        // journals on every dispatch. No peer travel test as a local gate.
        if(historicalStop==null||historicalRestart==null||historicalAdmin==null||
           !map.HistoricalStoreProtected||map.ObservedReadOnlyImported||map.Maintenance||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           KovaCompatibleWorldBackup.InstallationRecoveryRequired(map)||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||
           map.RconHost!="127.0.0.1"||map.RconPort is <1024 or >65535||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected))return false;
        try{return LegacyStoreV1PinnedSource.HasApprovedLocalSource(cluster.Store.Root,map);}
        catch(Exception){return false;}
    }
    private bool Eligible(LocalMap map)=>map.HistoricalStoreProtected?
        HistoricalEligible(map):
        !KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map)&&
        string.IsNullOrWhiteSpace(map.StoreTestRoot)&&safeMap(map);
    private void CheckLinks()
    {
        for(DirectoryInfo? node=new(folder);node!=null;node=node.Parent)
            if(node.Exists && (node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-ASA-SCHEDULE-LINKED-PATH");
        if(File.Exists(path) &&
           (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ASA-SCHEDULE-LINKED-JOURNAL");
    }
    private T Locked<T>(Func<T> work)
    {
        using var sem=new Semaphore(1,1,mutexName);
        if(!sem.WaitOne(TimeSpan.FromSeconds(2)))
            throw new IOException("UM-ASA-SCHEDULE-JOURNAL-BUSY");
        try {CheckLinks();return work();}
        finally {sem.Release();}
    }
    private State Read()
    {
        if(!File.Exists(path))return new State();
        var fi=new FileInfo(path);
        if(fi.Length is <2 or >MaxJournalBytes ||
           (fi.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ASA-SCHEDULE-JOURNAL-SIZE");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,
            4096,FileOptions.SequentialScan);
        var state=JsonSerializer.Deserialize<State>(stream,Json) ??
            throw new InvalidDataException("UM-ASA-SCHEDULE-JOURNAL-INVALID");
        if(state.Jobs==null||state.Jobs.Count>10000||
           state.Jobs.Any(j=>j==null||j.MapId==Guid.Empty||
                (j.Kind!="SaveWorld"&&j.Kind!="Restart"&&j.Kind!="WildWipe")||
                (j.Frequency!="Interval"&&j.Frequency!="Daily"&&j.Frequency!="Once")||
                (j.PostRestartWipe&&j.Kind!="Restart")||
                j.WipeDelaySeconds is <0 or >600||(!j.PostRestartWipe&&j.WipeDelaySeconds!=0)||
                (j.Frequency=="Once"&&j.OnceAtUtc==null&&j.IntervalMinutes is <1 or >2)||
                (j.OnceAtUtc!=null&&j.Frequency!="Once")||
                !ValidCalendar(j.TimeZoneId,j.DayMinutes,j.Frequency)||
                j.DaysMask is <1 or >127 || j.MinuteOfDay is <0 or >1439 ||
                j.Fingerprint.Length!=64 || j.LastStatus.Length>90)||
           state.Jobs.GroupBy(j=>(j.MapId,j.Kind)).Any(group=>group.Count()>1))
            throw new InvalidDataException("UM-ASA-SCHEDULE-JOURNAL-INVALID");
        if(state.History==null||state.History.Count>MaxHistory||
           state.History.Any(e=>e==null||e.RunId==Guid.Empty||e.MapId==Guid.Empty||
               (e.Kind!="SaveWorld"&&e.Kind!="Restart"&&e.Kind!="WildWipe")||
               string.IsNullOrWhiteSpace(e.Status)||e.Status.Length>90||
               e.Evidence==null||e.Evidence.Length>240)||
           state.History.GroupBy(e=>e.RunId).Any(g=>g.Count()>1))
            throw new InvalidDataException("UM-ASA-SCHEDULE-HISTORY-INVALID");
        return state;
    }
    // Read-only retained history for explicit historical backfill. A missing
    // cursor is not silently reinterpreted as a new baseline. No phases
    // or event timestamps are reconstructed from the terminal evidence.
    internal object LegacyOperationHistory(Guid? after,int limit)
    {
        if(limit is <1 or >100)throw new InvalidDataException("UM-DC-EVENTS-LIMIT");
        return Locked(()=>{
            var history=Read().History;
            int offset=after.HasValue?history.FindIndex(x=>x.RunId==after.Value)+1:0;
            bool invalid=after.HasValue&&offset==0;
            var rows=invalid?Array.Empty<ScheduledAsaExecution>():history.Skip(offset).Take(limit).ToArray();
            string SafeCode(string text)=>System.Text.RegularExpressions.Regex.IsMatch(text??"",@"\A[A-Za-z0-9_-]{1,240}\z")?text:"EVIDENCE_NOT_EXPORTABLE";
            return new {version=1,hostId=cluster.Config.LocalHostId,origin="scheduler",historical=true,
                state=invalid?"cursorInvalid":"ok",gap=invalid,retentionMayBeTruncated=history.Count==MaxHistory,
                nextRunId=invalid?(Guid?)null:rows.LastOrDefault()?.RunId??after,
                more=!invalid&&offset+rows.Length<history.Count,
                runs=rows.Select(x=>new {x.RunId,x.MapId,x.Kind,x.ScheduledUtc,x.ClaimedUtc,x.FinishedUtc,
                    currentMapName=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==x.MapId)?.Name,
                    status=SafeCode(x.Status),evidence=SafeCode(x.Evidence)})};
        });
    }

    private void AddHistory(State state,ScheduledAsaExecution entry)
    {
        // Durable publication obligation is committed with the native claim,
        // before the first operation dispatch. Old v1 records default false.
        state.History.Add(entry with{TerminalPublicationPending=true});
        if(state.History.Count>MaxHistory)
        {
            // Unresolved claims are authorization blockers, not disposable
            // display history. Never evict them to make room for newer rows.
            int index=state.History.FindIndex(x=>!UnknownSlot(x.Status));
            if(index<0||index==state.History.Count-1)
                throw new InvalidDataException("UM-ASA-SCHEDULE-HISTORY-REVIEW-REQUIRED");
            if(state.History[index].TerminalPublicationPending)eventStream.MarkGap();
            state.History.RemoveAt(index);
        }
    }
    private void PublishTerminal(ScheduledAsaExecution entry,bool recovered)
    {
        if(!entry.TerminalPublicationPending||!entry.FinishedUtc.HasValue)return;
        if(!eventStream.Record(entry.RunId,new[]{entry.MapId},entry.Kind,"scheduler",null,
            "terminal",entry.Status,entry.Evidence,entry.FinishedUtc.Value,entry.ScheduledUtc,
            historical:recovered))return;
        // A separate acknowledgement can fail; retrying publication is safe
        // by immutable run identity. Never retry the underlying ASA action.
        try
        {
            Locked(()=>{
                var state=Read();
                int i=state.History.FindIndex(x=>x.RunId==entry.RunId);
                if(i>=0&&state.History[i]==entry)
                {
                    state.History[i]=entry with{TerminalPublicationPending=false};
                    Write(state);
                }
                return true;
            });
        }
        catch{/* Retain the pending bit for next reconciliation. */}
    }
    internal void ReconcileEventTerminals(int limit=32)
    {
        if(limit is <1 or >100)throw new ArgumentOutOfRangeException(nameof(limit));
        // No bridge calls, no command re-dispatch, no synthetic phase timestamps.
        var pending=Locked(()=>Read().History.Where(x=>x.TerminalPublicationPending&&
            x.FinishedUtc.HasValue).Take(limit).ToArray());
        foreach(var entry in pending)PublishTerminal(entry,true);
    }
    private void Write(State state)
    {
        CheckLinks();
        Directory.CreateDirectory(folder);
        var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,
                FileShare.None,4096,FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream,state,Json);
                if(stream.Length>MaxJournalBytes)
                    throw new InvalidDataException("UM-ASA-SCHEDULE-JOURNAL-LIMIT");
                stream.Flush(true);
            }
            File.Move(tmp,path,true);
        }
        finally {if(File.Exists(tmp))File.Delete(tmp);}
    }
    private LocalMap Owned(Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==id) ??
        throw new UnauthorizedAccessException("UM-ASA-SCHEDULE-NOT-LOCAL");
    private static string Fingerprint(LocalMap map)
    {
        // Includes configuration identity but NEVER stores a DPAPI credential.
        var material=string.Join("\n",map.Id,map.InstallationPath,
            map.ExecutablePath,map.Arguments,map.ApiLoaderArguments,
            map.StartWithAsaApi,map.GamePort,map.QueryPort,map.RconPort,
            map.RconHost,map.RconPasswordProtected,map.Maintenance,
            map.HistoricalStoreProtected);
        if(map.HistoricalStoreProtected)material+=string.Join("\n","",map.HistoricalArchiveSha256,map.HistoricalMapKey,
            map.HistoricalRuntimeClusterId,map.HistoricalRuntimeTransferDirectory,map.ApiLoaderPath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
    private static TimeZoneInfo Zone(string id)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>100||id.Any(char.IsControl))
            throw new InvalidDataException("UM-ASA-SCHEDULE-TIMEZONE");
        try{return TimeZoneInfo.FindSystemTimeZoneById(id);}
        catch(TimeZoneNotFoundException) when(id=="Europe/Budapest")
        {return TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");}
    }
    private static bool ValidCalendar(string zone,int[]? minutes,string frequency)
    {
        if(minutes!=null&&(frequency!="Daily"||minutes.Length!=7||minutes.Any(x=>x is <0 or >1439)))
            return false;
        try{_=Zone(zone);return true;}catch{return false;}
    }
    // Explicit DST policy: spring gap advances to the first valid local
    // minute; autumn overlap uses the later UTC instant exactly once.
    private static DateTimeOffset WallToUtc(DateTime wall,TimeZoneInfo zone)
    {
        wall=DateTime.SpecifyKind(wall,DateTimeKind.Unspecified);
        while(zone.IsInvalidTime(wall))wall=wall.AddMinutes(1);
        var shift=zone.IsAmbiguousTime(wall)?zone.GetAmbiguousTimeOffsets(wall).Min():zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall,shift).ToUniversalTime();
    }
    private static int DayMinute(ScheduledAsaOperation item,int index)=>item.DayMinutes?[index]??item.MinuteOfDay;
    internal static DateTimeOffset Next(ScheduledAsaOperation item,DateTimeOffset now)
    {
        now=now.ToUniversalTime();
        if(item.Frequency=="Once")return item.NextDueUtc==default?
            item.OnceAtUtc??now.AddMinutes(item.IntervalMinutes):DateTimeOffset.MaxValue;
        if(item.Frequency=="Interval")
        {
            if(item.IntervalMinutes is <15 or >10080)
                throw new InvalidDataException("UM-ASA-SCHEDULE-INTERVAL");
            // Preserve the original cadence, even across UI/process restart.
            var cursor=item.NextDueUtc==default?
                now.AddMinutes(item.IntervalMinutes):item.NextDueUtc;
            if(cursor<=now)
            {
                var steps=(long)Math.Floor((now-cursor).TotalMinutes/item.IntervalMinutes)+1;
                cursor=cursor.AddMinutes(steps*item.IntervalMinutes);
            }
            return cursor;
        }
        var zone=Zone(item.TimeZoneId);
        var local=TimeZoneInfo.ConvertTime(now,zone).Date;
        for(int offset=0;offset<9;offset++)
        {
            var day=local.AddDays(offset);
            if((item.DaysMask&(1<<(int)day.DayOfWeek))==0)continue;
            var candidate=WallToUtc(day.AddMinutes(DayMinute(item,(int)day.DayOfWeek)),zone);
            if(candidate>now)return candidate;
        }
        throw new InvalidDataException("UM-ASA-SCHEDULE-NEXT-UNAVAILABLE");
    }
    public ScheduledAsaOperation[] Snapshot()=>
        Locked(()=>Read().Jobs.OrderBy(x=>x.MapId).ThenBy(x=>x.Kind).ToArray());

    // Never expose other maps' history via a map-specific WebView request.
    public ScheduledAsaExecution[] History(Guid mapId)
    {
        Owned(mapId);
        return Locked(()=>Read().History.Where(x=>x.MapId==mapId)
            .OrderByDescending(x=>UnknownSlot(x.Status)).ThenByDescending(x=>x.ClaimedUtc)
            .Take(100).ToArray());
    }

    // Explicit opt-in may preserve ONLY an already enabled recurring
    // restart, after all installation journals are closed. No inference
    // that a past scheduled command succeeded; no dispatch from this path.
    private bool CanKeepFutureRestart(State state,Guid mapId,string kind,Guid runId)
    {
        if(kind!="Restart"||RuntimeState is "journal-write-needs-operator" or
            "journal-recovery-needs-operator"||
            state.History.Any(x=>x.MapId==mapId&&x.Kind==kind&&
                x.RunId!=runId&&UnknownSlot(x.Status)))return false;
        var job=state.Jobs.SingleOrDefault(x=>x.MapId==mapId&&x.Kind==kind);
        if(job==null||!job.Enabled||job.Frequency!="Daily")return false;
        try
        {
            var map=Owned(mapId);
            if(!Eligible(map)||Fingerprint(map)!=job.Fingerprint||
                AutomaticRecoveryPending?.Invoke(mapId)==true)return false;
            var local=AsaLocalJournalReadOnly.Inspect(cluster.Operations,map.InstallationPath);
            return !local.AnyUnreadable&&
                (local.Operation==null||local.Operation.Phase is
                    UntamedOperationPhase.Completed or UntamedOperationPhase.CancelledBeforeMutation or
                    UntamedOperationPhase.OperatorReviewed)&&
                (local.Restart==null||(local.Restart.MapId==mapId&&local.Restart.Phase is
                    (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed)));
        }
        catch(Exception){return false;}
    }

    // The old slot is orchestration history. Only an owned schedule lane
    // with clear native installation journals may retire an abandoned slot.
    // Preserve the exact old bytes; never replay an old occurrence.
    private DateTimeOffset RetireUnknownSlots(State state,LocalMap map,string kind,string evidence)
    {
        var old=state.History.Where(x=>x.MapId==map.Id&&x.Kind==kind&&UnknownSlot(x.Status)).ToArray();
        var now=DateTimeOffset.UtcNow;
        if(old.Length==0)return now;
        if(AutomaticRecoveryPending?.Invoke(map.Id)==true||
           cluster.Operations.IsInstallationBusy(map.InstallationPath))
            throw new InvalidOperationException("UM-ASA-SCHEDULE-NATIVE-OPERATION-BUSY");
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        new AsaManualStopJournal(map.InstallationPath).DemandNoPending(map.Id);
        if(cluster.Operations.Observe(map.InstallationPath)?.Phase is
            (UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)||
           (map.HistoricalStoreProtected&&LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id)?.Phase is ("prepared" or "needsOperator")))
            throw new InvalidOperationException("UM-ASA-SCHEDULE-NATIVE-OPERATION-PENDING");
        byte[] original=File.ReadAllBytes(path);
        string digest=Convert.ToHexString(SHA256.HashData(original));
        if(original.Length is <2 or >MaxJournalBytes)throw new InvalidDataException("UM-ASA-SCHEDULE-REVIEW-AUDIT-SIZE");
        foreach(var entry in old)
        {
            string audit=path+"."+entry.RunId.ToString("N")+".auto-retired-"+digest+".json";
            if(File.Exists(audit))
            {
                if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
                    !File.ReadAllBytes(audit).SequenceEqual(original))
                    throw new InvalidDataException("UM-ASA-SCHEDULE-REVIEW-AUDIT-MISMATCH");
            }
            else
            {
                string temp=audit+"."+Guid.NewGuid().ToString("N")+".tmp";
                try
                {
                    using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
                    {stream.Write(original);stream.Flush(true);}
                    File.Move(temp,audit);
                }
                finally{if(File.Exists(temp))File.Delete(temp);}
            }
        }
        // Finish ALL archives before modifying the in-memory state; a
        // failed archive must not partially retire an unknown batch.
        foreach(var entry in old)
        {
            int index=state.History.FindIndex(x=>x.RunId==entry.RunId);
            state.History[index]=entry with{
                Status="auto-retired-unknown-no-replay",
                FinishedUtc=entry.FinishedUtc??now,
                Evidence=evidence
            };
        }
        return old.SelectMany(x=>new[]{x.ScheduledUtc,x.ClaimedUtc}).Append(now).Max();
    }

    public ScheduledAsaOperation Configure(Guid mapId,string kind,string frequency,
        int intervalMinutes,int minuteOfDay,int daysMask,bool postRestartWipe=false,int wipeDelaySeconds=0,
        string timeZoneId="Europe/Budapest",int[]? dayMinutes=null,string? onceAtLocal=null)
    {
        if(mapId==Guid.Empty || kind is not ("SaveWorld" or "Restart" or "WildWipe") ||
           frequency is not ("Interval" or "Daily" or "Once") ||
           (kind=="Restart"&&frequency=="Interval") ||
           (frequency=="Once"&&onceAtLocal==null&&intervalMinutes is <1 or >2) ||
           (onceAtLocal!=null&&frequency!="Once")||!ValidCalendar(timeZoneId,dayMinutes,frequency)||
           (postRestartWipe&&kind!="Restart") ||
           wipeDelaySeconds is <0 or >600||(!postRestartWipe&&wipeDelaySeconds!=0)||
           minuteOfDay is <0 or >1439 || daysMask is <1 or >127 ||
           (frequency=="Interval"&&intervalMinutes is <15 or >10080))
            throw new InvalidDataException("UM-ASA-SCHEDULE-FIELDS");
        DateTimeOffset? onceAtUtc=null;
        if(onceAtLocal!=null)
        {
            if(!DateTime.TryParseExact(onceAtLocal,"yyyy-MM-dd'T'HH:mm",CultureInfo.InvariantCulture,
                DateTimeStyles.None,out var wall))
                throw new InvalidDataException("UM-ASA-SCHEDULE-ONCE-DATE");
            onceAtUtc=WallToUtc(wall,Zone(timeZoneId));
            var now=DateTimeOffset.UtcNow;
            if(onceAtUtc<=now||onceAtUtc>now.AddYears(1))
                throw new InvalidDataException("UM-ASA-SCHEDULE-ONCE-RANGE");
        }
        var map=Owned(mapId);
        if(!Eligible(map))
            throw new UnauthorizedAccessException("UM-ASA-SCHEDULE-PREFLIGHT-DENIED");
        bool retire=Locked(()=>Read().History.Any(x=>x.MapId==map.Id&&x.Kind==kind&&UnknownSlot(x.Status)));
        // Lock order matches actual execution: slot first, journal second.
        using var oldSlot=retire?AcquireOwnedSlot(map.Id,kind):null;
        return Locked(()=>{
            var state=Read();
            var fingerprint=Fingerprint(map);
            var sample=new ScheduledAsaOperation(map.Id,kind,frequency,intervalMinutes,
                minuteOfDay,daysMask,fingerprint,true,default,null,"scheduled","",postRestartWipe,wipeDelaySeconds,
                timeZoneId,dayMinutes?.ToArray(),onceAtUtc,Guid.NewGuid());
            DateTimeOffset cutoff=DateTimeOffset.UtcNow;
            if(state.History.Any(x=>x.MapId==map.Id&&x.Kind==kind&&UnknownSlot(x.Status)))
            {
                if(oldSlot==null)throw new InvalidOperationException("UM-ASA-SCHEDULE-SLOT-OTHER-WORKER");
                cutoff=RetireUnknownSlots(state,map,kind,"NEW_SCHEDULE_INTENT_OLD_SLOT_OUTCOME_UNKNOWN");
            }
            var task=sample with {NextDueUtc=Next(sample,cutoff)};
            int ix=state.Jobs.FindIndex(j=>j.MapId==map.Id&&j.Kind==kind);
            if(ix<0)state.Jobs.Add(task);else state.Jobs[ix]=task;
            Write(state);
            return task;
        });
    }
    public ScheduledAsaOperation Disable(Guid mapId,string kind)
    {
        if(kind is not ("SaveWorld" or "Restart" or "WildWipe"))
            throw new InvalidDataException("UM-ASA-SCHEDULE-KIND");
        Owned(mapId);
        return Locked(()=>{
            var state=Read();
            int ix=state.Jobs.FindIndex(j=>j.MapId==mapId&&j.Kind==kind);
            if(ix<0)throw new InvalidDataException("UM-ASA-SCHEDULE-NOT-FOUND");
            var item=state.Jobs[ix];
            var changed=item with {Enabled=false,LastStatus=item.LastStatus=="claimed-no-auto-retry"?item.LastStatus:"disabled"};
            state.Jobs[ix]=changed;Write(state);return changed;
        });
    }
    public string[] RestartConflicts(Guid mapId)
    {
        Owned(mapId);
        return Locked(()=>{
            var jobs=Read().Jobs;
            var self=jobs.FirstOrDefault(x=>x.MapId==mapId&&x.Kind=="Restart"&&x.Frequency=="Daily"&&x.Enabled);
            if(self==null)return Array.Empty<string>();
            return jobs.Where(x=>x.MapId!=mapId&&x.Kind=="Restart"&&x.Frequency=="Daily"&&x.Enabled&&
                    (x.NextDueUtc==self.NextDueUtc || (x.TimeZoneId==self.TimeZoneId&&
                    Enumerable.Range(0,7).Any(d=>(x.DaysMask&self.DaysMask&(1<<d))!=0&&DayMinute(x,d)==DayMinute(self,d)))))
                .Select(x=>cluster.Config.LocalMaps.FirstOrDefault(m=>m.Id==x.MapId)?.Name)
                .Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x!)
                .Distinct().ToArray();
        });
    }

    private static object Data(ScheduledAsaOperation job)=>new {
        mapId=job.MapId,kind=job.Kind,frequency=job.Frequency,
        intervalMinutes=job.IntervalMinutes,minuteOfDay=job.MinuteOfDay,
        daysMask=job.DaysMask,enabled=job.Enabled,nextDueUtc=job.NextDueUtc,postRestartWipe=job.PostRestartWipe,wipeDelaySeconds=job.WipeDelaySeconds,
        timeZoneId=job.TimeZoneId,dayMinutes=job.DayMinutes,onceAtUtc=job.OnceAtUtc,revision=job.Revision,
        lastClaimedUtc=job.LastClaimedUtc,lastStatus=job.LastStatus,
        lastEvidence=job.LastEvidence,
        evidence="DURABLE_NATIVE_LOCAL_SCHEDULE_NO_AUTO_REPLAY"
    };
    private static object HistoryData(ScheduledAsaExecution entry)=>new {
        runId=entry.RunId,mapId=entry.MapId,kind=entry.Kind,
        scheduledUtc=entry.ScheduledUtc,claimedUtc=entry.ClaimedUtc,
        finishedUtc=entry.FinishedUtc,status=entry.Status,evidence=entry.Evidence
    };
    private string JournalHash()
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.Read,4096,FileOptions.SequentialScan);
        if(stream.Length is <2 or >MaxJournalBytes)
            throw new InvalidDataException("UM-ASA-SCHEDULE-JOURNAL-SIZE");
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)return Task.FromResult(Fail(null,"UM-ASA-SCHEDULE-MESSAGE"));
            using var doc=JsonDocument.Parse(raw);
            var args=doc.RootElement;
            if(args.ValueKind!=JsonValueKind.Object||
               !args.TryGetProperty("id",out var requestId)||
               requestId.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(requestId.GetString(),out var parsed)||
               parsed==Guid.Empty||
               !args.TryGetProperty("method",out var methodRaw)||
               methodRaw.ValueKind!=JsonValueKind.String)
                return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-MESSAGE"));
            id=parsed.ToString("D");
            var method=methodRaw.GetString();
            if(method=="schedule.nativeOverview")
            {
                // Single read of the persisted local journal, no remote map
                // discovery or mutation; expired/unowned jobs are filtered.
                var localMaps=cluster.Config.LocalMaps.ToArray();
                var ownedIds=localMaps.Select(map=>map.Id).ToHashSet();
                return Task.FromResult(Ok(id,new {
                    hostName=cluster.Config.LocalHostName,runnerState=RuntimeState,
                    jobs=Snapshot().Where(job=>ownedIds.Contains(job.MapId))
                        .OrderBy(job=>job.NextDueUtc).Select(Data).ToArray(),
                    remoteExecutionAllowed=false,
                    evidence="NATIVE_LOCAL_SCHEDULE_SUMMARY_READ_ONLY"
                }));
            }
            if(!args.TryGetProperty("mapId",out var rawMap)||
               rawMap.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(rawMap.GetString(),out var mapId)||mapId==Guid.Empty)
                return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-MESSAGE"));
            Owned(mapId);
            if(method=="schedule.nativeStatus")
            {
                var history=History(mapId);
                bool unknown=history.Any(x=>UnknownSlot(x.Status));
                return Task.FromResult(Ok(id,new {
                    mapId,runnerState=RuntimeState,
                    jobs=Snapshot().Where(x=>x.MapId==mapId).Select(Data).ToArray(),
                    history=history.Select(HistoryData).ToArray(),
                    unknownRunIds=history.Where(x=>UnknownSlot(x.Status))
                        .Select(x=>x.RunId).ToArray(),
                    blockedScope=RuntimeState is "journal-write-needs-operator" or
                        "journal-recovery-needs-operator"?"supervisor":
                        unknown?"task":"none",
                    supportedNextAction=unknown?"schedule.nativeReviewPreview":
                        RuntimeState is "journal-write-needs-operator" or
                            "journal-recovery-needs-operator"?
                            "schedule.nativeResume":"NEW_EXPLICIT_CONFIGURATION",
                    restartConflicts=RestartConflicts(mapId),
                    protectedStore=KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(Owned(mapId)),
                    historicalSchedulingSupported=HistoricalEligible(Owned(mapId)),
                    canConfigure=Eligible(Owned(mapId)),
                    autoUpdateAvailable=false,
                    remoteExecutionAllowed=false
                }));
            }
            if(method=="schedule.nativeResume")
            {
                // Operator explicitly re-arms the LOCAL supervisor only
                // after the old unknown claims were individually archived.
                // This is NOT recovery of any old slot, SaveWorld or Restart.
                if(!args.TryGetProperty("confirm",out var consent)||
                   consent.ValueKind!=JsonValueKind.True)
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-RESUME-APPROVAL"));
                if(RuntimeState is not ("journal-write-needs-operator" or
                    "journal-recovery-needs-operator"))
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-RESUME-NOT-BLOCKED"));
                if(tickGate.CurrentCount==0||
                   loop is {IsCompleted:false})
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-RESUME-WORKER-STILL-ACTIVE"));
                bool pending=Locked(()=>Read().History.Any(x=>UnknownSlot(x.Status)));
                if(pending)
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-RESUME-PENDING-OLD-CLAIM"));
                // Safe starting point is after missed slots have been
                // advanced, with no catch-up or implicit task re-enable.
                Volatile.Write(ref runtimeState,"stopped");
                Interlocked.Exchange(ref started,0);
                Start();
                return Task.FromResult(Ok(id,new{
                    supervisorResumed=RuntimeState=="active",
                    oldSlotReplayed=false,oldTaskReenabled=false,
                    nextTaskRequiresExplicitOptIn=true,
                    evidence="LOCAL_SCHEDULE_SUPERVISOR_REARM_NO_OLD_DISPATCH"
                }));
            }
            if(method is "schedule.nativeReviewPreview" or
                "schedule.nativeReviewApply")
            {
                // Only old, actually persisted unknown runs can be reviewed.
                // Review never calls runner(), restarter, RCON or TickOnce.
                if(tickGate.CurrentCount==0)
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-REVIEW-WORKER-BUSY"));
                if(method=="schedule.nativeReviewPreview")
                {
                    if(!args.TryGetProperty("runId",out var runArg)||
                       !Guid.TryParse(runArg.GetString(),out var runId)||
                       !args.TryGetProperty("kind",out var kindArg)||
                       kindArg.ValueKind!=JsonValueKind.String)
                        return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-REVIEW-IDENTITY"));
                    string reviewKind=kindArg.GetString()??"";
                    var candidate=Locked(()=>Read().History.SingleOrDefault(x=>
                        x.RunId==runId&&x.MapId==mapId&&x.Kind==reviewKind));
                    if(candidate==null||!UnknownSlot(candidate.Status))
                        return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-REVIEW-NOT-PENDING"));
                    if(minimumReviewAge>TimeSpan.Zero&&
                       DateTimeOffset.UtcNow-candidate.ClaimedUtc<minimumReviewAge)
                        return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-REVIEW-TOO-RECENT"));
                    // An old persisted claim might still be inside another
                    // Manager's long-running RCON worker. This is NOT a
                    // timeout unlock: the OS semaphore is mandatory.
                    using var slot=AcquireOwnedSlot(mapId,reviewKind);
                    var previewToken=Guid.NewGuid();
                    var snapshot=Locked(()=>{
                        var state=Read();
                        if(!state.History.Any(x=>x==candidate))
                            throw new InvalidOperationException("UM-ASA-SCHEDULE-REVIEW-RUN-CHANGED");
                        return (Hash:JournalHash(),Keep:CanKeepFutureRestart(state,mapId,reviewKind,runId));
                    });
                    reviewTokens[mapId]=new(previewToken,mapId,reviewKind,runId,
                        snapshot.Hash,DateTimeOffset.UtcNow,snapshot.Keep);
                    return Task.FromResult(Ok(id,new{
                        token=previewToken,mapId,kind=reviewKind,runId,
                        previousStatus=candidate.Status,
                        outcomeStillUnknown=true,automaticRetry=false,
                        scheduledTaskWillBeDisabled=true,
                        canKeepFutureRestart=snapshot.Keep,
                        journalBytesWillBeAudited=true,
                        originalRunWillNotExecute=true,
                        evidence="SCHEDULE_DURABLE_UNCERTAIN_SLOT_REVIEW_PREVIEW"
                    }));
                }
                if(!args.TryGetProperty("token",out var tokenArg)||
                   tokenArg.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(tokenArg.GetString(),out var token)||
                   !args.TryGetProperty("confirm",out var approved)||
                   approved.ValueKind!=JsonValueKind.True)
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-REVIEW-APPROVAL"));
                if(!reviewTokens.TryGetValue(mapId,out var permit)||
                   permit.Token!=token)
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-REVIEW-TOKEN"));
                reviewTokens.Remove(mapId); // tokens cannot be replayed
                if(DateTimeOffset.UtcNow-permit.CreatedUtc>TimeSpan.FromMinutes(5))
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-REVIEW-EXPIRED"));
                bool keepFuture=args.TryGetProperty("keepFutureRestart",out var keep)&&
                    keep.ValueKind==JsonValueKind.True;
                if(keepFuture&&!permit.CanKeepFutureRestart)
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-FUTURE-RESTART-NOT-VERIFIED"));
                using var reviewSlot=AcquireOwnedSlot(mapId,permit.Kind);
                var finalized=Locked(()=>{
                    if(JournalHash()!=permit.JournalSha256)
                        throw new InvalidOperationException(
                            "UM-ASA-SCHEDULE-REVIEW-JOURNAL-CHANGED");
                    var state=Read();
                    int index=state.History.FindIndex(x=>
                        x.RunId==permit.RunId&&x.MapId==permit.MapId&&
                        x.Kind==permit.Kind&&UnknownSlot(x.Status));
                    if(index<0)
                        throw new InvalidOperationException(
                            "UM-ASA-SCHEDULE-REVIEW-RUN-CHANGED");
                    if(minimumReviewAge>TimeSpan.Zero&&DateTimeOffset.UtcNow-
                        state.History[index].ClaimedUtc<minimumReviewAge)
                        throw new InvalidOperationException(
                            "UM-ASA-SCHEDULE-REVIEW-TOO-RECENT");
                    if(keepFuture&&!CanKeepFutureRestart(state,permit.MapId,permit.Kind,permit.RunId))
                        throw new InvalidOperationException("UM-ASA-SCHEDULE-FUTURE-RESTART-NOT-VERIFIED");
                    // Preserve a SHA-correlated BYTE-EXACT copy of original
                    // durable receipt BEFORE changing its status. Never
                    // delete or truncate this old journal or invent success.
                    string audit=path+"."+permit.RunId.ToString("N")+
                        ".operator-reviewed.json";
                    byte[] original=File.ReadAllBytes(path);
                    if(original.Length is <2 or >MaxJournalBytes)
                        throw new InvalidDataException("UM-ASA-SCHEDULE-REVIEW-AUDIT-SIZE");
                    if(File.Exists(audit))
                    {
                        if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
                           !CryptographicOperations.FixedTimeEquals(
                               SHA256.HashData(File.ReadAllBytes(audit)),
                               SHA256.HashData(original)))
                            throw new InvalidDataException(
                                "UM-ASA-SCHEDULE-REVIEW-AUDIT-MISMATCH");
                    }
                    else
                    {
                        using var stream=new FileStream(audit,FileMode.CreateNew,
                            FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
                        stream.Write(original);stream.Flush(true);
                    }
                    var previous=state.History[index];
                    state.History[index]=previous with {
                        Status="operator-reviewed-unknown-no-replay",
                        FinishedUtc=DateTimeOffset.UtcNow,
                        Evidence="OPERATOR_REVIEW_NO_COMMAND_REPLAY"
                    };
                    int ix=state.Jobs.FindIndex(x=>x.MapId==permit.MapId&&
                        x.Kind==permit.Kind);
                    DateTimeOffset? nextDueUtc=null;
                    if(ix>=0)
                    {
                        var existing=state.Jobs[ix];
                        if(keepFuture)
                        {
                            var cutoff=DateTimeOffset.UtcNow;
                            if(previous.ClaimedUtc>cutoff)cutoff=previous.ClaimedUtc;
                            if(previous.ScheduledUtc>cutoff)cutoff=previous.ScheduledUtc;
                            // Never move the cursor backwards, even if the wall
                            // clock changed or several later slots were skipped.
                            nextDueUtc=existing.NextDueUtc>cutoff?
                                existing.NextDueUtc:Next(existing,cutoff);
                            state.Jobs[ix]=existing with{
                                NextDueUtc=nextDueUtc.Value,Revision=Guid.NewGuid(),
                                LastStatus="operator-reviewed-unknown-no-replay",
                                LastEvidence="OLD_SLOT_AUDITED_FUTURE_RESTART_ONLY"
                            };
                        }
                        else state.Jobs[ix]=existing with{
                            Enabled=false,LastStatus="operator-reviewed-unknown-no-replay",
                            LastEvidence="OLD_SLOT_UNKNOWN_NEEDS_NEW_OPT_IN"
                        };
                    }
                    Write(state);
                    return new {mapId=permit.MapId,kind=permit.Kind,runId=permit.RunId,
                        reviewed=true,outcomeStillUnknown=true,jobDisabled=!keepFuture,
                        futureRestartRetained=keepFuture,nextDueUtc,
                        noRconSent=true,noAutomaticReplay=true,oldJournalArchived=true,
                        evidence=keepFuture?"SCHEDULE_UNKNOWN_SLOT_AUDITED_FUTURE_RESTART_ONLY":
                            "SCHEDULE_UNKNOWN_SLOT_AUDITED_AND_DISABLED"};
                });
                return Task.FromResult(Ok(id,finalized));
            }
            if(method is not ("schedule.nativeSet" or "schedule.nativeDisable"))
                return Task.FromResult(Fail(id,"UM-METHOD-DENIED"));
            if(!args.TryGetProperty("confirm",out var confirm)||
               confirm.ValueKind!=JsonValueKind.True)
                return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-APPROVAL"));
            if(!args.TryGetProperty("kind",out var rawKind)||
               rawKind.ValueKind!=JsonValueKind.String)
                return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-KIND"));
            var kind=rawKind.GetString()!;
            if(method=="schedule.nativeDisable")
            {
                var changed=Disable(mapId,kind);
                return Task.FromResult(Ok(id,Data(changed)));
            }
            if(!args.TryGetProperty("frequency",out var rawFreq)||
               rawFreq.ValueKind!=JsonValueKind.String||
               !args.TryGetProperty("intervalMinutes",out var rawInt)||
               !rawInt.TryGetInt32(out var interval)||
               !args.TryGetProperty("minuteOfDay",out var rawMinute)||
               !rawMinute.TryGetInt32(out var minute)||
               !args.TryGetProperty("daysMask",out var rawDays)||
               !rawDays.TryGetInt32(out var days))
                return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-FIELDS"));
            bool wipe=false;
            if(args.TryGetProperty("postRestartWipe",out var wipeField))
            {
                if(wipeField.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-FIELDS"));
                wipe=wipeField.GetBoolean();
            }
            int delay=0;
            if(args.TryGetProperty("wipeDelaySeconds",out var delayField)&&
                (delayField.ValueKind!=JsonValueKind.Number||!delayField.TryGetInt32(out delay)))
                return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-FIELDS"));
            string zone=args.TryGetProperty("timeZoneId",out var zoneField)?
                zoneField.GetString()??throw new InvalidDataException("UM-ASA-SCHEDULE-TIMEZONE"):"Europe/Budapest";
            int[]? perDay=args.TryGetProperty("dayMinutes",out var daily)&&daily.ValueKind!=JsonValueKind.Null?
                daily.EnumerateArray().Select(x=>x.GetInt32()).ToArray():null;
            string? once=args.TryGetProperty("onceAtLocal",out var onceField)&&onceField.ValueKind!=JsonValueKind.Null?
                onceField.GetString()??throw new InvalidDataException("UM-ASA-SCHEDULE-ONCE-DATE"):null;
            var result=Configure(mapId,kind,rawFreq.GetString()!,interval,minute,days,wipe,delay,zone,perDay,once);
            return Task.FromResult(Ok(id,new {
                job=Data(result),restartConflicts=RestartConflicts(mapId)
            }));
        }
        catch(Exception) {return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-NATIVE-DENIED"));}
    }

    private static JsonElement Decode(string raw)
    {
        using var doc=JsonDocument.Parse(raw);
        var result=doc.RootElement;
        if(!result.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(result.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.String?
                NativeLifecycleFailureCode.From(new InvalidOperationException(error.GetString())):"UM-ASA-SCHEDULE-ENGINE-REJECTED");
        return result.GetProperty("data").Clone();
    }
    private static string Message(string method,object fields)
    {
        var payload=new Dictionary<string,object?> {
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        foreach(var p in JsonSerializer.SerializeToElement(fields,Json).EnumerateObject())
            payload[p.Name]=p.Value.Clone();
        return JsonSerializer.Serialize(payload,Json);
    }
    // Only native code invokes these existing bridges. No raw commands,
    // passwords, server paths or peer writes enter this scheduler's RPC.
    private void ScheduleEvent(Guid runId,ScheduledAsaOperation job,string phase,string result,string evidence,Guid? nativeJob=null)=>
        eventStream.Record(runId,new[]{job.MapId},job.Kind,"scheduler",null,phase,result,evidence,DateTimeOffset.UtcNow,job.NextDueUtc,nativeJob);

    private static string RestartFailureCode(JsonElement status,string fallback)
    {
        if(status.TryGetProperty("failureCode",out var field)&&
           field.ValueKind==JsonValueKind.String)
        {
            string? code=field.GetString();
            if(code!=null&&System.Text.RegularExpressions.Regex.IsMatch(code,
                @"\AUM-[A-Z0-9_-]{3,110}\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))return code;
        }
        return fallback;
    }

    // Bind the whole countdown to the old process epoch and the durable
    // scheduler claim. Use independent approval slots so chat previews
    // cannot replace a pending manual RCON approval.
    private async Task<ProcessIdentity> WarnBeforeRestartAsync(ScheduledAsaOperation job,
        Func<bool> stillApproved,CancellationToken ct)
    {
        var map=Owned(job.MapId);
        ProcessIdentity Observe()
        {
            var current=Owned(job.MapId);
            if(current.HistoricalStoreProtected)
                return LegacyStoreV1GracefulStop.Preview(cluster,job.MapId).Process;
            var state=cluster.Check(current);
            if(!state.Running||state.ConfirmedStopped||state.Identity is not {Pid:>0} identity||
               !RconPortOwnership.IsOwnedListener(current.RconPort,identity.Pid))
                throw new InvalidOperationException("UM-SHUTDOWN-COUNTDOWN-OWNER");
            return identity;
        }
        var first=Observe();
        using var historic=map.HistoricalStoreProtected?new DesktopHistoricalRconAdminBridge(cluster):null;
        bool StillSame()
        {
            if(!stillApproved()||AutomaticRecoveryPending?.Invoke(job.MapId)==true)return false;
            try
            {
                var now=Observe();
                return now.Pid==first.Pid&&now.StartedUtc==first.StartedUtc&&
                    string.Equals(now.ExecutablePath,first.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&
                    now.Arguments==first.Arguments;
            }
            catch{return false;}
        }
        async Task Send(string message,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string prefix=historic==null?"rcon":"historical";
            Func<string,Task<string>> handle=historic==null?
                raw=>countdownAdmin.HandleScheduledChatAsync(raw,first.Pid,first.StartedUtc):
                raw=>historic.HandleScheduledChatAsync(raw,first.Pid,first.StartedUtc);
            var preview=Decode(await handle(Message(prefix+".adminPreview",
                new{mapId=job.MapId,action="serverChat",message})));
            if(preview.GetProperty("mapId").GetGuid()!=job.MapId||
               preview.GetProperty("command").GetString()!="ServerChat "+message||!StillSame())
                throw new InvalidOperationException("UM-SHUTDOWN-COUNTDOWN-PREVIEW");
            token.ThrowIfCancellationRequested();
            var sent=Decode(await handle(Message(prefix+".adminSend",new{
                token=preview.GetProperty("token").GetGuid(),confirm=true,typedApproval="STORE-V1 ADMIN"})));
            if(sent.GetProperty("mapId").GetGuid()!=job.MapId||
               sent.GetProperty("command").GetString()!="ServerChat "+message||
               !sent.GetProperty("commandSent").GetBoolean()||!sent.GetProperty("oneShot").GetBoolean())
                throw new InvalidOperationException("UM-SHUTDOWN-COUNTDOWN-SEND");
        }
        await AsaShutdownCountdown.RunAsync(false,StillSame,Send,ct);
        return first;
    }

    private async Task<string> RunHistoricalAsync(ScheduledAsaOperation job,Guid runId,
        ScheduledAsaDispatchLane lane,CancellationToken ct,ProcessIdentity? warned=null)
    {
        var map=Owned(job.MapId);
        if(!HistoricalEligible(map)||Fingerprint(map)!=job.Fingerprint)
            throw new InvalidOperationException("UM-ASA-SCHEDULE-HISTORICAL-CHANGED");
        if(job.Kind=="WildWipe")
        {
            var proof=LegacyStoreV1GracefulStop.Preview(cluster,job.MapId);
            if(LegacyStoreV1GracefulStop.SaveOnlyStartupBlocker(proof)!=null)
                throw new InvalidOperationException("UM-SCHEDULE-WIPE-NOT-READY");
            var pid=proof.Process.Pid;
            var started=proof.Process.StartedUtc;
            return await DispatchStandaloneWipeAsync(job.MapId,job.Fingerprint,
                raw=>historicalAdmin!.HandleScheduledWipeAsync(raw,pid,started),
                "historical","STORE-V1 DINÓTÖRLÉS",ct);
        }
        bool save=job.Kind=="SaveWorld";
        Func<string,Task<string>> handle=save?historicalStop!.HandleAsync:historicalRestart!.HandleAsync;
        string prefix=save?"historical.saveWorld":"historical.restart";
        var pre=Decode(await handle(Message(prefix+"Preview",new{mapId=job.MapId})));
        if(pre.GetProperty("mapId").GetGuid()!=job.MapId||
           !pre.GetProperty("usesSaveWorld").GetBoolean()||
           pre.GetProperty("usesDoExit").GetBoolean()==save)
            throw new InvalidDataException("UM-SCHEDULE-HISTORICAL-PREVIEW");
        int oldPid=pre.GetProperty("pid").GetInt32();
        if(warned!=null&&(oldPid!=warned.Pid||pre.GetProperty("currentStartedUtc").GetDateTimeOffset()!=warned.StartedUtc))
            throw new InvalidOperationException("UM-SHUTDOWN-COUNTDOWN-PROCESS-CHANGED");
        ct.ThrowIfCancellationRequested();
        bool RetryStillApproved()=>Locked(()=>Read().Jobs.Any(x=>x.MapId==job.MapId&&
            x.Kind==job.Kind&&x.Enabled&&x.Revision==job.Revision&&
            x.Fingerprint==job.Fingerprint&&x.LastStatus=="claimed-no-auto-retry"))&&
            HistoricalEligible(Owned(job.MapId))&&Fingerprint(Owned(job.MapId))==job.Fingerprint;
        using var startupRetry=save?null:ScheduledRestartRetryScope.Enter(
            new ScheduledRestartRetryPermit(job.MapId,runId,RetryStillApproved,ct,lane.BackoffAsync));
        var begun=Decode(await handle(Message(prefix+"Apply",new{token=pre.GetProperty("token").GetGuid()})));
        if(!begun.GetProperty("started").GetBoolean()||
           begun.GetProperty("automaticRetry").GetBoolean()||
           begun.GetProperty("forceKill").GetBoolean()||
           begun.GetProperty("storeTouched").GetBoolean())
            throw new InvalidDataException("UM-SCHEDULE-HISTORICAL-APPLY");
        Guid id=begun.GetProperty("jobId").GetGuid();
        ScheduleEvent(runId,job,"native-dispatched","in-progress","NATIVE_JOB_ACCEPTED",id);
        // Four bounded startup attempts plus the 1/3/5-minute backoffs.
        var limit=DateTimeOffset.UtcNow.AddMinutes(save?16:95);
        bool savePublished=false,stopPublished=false,newPidPublished=false;
        while(DateTimeOffset.UtcNow<limit)
        {
            await Task.Delay(TimeSpan.FromSeconds(2),ct);
            var result=Decode(await handle(Message(prefix+"Status",new{jobId=id})));
            if(result.GetProperty("jobId").GetGuid()!=id||result.GetProperty("mapId").GetGuid()!=job.MapId)
                throw new InvalidDataException("UM-SCHEDULE-HISTORICAL-JOB-MISMATCH");
            // These fields are native structured evidence, not parsed stage
            // prose or a reconstructed terminal summary. Publish at the
            // first poll where each proof exists; time = observation time.
            bool Has(string name)=>result.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
            bool observedLog=Has("preExitSaveLogAndWorldObserved"),observedMeta=Has("preExitWorldMetadataObserved");
            if(!savePublished&&observedLog!=observedMeta)
            {
                ScheduleEvent(runId,job,"save-result",observedLog?"verified-local":"limited",
                    observedLog?"LOCAL_SAVE_LOG_WORLD":"LOCAL_WORLD_METADATA_LOG_UNVERIFIED",id);
                savePublished=true;
            }
            if(!save&&!stopPublished&&Has("oldProcessStopped")&&result.TryGetProperty("oldPid",out var previous)&&previous.TryGetInt32(out int previousPid)&&previousPid==oldPid)
            {ScheduleEvent(runId,job,"stop-result","verified-local","OLD_PROCESS_STOPPED",id);stopPublished=true;}
            if(!save&&stopPublished&&!newPidPublished&&result.TryGetProperty("newPid",out var process)&&process.TryGetInt32(out int freshPid)&&freshPid>0&&freshPid!=oldPid&&
               result.TryGetProperty("newStartedUtc",out var startedField)&&startedField.ValueKind==JsonValueKind.String&&startedField.TryGetDateTimeOffset(out _))
            {ScheduleEvent(runId,job,"new-process","verified-local","NEW_PID_OBSERVED",id);newPidPublished=true;}
            string? state=result.GetProperty("state").GetString();
            if(state is "needsOperator" or "notDispatched" or "reviewed")
                throw new InvalidOperationException(RestartFailureCode(result,
                    "UM-SCHEDULE-HISTORICAL-NEEDS-REVIEW"));
            if(state!="completed")continue;
            if(!save&&(!result.TryGetProperty("readyCertified",out var startupReady)||
               startupReady.ValueKind!=JsonValueKind.True))
                throw new InvalidOperationException(
                    "UM-SCHEDULE-HISTORICAL-RESTART-READINESS-NOT-CERTIFIED");
            bool log=result.GetProperty("preExitSaveLogAndWorldObserved").GetBoolean();
            bool meta=result.GetProperty("preExitWorldMetadataObserved").GetBoolean();
            if(log==meta)throw new InvalidDataException("UM-SCHEDULE-HISTORICAL-SAVE-EVIDENCE");
            string evidence=log?"LOCAL_SAVE_LOG_WORLD":"LOCAL_WORLD_METADATA_LOG_UNVERIFIED";
            if(save)
            {
                if(result.GetProperty("kind").GetString()!="SaveWorldOnly"||
                   result.GetProperty("confirmedStopped").GetBoolean())
                    throw new InvalidDataException("UM-SCHEDULE-HISTORICAL-SAVE-RECEIPT");
                return evidence;
            }
            int pid=result.GetProperty("newPid").GetInt32();
            if(result.GetProperty("mapId").GetGuid()!=job.MapId||
               !result.GetProperty("oldProcessStopped").GetBoolean()||
               result.GetProperty("oldPid").GetInt32()!=oldPid||pid<=0||pid==oldPid)
                throw new InvalidDataException("UM-SCHEDULE-HISTORICAL-RESTART-RECEIPT");
            ScheduleEvent(runId,job,"readiness","verified-local",
                "NEW_ASA_API_PLUGIN_JOIN_RCON_READY",id);
            if(!job.PostRestartWipe)return evidence+"_RESTART_VERIFIED_READY";
            DateTimeOffset started=result.GetProperty("newStartedUtc").GetDateTimeOffset();
            return await RunPostReadyWipeAsync(job,runId,id,pid,started,
                evidence+"_RESTART_VERIFIED_READY",true,ct);
        }
        throw new InvalidOperationException("UM-SCHEDULE-HISTORICAL-TIMEOUT");
    }
    // Reconnect/probe, settle, send once. Wipe failure is separate
    // from an already verified successful restart.
    private async Task<string> RunPostReadyWipeAsync(ScheduledAsaOperation job,Guid runId,
        Guid nativeId,int pid,DateTimeOffset started,string restartEvidence,bool historical,CancellationToken ct)
    {
        bool Enabled()=>Locked(()=>Read().Jobs.Any(x=>x.MapId==job.MapId&&
            x.Kind==job.Kind&&x.Enabled&&x.PostRestartWipe&&x.WipeDelaySeconds==job.WipeDelaySeconds&&
            x.Revision==job.Revision&&x.Fingerprint==job.Fingerprint&&x.LastStatus=="claimed-no-auto-retry"));
        LocalMap Verify()
        {
            var map=Owned(job.MapId);
            if(!Enabled()||!Eligible(map)||Fingerprint(map)!=job.Fingerprint)
                throw new InvalidOperationException("UM-SCHEDULE-WIPE-DISABLED-OR-CHANGED");
            if(historical)
            {
                var proof=LegacyStoreV1GracefulStop.Preview(cluster,job.MapId);
                if(proof.Process.Pid!=pid||proof.Process.StartedUtc!=started||
                    LegacyStoreV1GracefulStop.SaveOnlyStartupBlocker(proof)!=null)
                    throw new InvalidOperationException("UM-SCHEDULE-WIPE-PID-OR-READY-CHANGED");
            }
            else
            {
                var state=cluster.Check(map);
                if(!state.Running||state.ConfirmedStopped||state.Identity is not { } child||
                    child.Pid!=pid||child.StartedUtc!=started||!RconPortOwnership.IsOwnedListener(map.RconPort,pid)||
                    !AsaRuntimeLogEvidence.Inspect(map,child,state.LoaderIdentity).JoinAdvertised)
                    throw new InvalidOperationException("UM-SCHEDULE-WIPE-PID-OR-READY-CHANGED");
            }
            return map;
        }
        try
        {
            if(!Enabled())return restartEvidence+"_WIPE_DISABLED";
            var map=Verify();
            var limit=DateTimeOffset.UtcNow.AddSeconds(60);
            bool ready=false;
            do
            {
                ct.ThrowIfCancellationRequested();map=Verify();
                try
                {
                    _=await RconProtocol.Execute("127.0.0.1",map.RconPort,
                        SecureStore.Unprotect(map.RconPasswordProtected),"ListPlayers",ct);
                    Verify();ready=true;break;
                }
                catch(Exception ex) when(ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
                {ct.ThrowIfCancellationRequested();}
                await Task.Delay(TimeSpan.FromSeconds(1),ct);
            }while(DateTimeOffset.UtcNow<limit);
            if(!ready)throw new InvalidOperationException("UM-SCHEDULE-WIPE-RCON-NOT-READY");
            var settle=DateTimeOffset.UtcNow.AddSeconds(Math.Max(20,job.WipeDelaySeconds));
            while(DateTimeOffset.UtcNow<settle)
            {
                ct.ThrowIfCancellationRequested();
                if(!Enabled())return restartEvidence+"_WIPE_DISABLED";
                Verify();await Task.Delay(TimeSpan.FromSeconds(1),ct);
            }
            ct.ThrowIfCancellationRequested();Verify();
            string prefix=historical?"historical":"rcon";
            Func<string,Task<string>> handle=historical?
                raw=>historicalAdmin!.HandleScheduledWipeAsync(raw,pid,started):
                raw=>freshWipeAdmin.HandleScheduledWipeAsync(raw,pid,started);
            var wipe=Decode(await handle(Message(prefix+".adminPreview",new{mapId=job.MapId,action="wildWipe",message=""})));
            if(wipe.GetProperty("mapId").GetGuid()!=job.MapId||
                wipe.GetProperty("command").GetString()!="DestroyWildDinos")
                throw new InvalidDataException("UM-SCHEDULE-WIPE-PREVIEW");
            ct.ThrowIfCancellationRequested();Verify();
            ScheduleEvent(runId,job,"wipe-dispatch","in-progress","WIPE_INTENT_NO_EFFECT_PROOF",nativeId);
            var sent=Decode(await handle(Message(prefix+".adminSend",new{
                token=wipe.GetProperty("token").GetGuid(),confirm=true,
                typedApproval=historical?"STORE-V1 DINÓTÖRLÉS":"DINÓTÖRLÉS"})));
            if(sent.GetProperty("mapId").GetGuid()!=job.MapId||
                sent.GetProperty("command").GetString()!="DestroyWildDinos"||
                !sent.GetProperty("commandSent").GetBoolean()||!sent.GetProperty("oneShot").GetBoolean())
                throw new InvalidDataException("UM-SCHEDULE-WIPE-RECEIPT");
            bool acknowledged=sent.TryGetProperty("response",out var response)&&
                response.GetString()=="ADMIN_RCON_ACKNOWLEDGED_NO_GAMEPLAY_PROOF";
            string outcome=acknowledged?"WIPE_RCON_ACK_EFFECT_UNVERIFIED":"WIPE_SENT_EFFECT_UNVERIFIED";
            ScheduleEvent(runId,job,"wipe-result","limited",outcome,nativeId);
            return restartEvidence+"_"+outcome;
        }
        catch(Exception ex)
        {
            string code=NativeLifecycleFailureCode.From(ex);
            ScheduleEvent(runId,job,"wipe-result","failed",code,nativeId);
            return restartEvidence+"_WIPE_FAILED_"+code;
        }
    }
    private async Task<string> DispatchStandaloneWipeAsync(Guid mapId,string fingerprint,
        Func<string,Task<string>> handle,string prefix,string approval,CancellationToken ct)
    {
        // Only a durable enabled/claimed standalone WildWipe can dispatch.
        // Recheck configuration, cancellation and the claimed slot before
        // sending. Native admin preview/apply binds exact current PID/epoch,
        // acquires the installation operation lease and does NOT retry.
        bool StillApproved()=>Locked(()=>Read().Jobs.Any(x=>x.MapId==mapId&&
            x.Kind=="WildWipe"&&x.Enabled&&x.Fingerprint==fingerprint&&
            x.LastStatus=="claimed-no-auto-retry"))&&
            Eligible(Owned(mapId))&&Fingerprint(Owned(mapId))==fingerprint;
        ct.ThrowIfCancellationRequested();
        if(!StillApproved())return "WILD_WIPE_DISABLED_BEFORE_DISPATCH";
        var preview=Decode(await handle(Message(prefix+".adminPreview",
            new{mapId,action="wildWipe",message=""})));
        if(preview.GetProperty("mapId").GetGuid()!=mapId||
           preview.GetProperty("command").GetString()!="DestroyWildDinos")
            throw new InvalidDataException("UM-SCHEDULE-WIPE-PREVIEW");
        ct.ThrowIfCancellationRequested();
        if(!StillApproved())return "WILD_WIPE_DISABLED_BEFORE_DISPATCH";
        var result=Decode(await handle(Message(prefix+".adminSend",
            new{token=preview.GetProperty("token").GetGuid(),confirm=true,typedApproval=approval})));
        if(result.GetProperty("mapId").GetGuid()!=mapId||
           result.GetProperty("command").GetString()!="DestroyWildDinos"||
           !result.GetProperty("commandSent").GetBoolean()||
           !result.GetProperty("oneShot").GetBoolean())
            throw new InvalidDataException("UM-SCHEDULE-WIPE-RECEIPT");
        return "WILD_WIPE_SINGLE_SEND_GAME_EFFECT_UNVERIFIED";
    }
    private Task<string> RunNativeAsync(Guid mapId,string kind,CancellationToken ct)=>RunNativeAsync(mapId,kind,ct,null);
    private async Task<string> RunNativeAsync(Guid mapId,string kind,CancellationToken ct,ProcessIdentity? warned,ScheduledAsaOperation? scheduled=null,Guid? runId=null)
    {
        if(kind=="WildWipe")
        {
            var map=Owned(mapId);
            var state=cluster.Check(map);
            if(!state.Running||state.ConfirmedStopped||state.Identity is not {Pid:>0} child||
               !RconPortOwnership.IsOwnedListener(map.RconPort,child.Pid)||
               !AsaRuntimeLogEvidence.Inspect(map,child,state.LoaderIdentity).JoinAdvertised)
                throw new InvalidOperationException("UM-SCHEDULE-WIPE-NOT-READY");
            return await DispatchStandaloneWipeAsync(mapId,Fingerprint(map),
                raw=>freshWipeAdmin.HandleScheduledWipeAsync(raw,child.Pid,child.StartedUtc),
                "rcon","DINÓTÖRLÉS",ct);
        }
        if(kind=="SaveWorld")
        {
            var map=Owned(mapId);
            // The native SaveWorld bridge owns the non-recursive lease.
            // Do not acquire the same installation lock twice.
            var check=cluster.Check(map);
            if(!check.Running||check.ConfirmedStopped||check.Identity is not {Pid:>0} ||
               !safeMap(map))
                throw new InvalidOperationException("UM-ASA-SCHEDULE-SAVE-NOT-OWNED");
            var prepared=Decode(await rcon.HandleAsync(
                Message("rcon.saveWorld",new {mapId,confirm=true})));
            if(!prepared.GetProperty("nativeWitnessAvailable").GetBoolean()||
               prepared.GetProperty("watchId").ValueKind!=JsonValueKind.String)
                throw new InvalidOperationException("UM-ASA-SCHEDULE-SAVE-WITNESS-MISSING");
            var watchId=prepared.GetProperty("watchId").GetGuid();
            for(int n=0;n<36;n++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(3),ct);
                var saveStatus=Decode(await rcon.HandleAsync(
                    Message("rcon.saveStatus",new {mapId,watchId})));
                if(saveStatus.GetProperty("combinedSaveEvidenceObserved").GetBoolean())
                {
                    return "ASA_LOG_AND_WORLD_STABLE_NO_FSYNC_CERTIFICATION";
                }
                // ASA does not always emit the paired save log markers.
                // Distinguish a stable changed .ark from log-certified success.
                if(saveStatus.TryGetProperty("worldMetadataObserved",out var metadata)&&
                   metadata.ValueKind==JsonValueKind.True)
                {
                    return "ASA_WORLD_METADATA_STABLE_LOG_UNVERIFIED";
                }
                var stage=saveStatus.GetProperty("stage").GetString();
                if(stage is "LOG_ROTATED_OR_TRUNCATED" or "WORLD_LINKED" or
                    "LOG_FILE_LINKED" or "LOG_CHANGED")
                    throw new InvalidOperationException("UM-ASA-SCHEDULE-SAVE-WITNESS_INVALID");
            }
            throw new InvalidOperationException("UM-ASA-SCHEDULE-SAVE-WITNESS_TIMEOUT");
        }
        if(kind!="Restart")
            throw new UnauthorizedAccessException("UM-ASA-SCHEDULE-UNSUPPORTED");
        var preview=Decode(await restart.HandleAsync(
            Message("servers.restartPreview",new {mapId})));
        if(warned!=null&&(preview.GetProperty("currentPid").GetInt32()!=warned.Pid||
            preview.GetProperty("currentStartedUtc").GetDateTimeOffset()!=warned.StartedUtc))
            throw new InvalidOperationException("UM-SHUTDOWN-COUNTDOWN-PROCESS-CHANGED");
        ct.ThrowIfCancellationRequested();
        var token=preview.GetProperty("token").GetGuid();
        var started=Decode(await restart.HandleAsync(
            Message("servers.restartApply",new {token,confirm=true})));
        var jobId=started.GetProperty("jobId").GetGuid();
        // Native graceful stop may consume two minutes and an ASA API
        // loader-child/RCON handshake may consume another two. Do not mark
        // the scheduled restart failed while its native one-shot job is still
        // within those guarded limits. The operation remains serialized.
        for(int n=0;n<2850;n++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2),ct);
            var current=Decode(await restart.HandleAsync(
                Message("servers.restartStatus",new {jobId})));
            var state=current.GetProperty("state").GetString();
            if(state=="completed")
            {
                if(!current.TryGetProperty("readinessCertified",out var ready)||
                   ready.ValueKind!=JsonValueKind.True)
                    throw new InvalidOperationException(
                        "UM-ASA-SCHEDULE-RESTART-READINESS-NOT-CERTIFIED");
                const string evidence="RESTART_NEW_ASA_API_PLUGIN_JOIN_RCON_VERIFIED";
                if(scheduled?.PostRestartWipe==true&&runId.HasValue)
                {
                    int pid=current.GetProperty("newAsaServerPid").GetInt32();
                    var epoch=current.GetProperty("newAsaStartedUtc").GetDateTimeOffset();
                    if(pid<=0||pid==preview.GetProperty("currentPid").GetInt32())
                        throw new InvalidDataException("UM-SCHEDULE-WIPE-NEW-PID");
                    return await RunPostReadyWipeAsync(scheduled,runId.Value,jobId,pid,epoch,evidence,false,ct);
                }
                return evidence;
            }
            if(state=="needsOperator")
                throw new InvalidOperationException(RestartFailureCode(current,
                    "UM-ASA-SCHEDULE-RESTART-NEEDS_OPERATOR"));
        }
        throw new InvalidOperationException("UM-ASA-SCHEDULE-RESTART-TIMED_OUT");
    }

    public Task<ScheduledAsaOperation[]> TickOnceAsync(DateTimeOffset now,
        CancellationToken token=default)=>DispatchTickAsync(now,true,token);

    // The production timer keeps dispatching newly due MAPS during a known
    // CurseForge backoff. Explicit tests/callers can await this tick's work.
    internal async Task<ScheduledAsaOperation[]> DispatchTickAsync(DateTimeOffset now,
        bool waitForWorkers,CancellationToken token=default)
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        if(RuntimeState=="journal-write-needs-operator")
            throw new InvalidOperationException(
                "UM-ASA-SCHEDULE-OPERATOR-REVIEW-REQUIRED");
        if(!await tickGate.WaitAsync(0,token))return Snapshot();
        try
        {
            now=now.ToUniversalTime();
            foreach(var done in activeMapWorkers.Where(x=>x.Value.IsCompleted).ToArray())
            {
                activeMapWorkers.Remove(done.Key);
                await done.Value; // Observe all worker faults, including journal writes.
            }
            ReconcileEventTerminals();
            var due=Locked(()=>Read().Jobs.Where(x=>x.Enabled&&x.NextDueUtc<=now)
                .OrderBy(x=>x.NextDueUtc).ThenBy(x=>x.MapId).ToArray());
            var queued=new List<Task>();
            foreach(var group in due.GroupBy(x=>x.MapId))
            {
                if(activeMapWorkers.ContainsKey(group.Key)||!discordScope.TryBeginWorker(group.Key))continue;
                var mapDue=group.ToArray();
                var workerMapId=group.Key;
                Task task;
                try{
                    task=Task.Run(async()=>{
                        try{await RunDueMapAsync(mapDue,now,token);}
                        finally{discordScope.EndWorker(workerMapId);}
                    });
                }catch{discordScope.EndWorker(workerMapId);throw;}
                activeMapWorkers.Add(workerMapId,task);queued.Add(task);
            }
            if(waitForWorkers)
            {
                try{await Task.WhenAll(queued);}
                finally
                {
                    foreach(var done in activeMapWorkers.Where(x=>x.Value.IsCompleted).ToArray())
                        if(queued.Contains(done.Value))activeMapWorkers.Remove(done.Key);
                }
            }
            return Snapshot();
        }
        finally{tickGate.Release();}
    }

    private async Task RunDueMapAsync(ScheduledAsaOperation[] due,DateTimeOffset now,
        CancellationToken token)
    {
            // One host lane for native RCON/Start operations; backoff alone
            // yields it while the current map worker retains its slot.
            using var lane=await ScheduledAsaDispatchLane.EnterAsync(dispatchLane,token);
            if(RuntimeState=="journal-write-needs-operator")
                throw new InvalidOperationException("UM-ASA-SCHEDULE-OPERATOR-REVIEW-REQUIRED");
            foreach(var observed in due)
            {
                token.ThrowIfCancellationRequested();
                // A claimed crash launch owns the map until its readiness or
                // terminal failure. Do not consume/execute a scheduler slot
                // during that window; ordinary missed-slot rules apply later.
                if(AutomaticRecoveryPending?.Invoke(observed.MapId)==true)continue;
                // Acquire before publishing a claim. A second Manager's
                // live slot is contention, not an interrupted dispatch.
                OwnedSlot activeSlot;
                try{activeSlot=AcquireOwnedSlot(observed.MapId,observed.Kind);}
                catch(IOException ex) when(ex.Message=="UM-ASA-SCHEDULE-SLOT-OTHER-WORKER"){continue;}
                using var slotLifetime=activeSlot;
                bool missed=now-observed.NextDueUtc>TimeSpan.FromMinutes(5);
                var claimId=Guid.NewGuid();
                bool dispatched=Locked(()=>{
                    var state=Read();
                    int ix=state.Jobs.FindIndex(x=>x.MapId==observed.MapId&&
                        x.Kind==observed.Kind);
                    if(ix<0)return false;
                    var actual=state.Jobs[ix];
                    if(!actual.Enabled||actual.NextDueUtc!=observed.NextDueUtc||
                       actual.Revision!=observed.Revision||actual.Fingerprint!=observed.Fingerprint||actual.PostRestartWipe!=observed.PostRestartWipe||actual.WipeDelaySeconds!=observed.WipeDelaySeconds)return false;
                    // A still-enabled recurring policy already authorizes
                    // future slots, not replay of an old dispatch. Under the
                    // exact OS slot lease, archive the old outcome only if
                    // the same profile and native installation are clear.
                    if(state.History.Any(x=>x.MapId==actual.MapId&&
                       x.Kind==actual.Kind&&UnknownSlot(x.Status)))
                    {
                        bool retired=false;
                        DateTimeOffset cutoff=now;
                        if(actual.Frequency!="Once")
                        {
                            try
                            {
                                var map=Owned(actual.MapId);
                                if(Eligible(map)&&Fingerprint(map)==actual.Fingerprint)
                                {
                                    var oldCutoff=RetireUnknownSlots(state,map,actual.Kind,
                                        "RECURRING_POLICY_OLD_SLOT_OUTCOME_UNKNOWN");
                                    if(oldCutoff>cutoff)cutoff=oldCutoff;
                                    retired=true;
                                }
                            }
                            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                                InvalidOperationException or InvalidDataException or ArgumentException or JsonException)
                            {System.Diagnostics.Trace.TraceWarning("Scheduled slot reconciliation {0}: {1}",actual.MapId,ex.GetType().Name);}
                        }
                        string skippedStatus=retired?"skipped-auto-retired-prior-slot":"skipped-blocked-prior-unknown-slot";
                        string skippedEvidence=retired?"OLD_SLOT_ARCHIVED_NEXT_OCCURRENCE_ONLY":"PRIOR_SLOT_NATIVE_STATE_UNRESOLVED";
                        state.Jobs[ix]=actual with {
                            NextDueUtc=Next(actual,cutoff),Enabled=actual.Frequency!="Once",
                            LastStatus=retired?"auto-retired-unknown-no-replay":"blocked-prior-unknown-slot",
                            LastEvidence=skippedEvidence
                        };
                        // Skip this occurrence as well; never dispatch during
                        // bookkeeping or run a catch-up command.
                        AddHistory(state,new ScheduledAsaExecution(claimId,
                            actual.MapId,actual.Kind,observed.NextDueUtc,now,
                            now,skippedStatus,skippedEvidence));
                        Write(state);return false;
                    }
                    // If a restart with a post-ready wipe is due in this
                    // same tick, do not issue a second standalone wipe. Keep
                    // the skipped occurrence durable even if the restart fails.
                    if(actual.Kind=="WildWipe"&&due.Any(x=>x.MapId==actual.MapId&&
                        x.Kind=="Restart"&&x.PostRestartWipe))
                    {
                        state.Jobs[ix]=actual with {NextDueUtc=Next(actual,now),
                            Enabled=actual.Frequency!="Once",LastClaimedUtc=now,
                            LastStatus="skipped-overlapping-restart-wipe",
                            LastEvidence="POST_RESTART_WIPE_TAKES_PRECEDENCE"};
                        AddHistory(state,new ScheduledAsaExecution(claimId,actual.MapId,
                            actual.Kind,observed.NextDueUtc,now,now,
                            "skipped-overlapping-restart-wipe","POST_RESTART_WIPE_TAKES_PRECEDENCE"));
                        Write(state);return false;
                    }
                    var next=Next(actual,now);
                    var claimStatus=missed?"missed-no-catchup":"claimed-no-auto-retry";
                    state.Jobs[ix]=actual with {
                        NextDueUtc=next,LastClaimedUtc=now,Enabled=!(missed&&actual.Frequency=="Once"),
                        LastStatus=claimStatus,LastEvidence=""
                    };
                    AddHistory(state,new ScheduledAsaExecution(claimId,actual.MapId,
                        actual.Kind,observed.NextDueUtc,now,missed?now:null,
                        claimStatus,""));
                    Write(state);
                    return !missed;
                });
                if(!dispatched)
                {
                    var skipped=Locked(()=>Read().History.SingleOrDefault(x=>x.RunId==claimId));
                    if(skipped!=null)PublishTerminal(skipped,false);
                    continue;
                }
                ScheduleEvent(claimId,observed,"claimed","in-progress","DURABLE_SLOT_CLAIMED_NO_REPLAY");
                string status="failed-no-auto-retry",evidence="";
                try
                {
                    var map=Owned(observed.MapId);
                    bool stillApproved=Locked(()=>Read().Jobs.Any(x=>
                        x.MapId==observed.MapId&&x.Kind==observed.Kind&&
                        x.Enabled&&x.Revision==observed.Revision&&x.Fingerprint==observed.Fingerprint&&x.PostRestartWipe==observed.PostRestartWipe&&x.WipeDelaySeconds==observed.WipeDelaySeconds&&
                        x.LastClaimedUtc==now&&x.LastStatus=="claimed-no-auto-retry"));
                    if(stillApproved&&Eligible(map)&&Fingerprint(map)==observed.Fingerprint)
                    {
                        ScheduleEvent(claimId,observed,"started","in-progress","NATIVE_PREFLIGHT_PASSED");
                        using var origin=DiscordOperationEvents.EnterOrigin("scheduler",claimId,maps:new[]{map.Id},action:observed.Kind,suppress:true);
                        bool RetryStillApproved()=>Locked(()=>Read().Jobs.Any(x=>
                            x.MapId==observed.MapId&&x.Kind==observed.Kind&&
                            x.Enabled&&x.Revision==observed.Revision&&x.Fingerprint==observed.Fingerprint&&
                            x.LastClaimedUtc==now&&x.LastStatus=="claimed-no-auto-retry"))&&
                            Eligible(Owned(observed.MapId))&&Fingerprint(Owned(observed.MapId))==observed.Fingerprint;
                        using var freshRetry=map.HistoricalStoreProtected||observed.Kind!="Restart"?null:
                            ScheduledRestartRetryScope.Enter(new ScheduledRestartRetryPermit(
                                map.Id,claimId,RetryStillApproved,token,lane.BackoffAsync));
                        ProcessIdentity? warned=null;
                        if(nativeCountdown&&observed.Kind=="Restart")
                        {
                            ScheduleEvent(claimId,observed,"countdown","in-progress","FIVE_MINUTE_CHAT_COUNTDOWN");
                            warned=await WarnBeforeRestartAsync(observed,RetryStillApproved,token);
                            if(!RetryStillApproved())throw new InvalidOperationException("UM-SHUTDOWN-COUNTDOWN-CANCELLED");
                        }
                        evidence=map.HistoricalStoreProtected?
                            await RunHistoricalAsync(observed,claimId,lane,token,warned):
                            nativeCountdown?await RunNativeAsync(map.Id,observed.Kind,token,warned,observed,claimId):
                             await runner(map.Id,observed.Kind,token);
                        status="executed-no-fsync-or-client-certification";
                    }
                    else status="config-or-store-blocked";
                }
                catch(OperationCanceledException)
                {
                    status="interrupted-needs-operator";
                    throw;
                }
                catch(Exception ex) {status="failed-needs-operator-no-retry";evidence=NativeLifecycleFailureCode.From(ex);}
                finally
                {
                    try
                    {
                        Locked(()=>{
                            var state=Read();
                            int ix=state.Jobs.FindIndex(x=>x.MapId==observed.MapId&&
                                x.Kind==observed.Kind);
                            if(ix>=0 && state.Jobs[ix].LastClaimedUtc==now &&
                               state.Jobs[ix].LastStatus=="claimed-no-auto-retry")
                            {
                                state.Jobs[ix]=state.Jobs[ix] with {
                                    LastStatus=status,LastEvidence=evidence,
                                    Enabled=state.Jobs[ix].Enabled&&state.Jobs[ix].Frequency!="Once"
                                };
                                int historyIndex=state.History.FindIndex(x=>x.RunId==claimId);
                                if(historyIndex>=0 &&
                                   state.History[historyIndex].Status=="claimed-no-auto-retry")
                                    state.History[historyIndex]=state.History[historyIndex] with {
                                        FinishedUtc=DateTimeOffset.UtcNow,
                                        Status=status,Evidence=evidence
                                    };
                                Write(state);
                            }
                            return true;
                        });
                        var persisted=Locked(()=>Read().History.SingleOrDefault(x=>x.RunId==claimId));
                        if(persisted!=null)PublishTerminal(persisted,false);
                        // Persist the incident independently from Discord
                        // delivery. A webhook failure cannot delay native
                        // recovery or erase the original failed schedule claim.
                        if(observed.Kind=="Restart")
                        {
                            try
                            {
                                if(status=="executed-no-fsync-or-client-certification")
                                {
                                    var prior=ScheduledRestartOutageLedger.Read(
                                        folder,observed.MapId);
                                    if(prior?.Offline==true)
                                        ScheduledRestartOutageLedger.Record(folder,
                                            observed.MapId,prior.RunId,prior.MapName,
                                            prior.OutageStartedUtc,prior.Error,
                                            prior.Attempts,"NEW_INSTANCE_READY",
                                            DateTimeOffset.UtcNow);
                                }
                                else
                                {
                                    var map=Owned(observed.MapId);
                                    var prior=ScheduledRestartOutageLedger.Read(folder,observed.MapId);
                                    var receipt=new AsaManualRestartJournal(map.InstallationPath).Observe();
                                    // A preflight refusal or failed post-ready
                                    // wipe is NOT an offline server incident.
                                    var check=map.HistoricalStoreProtected?
                                        LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(map,cluster.Config.LocalMaps):
                                        cluster.Check(map);
                                    if(check.ConfirmedStopped&&!check.Running&&
                                       check.Identity==null&&check.LoaderIdentity==null&&
                                       receipt?.ScheduledRunId==claimId&&
                                       receipt.Phase!=AsaRestartPhase.Completed)
                                        ScheduledRestartOutageLedger.Record(folder,
                                            observed.MapId,claimId,map.Name,
                                            prior?.RunId==claimId?prior.OutageStartedUtc:now,
                                            string.IsNullOrWhiteSpace(evidence)?
                                                "UM-SCHEDULE-RESTART-FAILED":evidence,
                                            receipt.StartupAttempt,"CHECK_STOPPED_FAMILY_AND_JOURNALS_THEN_MANUAL_START");
                                }
                            }
                            catch
                            {
                                // Never turn a successful ASA restart into
                                // failure just because notification storage
                                // is damaged, full or unavailable.
                            }
                        }
                        DesktopDiscordNotifications.Record(cluster,observed.MapId,"schedule",status!="executed-no-fsync-or-client-certification");
                    }
                    catch
                    {
                        // A previously dispatched SaveWorld/Restart may have
                        // completed while the durable receipt could not be
                        // persisted. Never schedule another slot or report
                        // "active" until an operator inspects the journal.
                        Volatile.Write(ref runtimeState,"journal-write-needs-operator");
                        throw new InvalidOperationException(
                            "UM-ASA-SCHEDULE-JOURNAL-WRITE-NEEDS-OPERATOR");
                    }
                }
            }
    }
    // Manager startup is NOT a delayed execution window: old unclaimed
    // due times are advanced before arming the timer. A crash after a claim
    // is already protected by LastClaimedUtc and the persisted next cursor.
    public void SkipMissedOnManagerStart(DateTimeOffset now)
    {
        now=now.ToUniversalTime();
        Locked(()=>{
            var state=Read();
            bool changed=false;
            for(int i=0;i<state.Jobs.Count;i++)
            {
                var item=state.Jobs[i];
                if(!item.Enabled||item.NextDueUtc>now)continue;
                state.Jobs[i]=item with {
                    NextDueUtc=Next(item,now),Enabled=item.Frequency!="Once",
                    LastStatus="manager-start-skipped-no-replay",
                    LastEvidence=""
                };
                AddHistory(state,new ScheduledAsaExecution(Guid.NewGuid(),item.MapId,
                    item.Kind,item.NextDueUtc,now,now,
                    "manager-start-skipped-no-replay",""));
                changed=true;
            }
            if(changed)Write(state);
            return true;
        });
    }
    public void Start()
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        if(Interlocked.Exchange(ref started,1)!=0)return;
        try {SkipMissedOnManagerStart(DateTimeOffset.UtcNow);ReconcileEventTerminals();}
        catch(Exception) {Volatile.Write(ref runtimeState,"journal-recovery-needs-operator");return;}
        Volatile.Write(ref runtimeState,"active");
        loop=Task.Run(async()=>{
            using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                do
                {
                    try
                    {
                        await DispatchTickAsync(DateTimeOffset.UtcNow,false,cancel.Token);
                        if(RuntimeState=="journal-write-needs-operator")break;
                        Volatile.Write(ref runtimeState,"active");
                    }
                    catch(OperationCanceledException) when(cancel.IsCancellationRequested){break;}
                    catch(Exception)
                    {
                        if(RuntimeState=="journal-write-needs-operator")break;
                        Volatile.Write(ref runtimeState,"journal-or-operation-error");
                    }
                } while(await timer.WaitForNextTickAsync(cancel.Token));
            }
            catch(OperationCanceledException){/* Manager closing, never replay. */}
            finally
            {
                // No new tick after cancellation. Observe existing workers;
                // their durable claims remain the authority on interruption.
                Task[] workers;
                await tickGate.WaitAsync();
                try{workers=activeMapWorkers.Values.ToArray();}
                finally{tickGate.Release();}
                try{await Task.WhenAll(workers);}catch{/* individual receipts retained */}
                if(RuntimeState!="journal-write-needs-operator")
                    Volatile.Write(ref runtimeState,"stopped");
            }
        });
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        cancel.Cancel();
        // Do NOT wait synchronously on the Windows UI thread or kill any ASA.
        _=loop;
    }
    private static string Ok(string? id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
}
