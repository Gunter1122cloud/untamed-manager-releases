using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

// Explicitly armed automatic crash recovery for the Manager's LOCAL maps.
// Standalone supervisor remains observation-only. Native UI lifecycle
// bridges retain exact source/port/launch-mode proofs and durable leases.
// No kill, DoExit, Store conversion, update or automatic uncertain replay.
public sealed class DesktopWatchdogRecovery : IDisposable
{
    public sealed record State(Guid MapId,string Status,string ProfileHash,string LaunchHash,
        int Pid,DateTimeOffset StartedUtc,int? LoaderPid,DateTimeOffset? LoaderStartedUtc,
        DateTimeOffset ArmedUtc,DateTimeOffset? LastAttemptUtc,Guid? Claim,
        int? LaunchPid,DateTimeOffset? LaunchStartedUtc,DateTimeOffset? ReadyDeadlineUtc,
        int StoppedSamples,DateTimeOffset? LastStoppedUtc,string Evidence);
    internal interface IExitWitness : IDisposable
    {
        bool HasExited {get;}
        int ExitCode {get;}
    }
    private sealed class WindowsExitWitness : IExitWitness
    {
        private readonly Process process;
        public WindowsExitWitness(ProcessIdentity identity)
        {
            process=Process.GetProcessById(identity.Pid);
            try
            {
                // Force a retained Windows handle, not a later query by PID.
                _=process.SafeHandle;
                if(process.HasExited||new DateTimeOffset(process.StartTime.ToUniversalTime())!=identity.StartedUtc||
                    !string.Equals(process.MainModule?.FileName,identity.ExecutablePath,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("UM-WATCHDOG-HANDLE-OWNER");
            }
            catch{process.Dispose();throw;}
        }
        public bool HasExited=>process.HasExited;
        public int ExitCode=>process.ExitCode;
        public void Dispose()=>process.Dispose();
    }
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim hostGate;
    private readonly Func<bool> tryEnterMaintenanceLane;
    private readonly Action releaseMaintenanceLane;
    private readonly Func<bool> handoff;
    private readonly Func<LocalMap,ProcessCheck> observe;
    private readonly Func<ProcessIdentity,IExitWitness> witness;
    private readonly Func<LocalMap,string> validateLaunchHash;
    private readonly Func<LocalMap,ProcessCheck,Task<bool>> ready;
    private readonly Func<LocalMap,Task<(int Pid,DateTimeOffset Started)>> launch;
    private readonly string directory;
    private readonly Dictionary<Guid,IExitWitness> handles=new();
    private readonly Dictionary<Guid,(int Pid,DateTimeOffset Start)> epochs=new();
    private readonly CancellationTokenSource cancel=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private int started;
    private volatile bool closing;
    internal DesktopWatchdogRecovery(ClusterService cluster,SemaphoreSlim hostGate,
        Func<bool> tryEnterMaintenanceLane,Action releaseMaintenanceLane,Func<bool> handoff,
        Func<string,Task<string>> freshStart,Func<string,Task<string>> historicalStart,
        Func<string,Task<string>> historicalReadOnly,
        Func<LocalMap,ProcessCheck>? isolatedObservation=null,
        Func<ProcessIdentity,IExitWitness>? isolatedWitness=null,
        Func<LocalMap,string>? isolatedLaunchHash=null,
        Func<LocalMap,ProcessCheck,Task<bool>>? isolatedReady=null,
        Func<LocalMap,Task<(int Pid,DateTimeOffset Started)>>? isolatedLaunch=null)
    {
        this.cluster=cluster;this.hostGate=hostGate;
        this.tryEnterMaintenanceLane=tryEnterMaintenanceLane;this.releaseMaintenanceLane=releaseMaintenanceLane;
        this.handoff=handoff;
        directory=Path.Combine(cluster.Store.Root,"watchdog-recovery-v1");
        _=UntamedOperationCoordinator.ResourceKey(directory);
        observe=isolatedObservation??(map=>{
            if(!map.HistoricalStoreProtected)return cluster.Check(map);
            var state=cluster.ObserveLocalProcessesReadOnly(map);
            return !state.Running&&!state.ConfirmedStopped&&state.Identity==null&&state.LoaderIdentity==null?
                LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(map,cluster.Config.LocalMaps):state;
        });
        witness=isolatedWitness??(identity=>new WindowsExitWitness(identity));
        validateLaunchHash=isolatedLaunchHash??(map=>{
            string source=map.HistoricalStoreProtected?
                LegacyStoreV1GracefulStop.OriginalSource(cluster,map):"fresh";
            if(!AsaReadyProbe.IsRegisteredServerBinary(map)||!AsaRconConfigurationEvidence.Matches(map)||
                KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
                throw new InvalidOperationException("UM-WATCHDOG-LAUNCH-CONFIG");
            string? build=AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath);
            if(string.IsNullOrWhiteSpace(build))throw new InvalidOperationException("UM-WATCHDOG-BUILD-UNKNOWN");
            return Hash(source+"|"+build+"|"+DesktopFreshAsaStartBridge.IniFingerprint(map)+"|"+
                AsaReadyProbe.ApprovedLaunchBinariesFingerprint(map));
        });
        ready=isolatedReady??(async(map,seen)=>{
            if(!Running(map,seen)||!RconPortOwnership.IsOwnedListener(map.RconPort,seen.Identity!.Pid))return false;
            if(map.HistoricalStoreProtected)
            {
                var proof=LegacyStoreV1GracefulStop.Preview(cluster,map.Id);
                if(!Same(proof.Process,seen.Identity)||LegacyStoreV1GracefulStop.SaveOnlyStartupBlocker(proof)!=null)
                    return false;
                if(map.StartWithAsaApi)
                {
                    var log=AsaRuntimeLogEvidence.Inspect(map,seen.Identity,seen.LoaderIdentity);
                    if(!log.ApiInitialized||!AsaApiModuleEvidence.IsLoaded(map,seen.Identity))return false;
                }
                var reply=Decode(await historicalReadOnly(Message("historical.rconPlayers",new{mapId=map.Id})));
                return reply.GetProperty("mapId").GetGuid()==map.Id&&reply.GetProperty("readOnly").GetBoolean();
            }
            var data=Decode(await freshStart(Message("servers.freshReadiness",new{mapId=map.Id})));
            return data.GetProperty("mapId").GetGuid()==map.Id&&data.GetProperty("productionReady").GetBoolean();
        });
        launch=isolatedLaunch??(async map=>{
            Func<string,Task<string>> invoke=map.HistoricalStoreProtected?historicalStart:freshStart;
            string prefix=map.HistoricalStoreProtected?"historical.activeStart":"servers.freshStart";
            var pre=Decode(await invoke(Message(prefix+"Preview",new{mapId=map.Id})));
            if(pre.GetProperty("mapId").GetGuid()!=map.Id||
                pre.GetProperty("launchMode").GetString()!=(map.StartWithAsaApi?"asaApiLoader":"directAsa"))
                throw new InvalidDataException("UM-WATCHDOG-PREFLIGHT-MISMATCH");
            if(closing||handoff())throw new InvalidOperationException("UM-WATCHDOG-CLOSING");
            var applied=Decode(await invoke(Message(prefix+"Apply",new{
                token=pre.GetProperty("token").GetGuid(),confirm=true})));
            if(applied.GetProperty("mapId").GetGuid()!=map.Id||
                !applied.GetProperty("startRequested").GetBoolean()||
                applied.GetProperty("launchMode").GetString()!=(map.StartWithAsaApi?"asaApiLoader":"directAsa"))
                throw new InvalidDataException("UM-WATCHDOG-LAUNCH-RECEIPT");
            return (applied.GetProperty("pid").GetInt32(),applied.GetProperty("createdUtc").GetDateTimeOffset());
        });
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string ProfileHash(LocalMap map)
    {
        // Native start and API child reattachment may legitimately update
        // only these identities; every other profile field remains pinned.
        var node=JsonSerializer.SerializeToNode(map)!.AsObject();
        node.Remove(nameof(LocalMap.LastProcess));node.Remove(nameof(LocalMap.LastLoader));
        return Hash(node.ToJsonString());
    }
    private LocalMap Owned(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id)??
        throw new UnauthorizedAccessException("UM-WATCHDOG-LOCAL-ONLY");
    private static bool Same(ProcessIdentity? a,ProcessIdentity? b)=>a!=null&&b!=null&&
        a.Pid==b.Pid&&a.StartedUtc==b.StartedUtc&&a.ExecutablePath.Equals(b.ExecutablePath,StringComparison.OrdinalIgnoreCase);
    private static bool Running(LocalMap map,ProcessCheck state)=>state.Running&&!state.ConfirmedStopped&&
        state.Identity is {Pid:>0}&&(!map.StartWithAsaApi||state.LoaderIdentity is {Pid:>0})&&
        (!map.StartWithAsaApi||state.LoaderIdentity!.Pid!=state.Identity.Pid);
    private static bool Stopped(ProcessCheck state)=>state.ConfirmedStopped&&!state.Running&&
        state.Identity==null&&state.LoaderIdentity==null;
    private static bool SameFamily(State state,ProcessCheck now)=>now.Identity?.Pid==state.Pid&&
        now.Identity.StartedUtc==state.StartedUtc&&now.LoaderIdentity?.Pid==state.LoaderPid&&
        now.LoaderIdentity?.StartedUtc==state.LoaderStartedUtc;
    // Only Windows fatal exception/abort results are crash evidence. A clean
    // exit, arbitrary nonzero code, lost handle or RCON-only failure is not.
    internal static bool FatalExitCode(int code)=>unchecked((uint)code) is
        0xC0000005 or 0xC000001D or 0xC0000094 or 0xC0000096 or 0xC00000FD or
        0xC0000374 or 0xC0000409 or 0x80000003 or 0xE06D7363;
    private void CheckPath(string path)
    {
        _=UntamedOperationCoordinator.ResourceKey(directory);
        if(Directory.Exists(path)||(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0))
            throw new InvalidDataException("UM-WATCHDOG-LINKED-JOURNAL");
    }
    private string FileFor(Guid id)=>Path.Combine(directory,id.ToString("N")+".json");
    private FileStream Lock(Guid id)
    {
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,id.ToString("N")+".lock");CheckPath(path);
        return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    private State? Read(Guid id)
    {
        string path=FileFor(id);CheckPath(path);
        if(!File.Exists(path))return null;
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(input.Length is <2 or >32768)throw new InvalidDataException("UM-WATCHDOG-JOURNAL");
        var row=JsonSerializer.Deserialize<State>(input,Json);
        if(row==null||row.MapId!=id||row.ProfileHash==null||row.LaunchHash==null||
            row.ProfileHash.Length!=64||row.LaunchHash.Length!=64||row.Pid<=0||
            row.StartedUtc==default||row.ArmedUtc==default||row.StoppedSamples is <0 or >3||
            row.Status is not ("armed" or "disarmed" or "claimed" or "awaitingReady" or "needsReview"))
            throw new InvalidDataException("UM-WATCHDOG-JOURNAL");
        return row;
    }
    private void Write(State row)
    {
        string path=FileFor(row.MapId);CheckPath(path);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {JsonSerializer.Serialize(output,row,Json);output.Flush(true);}
            File.Move(temp,path,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private void Drop(Guid map)
    {
        if(handles.Remove(map,out var held))held.Dispose();
        epochs.Remove(map);
    }
    private void Attach(Guid id,ProcessIdentity identity)
    {
        Drop(id);handles[id]=witness(identity);epochs[id]=(identity.Pid,identity.StartedUtc);
    }
    private void End(State row,string status,string evidence,bool notify=false)
    {
        Write(row with{Status=status,Evidence=evidence});Drop(row.MapId);
        if(notify)DesktopDiscordNotifications.Record(cluster,row.MapId,"recovery",true);
    }
    private void DemandProfile(LocalMap map,State row,bool disk)
    {
        if(map.Maintenance||map.ObservedReadOnlyImported||map.HistoricalLaunchModeNeedsApproval||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||ProfileHash(map)!=row.ProfileHash)
            throw new InvalidOperationException("UM-WATCHDOG-PROFILE-CHANGED");
        if(disk)
        {
            var saved=cluster.Store.Read();
            var local=saved.LocalMaps.SingleOrDefault(m=>m.Id==map.Id);
            if(saved.LocalHostId!=cluster.Config.LocalHostId||local==null||ProfileHash(local)!=row.ProfileHash||
                validateLaunchHash(map)!=row.LaunchHash)
                throw new InvalidOperationException("UM-WATCHDOG-SOURCE-CHANGED");
        }
    }
    private void DemandNoPending(LocalMap map)
    {
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        new AsaManualStopJournal(map.InstallationPath).DemandNoPending(map.Id);
        var op=cluster.Operations.Observe(map.InstallationPath);
        if(op?.Phase is UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)
            throw new InvalidOperationException("UM-WATCHDOG-OPERATION-PENDING");
        if(map.HistoricalStoreProtected&&LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id)?.Phase is "prepared" or "needsOperator")
            throw new InvalidOperationException("UM-WATCHDOG-SAVE-PENDING");
    }
    private static string Message(string method,object fields)
    {
        var items=new Dictionary<string,object?>{["id"]=Guid.NewGuid(),["method"]=method};
        foreach(var field in JsonSerializer.SerializeToElement(fields,Json).EnumerateObject())
            items[field.Name]=field.Value.Clone();
        return JsonSerializer.Serialize(items,Json);
    }
    private static JsonElement Decode(string raw)
    {
        using var doc=JsonDocument.Parse(raw);
        if(!doc.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("UM-WATCHDOG-NATIVE-REJECTED");
        return doc.RootElement.GetProperty("data").Clone();
    }
    private static string Ok(string? id,object data)=>JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>JsonSerializer.Serialize(new{id,ok=false,error},Json);
    private static object Data(State? row,LocalMap map)=>new{mapId=map.Id,mapName=map.Name,
        status=row?.Status??"disarmed",evidence=row?.Evidence??"NOT_ARMED",
        lastAttemptUtc=row?.LastAttemptUtc,claim=row?.Claim,pid=row?.Pid,loaderPid=row?.LoaderPid,
        launchMode=map.StartWithAsaApi?"asaApiLoader":"directAsa",storeV1=map.HistoricalStoreProtected,
        stoppedSamples=row?.StoppedSamples??0,managerRequired=true};
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)return Fail(id,"UM-WATCHDOG-REQUEST");
            using var doc=JsonDocument.Parse(raw);var a=doc.RootElement;
            id=a.GetProperty("id").GetGuid().ToString("D");
            string? method=a.GetProperty("method").GetString();
            if(method=="recovery.status")
                return Ok(id,new{running=Volatile.Read(ref started)!=0&&!closing,
                    maps=cluster.Config.LocalMaps.Select(m=>Data(Read(m.Id),m)).ToArray()});
            if(method is not ("recovery.arm" or "recovery.disarm"))return Fail(id,"UM-METHOD-DENIED");
            if(closing||handoff()||!a.TryGetProperty("confirm",out var yes)||yes.ValueKind!=JsonValueKind.True)
                return Fail(id,"UM-WATCHDOG-CONFIRM");
            var map=Owned(a.GetProperty("mapId").GetGuid());
            using var held=Lock(map.Id);
            var old=Read(map.Id);
            if(method=="recovery.disarm")
            {
                if(old!=null)End(old,"disarmed","OPERATOR_DISABLED_NO_SERVER_STOP");
                else Drop(map.Id);
                return Ok(id,Data(Read(map.Id),map));
            }
            if(old?.Status is "claimed" or "awaitingReady")
                return Fail(id,"UM-WATCHDOG-ATTEMPT-IN-PROGRESS");
            if(map.Maintenance||map.ObservedReadOnlyImported||map.HistoricalLaunchModeNeedsApproval||
                !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
                map.RconHost!="127.0.0.1"||map.RconPort is <1024 or >65535||
                string.IsNullOrWhiteSpace(map.RconPasswordProtected))
                return Fail(id,"UM-WATCHDOG-LOCAL-PREFLIGHT");
            DemandNoPending(map);
            var before=observe(map);
            if(!Running(map,before)||!await ready(map,before))
                return Fail(id,"UM-WATCHDOG-ARM-REQUIRES-READY-ASA");
            var after=observe(map);
            if(!Running(map,after)||!Same(before.Identity,after.Identity)||
                (map.StartWithAsaApi&&!Same(before.LoaderIdentity,after.LoaderIdentity)))
                return Fail(id,"UM-WATCHDOG-ARM-IDENTITY-CHANGED");
            var now=DateTimeOffset.UtcNow;
            var row=new State(map.Id,"armed",ProfileHash(map),validateLaunchHash(map),
                after.Identity!.Pid,after.Identity.StartedUtc,after.LoaderIdentity?.Pid,after.LoaderIdentity?.StartedUtc,
                now,old?.LastAttemptUtc,null,null,null,null,0,null,"READY_OWNED_FAMILY_ARMED");
            DemandProfile(map,row,true);
            if(RecoveryStopIntent.Suppresses(map,row.Pid,row.StartedUtc,row.ArmedUtc))
                return Fail(id,"UM-WATCHDOG-PREVIOUS-STOP-INTENT");
            Attach(map.Id,after.Identity);
            Write(row);
            return Ok(id,Data(row,map));
        }
        catch(Exception ex){return Fail(id,NativeLifecycleFailureCode.From(ex));}
    }
    // Only called while the actual host command gate is held. This serializes
    // arm/disarm, native user lifecycle commands, and Manager update handoff.
    internal async Task TickOnceAsync(DateTimeOffset now,CancellationToken token=default)
    {
        if(closing||handoff()||!await hostGate.WaitAsync(0,token))return;
        try
        {
            if(closing||handoff())return;
            foreach(var map in cluster.Config.LocalMaps.ToArray())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if(!File.Exists(FileFor(map.Id)))continue;
                    using var held=Lock(map.Id);
                    var row=Read(map.Id);
                    if(row==null||row.Status is "disarmed" or "needsReview")continue;
                    if(row.Status=="claimed")
                    {
                        End(row,"needsReview","INTERRUPTED_DURABLE_CLAIM_NO_REPLAY",true);continue;
                    }
                    try{DemandProfile(map,row,false);}
                    catch{End(row,"disarmed","MAINTENANCE_OR_PROFILE_CHANGE");continue;}
                    if(RecoveryStopIntent.Suppresses(map,row.Pid,row.StartedUtc,row.ArmedUtc))
                    {End(row,"disarmed","INTENTIONAL_STOP_OR_RESTART");continue;}
                    var state=observe(map);
                    if(row.Status=="awaitingReady")
                    {
                        if(row.ReadyDeadlineUtc==null||now>=row.ReadyDeadlineUtc)
                        {End(row,"needsReview","RECOVERY_READINESS_TIMEOUT_NO_RETRY",true);continue;}
                        var epoch=state.Identity?.Pid==row.LaunchPid?state.Identity:
                            state.LoaderIdentity?.Pid==row.LaunchPid?state.LoaderIdentity:null;
                        if(Stopped(state))
                        {End(row,"needsReview","RECOVERY_PROCESS_EXITED_NO_RETRY",true);continue;}
                        if(!Running(map,state)||epoch==null||epoch.StartedUtc!=row.LaunchStartedUtc)continue;
                        // Read-only readiness may legitimately fail during
                        // initial startup. Never send a second Start.
                        bool verified=false;
                        try{verified=await ready(map,state);}catch{}
                        var confirmed=observe(map);
                        if(!verified||!Running(map,confirmed)||!Same(state.Identity,confirmed.Identity)||
                           (map.StartWithAsaApi&&!Same(state.LoaderIdentity,confirmed.LoaderIdentity)))continue;
                        Attach(map.Id,confirmed.Identity!);
                        Write(row with{Status="armed",Pid=confirmed.Identity!.Pid,StartedUtc=confirmed.Identity.StartedUtc,
                            LoaderPid=confirmed.LoaderIdentity?.Pid,LoaderStartedUtc=confirmed.LoaderIdentity?.StartedUtc,
                            ArmedUtc=now,StoppedSamples=0,LastStoppedUtc=null,Evidence="RECOVERED_NEW_PID_RCON_READY_GAME_CLIENT_UNVERIFIED"});
                        DesktopDiscordNotifications.Record(cluster,map.Id,"recovery",false);
                        continue;
                    }
                    if(!handles.TryGetValue(map.Id,out var exit)||!epochs.TryGetValue(map.Id,out var original)||
                        original!=(row.Pid,row.StartedUtc))
                    {
                        // Restarting Manager can reattach to a still-running
                        // family, but cannot reconstruct a lost exit witness.
                        if(Running(map,state)&&SameFamily(row,state))
                        {Attach(map.Id,state.Identity!);continue;}
                        End(row,"needsReview","NO_RETAINED_EXIT_HANDLE_NO_AUTOSTART",true);continue;
                    }
                    if(Running(map,state))
                    {
                        if(!SameFamily(row,state)||exit.HasExited)
                            End(row,"disarmed","DIFFERENT_PROCESS_FAMILY_REARM_REQUIRED");
                        else if(row.StoppedSamples!=0)Write(row with{StoppedSamples=0,LastStoppedUtc=null});
                        continue;
                    }
                    if(!exit.HasExited)
                    {
                        if(row.StoppedSamples!=0)Write(row with{StoppedSamples=0,LastStoppedUtc=null});
                        continue; // never kill an RCON-unreachable or partial family
                    }
                    if(!FatalExitCode(exit.ExitCode))
                    {End(row,"disarmed","CLEAN_OR_UNCLASSIFIED_EXIT_NO_AUTOSTART");continue;}
                    if(!Stopped(state))
                    {
                        if(row.StoppedSamples!=0)Write(row with{StoppedSamples=0,LastStoppedUtc=null});
                        continue; // API parent must also have exited
                    }
                    if(row.LastStoppedUtc.HasValue&&now<=row.LastStoppedUtc.Value.AddSeconds(9))continue;
                    int count=Math.Min(3,row.StoppedSamples+1);
                    row=row with{StoppedSamples=count,LastStoppedUtc=now,Evidence="FATAL_EXIT_CONFIRMED_STOP_SAMPLING"};
                    Write(row);
                    if(count<3)continue;
                    if(row.LastAttemptUtc.HasValue&&now<row.LastAttemptUtc.Value.AddMinutes(30))
                    {End(row,"needsReview","RECOVERY_COOLDOWN_NO_CRASH_LOOP",true);continue;}
                    // Freeze independent automatic lanes before final source
                    // proof. Existing native launch also acquires the map's
                    // machine-wide installation lease and rechecks all ports.
                    if(!tryEnterMaintenanceLane())continue;
                    try
                    {
                        DemandProfile(map,row,true);DemandNoPending(map);
                        if(RecoveryStopIntent.Suppresses(map,row.Pid,row.StartedUtc,row.ArmedUtc)||
                            !Stopped(observe(map))||!exit.HasExited||!FatalExitCode(exit.ExitCode))
                        {End(row,"disarmed","FINAL_STOP_OR_OWNER_CHANGED");continue;}
                        var claimed=row with{Status="claimed",Claim=Guid.NewGuid(),LastAttemptUtc=now,
                            Evidence="DURABLE_CRASH_RECOVERY_CLAIM"};
                        Write(claimed);Drop(map.Id);
                        try
                        {
                            var child=await launch(map);
                            if(child.Pid<=0||child.Started==default)
                                throw new InvalidDataException("UM-WATCHDOG-LAUNCH-IDENTITY");
                            Write(claimed with{Status="awaitingReady",LaunchPid=child.Pid,LaunchStartedUtc=child.Started,
                                ReadyDeadlineUtc=DateTimeOffset.UtcNow.AddMinutes(12),Evidence="NATIVE_START_DISPATCHED_WAITING_READY"});
                        }
                        catch{End(claimed,"needsReview","RECOVERY_START_UNCERTAIN_NO_RETRY",true);}
                    }
                    catch{End(row,"needsReview","RECOVERY_PREFLIGHT_REJECTED_NO_RETRY",true);}
                    finally{releaseMaintenanceLane();}
                }
                catch(Exception ex) when(ex is not OperationCanceledException)
                {
                    // A damaged or busy journal must never grant permission.
                    // If a durable claim already exists, later ticks will
                    // classify it as review-only, not dispatch again.
                }
            }
        }
        finally{hostGate.Release();}
    }
    public void Start()
    {
        if(Interlocked.Exchange(ref started,1)!=0)return;
        _=Task.Run(async()=>{
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));
            try{while(await timer.WaitForNextTickAsync(cancel.Token))
                {try{await TickOnceAsync(DateTimeOffset.UtcNow,cancel.Token);}catch(OperationCanceledException){throw;}catch{}}
            }catch(OperationCanceledException){}
        });
    }
    // Called under the same host gate during Manager update. Do not interrupt
    // an automatic launch's pending readiness receipt.
    internal bool CanManagerUpdate()
    {
        try{return cluster.Config.LocalMaps.All(m=>Read(m.Id)?.Status is not ("claimed" or "awaitingReady"));}
        catch{return false;}
    }
    public void Dispose()
    {
        closing=true;cancel.Cancel();
        // No asynchronous server operation is cancelled or replayed by this
        // method; OS closes retained observation handles on process exit.
        if(hostGate.Wait(0))
        {try{foreach(var id in handles.Keys.ToArray())Drop(id);}finally{hostGate.Release();}}
    }
}
