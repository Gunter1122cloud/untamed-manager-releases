using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit emergency Kill for a proven, LOCAL, owned non-historical ASA
/// process. No SaveWorld, Store migration, retry or remote operation.
/// Uses the same machine-wide per-installation coordinator as Start/Stop.
/// </summary>
public sealed class DesktopOwnedAsaKillBridge
{
    private sealed record Approval(Guid Token,Guid MapId,int Pid,
        DateTimeOffset StartedUtc,string Executable,string InstallRoot,
        int RconPort,DateTimeOffset ExpiresUtc);
    private readonly ClusterService cluster;
    private readonly Func<LocalMap,ProcessIdentity> identify;
    private readonly Func<ProcessIdentity,bool> terminate;
    private readonly SemaphoreSlim mutex=new(1,1);
    private Approval? approval;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public DesktopOwnedAsaKillBridge(ClusterService owner,
        Func<LocalMap,ProcessIdentity>? identifyForTests=null,
        Func<ProcessIdentity,bool>? terminateForTests=null)
    {
        cluster=owner??throw new ArgumentNullException(nameof(owner));
        identify=identifyForTests??RequireOwned;
        terminate=terminateForTests??TerminateExact;
    }

    private ProcessIdentity RequireOwned(LocalMap map)
    {
        // The historical Kova Store-v1 protocol has a separate protected
        // lifecycle and cannot be emergency-killed by this route.
        if(map.HistoricalStoreProtected||map.Maintenance||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||
           map.RconHost!="127.0.0.1"||
           map.RconPort is <1024 or >65535||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected))
            throw new UnauthorizedAccessException("UM-KILL-NOT-OWNED-OR-STORE-PROTECTED");
        if(FreshStoreEnrollmentPolicy.IsEnrolled(map))
            FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);
        else FreshAsaInstallationSafety.Demand(cluster,map);
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        var state=cluster.Check(map);
        if(!state.Running||state.ConfirmedStopped||
           state.Identity is not {Pid:>0} process ||
           state.LoaderIdentity!=null||
           !RconPortOwnership.IsOwnedListener(map.RconPort,process.Pid))
            throw new InvalidOperationException("UM-KILL-NO-EXACT-LOCAL-OWNER");
        if(!Path.GetFullPath(process.ExecutablePath).Equals(
            Path.GetFullPath(map.ExecutablePath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UM-KILL-EXECUTABLE-CHANGED");
        return process;
    }

    private static bool TerminateExact(ProcessIdentity expected)
    {
        using var process=Process.GetProcessById(expected.Pid);
        if(process.HasExited)
            throw new InvalidOperationException("UM-KILL-PROCESS-ALREADY-EXITED");
        if(Math.Abs((process.StartTime.ToUniversalTime()-expected.StartedUtc.UtcDateTime)
                .TotalSeconds)>2)
            throw new InvalidOperationException("UM-KILL-PID-REUSED");
        string executable=process.MainModule?.FileName??"";
        if(!Path.GetFullPath(executable).Equals(
            Path.GetFullPath(expected.ExecutablePath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UM-KILL-OS-EXECUTABLE-MISMATCH");
        process.Kill(entireProcessTree:false);
        return process.WaitForExit(12000)&&process.HasExited;
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    private static bool GuidValue(JsonElement root,string key,out Guid id)
    {
        id=Guid.Empty;
        return root.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String&&
            Guid.TryParse(p.GetString(),out id)&&id!=Guid.Empty;
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >1600)return Fail(null,"UM-KILL-INVALID-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var value=doc.RootElement;
            if(value.ValueKind!=JsonValueKind.Object||
               !GuidValue(value,"id",out var req)||
               !value.TryGetProperty("method",out var methodValue)||
               methodValue.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-KILL-INVALID-REQUEST");
            id=req.ToString("D");
            string? method=methodValue.GetString();
            if(method is not("servers.ownedKillPreview" or "servers.ownedKillApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await mutex.WaitAsync();
            try
            {
                if(method=="servers.ownedKillPreview")
                {
                    approval=null;
                    if(!GuidValue(value,"mapId",out var mapId))
                        return Fail(id,"UM-KILL-MAP-INVALID");
                    var owned=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
                    if(owned==null)return Fail(id,"UM-KILL-MAP-NOT-LOCAL");
                    // No operation journal may have an unresolved mutation.
                    var existing=cluster.Operations.Observe(owned.InstallationPath);
                    if(existing?.Phase is UntamedOperationPhase.Prepared or
                        UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)
                        return Fail(id,"UM-KILL-LIFECYCLE-JOURNAL-REVIEW");
                    var identity=identify(owned);
                    approval=new(Guid.NewGuid(),mapId,identity.Pid,
                        identity.StartedUtc,identity.ExecutablePath,
                        owned.InstallationPath,owned.RconPort,DateTimeOffset.UtcNow.AddMinutes(2));
                    return Ok(id,new{
                        token=approval.Token,mapId,mapName=owned.Name,pid=identity.Pid,
                        saveWorldPerformed=false,processOnly=true,
                        serverDataLossPossible=true,originalStoreTouched=false,
                        remoteHostTouched=false,requiresTypedApproval=true,
                        evidence="EXACT_LOCAL_ASA_PID_OS_IDENTITY_RCON_KILL_PREFLIGHT"
                    });
                }
                if(!GuidValue(value,"token",out var token)||
                   approval==null||approval.Token!=token||
                   !value.TryGetProperty("confirm",out var yes)||
                   yes.ValueKind!=JsonValueKind.True||
                   !value.TryGetProperty("typedApproval",out var phrase)||
                   phrase.ValueKind!=JsonValueKind.String||
                   phrase.GetString()!="KILL LOCAL ASA")
                    return Fail(id,"UM-KILL-TYPED-APPROVAL-REQUIRED");
                var pinned=approval;approval=null;
                if(pinned.ExpiresUtc<DateTimeOffset.UtcNow)
                    return Fail(id,"UM-KILL-APPROVAL-EXPIRED");
                var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==pinned.MapId);
                if(map==null||map.InstallationPath!=pinned.InstallRoot||
                   map.RconPort!=pinned.RconPort)
                    return Fail(id,"UM-KILL-MAP-CHANGED");
                using var lease=cluster.Operations.Acquire(map.InstallationPath,
                    UntamedOperationKind.EmergencyKill);
                var current=identify(map);
                if(current.Pid!=pinned.Pid||
                   current.StartedUtc!=pinned.StartedUtc||
                   !current.ExecutablePath.Equals(pinned.Executable,
                       StringComparison.OrdinalIgnoreCase))
                    return Fail(id,"UM-KILL-PID-OR-OWNER-CHANGED");
                lease.MarkMutationStarted();
                if(!terminate(current))
                    return Fail(id,"UM-KILL-EXIT-UNVERIFIED");
                var stopped=cluster.Check(map);
                if(!stopped.ConfirmedStopped||stopped.Running||
                   stopped.Identity!=null||stopped.LoaderIdentity!=null)
                    return Fail(id,"UM-KILL-STOPPED-NOT-CERTIFIED");
                lease.Complete();
                return Ok(id,new{
                    mapId=map.Id,pid=current.Pid,confirmedStopped=true,
                    saveWorldPerformed=false,serverDataLossPossible=true,
                    serverRestarted=false,originalStoreTouched=false,
                    remoteHostTouched=false,
                    evidence="EXPLICIT_LOCAL_NONSTORE_OS_KILL_WITH_CONFIRMED_EXIT"
                });
            }
            finally{mutex.Release();}
        }
        catch(Exception)
        {
            // Deliberately do not return untrusted process paths or credentials.
            return Fail(id,"UM-KILL-FAILED-REVIEW-NATIVE-JOURNAL");
        }
    }
}
