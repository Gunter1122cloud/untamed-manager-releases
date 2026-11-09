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
/// One operator-approved native workflow using the EXISTING independently
/// guarded SaveWorld/DoExit, stopped SteamCMD backup/update and fresh Start
/// bridges. It does NOT introduce an ASA backup engine, process launcher,
/// Store writer, global cross-host update lock or arbitrary remote commands.
/// </summary>
public sealed class DesktopLiveSteamMaintenanceBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,string ProfileSha,
        int Pid,DateTimeOffset PidCreated,string InitialBuild,DateTimeOffset Expires);
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid(),MapId;
        public string State="queued",Stage="queued",ProfileSha="",InitialBuild="";
        public DateTimeOffset ChangedUtc=DateTimeOffset.UtcNow;
        public bool StopVerified,UpdateVerified,StartRequested,ReadyVerified;
    }
    private readonly ClusterService cluster;
    private readonly DesktopFreshAsaStopBridge stop;
    private readonly DesktopStoppedSteamUpdateBridge steam;
    private readonly DesktopFreshAsaStartBridge start;
    private readonly Func<LocalMap,ProcessCheck> observer;
    private readonly Func<LocalMap,bool> eligible;
    private readonly Func<LocalMap,Action<string>,Task> runner;
    private readonly Func<LocalMap,string?> build;
    private readonly SemaphoreSlim serial=new(1,1);
    private readonly object sync=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private Approval? pending;
    private Job? current;
    private bool invalidJournal;

    public DesktopLiveSteamMaintenanceBridge(ClusterService cluster,
        DesktopFreshAsaStopBridge stop,DesktopStoppedSteamUpdateBridge steam,
        DesktopFreshAsaStartBridge start)
        :this(cluster,stop,steam,start,null,null,null,null){}

    // Fixture-specific dependency injection is INTERNAL and available only
    // to the separate DesktopBridgeTests executable; the shipping constructor
    // always calls the real native lifecycle/Steam/RCON bridges.
    internal DesktopLiveSteamMaintenanceBridge(ClusterService cluster,
        DesktopFreshAsaStopBridge stop,DesktopStoppedSteamUpdateBridge steam,
        DesktopFreshAsaStartBridge start,
        Func<LocalMap,ProcessCheck>? processObserver,
        Func<LocalMap,bool>? preflight,
        Func<LocalMap,Action<string>,Task>? isolatedRunner,
        Func<LocalMap,string?>? buildProbe)
    {
        this.cluster=cluster??throw new ArgumentNullException(nameof(cluster));
        this.stop=stop??throw new ArgumentNullException(nameof(stop));
        this.steam=steam??throw new ArgumentNullException(nameof(steam));
        this.start=start??throw new ArgumentNullException(nameof(start));
        observer=processObserver??cluster.Check;
        eligible=preflight??NativeEligible;
        runner=isolatedRunner??RunNative;
        build=buildProbe??(map=>AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath));
        try
        {
            var old=SteamLiveCycleJournal.Load(cluster.Store.Root);
            if(old!=null)
            {
                current=new Job{
                    Id=old.JobId,MapId=old.MapId,
                    State=old.State is "queued" or "running"?"needsOperator":old.State,
                    Stage=old.State is "queued" or "running"?
                        "interrupted-manager-review-required":old.Stage,
                    ProfileSha=old.ProfileSha256,InitialBuild=old.InitialBuild,
                    ChangedUtc=old.ChangedUtc
                };
            }
        }
        catch(Exception e) when(e is IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or ArgumentException)
        {
            invalidJournal=true;
            current=new Job{State="needsOperator",
                Stage="manager-local-journal-integrity-review-required"};
        }
    }

    private static string Fingerprint(LocalMap map)=>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(map,Json))));

    private bool NativeEligible(LocalMap map)
    {
        if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map)||
           FreshStoreEnrollmentPolicy.IsEnrolled(map)||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||
           map.RconHost!="127.0.0.1"||
           map.RconPort is <1024 or >65535 ||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected))
            return false;
        // SteamCMD updates an installation, not a running map. One running
        // ASA per installation is a bounded initial implementation; refuse
        // shared roots rather than stopping an unrelated second map.
        string root=Path.GetFullPath(map.InstallationPath);
        if(cluster.Config.LocalMaps.Count(m=>
            string.Equals(Path.GetFullPath(m.InstallationPath),
                root,StringComparison.OrdinalIgnoreCase))!=1)
            return false;
        var status=cluster.Check(map);
        if(!status.Running||status.ConfirmedStopped||
           status.Identity is not {Pid:>0} owned)
            return false;
        if(!RconPortOwnership.IsOwnedListener(map.RconPort,owned.Pid))
            return false;
        SteamCmdPreflight.Inspect(cluster.Config.SteamCmdExecutable);
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        var queue=new SteamSerialMaintenanceQueue(
            cluster.Store.Root,cluster.Config.LocalHostId).Observe();
        if(queue?.Phase is SteamQueuePhase.Prepared or
            SteamQueuePhase.MapInProgress or SteamQueuePhase.NeedsOperator)
            return false;
        return !string.IsNullOrWhiteSpace(
            AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath));
    }

    private bool Allowed(LocalMap? map)
    {
        if(map==null||map.Id==Guid.Empty||!cluster.Config.LocalMaps.Contains(map)||
           map.HistoricalStoreProtected||map.Maintenance||
           KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           FreshStoreEnrollmentPolicy.IsEnrolled(map)||
           (map.Arguments+" "+map.ApiLoaderArguments).Contains("store",
               StringComparison.OrdinalIgnoreCase)||
           (map.Arguments+" "+map.ApiLoaderArguments).Contains("convert",
               StringComparison.OrdinalIgnoreCase))
            return false;
        try{return eligible(map);}
        catch(Exception e) when(e is IOException or InvalidDataException or
            ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {return false;}
    }

    private static string State(Job job,string? detail=null)=>
        detail??job.Stage;

    private void Stage(Job job,string stage,string state="running")
    {
        lock(sync)
        {
            job.State=state;job.Stage=stage;job.ChangedUtc=DateTimeOffset.UtcNow;
            SteamLiveCycleJournal.Save(cluster.Store.Root,new(
                job.Id,job.MapId,job.State,job.Stage,
                job.ProfileSha,job.InitialBuild,job.ChangedUtc));
        }
    }
    private object Snapshot(Job job)
    {
        lock(sync)return new{
            jobId=job.Id,mapId=job.MapId,state=job.State,
            stage=job.Stage,changedUtc=job.ChangedUtc,
            stopVerified=job.StopVerified,
            steamUpdateVerified=job.UpdateVerified,
            startRequested=job.StartRequested,
            productionReady=job.ReadyVerified,
            originalStoreTouched=false,remoteHostTouched=false,
            gameClientTravelVerified=false,liveStoreWriterCertified=false,
            automaticRetry=false,
            evidence="GUARDED_EXISTING_LOCAL_STOP_UPDATE_START_BRIDGES"
        };
    }

    private static async Task<JsonElement> Send(
        Func<string,Task<string>> handler,string method,object? input=null)
    {
        var message=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(input!=null)
        {
            var values=JsonSerializer.SerializeToElement(input,Json);
            foreach(var property in values.EnumerateObject())
                message[property.Name]=property.Value.Clone();
        }
        using var parsed=JsonDocument.Parse(await handler(
            JsonSerializer.Serialize(message,Json)));
        var root=parsed.RootElement;
        if(!root.TryGetProperty("ok",out var ok)||ok.ValueKind!=JsonValueKind.True)
        {
            string code=root.TryGetProperty("error",out var error)?
                error.GetString()??"unknown":"unknown";
            throw new InvalidOperationException("UM-LIVE-STEAM-STAGE-"+method+":"+code);
        }
        return root.GetProperty("data").Clone();
    }
    private static async Task<JsonElement> WaitFor(
        Func<string,Task<string>> handler,string route,string jobId,
        TimeSpan timeout)
    {
        var until=DateTimeOffset.UtcNow+timeout;
        while(DateTimeOffset.UtcNow<until)
        {
            var result=await Send(handler,route,new{jobId});
            string state=result.GetProperty("state").GetString()??"";
            if(state=="completed")return result;
            if(state=="needsOperator")
                throw new InvalidOperationException("UM-LIVE-STEAM-NATIVE-STAGE-NEEDS-OPERATOR");
            if(state is not ("queued" or "running"))
                throw new InvalidDataException("UM-LIVE-STEAM-NATIVE-STAGE-UNKNOWN");
            await Task.Delay(950);
        }
        throw new TimeoutException("UM-LIVE-STEAM-NATIVE-STAGE-TIMEOUT");
    }

    private async Task RunNative(LocalMap map,Action<string> set)
    {
        // The initial operator approval covers the complete guarded
        // maintenance. EACH existing native component nevertheless mints and
        // consumes its own single-use fresh preflight token. No raw RCON or
        // process command is supplied by the renderer.
        set("SAVEWORLD_STOP_PREFLIGHT");
        var stopping=await Send(stop.HandleAsync,"servers.gracefulStopPreview",
            new{mapId=map.Id});
        var stopToken=stopping.GetProperty("token").GetString()!;
        var stopQueued=await Send(stop.HandleAsync,"servers.gracefulStopApply",
            new{token=stopToken,confirm=true});
        set("SAVEWORLD_STABLE_ARK_DOEXIT");
        var stopped=await WaitFor(stop.HandleAsync,"servers.gracefulStopStatus",
            stopQueued.GetProperty("jobId").GetString()!,TimeSpan.FromMinutes(8));
        if(!stopped.GetProperty("confirmedStopped").GetBoolean()||
           !stopped.GetProperty("preExitSaveEvidenceObserved").GetBoolean())
            throw new InvalidDataException("UM-LIVE-STEAM-SAVEWORLD-STOP-NOT-VERIFIED");
        set("STOP_VERIFIED");

        set("STEAMCMD_STOPPED_PREFLIGHT");
        var updater=await Send(steam.HandleAsync,"steam.stoppedPreview",
            new{mapId=map.Id});
        var steamQueued=await Send(steam.HandleAsync,"steam.stoppedApply",
            new{token=updater.GetProperty("token").GetString(),confirm=true});
        set("STEAMCMD_BACKUP_DOWNLOAD_VERIFY");
        var updated=await WaitFor(steam.HandleAsync,"steam.stoppedStatus",
            steamQueued.GetProperty("jobId").GetString()!,TimeSpan.FromHours(3));
        if(!updated.GetProperty("protectedFilesIntact").GetBoolean()||
           string.IsNullOrWhiteSpace(updated.GetProperty("newBuild").GetString()))
            throw new InvalidDataException("UM-LIVE-STEAM-UPDATE-NOT-VERIFIED");
        set("UPDATE_VERIFIED");

        set("ASA_START_PREFLIGHT");
        var startPlan=await Send(start.HandleAsync,"servers.freshStartPreview",
            new{mapId=map.Id});
        var started=await Send(start.HandleAsync,"servers.freshStartApply",
            new{token=startPlan.GetProperty("token").GetString(),confirm=true});
        if(!started.GetProperty("startRequested").GetBoolean())
            throw new InvalidDataException("UM-LIVE-STEAM-START-NOT-REQUESTED");
        set("ASA_PROCESS_START_REQUESTED");
        // Process.Start is not proof of actual ASA readiness. RCON and
        // official ASA readiness must independently succeed before success.
        var deadline=DateTimeOffset.UtcNow.AddMinutes(15);
        while(DateTimeOffset.UtcNow<deadline)
        {
            try
            {
                var observed=await Send(start.HandleAsync,"servers.freshReadiness",
                    new{mapId=map.Id});
                if(observed.GetProperty("productionReady").GetBoolean())
                {
                    set("ASA_PRODUCTION_READY");
                    return;
                }
            }
            catch(InvalidOperationException)
            {
                // Only readiness checks retry; Process.Start / Stop / RCON
                // commands are NEVER replayed.
            }
            await Task.Delay(2000);
        }
        throw new TimeoutException("UM-LIVE-STEAM-ASA-READINESS-NOT-CONFIRMED");
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2800)return Fail(null,"UM-LIVE-STEAM-REQUEST");
            using var parsed=JsonDocument.Parse(raw);
            var root=parsed.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||
               !root.TryGetProperty("id",out var request)||
               request.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(request.GetString(),out var guid)||
               !root.TryGetProperty("method",out var methodRaw)||
               methodRaw.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-LIVE-STEAM-REQUEST");
            id=guid.ToString("D");
            string? method=methodRaw.GetString();
            if(method is not ("steam.liveStatus" or "steam.livePreview" or
                "steam.liveApply" or "steam.liveCurrent"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="steam.liveCurrent")
                    return Ok(id,current==null?
                        new{exists=false,state="none",stage="no-job"}:
                        new{exists=true,state=current.State,stage=current.Stage,
                            mapId=current.MapId,jobId=current.Id});
                if(method=="steam.liveStatus")
                {
                    if(!root.TryGetProperty("jobId",out var jobField)||
                       jobField.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(jobField.GetString(),out var requestedJob)||
                       current==null||current.Id!=requestedJob)
                        return Fail(id,"UM-LIVE-STEAM-JOB-MISSING");
                    return Ok(id,Snapshot(current));
                }
                if(invalidJournal)
                    return Fail(id,"UM-LIVE-STEAM-JOURNAL-INVALID");
                lock(sync)
                {
                    if(current?.State is "queued" or "running")
                        return Fail(id,"UM-LIVE-STEAM-BUSY");
                }
                if(method=="steam.livePreview")
                {
                    pending=null;
                    if(!root.TryGetProperty("mapId",out var mapRaw)||
                       mapRaw.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(mapRaw.GetString(),out var mapId)||mapId==Guid.Empty)
                        return Fail(id,"UM-LIVE-STEAM-INVALID-MAP");
                    var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId);
                    if(!Allowed(map))return Fail(id,"UM-LIVE-STEAM-PREFLIGHT-DENIED");
                    var observed=observer(map!);
                    if(!observed.Running||observed.ConfirmedStopped||
                       observed.Identity is not {Pid:>0} process)
                        return Fail(id,"UM-LIVE-STEAM-PROCESS-NOT-OWNED");
                    string? installed=build(map!);
                    if(string.IsNullOrWhiteSpace(installed)||
                       installed.Any(ch=>!char.IsAsciiDigit(ch)))
                        return Fail(id,"UM-LIVE-STEAM-BUILD-UNKNOWN");
                    pending=new Approval(Guid.NewGuid(),map!.Id,Fingerprint(map),
                        process.Pid,process.StartedUtc,installed,
                        DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id,new{
                        token=pending.Token,mapId=map.Id,mapName=map.Name,
                        pid=process.Pid,installedBuild=installed,
                        stages=new[]{"SaveWorld","DoExit","VerifiedStopped",
                            "ProtectiveBackup","SteamCMD","BuildVerified",
                            "Start","AsaReadiness"},
                        requiresExplicitApproval=true,automaticRetry=false,
                        storeSupported=false,remoteUpdates=false,
                        sourceVerified=true,productionReadyNow=false,
                        evidence="LOCAL_RUNNING_FRESH_ASA_NATIVE_SEQUENCE_PREFLIGHT"
                    });
                }
                if(pending==null||
                   !root.TryGetProperty("token",out var permit)||
                   permit.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(permit.GetString(),out var token)||
                   token!=pending.Token||
                   !root.TryGetProperty("confirm",out var consent)||
                   consent.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-LIVE-STEAM-EXPLICIT-APPROVAL");
                var approved=pending;pending=null;
                if(DateTimeOffset.UtcNow>approved.Expires)
                    return Fail(id,"UM-LIVE-STEAM-APPROVAL-EXPIRED");
                var existing=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==approved.MapId);
                if(!Allowed(existing)||Fingerprint(existing!)!=approved.ProfileSha||
                   build(existing!)!=approved.InitialBuild)
                    return Fail(id,"UM-LIVE-STEAM-STATE-CHANGED");
                var active=observer(existing!);
                if(!active.Running||active.ConfirmedStopped||
                   active.Identity is not {Pid:>0} same||
                   same.Pid!=approved.Pid||same.StartedUtc!=approved.PidCreated)
                    return Fail(id,"UM-LIVE-STEAM-PID-CHANGED");
                var lease=SteamLiveCycleJournal.AcquireLease(cluster.Store.Root);
                bool dispatched=false;
                Job task;
                try
                {
                    SteamLiveCycleJournal.ArchivePrevious(cluster.Store.Root);
                    task=new Job{MapId=existing!.Id,ProfileSha=approved.ProfileSha,
                        InitialBuild=approved.InitialBuild};
                    Stage(task,"queued","queued"); // durable before first RCON dispatch
                    current=task;
                    _=Task.Run(async()=>{
                        using(lease)
                        {
                            try
                            {
                                Stage(task,"saveworld-stop-preflight");
                                await runner(existing,step=>
                                {
                                    Stage(task,step);
                                    if(step=="STOP_VERIFIED")
                                        task.StopVerified=true;
                                    if(step=="UPDATE_VERIFIED")
                                        task.UpdateVerified=true;
                                    if(step=="ASA_PROCESS_START_REQUESTED")
                                        task.StartRequested=true;
                                    if(step=="ASA_PRODUCTION_READY")
                                        task.ReadyVerified=true;
                                });
                                if(!task.StopVerified||!task.UpdateVerified||
                                   !task.StartRequested||!task.ReadyVerified)
                                    throw new InvalidDataException(
                                        "UM-LIVE-STEAM-INCOMPLETE-NATIVE-STAGE");
                                Stage(task,"verified-asa-production-ready","completed");
                            }
                            catch(Exception)
                            {
                                try{Stage(task,"operator-review-after-interrupted-cycle",
                                    "needsOperator");}
                                catch(Exception)
                                {
                                    lock(sync)
                                    {
                                        task.State="needsOperator";
                                        task.Stage="journal-persistence-error-needs-operator";
                                    }
                                }
                            }
                        }
                    });
                    dispatched=true;
                }
                finally
                {
                    if(!dispatched)lease.Dispose();
                }
                return Ok(id,new{
                    jobId=task.Id,started=true,automaticRetry=false,
                    localOnly=true,originalStoreTouched=false,
                    evidence="EXISTING_NATIVE_STOP_STEAM_START_WORKFLOW_QUEUED"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-LIVE-STEAM-SAFETY-CHECK-FAILED");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>serial.Dispose();
}
