using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit two-stage FIRST real ASA start on a registered local fresh Steam
/// installation. Existing Store-v1 and converted installations remain locked.
/// The receiver never accepts a launch command line, executable path or Store
/// metadata from WebView2. Real readiness is not inferred from Process.Start.
/// </summary>
public sealed class DesktopFreshAsaStartBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid Map,string Root,string Executable,
        string Arguments,int Game,int Query,int Rcon,string Build,
        DateTimeOffset CreatedUtc);
    private readonly ClusterService cluster;
    private readonly Func<Guid,bool,ProcessIdentity> launch;
    private readonly SemaphoreSlim gate=new(1,1);
    private Approval? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopFreshAsaStartBridge(ClusterService service,
        Func<Guid,bool,ProcessIdentity>? starter=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        launch=starter??cluster.LaunchFreshAsa;
    }

    private LocalMap? Owned(Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(map=>map.Id==id);
    private static bool ReadGuid(JsonElement item,string name,out Guid guid)
    {
        guid=Guid.Empty;
        return item.TryGetProperty(name,out var value)&&
            value.ValueKind==JsonValueKind.String &&
            Guid.TryParse(value.GetString(),out guid) && guid!=Guid.Empty;
    }
    private bool Eligible(LocalMap map)
    {
        if(KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map) || map.Maintenance ||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot) ||
           (map.Arguments+" "+map.ApiLoaderArguments).Contains("store",
               StringComparison.OrdinalIgnoreCase)||
           map.GamePort is <1024 or >65535 ||
           map.QueryPort is <1024 or >65535 ||
           string.IsNullOrWhiteSpace(map.Arguments) ||
           string.IsNullOrWhiteSpace(map.InstallationPath) ||
           string.IsNullOrWhiteSpace(map.ExecutablePath) ||
           !Path.IsPathFullyQualified(map.InstallationPath) ||
           !Path.IsPathFullyQualified(map.ExecutablePath) ||
           map.InstallationPath.StartsWith(@"\\",StringComparison.Ordinal))
            return false;
        string expected=Path.Combine(Path.GetFullPath(map.InstallationPath),
            "ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
        if(!string.Equals(Path.GetFullPath(map.ExecutablePath),expected,
            StringComparison.OrdinalIgnoreCase) ||
           !File.Exists(expected))
            return false;
        if(!AsaReadyProbe.IsRegisteredServerBinary(map))return false;
        if(string.IsNullOrWhiteSpace(
            AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath)))
            return false;
        if(KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
            return false;
        if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map))return false;
        LocalMapPortPolicy.RequireNoConflicts(map,cluster.Config.LocalMaps);
        if(!AsaLocalPortPreflight.CanStart(map))return false;
        AsaApiLaunchPolicy.Resolve(map);
        var state=cluster.Check(map);
        return ReleaseCapabilityGate.Check(ManagerOperation.RealAsaStart,true,
            map.Maintenance,state.ConfirmedStopped,state.Running,
            verifiedFreshInstall:true,explicitOperatorApproval:true).Allowed &&
            state.Identity==null&&state.LoaderIdentity==null;
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2400)return Error(null,"UM-FRESH-START-REQUEST");
            using var document=JsonDocument.Parse(raw);
            var item=document.RootElement;
            if(item.ValueKind!=JsonValueKind.Object ||
               !ReadGuid(item,"id",out var requestId)||
               !item.TryGetProperty("method",out var field) ||
               field.ValueKind!=JsonValueKind.String)
                return Error(null,"UM-FRESH-START-REQUEST");
            id=requestId.ToString("D");
            var method=field.GetString();
            if(method is not ("servers.freshStartPreview" or
                "servers.freshStartApply" or "servers.freshStartStatus" or
                "servers.freshReadiness"))
                return Error(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="servers.freshReadiness")
                {
                    if(!ReadGuid(item,"mapId",out var mapId))
                        return Error(id,"UM-FRESH-MAP");
                    var map=Owned(mapId);
                    if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                    if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map)||
                       !AsaReadyProbe.IsRegisteredServerBinary(map)||
                       map.Maintenance)
                        return Error(id,"UM-FRESH-READINESS-PROTECTED");
                    var runtime=cluster.Check(map);
                    if(!runtime.Running||runtime.ConfirmedStopped ||
                       runtime.Identity is not {Pid:>0} pid)
                        return Error(id,"UM-FRESH-READINESS-NOT-RUNNING");
                    if(map.RconPort>0 && !string.IsNullOrWhiteSpace(map.RconPasswordProtected) &&
                       !RconPortOwnership.IsOwnedListener(map.RconPort,pid.Pid))
                        return Error(id,"UM-FRESH-READINESS-RCON-OWNER");
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(9));
                    var observed=await AsaReadyProbe.ProbeAsync(cluster,map.Id,deadline.Token);
                    return Ok(id,new{
                        mapId=map.Id,mapName=map.Name,
                        stage=observed.Stage.ToString(),
                        observedUtc=observed.ObservedUtc,
                        loaderObserved=observed.LoaderObserved,
                        apiInitialized=observed.ApiInitialized,
                        joinAdvertised=observed.JoinAdvertised,
                        rconAuthenticated=observed.Stage==AsaRuntimeStage.RconAuthenticated,
                        productionReady=observed.ProductionReady,
                        description=observed.Explanation,
                        storeTouched=false,
                        evidence="READ_ONLY_LOCAL_ASA_PROCESS_AND_RCON_READINESS"
                    });
                }
                if(method=="servers.freshStartStatus")
                {
                    if(!ReadGuid(item,"mapId",out var mapId))
                        return Error(id,"UM-FRESH-MAP");
                    var map=Owned(mapId);
                    if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                    var observed=cluster.Check(map);
                    return Ok(id,new {
                        mapId=map.Id,mapName=map.Name,
                        running=observed.Running,
                        confirmedStopped=observed.ConfirmedStopped,
                        pid=observed.Identity?.Pid,
                        readyCertified=false,
                        status=observed.Status,
                        evidence="LOCAL_OS_PROCESS_OBSERVATION_NOT_ASA_READINESS"
                    });
                }
                if(method=="servers.freshStartPreview")
                {
                    if(!ReadGuid(item,"mapId",out var mapId))
                        return Error(id,"UM-FRESH-MAP");
                    var map=Owned(mapId);
                    if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                    if(!Eligible(map))return Error(id,"UM-FRESH-START-NOT-READY");
                    pending=new Approval(Guid.NewGuid(),map.Id,map.InstallationPath,
                        map.ExecutablePath,map.Arguments,map.GamePort,map.QueryPort,
                        map.RconPort,AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath),
                        DateTimeOffset.UtcNow);
                    return Ok(id,new {
                        token=pending.Token,mapId=map.Id,mapName=map.Name,
                        steamBuild=pending.Build,gamePort=map.GamePort,
                        queryPort=map.QueryPort,rconConfigured=map.RconPort>0 &&
                            !string.IsNullOrWhiteSpace(map.RconPasswordProtected),
                        confirmedStopped=true,serverStarted=false,
                        evidence="FRESH_LOCAL_ASA_START_PREVIEW_NO_WRITE"
                    });
                }
                if(!ReadGuid(item,"token",out var token) || pending==null ||
                   pending.Token!=token ||
                   DateTimeOffset.UtcNow-pending.CreatedUtc>TimeSpan.FromMinutes(5))
                    return Error(id,"UM-FRESH-START-TOKEN-EXPIRED");
                if(!item.TryGetProperty("confirm",out var confirmation) ||
                   confirmation.ValueKind!=JsonValueKind.True)
                    return Error(id,"UM-FRESH-START-APPROVAL");
                var permit=pending;
                pending=null; // a start is never replayed, even after an error
                var current=Owned(permit.Map);
                if(current==null || !Eligible(current) ||
                   !string.Equals(current.InstallationPath,permit.Root,
                       StringComparison.OrdinalIgnoreCase) ||
                   !string.Equals(current.ExecutablePath,permit.Executable,
                       StringComparison.OrdinalIgnoreCase) ||
                   current.Arguments!=permit.Arguments ||
                   current.GamePort!=permit.Game || current.QueryPort!=permit.Query ||
                   current.RconPort!=permit.Rcon ||
                   AsaSteamBuildCatalog.InstalledBuild(current.InstallationPath)!=permit.Build)
                    return Error(id,"UM-FRESH-START-CHANGED");
                var identity=launch(current.Id,true);
                if(identity.Pid<=0 || identity.StartedUtc==default)
                    return Error(id,"UM-FRESH-START-UNVERIFIED-PROCESS");
                return Ok(id,new{
                    mapId=current.Id,mapName=current.Name,pid=identity.Pid,
                    createdUtc=identity.StartedUtc,
                    startRequested=true,readyCertified=false,
                    requiresRconReadiness=true,storeTouched=false,
                    evidence="LOCAL_REAL_ASA_PROCESS_STARTED_NOT_READY"
                });
            }
            finally{gate.Release();}
        }
        catch(Exception)
        {
            // Do not leak launch arguments, passwords, Store identifiers, or
            // absolute install paths to untrusted renderer messages.
            return Error(id,"UM-FRESH-START-FAILED");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string message)=>
        JsonSerializer.Serialize(new{id,ok=false,error=message},Json);
    public void Dispose()=>gate.Dispose();
}
