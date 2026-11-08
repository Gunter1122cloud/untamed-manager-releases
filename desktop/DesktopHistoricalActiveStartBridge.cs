using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit historical ACTIVE/DONE Store-v1 launch, not a fake-server demo
/// and not a Store converter. The old Manager session, original writer
/// records and shared transfer root remain byte-identical. Only an owned
/// locally registered map with real RCON handoff can reach this route.
/// All native originals/command line remain invisible to React.
/// </summary>
public sealed class DesktopHistoricalActiveStartBridge : IDisposable
{
    private sealed record Pending(Guid Token,Guid MapId,string ProfileJson,
        string PlanFingerprint,int Game,int Query,int Rcon,bool UseApi,DateTimeOffset Created);
    private readonly ClusterService cluster;
    private readonly Func<LocalMap,LegacyStoreV1NativeStartPlan> prepare;
    private readonly Func<Guid,string,bool,ProcessIdentity> start;
    private readonly SemaphoreSlim serial=new(1,1);
    private Pending? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopHistoricalActiveStartBridge(ClusterService service):
        this(service,null,null){}

    // Dependency injection only for synthetic native Windows tests; never
    // exposed to WebView, runtime plugins or the remote LAN status protocol.
    internal DesktopHistoricalActiveStartBridge(ClusterService service,
        Func<LocalMap,LegacyStoreV1NativeStartPlan>? preparation,
        Func<Guid,string,bool,ProcessIdentity>? launcher)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        prepare=preparation??(map=>LegacyStoreV1NativeStartPlanner.Prepare(cluster,map));
        start=launcher??((map,fingerprint,approved)=>
            cluster.LaunchOriginalActiveStoreAsa(map,fingerprint,approved));
    }

    private static bool ReadId(JsonElement root,string key,out Guid id)
    {
        id=Guid.Empty;
        return root.TryGetProperty(key,out var value)&&
            value.ValueKind==JsonValueKind.String&&
            Guid.TryParse(value.GetString(),out id)&&id!=Guid.Empty;
    }
    private LocalMap? Owned(Guid mapId)=>
        cluster.Config.LocalMaps.SingleOrDefault(map=>map.Id==mapId);
    private static bool Imported(LocalMap? map)=>
        map!=null&&map.Id!=Guid.Empty&&map.HistoricalStoreProtected&&
        !map.Maintenance&&map.RconPort is >=1024 and <=65535&&
        !string.IsNullOrWhiteSpace(map.RconPasswordProtected)&&
        string.IsNullOrWhiteSpace(map.StoreTestRoot)&&
        string.IsNullOrWhiteSpace(map.Arguments)&&
        string.IsNullOrWhiteSpace(map.ApiLoaderArguments)&&
        (!map.StartWithAsaApi||!string.IsNullOrWhiteSpace(map.ApiLoaderPath));

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3072)return Fail(null,"UM-STORE-ACTIVE-REQUEST");
            using var parsed=JsonDocument.Parse(raw);
            var request=parsed.RootElement;
            if(request.ValueKind!=JsonValueKind.Object||
               !ReadId(request,"id",out var requestId)||
               !request.TryGetProperty("method",out var route)||
               route.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-STORE-ACTIVE-REQUEST");
            id=requestId.ToString("D");
            string? method=route.GetString();
            if(method is not ("historical.activeStartStatus" or
                "historical.activeStartPreview" or "historical.activeStartApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="historical.activeStartStatus")
                {
                    if(!ReadId(request,"mapId",out var mapId))
                        return Fail(id,"UM-STORE-ACTIVE-MAP");
                    var map=Owned(mapId);
                    if(map==null||!map.HistoricalStoreProtected)
                        return Fail(id,"UM-STORE-ACTIVE-LOCAL-ONLY");
                    var observed=cluster.ObserveLocalProcessesReadOnly(map);
                    return Ok(id,new{
                        mapId,mapName=map.Name,running=observed.Running,
                        confirmedStopped=observed.ConfirmedStopped,
                        pid=observed.Identity?.Pid,
                        serverPid=observed.Identity?.Pid,
                        loaderPid=observed.LoaderIdentity?.Pid,
                        requestedMode=map.StartWithAsaApi?"asaApiLoader":"directAsa",
                        rconConfigured=map.RconPort>0&&
                            !string.IsNullOrWhiteSpace(map.RconPasswordProtected),
                        originalStartAutomatic=false,
                        saveCompleteProven=false,gameplayTravelVerified=false,
                        evidence="HISTORICAL_ORIGINAL_PROCESS_READ_ONLY_STATUS"
                    });
                }
                if(method=="historical.activeStartPreview")
                {
                    pending=null;
                    if(!ReadId(request,"mapId",out var mapId))
                        return Fail(id,"UM-STORE-ACTIVE-MAP");
                    var map=Owned(mapId);
                    if(!Imported(map))return Fail(id,"UM-STORE-ACTIVE-RCON-REQUIRED");
                    var plan=await Task.Run(()=>prepare(map!));
                    if(plan==null||plan.Fingerprint.Length!=64||
                       plan.UseApi!=map!.StartWithAsaApi||
                       plan.Arguments.Length is <16 or >8192)
                        return Fail(id,"UM-STORE-ACTIVE-PREFLIGHT-INCOMPLETE");
                    var permit=new Pending(Guid.NewGuid(),map!.Id,
                        JsonSerializer.Serialize(map),plan.Fingerprint,
                        plan.GamePort,plan.QueryPort,plan.RconPort,
                        plan.UseApi,DateTimeOffset.UtcNow);
                    pending=permit;
                    return Ok(id,new{
                        token=permit.Token,mapId=map.Id,mapName=map.Name,
                        gamePort=permit.Game,queryPort=permit.Query,
                        rconPort=permit.Rcon,launchMode=permit.UseApi?"asaApiLoader":"directAsa",
                        originalTwoPeersVerified=true,
                        conversionAllowed=false,automaticRetryAllowed=false,
                        serverStarted=false,readyCertified=false,
                        gameplayTravelVerified=false,
                        evidence="ORIGINAL_ACTIVE_STORE_NATIVE_START_PREFLIGHT"
                    });
                }
                if(!ReadId(request,"token",out var token)||pending==null||
                   pending.Token!=token||
                   DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-STORE-ACTIVE-APPROVAL-EXPIRED");
                if(!request.TryGetProperty("confirm",out var confirmed)||
                   confirmed.ValueKind!=JsonValueKind.True||
                   !request.TryGetProperty("typedApproval",out var phrase)||
                   phrase.ValueKind!=JsonValueKind.String||
                   phrase.GetString()!="STORE-V1 ACTIVE START")
                    return Fail(id,"UM-STORE-ACTIVE-EXPLICIT-APPROVAL");
                var approved=pending;pending=null;
                var live=Owned(approved.MapId);
                if(!Imported(live)||
                   JsonSerializer.Serialize(live)!=approved.ProfileJson)
                    return Fail(id,"UM-STORE-ACTIVE-MAP-CHANGED");
                var current=await Task.Run(()=>prepare(live!));
                if(current.Fingerprint!=approved.PlanFingerprint||
                   current.GamePort!=approved.Game||current.QueryPort!=approved.Query||
                   current.RconPort!=approved.Rcon||current.UseApi!=approved.UseApi)
                    return Fail(id,"UM-STORE-ACTIVE-ORIGINAL-SOURCE-CHANGED");
                // Process.Start and journal persistence run on the native
                // Windows worker. This is an actual ASA process start.
                var identity=await Task.Run(()=>start(live!.Id,approved.PlanFingerprint,true));
                string expectedExe=approved.UseApi?live!.ApiLoaderPath:live!.ExecutablePath;
                if(identity.Pid<=0||identity.StartedUtc==default||
                   !string.Equals(Path.GetFullPath(identity.ExecutablePath),
                       Path.GetFullPath(expectedExe),StringComparison.OrdinalIgnoreCase))
                    return Fail(id,"UM-STORE-ACTIVE-PROCESS-UNVERIFIED");
                return Ok(id,new{
                    mapId=live!.Id,mapName=live.Name,
                    pid=identity.Pid,createdUtc=identity.StartedUtc,
                    processRole=approved.UseApi?"loaderParent":"asaServer",
                    launchMode=approved.UseApi?"asaApiLoader":"directAsa",
                    loaderPid=approved.UseApi?(int?)identity.Pid:null,
                    serverPid=approved.UseApi?(int?)null:identity.Pid,
                    startRequested=true,readyCertified=false,
                    originalStoreMetadataChanged=false,conversionPerformed=false,
                    gameClientRejoinVerified=false,automaticRetryAllowed=false,
                    evidence=approved.UseApi?
                        "ORIGINAL_STORE_V1_LOADER_PARENT_SPAWNED_CHILD_UNVERIFIED":
                        "ORIGINAL_STORE_V1_REAL_LOCAL_ASA_START_PID_OBSERVED"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-STORE-ACTIVE-START-SAFETY-FAILED");
        }
    }
    private static string Ok(string id,object payload)=>
        JsonSerializer.Serialize(new{id,ok=true,data=payload},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>serial.Dispose();
}
