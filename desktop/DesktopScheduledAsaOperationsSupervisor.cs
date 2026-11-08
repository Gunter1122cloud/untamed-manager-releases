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

// Only locally owned, explicitly opted-in NEW ASA installations. The native
// runner calls the existing PID-bound RCON and Restart bridges, never a second
// implementation of SaveWorld / DoExit / Start. The journal claims a slot
// BEFORE any RCON bytes; crashes, missed slots and failed jobs never replay.
public sealed record ScheduledAsaOperation(
    Guid MapId,string Kind,string Frequency,int IntervalMinutes,
    int MinuteOfDay,int DaysMask,string Fingerprint,bool Enabled,
    DateTimeOffset NextDueUtc,DateTimeOffset? LastClaimedUtc,
    string LastStatus,string LastEvidence);

public sealed class DesktopScheduledAsaOperationsSupervisor : IDisposable
{
    private sealed class State
    {
        public List<ScheduledAsaOperation> Jobs {get;set;}=new();
    }
    private const int MaxJournalBytes=2_000_000;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ClusterService cluster;
    private readonly DesktopRconReadOnlyBridge rcon;
    private readonly DesktopFreshAsaRestartBridge restart;
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
        Func<LocalMap,bool>? isolatedPreflight=null)
    {
        this.cluster=cluster??throw new ArgumentNullException(nameof(cluster));
        this.rcon=rcon??throw new ArgumentNullException(nameof(rcon));
        this.restart=restart??throw new ArgumentNullException(nameof(restart));
        var root=cluster.Store.Root;
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-ASA-SCHEDULE-LOCAL-PROFILE");
        folder=Path.GetFullPath(root);
        if(new DriveInfo(Path.GetPathRoot(folder)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-ASA-SCHEDULE-FIXED-DISK");
        CheckLinks();
        path=Path.Combine(folder,"native-asa-schedules-v1.json");
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
                (j.Frequency!="Interval"&&j.Frequency!="Daily")||
                j.DaysMask is <1 or >127 || j.MinuteOfDay is <0 or >1439 ||
                j.Fingerprint.Length!=64 || j.LastStatus.Length>90)||
           state.Jobs.GroupBy(j=>(j.MapId,j.Kind)).Any(group=>group.Count()>1))
            throw new InvalidDataException("UM-ASA-SCHEDULE-JOURNAL-INVALID");
        return state;
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

    public ScheduledAsaOperation Configure(Guid mapId,string kind,string frequency,
        int intervalMinutes,int minuteOfDay,int daysMask)
    {
        if(mapId==Guid.Empty || kind is not ("SaveWorld" or "Restart") ||
           frequency is not ("Interval" or "Daily") ||
           (kind=="Restart"&&frequency!="Daily") ||
           minuteOfDay is <0 or >1439 || daysMask is <1 or >127 ||
           (frequency=="Interval"&&intervalMinutes is <15 or >10080))
            throw new InvalidDataException("UM-ASA-SCHEDULE-FIELDS");
        var map=Owned(mapId);
        if(!safeMap(map))
            throw new UnauthorizedAccessException("UM-ASA-SCHEDULE-PREFLIGHT-DENIED");
        return Locked(()=>{
            var state=Read();
            var fingerprint=Fingerprint(map);
            var sample=new ScheduledAsaOperation(map.Id,kind,frequency,intervalMinutes,
                minuteOfDay,daysMask,fingerprint,true,default,null,"scheduled","");
            var task=sample with {NextDueUtc=Next(sample,DateTimeOffset.UtcNow)};
            int ix=state.Jobs.FindIndex(j=>j.MapId==map.Id&&j.Kind==kind);
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
            var self=jobs.FirstOrDefault(x=>x.MapId==mapId&&x.Kind=="Restart"&&x.Enabled);
            if(self==null)return Array.Empty<string>();
            return jobs.Where(x=>x.MapId!=mapId&&x.Kind=="Restart"&&x.Enabled&&
                    x.MinuteOfDay==self.MinuteOfDay&&(x.DaysMask&self.DaysMask)!=0)
                .Select(x=>cluster.Config.LocalMaps.FirstOrDefault(m=>m.Id==x.MapId)?.Name)
                .Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x!)
                .Distinct().ToArray();
        });
    }

    private static object Data(ScheduledAsaOperation job)=>new {
        mapId=job.MapId,kind=job.Kind,frequency=job.Frequency,
        intervalMinutes=job.IntervalMinutes,minuteOfDay=job.MinuteOfDay,
        daysMask=job.DaysMask,enabled=job.Enabled,nextDueUtc=job.NextDueUtc,
        lastClaimedUtc=job.LastClaimedUtc,lastStatus=job.LastStatus,
        lastEvidence=job.LastEvidence,
        evidence="DURABLE_NATIVE_LOCAL_SCHEDULE_NO_AUTO_REPLAY"
    };
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
               methodRaw.ValueKind!=JsonValueKind.String ||
               !args.TryGetProperty("mapId",out var rawMap)||
               rawMap.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(rawMap.GetString(),out var mapId)||mapId==Guid.Empty)
                return Task.FromResult(Fail(id,"UM-ASA-SCHEDULE-MESSAGE"));
            id=parsed.ToString("D");
            Owned(mapId);
            var method=methodRaw.GetString();
            if(method=="schedule.nativeStatus")
                return Task.FromResult(Ok(id,new {
                    mapId,runnerState=RuntimeState,
                    jobs=Snapshot().Where(x=>x.MapId==mapId).Select(Data).ToArray(),
                    restartConflicts=RestartConflicts(mapId),
                    protectedStore=KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(Owned(mapId)),
                    autoUpdateAvailable=false,
                    remoteExecutionAllowed=false
                }));
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
            var result=Configure(mapId,kind,rawFreq.GetString()!,interval,minute,days);
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
    private async Task<string> RunNativeAsync(Guid mapId,string kind,CancellationToken ct)
    {
        if(kind=="SaveWorld")
        {
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
                var check=Decode(await rcon.HandleAsync(
                    Message("rcon.saveStatus",new {mapId,watchId})));
                if(check.GetProperty("combinedSaveEvidenceObserved").GetBoolean())
                    return "ASA_LOG_AND_WORLD_STABLE_PREEXIT_NO_FSYNC_CERTIFICATION";
                var stage=check.GetProperty("stage").GetString();
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
        for(int n=0;n<105;n++)
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
                bool dispatched=Locked(()=>{
                    var state=Read();
                    int ix=state.Jobs.FindIndex(x=>x.MapId==observed.MapId&&
                        x.Kind==observed.Kind);
                    if(ix<0)return false;
                    var actual=state.Jobs[ix];
                    if(!actual.Enabled||actual.NextDueUtc!=observed.NextDueUtc||
                       actual.Fingerprint!=observed.Fingerprint)return false;
                    var next=Next(actual,now);
                    state.Jobs[ix]=actual with {
                        NextDueUtc=next,LastClaimedUtc=now,
                        LastStatus=missed?"missed-no-catchup":"claimed-no-auto-retry",
                        LastEvidence=""
                    };
                    Write(state);
                    return !missed;
                });
                if(!dispatched)continue;
                string status="failed-no-auto-retry",evidence="";
                try
                {
                    var map=Owned(observed.MapId);
                    bool stillApproved=Locked(()=>Read().Jobs.Any(x=>
                        x.MapId==observed.MapId&&x.Kind==observed.Kind&&
                        x.Enabled&&x.Fingerprint==observed.Fingerprint&&
                        x.LastClaimedUtc==now&&x.LastStatus=="claimed-no-auto-retry"));
                    if(stillApproved&&safeMap(map)&&Fingerprint(map)==observed.Fingerprint)
                    {
                        evidence=await runner(map.Id,observed.Kind,token);
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
                                    LastStatus=status,LastEvidence=evidence
                                };
                                Write(state);
                            }
                            return true;
                        });
                    }
                    catch {Volatile.Write(ref runtimeState,"journal-write-needs-operator");}
                }
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
                    try
                    {
                        await TickOnceAsync(DateTimeOffset.UtcNow,cancel.Token);
                        Volatile.Write(ref runtimeState,"active");
                    }
                    catch(OperationCanceledException) when(cancel.IsCancellationRequested){break;}
                    catch(Exception){Volatile.Write(ref runtimeState,"journal-or-operation-error");}
                } while(await timer.WaitForNextTickAsync(cancel.Token));
            }
            catch(OperationCanceledException){/* Manager closing, never replay. */}
            finally{Volatile.Write(ref runtimeState,"stopped");}
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
