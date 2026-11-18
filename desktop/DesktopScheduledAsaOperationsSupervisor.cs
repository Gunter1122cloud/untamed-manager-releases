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

// Only locally owned, explicitly opted-in ASA installations, including pinned Store-v1. The native
// runner calls the existing PID-bound RCON and Restart bridges, never a second
// implementation of SaveWorld / DoExit / Start. The journal claims a slot
// BEFORE any RCON bytes; crashes, missed slots and failed jobs never replay.
public sealed record ScheduledAsaOperation(
    Guid MapId,string Kind,string Frequency,int IntervalMinutes,
    int MinuteOfDay,int DaysMask,string Fingerprint,bool Enabled,
    DateTimeOffset NextDueUtc,DateTimeOffset? LastClaimedUtc,
    string LastStatus,string LastEvidence,bool PostRestartWipe=false);

// Bounded, persisted execution evidence. A slot is recorded BEFORE dispatch;
// after a crash an unfinished claim remains visible and is never auto-replayed.
public sealed record ScheduledAsaExecution(
    Guid RunId,Guid MapId,string Kind,DateTimeOffset ScheduledUtc,
    DateTimeOffset ClaimedUtc,DateTimeOffset? FinishedUtc,string Status,string Evidence);

public sealed class DesktopScheduledAsaOperationsSupervisor : IDisposable
{
    private sealed record ScheduleReviewApproval(Guid Token,Guid MapId,
        string Kind,Guid RunId,string JournalSha256,DateTimeOffset CreatedUtc);
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
    private readonly DesktopRconReadOnlyBridge rcon;
    private readonly DesktopFreshAsaRestartBridge restart;
    private readonly DesktopHistoricalGracefulStopBridge? historicalStop;
    private readonly DesktopHistoricalRestartBridge? historicalRestart;
    private readonly DesktopHistoricalRconAdminBridge? historicalAdmin;
    private readonly Func<Guid,string,CancellationToken,Task<string>> runner;
    private readonly Func<LocalMap,bool> safeMap;
    private readonly string folder,path,mutexName;
    private readonly SemaphoreSlim tickGate=new(1,1);
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
        this.rcon=rcon??throw new ArgumentNullException(nameof(rcon));
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
                (j.Kind!="SaveWorld"&&j.Kind!="Restart")||
                (j.Frequency!="Interval"&&j.Frequency!="Daily"&&j.Frequency!="Once")||
                (j.PostRestartWipe&&j.Kind!="Restart")||
                (j.Frequency=="Once"&&j.IntervalMinutes is <1 or >2)||
                j.DaysMask is <1 or >127 || j.MinuteOfDay is <0 or >1439 ||
                j.Fingerprint.Length!=64 || j.LastStatus.Length>90)||
           state.Jobs.GroupBy(j=>(j.MapId,j.Kind)).Any(group=>group.Count()>1))
            throw new InvalidDataException("UM-ASA-SCHEDULE-JOURNAL-INVALID");
        if(state.History==null||state.History.Count>MaxHistory||
           state.History.Any(e=>e==null||e.RunId==Guid.Empty||e.MapId==Guid.Empty||
               (e.Kind!="SaveWorld"&&e.Kind!="Restart")||
               string.IsNullOrWhiteSpace(e.Status)||e.Status.Length>90||
               e.Evidence==null||e.Evidence.Length>240)||
           state.History.GroupBy(e=>e.RunId).Any(g=>g.Count()>1))
            throw new InvalidDataException("UM-ASA-SCHEDULE-HISTORY-INVALID");
        return state;
    }
    private static void AddHistory(State state,ScheduledAsaExecution entry)
    {
        state.History.Add(entry);
        if(state.History.Count>MaxHistory)
            state.History.RemoveRange(0,state.History.Count-MaxHistory);
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
    private static TimeZoneInfo Zone()
    {
        try{return TimeZoneInfo.FindSystemTimeZoneById("Europe/Budapest");}
        catch(TimeZoneNotFoundException)
        {return TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");}
    }
    private static DateTimeOffset Next(ScheduledAsaOperation item,DateTimeOffset now)
    {
        now=now.ToUniversalTime();
        if(item.Frequency=="Once")return item.NextDueUtc==default?now.AddMinutes(item.IntervalMinutes):DateTimeOffset.MaxValue;
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
        var zone=Zone();
        var local=TimeZoneInfo.ConvertTime(now,zone).Date;
        for(int offset=0;offset<9;offset++)
        {
            var day=local.AddDays(offset);
            if((item.DaysMask&(1<<(int)day.DayOfWeek))==0)continue;
            var wall=DateTime.SpecifyKind(day.AddMinutes(item.MinuteOfDay),
                DateTimeKind.Unspecified);
            while(zone.IsInvalidTime(wall))wall=wall.AddMinutes(1);
            var shift=zone.IsAmbiguousTime(wall)?
                zone.GetAmbiguousTimeOffsets(wall).Min():zone.GetUtcOffset(wall);
            var candidate=new DateTimeOffset(wall,shift).ToUniversalTime();
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
            .OrderByDescending(x=>x.ClaimedUtc).Take(100).ToArray());
    }

    public ScheduledAsaOperation Configure(Guid mapId,string kind,string frequency,
        int intervalMinutes,int minuteOfDay,int daysMask,bool postRestartWipe=false)
    {
        if(mapId==Guid.Empty || kind is not ("SaveWorld" or "Restart") ||
           frequency is not ("Interval" or "Daily" or "Once") ||
           (kind=="Restart"&&frequency=="Interval") ||
           (frequency=="Once"&&intervalMinutes is <1 or >2) ||
           (postRestartWipe&&kind!="Restart") ||
           minuteOfDay is <0 or >1439 || daysMask is <1 or >127 ||
           (frequency=="Interval"&&intervalMinutes is <15 or >10080))
            throw new InvalidDataException("UM-ASA-SCHEDULE-FIELDS");
        var map=Owned(mapId);
        if(!Eligible(map)||(postRestartWipe&&!HistoricalEligible(map)))
            throw new UnauthorizedAccessException("UM-ASA-SCHEDULE-PREFLIGHT-DENIED");
        return Locked(()=>{
            var state=Read();
            var fingerprint=Fingerprint(map);
            var sample=new ScheduledAsaOperation(map.Id,kind,frequency,intervalMinutes,
                minuteOfDay,daysMask,fingerprint,true,default,null,"scheduled","",postRestartWipe);
            var task=sample with {NextDueUtc=Next(sample,DateTimeOffset.UtcNow)};
            int ix=state.Jobs.FindIndex(j=>j.MapId==map.Id&&j.Kind==kind);
            if(state.History.Any(x=>x.MapId==map.Id&&x.Kind==kind&&UnknownSlot(x.Status)))
                throw new InvalidOperationException("UM-ASA-SCHEDULE-REVIEW-OLD-SLOT");
            if(ix<0)state.Jobs.Add(task);else state.Jobs[ix]=task;
            Write(state);
            return task;
        });
    }
    public ScheduledAsaOperation Disable(Guid mapId,string kind)
    {
        if(kind is not ("SaveWorld" or "Restart"))
            throw new InvalidDataException("UM-ASA-SCHEDULE-KIND");
        Owned(mapId);
        return Locked(()=>{
            var state=Read();
            int ix=state.Jobs.FindIndex(j=>j.MapId==mapId&&j.Kind==kind);
            if(ix<0)throw new InvalidDataException("UM-ASA-SCHEDULE-NOT-FOUND");
            var changed=state.Jobs[ix] with {Enabled=false,LastStatus="disabled"};
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
                    x.MinuteOfDay==self.MinuteOfDay&&(x.DaysMask&self.DaysMask)!=0)
                .Select(x=>cluster.Config.LocalMaps.FirstOrDefault(m=>m.Id==x.MapId)?.Name)
                .Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x!)
                .Distinct().ToArray();
        });
    }

    private static object Data(ScheduledAsaOperation job)=>new {
        mapId=job.MapId,kind=job.Kind,frequency=job.Frequency,
        intervalMinutes=job.IntervalMinutes,minuteOfDay=job.MinuteOfDay,
        daysMask=job.DaysMask,enabled=job.Enabled,nextDueUtc=job.NextDueUtc,postRestartWipe=job.PostRestartWipe,
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
                    using(var slot=AcquireOwnedSlot(mapId,reviewKind)){}
                    var previewToken=Guid.NewGuid();
                    reviewTokens[mapId]=new(previewToken,mapId,reviewKind,runId,
                        Locked(JournalHash),DateTimeOffset.UtcNow);
                    return Task.FromResult(Ok(id,new{
                        token=previewToken,mapId,kind=reviewKind,runId,
                        previousStatus=candidate.Status,
                        outcomeStillUnknown=true,automaticRetry=false,
                        scheduledTaskWillBeDisabled=true,
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
                    if(ix>=0)state.Jobs[ix]=state.Jobs[ix] with{
                        Enabled=false,LastStatus="operator-reviewed-unknown-no-replay",
                        LastEvidence="OLD_SLOT_UNKNOWN_NEEDS_NEW_OPT_IN"
                    };
                    Write(state);
                    return new {mapId=permit.MapId,kind=permit.Kind,runId=permit.RunId,
                        reviewed=true,outcomeStillUnknown=true,jobDisabled=true,
                        noRconSent=true,noAutomaticReplay=true,oldJournalArchived=true,
                        evidence="SCHEDULE_UNKNOWN_SLOT_AUDITED_AND_DISABLED"};
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
            var result=Configure(mapId,kind,rawFreq.GetString()!,interval,minute,days,wipe);
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
            throw new InvalidOperationException("UM-ASA-SCHEDULE-ENGINE-REJECTED");
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
    private async Task<string> RunHistoricalAsync(ScheduledAsaOperation job,CancellationToken ct)
    {
        var map=Owned(job.MapId);
        if(!HistoricalEligible(map)||Fingerprint(map)!=job.Fingerprint)
            throw new InvalidOperationException("UM-ASA-SCHEDULE-HISTORICAL-CHANGED");
        bool save=job.Kind=="SaveWorld";
        Func<string,Task<string>> handle=save?historicalStop!.HandleAsync:historicalRestart!.HandleAsync;
        string prefix=save?"historical.saveWorld":"historical.restart";
        var pre=Decode(await handle(Message(prefix+"Preview",new{mapId=job.MapId})));
        if(pre.GetProperty("mapId").GetGuid()!=job.MapId||
           !pre.GetProperty("usesSaveWorld").GetBoolean()||
           pre.GetProperty("usesDoExit").GetBoolean()==save)
            throw new InvalidDataException("UM-SCHEDULE-HISTORICAL-PREVIEW");
        int oldPid=pre.GetProperty("pid").GetInt32();
        ct.ThrowIfCancellationRequested();
        var begun=Decode(await handle(Message(prefix+"Apply",new{token=pre.GetProperty("token").GetGuid()})));
        if(!begun.GetProperty("started").GetBoolean()||
           begun.GetProperty("automaticRetry").GetBoolean()||
           begun.GetProperty("forceKill").GetBoolean()||
           begun.GetProperty("storeTouched").GetBoolean())
            throw new InvalidDataException("UM-SCHEDULE-HISTORICAL-APPLY");
        Guid id=begun.GetProperty("jobId").GetGuid();
        var limit=DateTimeOffset.UtcNow.AddMinutes(16);
        while(DateTimeOffset.UtcNow<limit)
        {
            await Task.Delay(TimeSpan.FromSeconds(2),ct);
            var result=Decode(await handle(Message(prefix+"Status",new{jobId=id})));
            if(result.GetProperty("jobId").GetGuid()!=id)
                throw new InvalidDataException("UM-SCHEDULE-HISTORICAL-JOB-MISMATCH");
            string? state=result.GetProperty("state").GetString();
            if(state is "needsOperator" or "notDispatched" or "reviewed")
                throw new InvalidOperationException("UM-SCHEDULE-HISTORICAL-NEEDS-REVIEW");
            if(state!="completed")continue;
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
            if(!job.PostRestartWipe)return evidence+"_RESTART_NEW_PID";
            DateTimeOffset started=result.GetProperty("newStartedUtc").GetDateTimeOffset();
            // Bind the wipe to the exact new PID/start time. Wait on freshly
            // observed own listener and current-run join advertising, never
            // infer readiness from a delay or the old process.
            var readyLimit=DateTimeOffset.UtcNow.AddMinutes(10);
            bool ready=false;
            while(DateTimeOffset.UtcNow<readyLimit)
            {
                ct.ThrowIfCancellationRequested();
                if(!HistoricalEligible(Owned(job.MapId))||
                   Fingerprint(Owned(job.MapId))!=job.Fingerprint)
                    throw new InvalidOperationException("UM-SCHEDULE-WIPE-CONFIG-CHANGED");
                LegacyStoreV1GracefulStop.Owned? proof=null;
                try{proof=LegacyStoreV1GracefulStop.Preview(cluster,job.MapId);}
                catch(Exception){/* startup/listener not ready; no dispatch */}
                if(proof!=null)
                {
                    if(proof.Process.Pid!=pid||proof.Process.StartedUtc!=started)
                        throw new InvalidOperationException("UM-SCHEDULE-WIPE-PID-CHANGED");
                    if(LegacyStoreV1GracefulStop.SaveOnlyStartupBlocker(proof)==null)
                    {ready=true;break;}
                }
                await Task.Delay(TimeSpan.FromSeconds(2),ct);
            }
            if(!ready)throw new InvalidOperationException("UM-SCHEDULE-WIPE-NOT-READY");
            // A disabled or edited job may finish its in-progress Restart,
            // but it must not send a newly pending wipe.
            bool wipeStillEnabled=Locked(()=>Read().Jobs.Any(x=>x.MapId==job.MapId&&
                x.Kind==job.Kind&&x.Enabled&&x.PostRestartWipe&&
                x.Fingerprint==job.Fingerprint&&x.LastStatus=="claimed-no-auto-retry"));
            if(!wipeStillEnabled)return evidence+"_RESTART_WIPE_DISABLED";
            ct.ThrowIfCancellationRequested();
            var wipe=Decode(await historicalAdmin!.HandleScheduledWipeAsync(
                Message("historical.adminPreview",new{mapId=job.MapId,action="wildWipe",message=""}),pid,started));
            if(wipe.GetProperty("mapId").GetGuid()!=job.MapId||
               wipe.GetProperty("command").GetString()!="DestroyWildDinos")
                throw new InvalidDataException("UM-SCHEDULE-WIPE-PREVIEW");
            ct.ThrowIfCancellationRequested();
            var sent=Decode(await historicalAdmin.HandleScheduledWipeAsync(
                Message("historical.adminSend",new{token=wipe.GetProperty("token").GetGuid(),
                    confirm=true,typedApproval="STORE-V1 DINÓTÖRLÉS"}),pid,started));
            if(sent.GetProperty("mapId").GetGuid()!=job.MapId||
               sent.GetProperty("command").GetString()!="DestroyWildDinos"||
               !sent.GetProperty("commandSent").GetBoolean()||
               !sent.GetProperty("oneShot").GetBoolean())
                throw new InvalidDataException("UM-SCHEDULE-WIPE-RECEIPT");
            return evidence+"_RESTART_READY_WIPE_SENT_EFFECT_UNVERIFIED";
        }
        throw new InvalidOperationException("UM-SCHEDULE-HISTORICAL-TIMEOUT");
    }
    private async Task<string> RunNativeAsync(Guid mapId,string kind,CancellationToken ct)
    {
        if(kind=="SaveWorld")
        {
            var map=Owned(mapId);
            // Shared installation coordinator prevents manual Stop/Restart or
            // SteamCMD from overlapping this scheduled SaveWorld command.
            using var lease=cluster.Operations.Acquire(map.InstallationPath,
                UntamedOperationKind.Backup);
            var check=cluster.Check(map);
            if(!check.Running||check.ConfirmedStopped||check.Identity is not {Pid:>0} ||
               !safeMap(map))
                throw new InvalidOperationException("UM-ASA-SCHEDULE-SAVE-NOT-OWNED");
            lease.MarkMutationStarted();
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
                    lease.Complete();
                    return "ASA_LOG_AND_WORLD_STABLE_NO_FSYNC_CERTIFICATION";
                }
                // ASA does not always emit the paired save log markers.
                // Distinguish a stable changed .ark from log-certified success.
                if(saveStatus.TryGetProperty("worldMetadataObserved",out var metadata)&&
                   metadata.ValueKind==JsonValueKind.True)
                {
                    lease.Complete();
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
        var token=preview.GetProperty("token").GetGuid();
        var started=Decode(await restart.HandleAsync(
            Message("servers.restartApply",new {token,confirm=true})));
        var jobId=started.GetProperty("jobId").GetGuid();
        // Native graceful stop may consume two minutes and an ASA API
        // loader-child/RCON handshake may consume another two. Do not mark
        // the scheduled restart failed while its native one-shot job is still
        // within those guarded limits. The operation remains serialized.
        for(int n=0;n<180;n++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2),ct);
            var current=Decode(await restart.HandleAsync(
                Message("servers.restartStatus",new {jobId})));
            var state=current.GetProperty("state").GetString();
            if(state=="completed")
                return "RESTART_NEW_WINDOWS_PID_OBSERVED_READINESS_UNVERIFIED";
            if(state=="needsOperator")
                throw new InvalidOperationException("UM-ASA-SCHEDULE-RESTART-NEEDS_OPERATOR");
        }
        throw new InvalidOperationException("UM-ASA-SCHEDULE-RESTART-TIMED_OUT");
    }

    public async Task<ScheduledAsaOperation[]> TickOnceAsync(DateTimeOffset now,
        CancellationToken token=default)
    {
        if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(GetType().Name);
        if(RuntimeState=="journal-write-needs-operator")
            throw new InvalidOperationException(
                "UM-ASA-SCHEDULE-OPERATOR-REVIEW-REQUIRED");
        if(!await tickGate.WaitAsync(0,token))return Snapshot();
        try
        {
            now=now.ToUniversalTime();
            var due=Locked(()=>Read().Jobs.Where(x=>x.Enabled&&x.NextDueUtc<=now)
                .OrderBy(x=>x.NextDueUtc).ThenBy(x=>x.MapId).ToArray());
            foreach(var observed in due)
            {
                token.ThrowIfCancellationRequested();
                bool missed=now-observed.NextDueUtc>TimeSpan.FromMinutes(5);
                var claimId=Guid.NewGuid();
                bool dispatched=Locked(()=>{
                    var state=Read();
                    int ix=state.Jobs.FindIndex(x=>x.MapId==observed.MapId&&
                        x.Kind==observed.Kind);
                    if(ix<0)return false;
                    var actual=state.Jobs[ix];
                    if(!actual.Enabled||actual.NextDueUtc!=observed.NextDueUtc||
                       actual.Fingerprint!=observed.Fingerprint||actual.PostRestartWipe!=observed.PostRestartWipe)return false;
                    // Unknown earlier dispatch blocks ONLY this task. Do not
                    // accidentally execute its next slot because the
                    // schedule cursor was advanced before a crash.
                    if(state.History.Any(x=>x.MapId==actual.MapId&&
                       x.Kind==actual.Kind&&UnknownSlot(x.Status)))
                    {
                        state.Jobs[ix]=actual with {
                            NextDueUtc=Next(actual,now),Enabled=actual.Frequency!="Once",
                            LastStatus="blocked-prior-unknown-slot",
                            LastEvidence="REVIEW_OLD_CLAIM_NO_REPLAY"
                        };
                        AddHistory(state,new ScheduledAsaExecution(claimId,
                            actual.MapId,actual.Kind,observed.NextDueUtc,now,
                            now,"skipped-blocked-prior-unknown-slot",
                            "PRIOR_SLOT_NEEDS_OPERATOR"));
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
                if(!dispatched)continue;
                using var activeSlot=AcquireOwnedSlot(observed.MapId,observed.Kind);
                string status="failed-no-auto-retry",evidence="";
                try
                {
                    var map=Owned(observed.MapId);
                    bool stillApproved=Locked(()=>Read().Jobs.Any(x=>
                        x.MapId==observed.MapId&&x.Kind==observed.Kind&&
                        x.Enabled&&x.Fingerprint==observed.Fingerprint&&x.PostRestartWipe==observed.PostRestartWipe&&
                        x.LastClaimedUtc==now&&x.LastStatus=="claimed-no-auto-retry"));
                    if(stillApproved&&Eligible(map)&&Fingerprint(map)==observed.Fingerprint)
                    {
                        evidence=map.HistoricalStoreProtected?
                            await RunHistoricalAsync(observed,token):await runner(map.Id,observed.Kind,token);
                        status="executed-no-fsync-or-client-certification";
                    }
                    else status="config-or-store-blocked";
                }
                catch(OperationCanceledException)
                {
                    status="interrupted-needs-operator";
                    throw;
                }
                catch(Exception) {status="failed-needs-operator-no-retry";}
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
            return Snapshot();
        }
        finally{tickGate.Release();}
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
        try {SkipMissedOnManagerStart(DateTimeOffset.UtcNow);}
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
                        await TickOnceAsync(DateTimeOffset.UtcNow,cancel.Token);
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
