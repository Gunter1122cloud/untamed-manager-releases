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

// Explicitly opted-in per-map maintenance. Old scheduled-stopped-steam-v1
// settings are never read or activated. This is a host-local serial queue.
// Actual stop/start, protected backup, Steam update and readiness use the
// existing native bridges, with their own durable receipts and process guards.
internal sealed class DesktopRollingSteamSupervisor : IDisposable
{
    internal sealed record Setting(Guid MapId,bool Enabled,bool ApiRiskAccepted,string Fingerprint);
    internal sealed record Run(Guid Id,Guid MapId,string State,string Stage,string OldBuild,
        string TargetBuild,bool OriginallyRunning,DateTimeOffset ChangedUtc,string? Error=null,
        string? ProfileFingerprint=null,string? NativeSteamStage=null);
    private sealed class State
    {
        public State(){}
        public List<Setting> Settings {get;set;}=new();
        public Run? Current {get;set;}
    }
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ClusterService cluster;
    private readonly DesktopLifecycleBatchBridge lifecycle;
    private readonly DesktopStoppedSteamUpdateBridge steam;
    private readonly SemaphoreSlim hostGate;
    private readonly Func<bool> enterLane;
    private readonly Action leaveLane;
    private readonly Func<bool> frozen;
    private readonly Func<int,int,bool> ownsPort;
    private readonly Func<TimeSpan,CancellationToken,Task> delay;
    private readonly DesktopRconAdminBridge? isolatedChat;
    private readonly string root,path,owner,mutexName;
    private readonly CancellationTokenSource closing=new();
    private int started,disposed,busy;
    private Task? loop;
    internal bool Busy=>Volatile.Read(ref busy)!=0;

    internal DesktopRollingSteamSupervisor(ClusterService cluster,DesktopLifecycleBatchBridge lifecycle,
        DesktopStoppedSteamUpdateBridge steam,SemaphoreSlim hostGate,Func<bool> enterLane,
        Action leaveLane,Func<bool> frozen):
        this(cluster,lifecycle,steam,hostGate,enterLane,leaveLane,frozen,null,null,null){}
    // Isolated native CI can replace the clock and RCON boundary together.
    // The shipping constructor always uses actual listeners and minute waits.
    internal DesktopRollingSteamSupervisor(ClusterService cluster,DesktopLifecycleBatchBridge lifecycle,
        DesktopStoppedSteamUpdateBridge steam,SemaphoreSlim hostGate,Func<bool> enterLane,
        Action leaveLane,Func<bool> frozen,DesktopRconAdminBridge? isolatedChat,
        Func<int,int,bool>? isolatedOwner,Func<TimeSpan,CancellationToken,Task>? isolatedDelay)
    {
        if((isolatedChat!=null)!=(isolatedOwner!=null)||(isolatedChat!=null)!=(isolatedDelay!=null))
            throw new ArgumentException("Isolated rolling witnesses must be supplied together");
        this.isolatedChat=isolatedChat;
        ownsPort=isolatedOwner??RconPortOwnership.IsOwnedListener;
        delay=isolatedDelay??((duration,token)=>Task.Delay(duration,token));
        this.cluster=cluster;this.lifecycle=lifecycle;this.steam=steam;this.hostGate=hostGate;
        this.enterLane=enterLane;this.leaveLane=leaveLane;this.frozen=frozen;
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(cluster.Store.Root));
        path=Path.Combine(root,"rolling-steam-v1.json");owner=Path.Combine(root,"rolling-steam-v1.lock");
        mutexName=@"Global\Untamed-RollingSteam-"+Hash(root.ToUpperInvariant());
        NoLinks();
    }
    private static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private void NoLinks()
    {
        if(root.StartsWith(@"\\",StringComparison.Ordinal)||new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-ROLLING-LOCAL-PROFILE");
        for(DirectoryInfo? d=new(root);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-ROLLING-LINKED-PROFILE");
        foreach(var file in new[]{path,owner})
        {
            try
            {
                if((File.GetAttributes(file)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)
                    throw new InvalidDataException("UM-ROLLING-LINKED-JOURNAL");
            }
            catch(FileNotFoundException){}
        }
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex=new Mutex(false,mutexName);
        bool held=false;
        try
        {
            try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}
            catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("UM-ROLLING-JOURNAL-BUSY");
            return action();
        }
        finally{if(held)mutex.ReleaseMutex();}
    }
    private State Read()
    {
        NoLinks();if(!File.Exists(path))return new();
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(stream.Length is <2 or >1_000_000)throw new InvalidDataException("UM-ROLLING-JOURNAL-SIZE");
        var state=JsonSerializer.Deserialize<State>(stream,Json)??throw new InvalidDataException("UM-ROLLING-JOURNAL");
        if(state.Settings==null||state.Settings.Count>256||
           state.Settings.Any(s=>s==null||s.MapId==Guid.Empty||s.Fingerprint is not {Length:64}||!s.Fingerprint.All(Uri.IsHexDigit))||
           state.Settings.Select(s=>s.MapId).Distinct().Count()!=state.Settings.Count||
           (state.Current!=null&&(state.Current.Id==Guid.Empty||state.Current.MapId==Guid.Empty||
             state.Current.State is not ("running" or "completed" or "needs-review" or "reviewed"))))
            throw new InvalidDataException("UM-ROLLING-JOURNAL-INVALID");
        return state;
    }
    private void Write(State state)
    {
        NoLinks();Directory.CreateDirectory(root);
        string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(stream,state,Json);
                if(stream.Length>1_000_000)throw new InvalidDataException("UM-ROLLING-JOURNAL-SIZE");
                stream.Flush(true);
            }
            // Windows may transiently deny replacement while a read-only
            // observer releases an old journal handle. Retry only publication
            // of the SAME flushed bytes, never any lifecycle/Steam action.
            for(int attempt=0;;attempt++)
            {
                NoLinks();
                try{File.Move(tmp,path,true);break;}
                catch(IOException ex) when(attempt<7&&(ex.HResult&0xffff) is (5 or 32 or 33))
                {Thread.Sleep(25*(attempt+1));}
                catch(UnauthorizedAccessException ex) when(attempt<7&&(ex.HResult&0xffff)==5)
                {Thread.Sleep(25*(attempt+1));}
            }
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    private FileStream Lease()
    {
        NoLinks();Directory.CreateDirectory(root);
        return new FileStream(owner,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    private LocalMap Owned(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id)??
        throw new UnauthorizedAccessException("UM-ROLLING-MAP-NOT-LOCAL");
    private static string Canonical(string p)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    private string Fingerprint(LocalMap m)=>Hash(JsonSerializer.Serialize(new{
        m.Id,m.InstallationPath,m.ExecutablePath,m.Arguments,m.ApiLoaderPath,m.ApiLoaderArguments,m.StartWithAsaApi,
        m.GamePort,m.QueryPort,m.RconPort,m.RconHost,m.RconPasswordProtected,m.Maintenance,m.ObservedReadOnlyImported,
        m.HistoricalStoreProtected,m.HistoricalArchiveSha256,m.HistoricalMapKey,m.HistoricalRuntimeClusterId,
        m.HistoricalRuntimeTransferDirectory,m.HistoricalLaunchModeNeedsApproval,m.FreshStoreClusterId,
        m.FreshStoreTransferDirectory,m.FreshStoreTransferFileId,m.ModChangesPending,m.DisabledModIds,
        m.HistoricalEnabledMods,m.HistoricalPassiveMods,cluster.Config.SteamCmdExecutable
    }));
    private void DemandEligible(LocalMap m)
    {
        if(m.Maintenance||m.ObservedReadOnlyImported||m.HistoricalLaunchModeNeedsApproval||m.ModChangesPending||
           !AsaReadyProbe.IsRegisteredServerBinary(m)||m.RconHost!="127.0.0.1"||m.RconPort is <1024 or >65535||
           string.IsNullOrWhiteSpace(m.RconPasswordProtected)||KovaCompatibleWorldBackup.InstallationRecoveryRequired(m))
            throw new InvalidOperationException("UM-ROLLING-MAP-INELIGIBLE");
        if(m.HistoricalStoreProtected)
        {
            if(!LegacyStoreV1PinnedSource.HasApprovedLocalSource(cluster.Store.Root,m))
                throw new InvalidOperationException("UM-ROLLING-STORE-SOURCE-UNVERIFIED");
        }
        else if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,m)||
                KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(m)||FreshStoreEnrollmentPolicy.IsEnrolled(m))
            throw new InvalidOperationException("UM-ROLLING-INSTALL-INELIGIBLE");
        var target=Canonical(m.InstallationPath);
        foreach(var other in cluster.Config.LocalMaps.Where(x=>x.Id!=m.Id&&!string.IsNullOrWhiteSpace(x.InstallationPath)))
        {
            var p=Canonical(other.InstallationPath);
            if(string.Equals(p,target,StringComparison.OrdinalIgnoreCase)||
               p.StartsWith(target+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
               target.StartsWith(p+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UM-ROLLING-SHARED-OR-OVERLAPPING-INSTALLATION");
        }
        _=SteamCmdPreflight.Inspect(cluster.Config.SteamCmdExecutable);
    }
    private void DemandClearJournals(LocalMap m)
    {
        new AsaManualRestartJournal(m.InstallationPath).DemandNoPending();
        new AsaManualStopJournal(m.InstallationPath).DemandNoPending(m.Id);
        var journal=cluster.Operations.Observe(m.InstallationPath);
        if(journal?.Phase is UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)
            throw new InvalidOperationException("UM-ROLLING-LOCAL-JOURNAL-NEEDS-REVIEW");
    }
    // Read-only diagnostic: saving future opt-in is separate from permission
    // to execute. Unknown or unreadable journals still fail closed at runtime.
    private string? JournalBlocker(LocalMap m)
    {
        try{DemandClearJournals(m);return null;}
        catch(Exception ex){return NativeLifecycleFailureCode.From(ex);}
    }
    private ProcessCheck Observe(LocalMap m)
    {
        if(!m.HistoricalStoreProtected)return cluster.Check(m);
        try
        {
            var proof=LegacyStoreV1GracefulStop.Preview(cluster,m.Id);
            if(LegacyStoreV1GracefulStop.SaveOnlyStartupBlocker(proof)!=null)
                throw new InvalidOperationException("UM-ROLLING-ASA-NOT-READY");
            // Reobserve the complete OS process family independently of the
            // historical stop proof DTO. The existing proof validates the
            // source and RCON listener; the second witness binds the Loader.
            var family=cluster.ObserveLocalProcessesReadOnly(m);
            if(!family.Running||family.ConfirmedStopped||!Same(family.Identity,proof.Process)||
               m.StartWithAsaApi!=(family.LoaderIdentity is {Pid:>0})||
               family.LoaderIdentity?.Pid==proof.Process.Pid||
               !RconPortOwnership.IsOwnedListener(m.RconPort,proof.Process.Pid))
                throw new InvalidOperationException("UM-ROLLING-FAMILY-CHANGED");
            return family;
        }
        catch{ return HistoricalLocalConfigGuard.DemandStopped(cluster,m); }
    }
    private static bool Stopped(ProcessCheck state)=>state.ConfirmedStopped&&!state.Running&&state.Identity==null&&state.LoaderIdentity==null;
    private static bool Same(ProcessIdentity? a,ProcessIdentity? b)=>a==null?b==null:
        b!=null&&a.Pid==b.Pid&&a.StartedUtc==b.StartedUtc&&
        string.Equals(a.ExecutablePath,b.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&a.Arguments==b.Arguments;
    private static bool Same(ProcessCheck a,ProcessCheck b)=>a.Running==b.Running&&a.ConfirmedStopped==b.ConfirmedStopped&&
        Same(a.Identity,b.Identity)&&Same(a.LoaderIdentity,b.LoaderIdentity);
    private bool StillEnabled(Setting s)=>Locked(()=>Read().Settings.Any(x=>x==s&&x.Enabled));
    private void DemandProfile(Setting s,bool requireEnabled)
    {
        var map=Owned(s.MapId);DemandEligible(map);
        if(Fingerprint(map)!=s.Fingerprint||map.StartWithAsaApi&&!s.ApiRiskAccepted||
           (requireEnabled&&!StillEnabled(s)))
            throw new InvalidOperationException("UM-ROLLING-APPROVAL-CHANGED");
    }
    private static bool Newer(string? available,string? installed)=>ulong.TryParse(available,out var a)&&
        ulong.TryParse(installed,out var i)&&a>i&&i>0;
    private static string Message(string method,object fields)
    {
        var payload=new Dictionary<string,object?>{["id"]=Guid.NewGuid(),["method"]=method};
        foreach(var f in JsonSerializer.SerializeToElement(fields).EnumerateObject())payload[f.Name]=f.Value.Clone();
        return JsonSerializer.Serialize(payload);
    }
    private static JsonElement Decode(string raw)
    {
        using var doc=JsonDocument.Parse(raw);var r=doc.RootElement;
        if(!r.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(r.GetProperty("error").GetString()??"UM-ROLLING-NATIVE-REFUSED");
        return r.GetProperty("data").Clone();
    }
    private static bool Yes(JsonElement r,string key)=>r.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.True;
    private void SetRun(Run run)=>Locked(()=>{var state=Read();state.Current=run;Write(state);return true;});

    private static bool BeforeLifecycle(Run? run)=>run!=null&&
        run.State is ("running" or "needs-review")&&run.Stage is ("preflight" or "countdown");

    // The host worker/file lease must already be held. No Stop or SteamCMD
    // can have been dispatched in these stages: Stage is flushed before
    // either call. Archive the exact run, not a fabricated successful update.
    private bool CloseBeforeLifecycle()=>Locked(()=>{
        var state=Read();var run=state.Current;
        if(!BeforeLifecycle(run))return false;
        var prior=run!;
        string audit=Path.Combine(root,"rolling-steam-abandoned-"+prior.Id.ToString("N")+".json");
        byte[] original=JsonSerializer.SerializeToUtf8Bytes(prior,Json);
        if(File.Exists(audit))
        {
            if((File.GetAttributes(audit)&FileAttributes.ReparsePoint)!=0||
               !File.ReadAllBytes(audit).SequenceEqual(original))
                throw new InvalidDataException("UM-ROLLING-ABANDONED-AUDIT-MISMATCH");
        }
        else
        {
            string tmp=audit+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {stream.Write(original);stream.Flush(true);}
                File.Move(tmp,audit);
            }
            finally{if(File.Exists(tmp))File.Delete(tmp);}
        }
        state.Current=prior with{State="reviewed",ChangedUtc=DateTimeOffset.UtcNow,
            Error=prior.Error??"UM-ROLLING-INTERRUPTED-BEFORE-LIFECYCLE"};
        Write(state);return true;
    });
    private async Task LifecycleAsync(LocalMap map,string action,CancellationToken ct,ProcessCheck expected)
    {
        var preview=Decode(await lifecycle.HandleAsync(Message("operations.lifecycleBatchPreview",new{
            mapIds=new[]{map.Id},action,delaySeconds=0})));
        if(!preview.GetProperty("maps").EnumerateArray().Select(x=>x.GetProperty("mapId").GetGuid()).SequenceEqual(new[]{map.Id})||
           !Same(expected,Observe(map)))throw new InvalidOperationException("UM-ROLLING-LIFECYCLE-SCOPE");
        ct.ThrowIfCancellationRequested();
        var applied=Decode(await lifecycle.HandleAsync(Message("operations.lifecycleBatchApply",new{
            token=preview.GetProperty("token").GetGuid(),confirm=true})));
        Guid job=applied.GetProperty("jobId").GetGuid();
        var until=DateTimeOffset.UtcNow.AddMinutes(35);
        while(DateTimeOffset.UtcNow<until)
        {
            ct.ThrowIfCancellationRequested();
            var r=Decode(await lifecycle.HandleAsync(Message("operations.lifecycleBatchCurrent",new{}))).GetProperty("receipt");
            if(r.GetProperty("jobId").GetGuid()!=job||r.GetProperty("action").GetString()!=action)
                throw new InvalidOperationException("UM-ROLLING-LIFECYCLE-JOB");
            string? status=r.GetProperty("state").GetString();
            if(status=="completed")
            {
                var targets=r.GetProperty("targets").EnumerateArray().ToArray();
                if(targets.Length==1&&targets[0].GetProperty("mapId").GetGuid()==map.Id&&
                   targets[0].GetProperty("state").GetString()=="completed")return;
                throw new InvalidOperationException("UM-ROLLING-LIFECYCLE-NOT-VERIFIED");
            }
            if(status!="running")
            {
                var row=r.GetProperty("targets").EnumerateArray().SingleOrDefault(x=>x.GetProperty("mapId").GetGuid()==map.Id);
                string? evidence=row.ValueKind==JsonValueKind.Object?row.GetProperty("evidence").GetString():null;
                string code=evidence!=null&&evidence.StartsWith("UM-",StringComparison.Ordinal)?
                    NativeLifecycleFailureCode.From(new InvalidOperationException(evidence)):"UM-ROLLING-LIFECYCLE-NEEDS-REVIEW";
                throw new InvalidOperationException(code);
            }
            await delay(TimeSpan.FromSeconds(2),ct);
        }
        throw new TimeoutException("UM-ROLLING-LIFECYCLE-TIMEOUT");
    }
    private async Task UpdateAsync(LocalMap map,string oldBuild,string target,CancellationToken ct,Action<string> reportStage)
    {
        if(!Stopped(Observe(map)))throw new InvalidOperationException("UM-ROLLING-NOT-STOPPED");
        var preview=Decode(await steam.HandleAsync(Message("steam.stoppedPreview",new{mapId=map.Id})));
        if(preview.GetProperty("mapId").GetGuid()!=map.Id||preview.GetProperty("installedBuild").GetString()!=oldBuild||
           preview.GetProperty("maps").GetArrayLength()!=1||preview.GetProperty("maps")[0].GetString()!=map.Name)
            throw new InvalidOperationException("UM-ROLLING-UPDATE-SCOPE");
        ct.ThrowIfCancellationRequested();
        var applied=Decode(await steam.HandleRollingApplyAsync(Message("steam.stoppedApply",new{
            token=preview.GetProperty("token").GetGuid(),confirm=true}),target));
        if(!Yes(applied,"started"))throw new InvalidOperationException("UM-ROLLING-UPDATE-REJECTED");
        Guid job=applied.GetProperty("jobId").GetGuid();
        var until=DateTimeOffset.UtcNow.AddHours(3);
        while(DateTimeOffset.UtcNow<until)
        {
            ct.ThrowIfCancellationRequested();
            var r=Decode(await steam.HandleAsync(Message("steam.stoppedStatus",new{jobId=job})));
            if(r.GetProperty("jobId").GetGuid()!=job||r.GetProperty("mapId").GetGuid()!=map.Id)
                throw new InvalidOperationException("UM-ROLLING-UPDATE-JOB");
            string? status=r.GetProperty("state").GetString();
            if(r.TryGetProperty("stage",out var stage)&&stage.GetString() is {Length:>0 and <=80} nativeStage)
                reportStage(nativeStage);
            if(status=="completed")
            {
                if(!Yes(r,"protectedFilesIntact")||r.GetProperty("oldBuild").GetString()!=oldBuild||
                   r.GetProperty("newBuild").GetString()!=target||
                   AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath)!=target)
                    throw new InvalidOperationException("UM-ROLLING-BUILD-OR-BACKUP-UNVERIFIED");
                return;
            }
            if(status is not ("running" or "queued"))
            {
                var code=r.TryGetProperty("errorCode",out var error)&&error.ValueKind==JsonValueKind.String?
                    error.GetString():null;
                throw new InvalidOperationException(code!=null?
                    NativeLifecycleFailureCode.From(new InvalidOperationException(code)):
                    "UM-ROLLING-UPDATE-NEEDS-REVIEW");
            }
            await delay(TimeSpan.FromSeconds(3),ct);
        }
        throw new TimeoutException("UM-ROLLING-UPDATE-TIMEOUT");
    }
    private async Task WarnAsync(Setting setting,LocalMap map,ProcessCheck initial,CancellationToken ct)
    {
        var identity=initial.Identity??throw new InvalidOperationException("UM-ROLLING-OWNER");
        var fresh=isolatedChat??new DesktopRconAdminBridge(cluster);
        using var historical=map.HistoricalStoreProtected?new DesktopHistoricalRconAdminBridge(cluster):null;
        bool StillApproved()
        {
            if(frozen()||closing.IsCancellationRequested)return false;
            try{DemandProfile(setting,true);return Same(initial,Observe(map));}
            catch{return false;}
        }
        async Task Send(string message,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string prefix=historical==null?"rcon":"historical";
            Func<string,Task<string>> handle=historical==null?
                raw=>fresh.HandleScheduledChatAsync(raw,identity.Pid,identity.StartedUtc):
                raw=>historical.HandleScheduledChatAsync(raw,identity.Pid,identity.StartedUtc);
            var pre=Decode(await handle(Message(prefix+".adminPreview",new{mapId=map.Id,action="serverChat",message})));
            if(pre.GetProperty("mapId").GetGuid()!=map.Id||pre.GetProperty("command").GetString()!="ServerChat "+message||
               !StillApproved())throw new InvalidOperationException("UM-ROLLING-CHAT-SCOPE");
            token.ThrowIfCancellationRequested();
            var sent=Decode(await handle(Message(prefix+".adminSend",new{
                token=pre.GetProperty("token").GetGuid(),confirm=true,typedApproval="STORE-V1 ADMIN"})));
            if(sent.GetProperty("mapId").GetGuid()!=map.Id||sent.GetProperty("command").GetString()!="ServerChat "+message||
               !Yes(sent,"oneShot")||!Yes(sent,"commandSent"))throw new InvalidOperationException("UM-ROLLING-CHAT-SEND");
        }
        await AsaShutdownCountdown.RunAsync(true,StillApproved,Send,ct,delay);
    }
    private async Task RunMapAsync(Setting setting,string target,CancellationToken ct)
    {
        DemandProfile(setting,true);
        var map=Owned(setting.MapId);DemandClearJournals(map);
        string? installed=AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath);
        if(!Newer(target,installed))return;
        var initial=Observe(map);
        bool running=initial.Running&&!initial.ConfirmedStopped&&initial.Identity is {Pid:>0}&&
            ownsPort(map.RconPort,initial.Identity.Pid);
        if(!running&&!Stopped(initial))throw new InvalidOperationException("UM-ROLLING-PROCESS-UNCERTAIN");
        var diagnostic=Decode(await steam.HandleAsync(Message("steam.stoppedDiagnostics",new{mapId=map.Id})));
        if(Yes(diagnostic,"journalBlocked")||Yes(diagnostic,"historicalBlocked")||!Yes(diagnostic,"steamCmdFound")||
           !Yes(diagnostic,"asaBinaryRegistered")||diagnostic.GetProperty("overlaps").GetArrayLength()!=0||
           diagnostic.GetProperty("availableBytes").GetInt64()<diagnostic.GetProperty("requiredBytes").GetInt64()||
           diagnostic.GetProperty("externalSteamError").ValueKind!=JsonValueKind.Null)
            throw new InvalidOperationException("UM-ROLLING-PREFLIGHT-BLOCKED");
        var run=new Run(Guid.NewGuid(),map.Id,"running","preflight",installed!,target,running,DateTimeOffset.UtcNow,
            ProfileFingerprint:setting.Fingerprint);
        SetRun(run); // durable claim BEFORE chat, stop or SteamCMD.
        void Stage(string stage){run=run with{Stage=stage,ChangedUtc=DateTimeOffset.UtcNow};SetRun(run);}
        try
        {
            if(running)
            {
                Stage("countdown");await WarnAsync(setting,map,initial,ct);
                DemandProfile(setting,true);DemandClearJournals(map);
                if(!Same(initial,Observe(map)))throw new InvalidOperationException("UM-ROLLING-PROCESS-CHANGED");
                Stage("stopping");await LifecycleAsync(map,"stop",ct,initial);
            }
            // After an intentional stop, disabling opts out of future cycles;
            // finish this already-claimed map if the unchanged identity allows.
            DemandProfile(setting,false);DemandClearJournals(map);
            if(!Stopped(Observe(map)))throw new InvalidOperationException("UM-ROLLING-NOT-STOPPED");
            Stage("updating");
            await UpdateAsync(map,installed!,target,ct,nativeStage=>{
                if(run.NativeSteamStage==nativeStage)return;
                run=run with{NativeSteamStage=nativeStage,ChangedUtc=DateTimeOffset.UtcNow};SetRun(run);
            });
            // The native completed receipt has verified the target manifest,
            // backup and protected files. Persist before any Start dispatch.
            Stage("files-verified");
            if(running)
            {
                DemandProfile(setting,false);DemandClearJournals(map);
                var stopped=Observe(map);
                if(!Stopped(stopped))throw new InvalidOperationException("UM-ROLLING-START-STATE-CHANGED");
                Stage("starting");await LifecycleAsync(map,"start",ct,stopped);
                var restarted=Observe(map);
                if(!restarted.Running||restarted.ConfirmedStopped||restarted.Identity is not {Pid:>0} child||
                   Same(initial.Identity,child)||!ownsPort(map.RconPort,child.Pid)||
                   map.StartWithAsaApi!=(restarted.LoaderIdentity is {Pid:>0}))
                    throw new InvalidOperationException("UM-ROLLING-START-PROOF-CHANGED");
            }
            else if(!Stopped(Observe(map)))
                throw new InvalidOperationException("UM-ROLLING-STOPPED-MAP-STATE-CHANGED");
            SetRun(run with{State="completed",Stage="verified",ChangedUtc=DateTimeOffset.UtcNow});
        }
        catch(Exception ex)
        {
            SetRun(run with{State="needs-review",Error=NativeLifecycleFailureCode.From(ex),ChangedUtc=DateTimeOffset.UtcNow});
            throw;
        }
    }
    private static bool CanResumeVerifiedFiles(Run? run)=>run!=null&&
        run.State is ("running" or "needs-review")&&run.Stage=="files-verified"&&
        run.ProfileFingerprint is {Length:64};

    private async Task<bool> ResumeVerifiedFilesAsync(Run run,State state,CancellationToken ct)
    {
        var setting=state.Settings.SingleOrDefault(s=>s.MapId==run.MapId&&s.Enabled&&
            s.Fingerprint==run.ProfileFingerprint);
        if(setting==null)return false;
        var map=Owned(run.MapId);
        DemandProfile(setting,true);DemandClearJournals(map);
        if(AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath)!=run.TargetBuild||
            !Stopped(Observe(map)))return false;
        // The previous worker could not have entered Start before writing
        // "starting". Never resume from that ambiguous stage.
        if(run.OriginallyRunning)
        {
            var stopped=Observe(map);
            if(!Stopped(stopped))return false;
            run=run with{State="running",Stage="starting",ChangedUtc=DateTimeOffset.UtcNow,Error=null};
            SetRun(run);
            try
            {
                await LifecycleAsync(map,"start",ct,stopped);
                var now=Observe(map);
                if(!now.Running||now.ConfirmedStopped||now.Identity is not {Pid:>0} child||
                   !ownsPort(map.RconPort,child.Pid)||map.StartWithAsaApi!=(now.LoaderIdentity is {Pid:>0}))
                    throw new InvalidOperationException("UM-ROLLING-RECOVERY-START-NOT-VERIFIED");
            }
            catch(Exception ex)
            {
                SetRun(run with{State="needs-review",ChangedUtc=DateTimeOffset.UtcNow,Error=NativeLifecycleFailureCode.From(ex)});
                throw;
            }
        }
        SetRun(run with{State="completed",Stage="verified",ChangedUtc=DateTimeOffset.UtcNow,Error=null});
        return true;
    }

    internal async Task TickOnceAsync(CancellationToken ct=default)
    {
        if(frozen()||!Locked(()=>{var s=Read();return
            (BeforeLifecycle(s.Current)||CanResumeVerifiedFiles(s.Current)||s.Current?.State is not ("running" or "needs-review"))&&
            s.Settings.Any(x=>x.Enabled);}))
            return;
        if(!await hostGate.WaitAsync(0,ct))return;
        bool entered=false;
        try
        {
            if(frozen()||!enterLane())return;
            entered=true;
            using var lease=Lease();
            Interlocked.Exchange(ref busy,1);
            var state=Locked(Read);
            if(BeforeLifecycle(state.Current))
            {
                CloseBeforeLifecycle();
                state=Locked(Read);
            }
            if(CanResumeVerifiedFiles(state.Current))
            {
                if(!await ResumeVerifiedFilesAsync(state.Current!,state,ct))return;
                state=Locked(Read);
            }
            if(state.Current?.State is "running" or "needs-review")return;
            var settings=state.Settings.Where(s=>s.Enabled).ToArray();
            string? latest=null;
            foreach(var setting in settings)
            {
                ct.ThrowIfCancellationRequested();
                if(!StillEnabled(setting))continue;
                try{DemandProfile(setting,true);}
                catch{continue;} // no mutation or claim on an invalid target
                var map=Owned(setting.MapId);
                // A stale per-map journal is not a failed rolling transaction.
                // Skip only that map before querying Steam or claiming work.
                // RunMapAsync still rechecks all journals before any mutation.
                if(JournalBlocker(map)!=null)continue;
                if(latest==null)
                {
                    // Reuse the native read-only build endpoint so the same
                    // checked receipt and isolated Steam metadata witness are
                    // exercised by both UI and automatic maintenance.
                    var report=Decode(await steam.HandleAsync(Message("steam.publishedBuild",new{mapId=map.Id})));
                    ct.ThrowIfCancellationRequested();
                    if(report.GetProperty("mapId").GetGuid()!=map.Id||
                       report.GetProperty("downloadStarted").GetBoolean()||
                       report.GetProperty("serverStopped").GetBoolean())
                        throw new InvalidOperationException("UM-ROLLING-PUBLISHED-BUILD-SCOPE");
                    latest=report.GetProperty("availableBuild").GetString();
                    if(string.IsNullOrWhiteSpace(latest))return;
                }
                try{await RunMapAsync(setting,latest,ct);} // no parallel map mutations
                catch(Exception ex) when(ex is not OperationCanceledException)
                {
                    var failed=Locked(Read).Current;
                    if(failed?.MapId==map.Id&&BeforeLifecycle(failed))
                    {
                        CloseBeforeLifecycle();
                        // A failed warning can leave its own local admin
                        // journal. Do not replay it; other maps are independent.
                        continue;
                    }
                    // Only a claimed stopping/updating/starting phase may
                    // have touched the server files/process. Preserve it.
                    if(failed?.State is "running" or "needs-review")throw;
                    System.Diagnostics.Trace.TraceWarning("Rolling preflight {0}: {1}",map.Id,NativeLifecycleFailureCode.From(ex));
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref busy,0);
            if(entered)leaveLane();
            hostGate.Release();
        }
    }
    internal void Start()
    {
        if(Volatile.Read(ref disposed)!=0||Interlocked.Exchange(ref started,1)!=0)return;
        loop=Task.Run(async()=>{
            using var timer=new PeriodicTimer(TimeSpan.FromMinutes(15));
            try
            {
                while(await timer.WaitForNextTickAsync(closing.Token))
                    try{await TickOnceAsync(closing.Token);}
                    catch(OperationCanceledException) when(closing.IsCancellationRequested){break;}
                    catch{/* durable claimed work is not replayed; preflight-only failures retry next poll */}
            }
            catch(OperationCanceledException){}
        });
    }
    internal Task<string> HandleAsync(string raw)
    {
        string? id=null;
        string Ok(object data)=>JsonSerializer.Serialize(new{id,ok=true,data},Json);
        string Fail(string code)=>JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
        try
        {
            if(raw.Length is <2 or >2048)return Task.FromResult(Fail("UM-ROLLING-REQUEST"));
            using var doc=JsonDocument.Parse(raw);var a=doc.RootElement;
            id=a.GetProperty("id").GetGuid().ToString("D");
            string? method=a.GetProperty("method").GetString();
            if(method=="steam.rollingStatus")
            {
                var state=Locked(Read);
                return Task.FromResult(Ok(new{settings=state.Settings.Select(x=>new{x.MapId,x.Enabled,x.ApiRiskAccepted,
                        journalBlocker=x.Enabled?cluster.Config.LocalMaps.FirstOrDefault(m=>m.Id==x.MapId) is { } m?
                            JournalBlocker(m):"UM-MAP-NOT-OWNED":null}),
                    current=state.Current,busy=Busy,intervalMinutes=15,warningMinutes=AsaShutdownCountdown.Minutes,
                    hostLocalOnly=true,runnerStarted=Volatile.Read(ref started)!=0}));
            }
            if(frozen()||Volatile.Read(ref disposed)!=0||!Yes(a,"confirm"))
                return Task.FromResult(Fail("UM-ROLLING-APPROVAL"));
            if(method=="steam.rollingSet")
            {
                var map=Owned(a.GetProperty("mapId").GetGuid());
                bool enabled=a.GetProperty("enabled").GetBoolean(),apiRisk=Yes(a,"apiRiskAccepted");
                if(enabled)
                {
                    if(Busy||Volatile.Read(ref started)==0||map.StartWithAsaApi&&!apiRisk)
                        return Task.FromResult(Fail("UM-ROLLING-ENABLE-BLOCKED"));
                    DemandEligible(map);
                    // Opt-in records intent only. A prior per-map journal
                    // remains intact and continues to prevent all dispatch.
                }
                var result=Locked(()=>{
                    var state=Read();
                    if(enabled&&state.Current?.State is ("running" or "needs-review")&&!BeforeLifecycle(state.Current))
                        throw new InvalidOperationException("UM-ROLLING-PRIOR-REVIEW");
                    var row=new Setting(map.Id,enabled,enabled&&apiRisk,Fingerprint(map));
                    state.Settings.RemoveAll(x=>x.MapId==map.Id);state.Settings.Add(row);Write(state);return row;
                });
                return Task.FromResult(Ok(new{result.MapId,result.Enabled,result.ApiRiskAccepted,
                    journalBlocker=result.Enabled?JournalBlocker(map):null}));
            }
            if(method=="steam.rollingReview")
            {
                if(Busy)return Task.FromResult(Fail("UM-ROLLING-BUSY"));
                using var lease=Lease();
                var runId=a.GetProperty("runId").GetGuid();
                return Task.FromResult(Ok(Locked(()=>{
                    var s=Read();var previous=s.Current;
                    if(previous==null||previous.Id!=runId||previous.State is not ("running" or "needs-review"))
                        throw new InvalidOperationException("UM-ROLLING-REVIEW-SCOPE");
                    var archive=Path.Combine(root,"rolling-steam-reviewed-"+runId.ToString("N")+".json");
                    using(var audit=new FileStream(archive,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {JsonSerializer.Serialize(audit,previous,Json);audit.Flush(true);}
                    s.Settings=s.Settings.Select(x=>x with{Enabled=false}).ToList();
                    s.Current=previous with{State="reviewed",ChangedUtc=DateTimeOffset.UtcNow};
                    Write(s);return new{reviewed=true,allDisabled=true,noReplay=true};
                })));
            }
            return Task.FromResult(Fail("UM-METHOD-DENIED"));
        }
        catch(Exception ex){return Task.FromResult(Fail(NativeLifecycleFailureCode.From(ex)));}
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)closing.Cancel();}
}
