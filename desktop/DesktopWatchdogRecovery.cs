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
        int StoppedSamples,DateTimeOffset? LastStoppedUtc,string Evidence,
        bool? Enabled=null,string? AsaPath=null,string? LoaderPath=null,
        Guid? TerminalClaim=null,string? TerminalResult=null,string? TerminalEvidence=null,DateTimeOffset? TerminalUtc=null,
        bool TerminalPublicationPending=false,DateTimeOffset? CrashConfirmedUtc=null);
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
    private readonly bool productionReadiness;
    private readonly Func<LocalMap,Task<(int Pid,DateTimeOffset Started)>> launch;
    private readonly string directory;
    private readonly DiscordOperationEvents events;
    private readonly Dictionary<Guid,DateTimeOffset> lastChecked=new();
    // WMI is queried on every production tick; never count a cached returned
    // ProcessCheck instance as several independent stop observations.
    private readonly Dictionary<Guid,ProcessCheck> lastStoppedObservation=new();
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
        events=new DiscordOperationEvents(cluster);
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
        productionReadiness=isolatedReady==null;
        ready=isolatedReady??(async(map,seen)=>{
            if(!Running(map,seen))return false;
            int pid=seen.Identity!.Pid;
            if(!RconPortOwnership.IsOwnedListener(map.RconPort,pid)||
                AsaReadyProbe.HasPortConflict(map,cluster.Config.LocalMaps)||
                AsaReadyProbe.DeclaredPortMismatch(map))return false;
            string udpEvidence=UdpReadinessEvidence(
                RconPortOwnership.InspectUdpPort(map.GamePort,pid),
                RconPortOwnership.InspectUdpPort(map.QueryPort,pid));
            // Query UDP is used for server discovery. A missing Query listener
            // is a degraded-service warning, not evidence that a live, RCON-
            // responsive ASA process cannot be monitored after a fatal crash.
            // A foreign owner or unreadable Windows ownership table still fails closed.
            if(!UdpReadinessAccepted(udpEvidence))return false;
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
        // Native Start may clear the submitted-mod draft marker after verified
        // ordered launch parity. The actual ordered mod/launch arguments remain
        // hashed, so this bookkeeping transition cannot invalidate rearm.
        node.Remove(nameof(LocalMap.LastProcess));node.Remove(nameof(LocalMap.LastLoader));
        node.Remove(nameof(LocalMap.ModChangesPending));
        return Hash(node.ToJsonString());
    }
    private LocalMap Owned(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id)??
        throw new UnauthorizedAccessException("UM-WATCHDOG-LOCAL-ONLY");
    private static bool Same(ProcessIdentity? a,ProcessIdentity? b)=>a!=null&&b!=null&&
        a.Pid==b.Pid&&a.StartedUtc==b.StartedUtc&&a.ExecutablePath.Equals(b.ExecutablePath,StringComparison.OrdinalIgnoreCase);
    private static bool Running(LocalMap map,ProcessCheck state)=>state.Running&&!state.ConfirmedStopped&&
        state.Identity is {Pid:>0}&&(!map.StartWithAsaApi||state.LoaderIdentity is {Pid:>0})&&
        (!map.StartWithAsaApi||state.LoaderIdentity!.Pid!=state.Identity.Pid);

    // Game UDP is required for the actual player transport. Query UDP is a
    // discovery endpoint: absence is surfaced as a warning but does not stop
    // crash monitoring when process identity, Game UDP, RCON and runtime logs
    // independently prove a healthy server. Never tolerate a foreign owner or
    // an unreadable owner table, and retain the separate configured-port collision check.
    internal static string UdpReadinessEvidence(RconPortOwnership.UdpOwnership game,
        RconPortOwnership.UdpOwnership query)
    {
        if(game!=RconPortOwnership.UdpOwnership.Owned)
            return game switch{
                RconPortOwnership.UdpOwnership.Missing=>"UM-WATCHDOG-GAME-UDP-MISSING",
                RconPortOwnership.UdpOwnership.ForeignOrAmbiguous=>"UM-WATCHDOG-GAME-UDP-FOREIGN",
                _=>"UM-WATCHDOG-GAME-UDP-UNVERIFIED"
            };
        if(query==RconPortOwnership.UdpOwnership.ForeignOrAmbiguous)
            return "UM-WATCHDOG-QUERY-UDP-FOREIGN";
        if(query==RconPortOwnership.UdpOwnership.Unverified)
            return "UM-WATCHDOG-QUERY-UDP-UNVERIFIED";
        return query==RconPortOwnership.UdpOwnership.Missing
            ?"READY_GAME_RCON_QUERY_UDP_MISSING":"READY_OWNED_FAMILY_ARMED";
    }
    internal static bool UdpReadinessAccepted(string evidence)=>
        evidence.StartsWith("READY_",StringComparison.Ordinal);

    private static bool Stopped(ProcessCheck state)=>state.ConfirmedStopped&&!state.Running&&
        state.Identity==null&&state.LoaderIdentity==null;
    private static bool SameFamily(State state,ProcessCheck now)=>now.Identity?.Pid==state.Pid&&
        now.Identity.StartedUtc==state.StartedUtc&&now.LoaderIdentity?.Pid==state.LoaderPid&&
        now.LoaderIdentity?.StartedUtc==state.LoaderStartedUtc&&
        (state.AsaPath==null||string.Equals(state.AsaPath,now.Identity.ExecutablePath,StringComparison.OrdinalIgnoreCase))&&
        (state.LoaderPath==null||string.Equals(state.LoaderPath,now.LoaderIdentity?.ExecutablePath,StringComparison.OrdinalIgnoreCase));
    // A retained handle to an armed, previously running ASA gives the real
    // exit result. A nonzero exit is unexpected unless Windows reports a
    // console/operator close. Explicit Stop intent is checked independently
    // before classifying the exit and again at the native Start boundary.
    internal static bool FatalExitCode(int code)
    {
        uint status=unchecked((uint)code);
        return status!=0 && status!=0xC000013A &&
            status!=0x40010004 && status!=0x40010005;
    }
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
            row.Status is not ("armed" or "disarmed" or "claimed" or "awaitingReady" or "needsReview" or "suspended" or "externalSuspended"))
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
        lastStoppedObservation.Remove(map);
    }
    private void Attach(Guid id,ProcessIdentity identity)
    {
        Drop(id);handles[id]=witness(identity);epochs[id]=(identity.Pid,identity.StartedUtc);
    }
    private void End(State row,string status,string evidence,bool notify=false)
    {
        var next=row with{Status=status,Evidence=evidence,Enabled=status=="disarmed"?false:Enabled(row)};
        if(notify&&row.Claim.HasValue)next=TerminalReceipt(next,row.Claim.Value,"uncertain",evidence);
        Write(next);Drop(row.MapId);
        FlushTerminal(next);
    }
    private static State TerminalReceipt(State next,Guid claim,string result,string evidence)=>next with{
        TerminalClaim=claim,TerminalResult=result,TerminalEvidence=evidence,TerminalUtc=DateTimeOffset.UtcNow,
        TerminalPublicationPending=true,Claim=null,CrashConfirmedUtc=null};
    // Source receipt and pending publication are committed together. Retrying
    // telemetry can never replay native Start. Original finish time is retained.
    private bool FlushTerminal(State row)
    {
        if(!row.TerminalPublicationPending)return true;
        if(!row.TerminalClaim.HasValue||!row.TerminalUtc.HasValue||row.TerminalResult==null||row.TerminalEvidence==null)
            return false;
        if(row.TerminalResult=="verified-local"&&!events.Record(row.TerminalClaim.Value,new[]{row.MapId},"Recovery","recovery",null,"readiness",
            row.TerminalResult,row.TerminalEvidence,row.TerminalUtc.Value))return false;
        if(!events.Record(row.TerminalClaim.Value,new[]{row.MapId},"Recovery","recovery",null,"terminal",
            row.TerminalResult,row.TerminalEvidence,row.TerminalUtc.Value))return false;
        Write(row with{TerminalPublicationPending=false});return true;
    }
    private static bool Enabled(State row)=>row.Enabled??row.Status!="disarmed";
    private void Publish(State row,string phase,string result,string evidence,DateTimeOffset? observedUtc=null)
    {
        if(row.Claim.HasValue)events.Record(row.Claim.Value,new[]{row.MapId},"Recovery","recovery",null,phase,result,evidence,observedUtc??DateTimeOffset.UtcNow);
    }
    private void DemandProfile(LocalMap map,State row,bool disk)
    {
        if(map.Maintenance||map.ObservedReadOnlyImported||map.HistoricalLaunchModeNeedsApproval||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||ProfileHash(map)!=row.ProfileHash)
            throw new InvalidOperationException("UM-WATCHDOG-PROFILE-CHANGED");
        if(disk)
        {
            var saved=new ConfigStore("watchdog-proof",cluster.Store.Root).Read();
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
    private static string NextAction(State? row)=>row?.Status switch
    {
        null or "disarmed" => "READY_MAP_REQUIRED_TO_ENABLE",
        "armed" when row?.CrashConfirmedUtc.HasValue==true => "WAIT_FOR_THREE_FRESH_STOP_SAMPLES",
        "armed" => "NO_ACTION_PROTECTION_ACTIVE",
        "claimed" or "awaitingReady" => "DO_NOT_RETRY_NATIVE_START",
        "suspended" => "WAIT_FOR_TRUSTED_NATIVE_START_AND_READINESS",
        "externalSuspended" => "FINISH_EXTERNAL_MAINTENANCE_THEN_ARM_ON_READY_MAP",
        "needsReview" when row.Evidence=="RECOVERY_READINESS_TIMEOUT_NO_RETRY" => "REVIEW_LISTENERS_RCON_AND_CURRENT_STARTUP_LOG",
        "needsReview" when row.Evidence is "RECOVERY_START_UNCERTAIN_NO_RETRY" or "INTERRUPTED_DURABLE_CLAIM_NO_REPLAY"
            => "RECONCILE_NATIVE_START_JOURNAL_AND_RUNNING_PROCESSES",
        "needsReview" when row.Evidence=="RECOVERY_COOLDOWN_NO_CRASH_LOOP" => "REVIEW_REPEATED_CRASH_AND_COOLDOWN",
        "needsReview" => "MANUAL_NATIVE_JOURNAL_AND_PROCESS_REVIEW",
        _ => "REVIEW_WATCHDOG_STATUS"
    };
    private object Data(State? row,LocalMap map)=>new{mapId=map.Id,mapName=map.Name,
        enabled=row!=null&&Enabled(row),nextAction=NextAction(row),lastCheckedUtc=lastChecked.TryGetValue(map.Id,out var checkedUtc)?(DateTimeOffset?)checkedUtc:null,
        nextAttemptUtc=row?.LastAttemptUtc?.AddMinutes(30),terminalPublicationPending=row?.TerminalPublicationPending??false,lastResultUtc=row?.TerminalUtc,lastResult=row?.TerminalResult,lastResultEvidence=row?.TerminalEvidence,
        status=row?.Status??"disarmed",evidence=row?.Evidence??"NOT_ARMED",crashConfirmedUtc=row?.CrashConfirmedUtc,
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
            if(method is not ("recovery.arm" or "recovery.disarm" or "recovery.suspend"))return Fail(id,"UM-METHOD-DENIED");
            if(closing||handoff()||!a.TryGetProperty("confirm",out var yes)||yes.ValueKind!=JsonValueKind.True)
                return Fail(id,"UM-WATCHDOG-CONFIRM");
            var map=Owned(a.GetProperty("mapId").GetGuid());
            using var held=Lock(map.Id);
            var old=Read(map.Id);
            if(old!=null&&!FlushTerminal(old))return Fail(id,"UM-WATCHDOG-EVENT-PUBLICATION-PENDING");
            old=Read(map.Id);
            if(old?.Status is "claimed" or "awaitingReady")
                return Fail(id,"UM-WATCHDOG-ATTEMPT-IN-PROGRESS");
            if(method=="recovery.suspend")
            {
                if(old!=null&&Enabled(old))End(old,"externalSuspended","OPERATOR_EXTERNAL_MAINTENANCE_NO_AUTOSTART",old.Claim.HasValue);
                return Ok(id,Data(Read(map.Id),map));
            }
            if(method=="recovery.disarm")
            {
                if(old!=null)End(old,"disarmed","OPERATOR_DISABLED_NO_SERVER_STOP",old.Claim.HasValue);
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
            if(!Running(map,before))
                return Fail(id,"UM-WATCHDOG-ARM-REQUIRES-READY-ASA");
            string armEvidence="READY_OWNED_FAMILY_ARMED";
            if(productionReadiness)
            {
                if(AsaReadyProbe.HasPortConflict(map,cluster.Config.LocalMaps)||
                    AsaReadyProbe.DeclaredPortMismatch(map))
                    return Fail(id,"UM-WATCHDOG-ARM-PORT-CONFIG");
                armEvidence=UdpReadinessEvidence(
                    RconPortOwnership.InspectUdpPort(map.GamePort,before.Identity!.Pid),
                    RconPortOwnership.InspectUdpPort(map.QueryPort,before.Identity.Pid));
                if(!UdpReadinessAccepted(armEvidence))return Fail(id,armEvidence);
            }
            if(!await ready(map,before))
                return Fail(id,"UM-WATCHDOG-ARM-REQUIRES-READY-ASA");
            var after=observe(map);
            if(!Running(map,after)||!Same(before.Identity,after.Identity)||
                (map.StartWithAsaApi&&!Same(before.LoaderIdentity,after.LoaderIdentity)))
                return Fail(id,"UM-WATCHDOG-ARM-IDENTITY-CHANGED");
            if(productionReadiness)
            {
                if(AsaReadyProbe.HasPortConflict(map,cluster.Config.LocalMaps)||
                    AsaReadyProbe.DeclaredPortMismatch(map))
                    return Fail(id,"UM-WATCHDOG-ARM-PORT-CONFIG");
                armEvidence=UdpReadinessEvidence(
                    RconPortOwnership.InspectUdpPort(map.GamePort,after.Identity!.Pid),
                    RconPortOwnership.InspectUdpPort(map.QueryPort,after.Identity.Pid));
                if(!UdpReadinessAccepted(armEvidence))return Fail(id,armEvidence);
            }
            var now=DateTimeOffset.UtcNow;
            var row=new State(map.Id,"armed",ProfileHash(map),validateLaunchHash(map),
                after.Identity!.Pid,after.Identity.StartedUtc,after.LoaderIdentity?.Pid,after.LoaderIdentity?.StartedUtc,
                now,old?.LastAttemptUtc,null,null,null,null,0,null,armEvidence,true,
                after.Identity.ExecutablePath,after.LoaderIdentity?.ExecutablePath);
            DemandProfile(map,row,true);
            if(RecoveryStopIntent.Suppresses(map,row.Pid,row.StartedUtc,row.ArmedUtc))
                return Fail(id,"UM-WATCHDOG-PREVIOUS-STOP-INTENT");
            if(old?.Claim.HasValue==true){End(old,"needsReview","OPERATOR_REARMED_DIFFERENT_VERIFIED_FAMILY",true);if(Read(map.Id)?.TerminalPublicationPending==true)return Fail(id,"UM-WATCHDOG-EVENT-PUBLICATION-PENDING");}
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
                    if(row==null||!FlushTerminal(row))continue;
                    row=Read(map.Id)!;
                    if(!Enabled(row)||row.Status is "disarmed" or "externalSuspended")continue;
                    if(row.Status=="needsReview")
                    {
                        // Re-arm observation only after the actual local ASA
                        // is already healthy and its native journals clear.
                        // Never launch a stopped map or replay a failed claim.
                        if(row.Claim.HasValue||!tryEnterMaintenanceLane())continue;
                        try
                        {
                            DemandProfile(map,row,true);DemandNoPending(map);
                            var before=observe(map);
                            if(!Running(map,before))continue;
                            var receipt=RecoveryStopIntent.ReadNativeStart(map);
                            bool same=SameFamily(row,before);
                            bool trustedReplacement=receipt!=null&&receipt.RecordedUtc>row.ArmedUtc&&
                                receipt.StartedUtc>row.StartedUtc&&
                                RecoveryStopIntent.MatchesNativeStart(receipt,map.StartWithAsaApi?before.LoaderIdentity:before.Identity);
                            if(!same&&!trustedReplacement)continue;
                            if(RecoveryStopIntent.Suppresses(map,before.Identity!.Pid,before.Identity.StartedUtc,before.Identity.StartedUtc)||
                                !await ready(map,before))continue;
                            var after=observe(map);
                            if(!Running(map,after)||!Same(before.Identity,after.Identity)||
                                !Same(before.LoaderIdentity,after.LoaderIdentity)||
                                RecoveryStopIntent.ReadNativeStart(map)!=receipt)continue;
                            DemandProfile(map,row,true);DemandNoPending(map);
                            Attach(map.Id,after.Identity!);
                            Write(row with{Status="armed",Pid=after.Identity!.Pid,StartedUtc=after.Identity.StartedUtc,
                                LoaderPid=after.LoaderIdentity?.Pid,LoaderStartedUtc=after.LoaderIdentity?.StartedUtc,
                                AsaPath=after.Identity.ExecutablePath,LoaderPath=after.LoaderIdentity?.ExecutablePath,
                                ArmedUtc=now,StoppedSamples=0,LastStoppedUtc=null,LaunchPid=null,LaunchStartedUtc=null,
                                ReadyDeadlineUtc=null,CrashConfirmedUtc=null,Evidence="HEALTHY_NATIVE_FAMILY_REARMED_NO_START"});
                        }
                        catch{/* Changed profile or unclear ownership stays blocked. */}
                        finally{releaseMaintenanceLane();}
                        continue;
                    }
                    lastChecked[map.Id]=now;
                    if(row.CrashConfirmedUtc.HasValue&&row.Claim.HasValue)
                        Publish(row,"crash-confirmed","in-progress","RECOVERY_FATAL_EXIT_HANDLE_CONFIRMED",row.CrashConfirmedUtc);
                    if(row.Status=="claimed")
                    {
                        // Reconcile only the exact receipt written by the claimed native launch.
                        if(row.LastAttemptUtc.HasValue){
                            Publish(row,"claimed","in-progress","RECOVERY_FATAL_EXIT_CONFIRMED",row.LastAttemptUtc);
                            Publish(row,"started","in-progress","RECOVERY_DURABLE_START_CLAIM",row.LastAttemptUtc);
                        }
                        var receipt=RecoveryStopIntent.ReadNativeStart(map);
                        if(row.Claim.HasValue&&row.LastAttemptUtc.HasValue&&receipt?.RecoveryClaim==row.Claim&&
                            receipt.StartedUtc>=row.LastAttemptUtc.Value&&receipt.RecordedUtc>=row.LastAttemptUtc.Value)
                        {
                            Write(row with{Status="awaitingReady",LaunchPid=receipt.Pid,LaunchStartedUtc=receipt.StartedUtc,
                                ReadyDeadlineUtc=row.LastAttemptUtc.Value.AddMinutes(12),Evidence="RECOVERY_RECONCILED_NATIVE_START_NO_REPLAY"});
                            Publish(row,"native-dispatched","in-progress","RECOVERY_NATIVE_START_DISPATCHED",receipt.StartedUtc);
                        }
                        else End(row,"needsReview","INTERRUPTED_DURABLE_CLAIM_NO_REPLAY",true);
                        continue;
                    }
                    try{DemandProfile(map,row,false);}
                    catch{End(row,"needsReview","MAINTENANCE_OR_PROFILE_CHANGE",row.Claim.HasValue);continue;}
                    var state=observe(map);
                    bool intentional=RecoveryStopIntent.Suppresses(map,row.Pid,row.StartedUtc,row.ArmedUtc);
                    if(intentional||row.Status=="suspended")
                    {
                        if(row.Status!="suspended"){
                            End(row,"suspended",row.Claim.HasValue?"RECOVERY_INTERRUPTED_BY_STOP_INTENT":"INTENTIONAL_STOP_WAITING_TRUSTED_START",row.Claim.HasValue);
                            row=Read(map.Id)!;if(row.TerminalPublicationPending)continue;
                        }
                        // Only a native Start receipt issued AFTER the old Stop
                        // can authorize replacement of the monitored family.
                        var intent=RecoveryStopIntent.Read(map);
                        var receipt=RecoveryStopIntent.ReadNativeStart(map);
                        if(intent==null||intent.MapId!=map.Id||receipt==null||receipt.RecordedUtc<=intent.MarkedUtc||
                            receipt.StartedUtc<=intent.MarkedUtc||receipt.RecoveryClaim.HasValue||
                            (intent.RestartId.HasValue&&intent.RestartId!=receipt.RestartId)||!Running(map,state)||
                            !RecoveryStopIntent.MatchesNativeStart(receipt,map.StartWithAsaApi?state.LoaderIdentity:state.Identity))continue;
                        if(!tryEnterMaintenanceLane())continue;
                        try{
                            DemandProfile(map,row,true);DemandNoPending(map);
                            if(!await ready(map,state))continue;
                            var confirmed=observe(map);
                            if(!Running(map,confirmed)||!Same(state.Identity,confirmed.Identity)||
                                (map.StartWithAsaApi&&!Same(state.LoaderIdentity,confirmed.LoaderIdentity))||
                                RecoveryStopIntent.Read(map)!=intent||RecoveryStopIntent.ReadNativeStart(map)!=receipt)continue;
                            Attach(map.Id,confirmed.Identity!);
                            Write(row with{Status="armed",Enabled=true,Pid=confirmed.Identity!.Pid,StartedUtc=confirmed.Identity.StartedUtc,
                                LoaderPid=confirmed.LoaderIdentity?.Pid,LoaderStartedUtc=confirmed.LoaderIdentity?.StartedUtc,
                                AsaPath=confirmed.Identity.ExecutablePath,LoaderPath=confirmed.LoaderIdentity?.ExecutablePath,
                                ArmedUtc=DateTimeOffset.UtcNow,Claim=null,LaunchPid=null,LaunchStartedUtc=null,ReadyDeadlineUtc=null,
                                StoppedSamples=0,LastStoppedUtc=null,Evidence="TRUSTED_NATIVE_START_READY_REARMED"});
                        }catch{/* Pending operation/readiness remains suspended, never grants Start. */}
                        finally{releaseMaintenanceLane();}
                        continue;
                    }
                    if(row.Status=="awaitingReady")
                    {
                        if(row.LastAttemptUtc.HasValue){
                            Publish(row,"claimed","in-progress","RECOVERY_FATAL_EXIT_CONFIRMED",row.LastAttemptUtc);
                            Publish(row,"started","in-progress","RECOVERY_DURABLE_START_CLAIM",row.LastAttemptUtc);
                        }
                        if(row.LaunchStartedUtc.HasValue)Publish(row,"native-dispatched","in-progress","RECOVERY_NATIVE_START_DISPATCHED",row.LaunchStartedUtc);
                        if(row.ReadyDeadlineUtc==null||now>=row.ReadyDeadlineUtc)
                        {End(row,"needsReview","RECOVERY_READINESS_TIMEOUT_NO_RETRY",true);continue;}
                        var epoch=state.Identity?.Pid==row.LaunchPid?state.Identity:
                            state.LoaderIdentity?.Pid==row.LaunchPid?state.LoaderIdentity:null;
                        if(Stopped(state))
                        {End(row,"needsReview","RECOVERY_PROCESS_EXITED_NO_RETRY",true);continue;}
                        if(!Running(map,state)||epoch==null||epoch.StartedUtc!=row.LaunchStartedUtc)continue;
                        // Read-only readiness may legitimately fail during
                        // initial startup. Never send a second Start.
                        Publish(row,"new-process","in-progress","RECOVERY_NEW_FAMILY_OBSERVED");
                        bool verified=false;
                        try{DemandNoPending(map);verified=await ready(map,state);}catch{}
                        var confirmed=observe(map);
                        if(!verified||!Running(map,confirmed)||!Same(state.Identity,confirmed.Identity)||
                           (map.StartWithAsaApi&&!Same(state.LoaderIdentity,confirmed.LoaderIdentity)))continue;
                        Attach(map.Id,confirmed.Identity!);
                        var recovered=row with{Status="armed",Pid=confirmed.Identity!.Pid,StartedUtc=confirmed.Identity.StartedUtc,
                            LoaderPid=confirmed.LoaderIdentity?.Pid,LoaderStartedUtc=confirmed.LoaderIdentity?.StartedUtc,
                            ArmedUtc=now,StoppedSamples=0,LastStoppedUtc=null,Enabled=true,
                            AsaPath=confirmed.Identity.ExecutablePath,LoaderPath=confirmed.LoaderIdentity?.ExecutablePath,
                            Evidence="RECOVERED_NATIVE_READINESS",Claim=null,LaunchPid=null,LaunchStartedUtc=null,ReadyDeadlineUtc=null};
                        if(!row.Claim.HasValue)throw new InvalidDataException("UM-WATCHDOG-MISSING-CLAIM");
                        recovered=TerminalReceipt(recovered,row.Claim.Value,"verified-local","RECOVERY_NATIVE_READINESS_PROVEN");
                        Write(recovered);
                        FlushTerminal(recovered);
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
                            End(row,"needsReview","DIFFERENT_PROCESS_FAMILY_REARM_REQUIRED",row.Claim.HasValue);
                        else if(row.StoppedSamples!=0)Write(row with{StoppedSamples=0,LastStoppedUtc=null});
                        continue;
                    }
                    if(!exit.HasExited)
                    {
                        if(row.StoppedSamples!=0)Write(row with{StoppedSamples=0,LastStoppedUtc=null});
                        continue; // never kill an RCON-unreachable or partial family
                    }
                    if(!FatalExitCode(exit.ExitCode))
                    {End(row,"needsReview","CLEAN_OR_UNCLASSIFIED_EXIT_NO_AUTOSTART");continue;}
                    if(!row.CrashConfirmedUtc.HasValue||!row.Claim.HasValue)
                    {
                        row=row with{Claim=Guid.NewGuid(),CrashConfirmedUtc=now,StoppedSamples=0,LastStoppedUtc=null,
                            Evidence="RECOVERY_FATAL_EXIT_WAITING_FULL_STOP"};
                        Write(row);
                    }
                    Publish(row,"crash-confirmed","in-progress","RECOVERY_FATAL_EXIT_HANDLE_CONFIRMED",row.CrashConfirmedUtc);
                    if(!Stopped(state))
                    {
                        lastStoppedObservation.Remove(map.Id);
                        if(now>=row.CrashConfirmedUtc!.Value.AddMinutes(12))
                        {End(row,"needsReview","RECOVERY_OLD_FAMILY_NOT_STOPPED_NO_START",true);continue;}
                        Write(row with{StoppedSamples=0,LastStoppedUtc=null,Evidence="RECOVERY_OLD_FAMILY_NOT_STOPPED"});
                        continue; // API parent must also have exited
                    }
                    if(row.LastStoppedUtc.HasValue&&now<=row.LastStoppedUtc.Value.AddSeconds(9))continue;
                    if(lastStoppedObservation.TryGetValue(map.Id,out var priorSample)&&ReferenceEquals(priorSample,state))
                        continue;
                    lastStoppedObservation[map.Id]=state;
                    int count=Math.Min(3,row.StoppedSamples+1);
                    row=row with{StoppedSamples=count,LastStoppedUtc=now,Evidence="FATAL_EXIT_CONFIRMED_STOP_SAMPLING"};
                    Write(row);
                    if(count<3)continue;
                    if(row.LastAttemptUtc.HasValue&&now<row.LastAttemptUtc.Value.AddMinutes(30))
                    {End(row,"needsReview","RECOVERY_COOLDOWN_NO_CRASH_LOOP",true);continue;}
                    // Freeze independent automatic lanes before final source
                    // proof. Existing native launch also acquires the map's
                    // machine-wide installation lease and rechecks all ports.
                    // Readiness is per-map. A different map's pending receipt
                    // does not own this installation. Actual dispatch remains
                    // serialized by the host gate and native installation lease.
                    if(!tryEnterMaintenanceLane()){Write(row with{Evidence="RECOVERY_NATIVE_LANE_BUSY"});continue;}
                    try
                    {
                        SteamHostLease? steamLease=null;
                        try{steamLease=SteamHostLease.Acquire();}catch(IOException){Write(row with{Evidence="RECOVERY_STEAM_HOST_BUSY"});continue;}
                        using var exclusiveSteam=steamLease;
                        SteamStoppedMapUpdater.DemandNoConcurrentSteamCmd();
                        DemandProfile(map,row,true);DemandNoPending(map);
                        if(RecoveryStopIntent.Suppresses(map,row.Pid,row.StartedUtc,row.ArmedUtc)||
                            !Stopped(observe(map))||!exit.HasExited||!FatalExitCode(exit.ExitCode))
                        {End(row,"needsReview","FINAL_STOP_OR_OWNER_CHANGED",true);continue;}
                        var claimed=row with{Status="claimed",LastAttemptUtc=now,
                            Evidence="DURABLE_CRASH_RECOVERY_CLAIM"};
                        Write(claimed);Drop(map.Id);
                        Publish(claimed,"claimed","in-progress","RECOVERY_FATAL_EXIT_CONFIRMED",claimed.LastAttemptUtc);
                        Publish(claimed,"started","in-progress","RECOVERY_DURABLE_START_CLAIM",claimed.LastAttemptUtc);
                        try
                        {
                            using var boundary=RecoveryNativeStartBoundary.Pin(id=>{
                                if(id!=map.Id||closing||handoff()||RecoveryStopIntent.Suppresses(map,row.Pid,row.StartedUtc,row.ArmedUtc))
                                    throw new InvalidOperationException("UM-WATCHDOG-FINAL-INTENT");
                                DemandProfile(map,row,true);
                                if(!Stopped(observe(map)))throw new InvalidOperationException("UM-WATCHDOG-FINAL-STATE");
                            },claimed.Claim);
                            using var origin=DiscordOperationEvents.EnterOrigin("recovery",claimed.Claim,maps:new[]{map.Id},action:"Recovery",suppress:true);
                            var child=await launch(map);
                            if(child.Pid<=0||child.Started==default)
                                throw new InvalidDataException("UM-WATCHDOG-LAUNCH-IDENTITY");
                            Write(claimed with{Status="awaitingReady",LaunchPid=child.Pid,LaunchStartedUtc=child.Started,
                                ReadyDeadlineUtc=claimed.LastAttemptUtc!.Value.AddMinutes(12),Evidence="NATIVE_START_DISPATCHED_WAITING_READY"});
                            Publish(claimed,"native-dispatched","in-progress","RECOVERY_NATIVE_START_DISPATCHED",child.Started);
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
    private static bool HoldsCrashLane(State? row)=>
        row!=null&&Enabled(row)&&
        (row.Status is "claimed" or "awaitingReady"||
         row.Status=="armed"&&row.Claim.HasValue&&row.CrashConfirmedUtc.HasValue);
    internal bool HasPendingRecovery(Guid mapId)
    {
        // A proven fatal exit remains a claimed crash even while the three
        // independent stop observations are still being collected. This
        // prevents a scheduled Restart/wipe chain from racing recovery.
        try{return HoldsCrashLane(Read(mapId));}
        catch{return true;}
    }
    internal bool CanManagerUpdate()
    {
        try{return cluster.Config.LocalMaps.All(m=>!HoldsCrashLane(Read(m.Id)));}
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
