using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Local new-Store ASA API Loader mode switch. No historical Kova map, command
/// line, transfer writer, live process, or remote host can enter this route.
/// A selected mode is configuration ONLY: no server spawn, RCON or cache write.
/// </summary>
public sealed class DesktopFreshStoreLoaderModeBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,string Profile,
        bool UseApi,string Loader,DateTimeOffset Created);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim serial=new(1,1);
    private Approval? pending;

    public DesktopFreshStoreLoaderModeBridge(ClusterService service)=>
        cluster=service??throw new ArgumentNullException(nameof(service));

    private LocalMap? Owned(Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id);

    private static bool ReadGuid(JsonElement request,string name,out Guid value)
    {
        value=Guid.Empty;
        return request.TryGetProperty(name,out var field)&&
            field.ValueKind==JsonValueKind.String&&
            Guid.TryParse(field.GetString(),out value)&&value!=Guid.Empty;
    }

    private static bool SamePath(string a,string b)=>
        string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);

    private (string Loader,string CacheState) Preflight(LocalMap map,bool useApi)
    {
        FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);
        if(map.HistoricalStoreProtected||map.Maintenance||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||
           !AsaRconConfigurationEvidence.Matches(map))
            throw new InvalidOperationException("UM-FRESH-STORE-API-PROTECTED");
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        var journal=cluster.Operations.Observe(map.InstallationPath);
        if(journal?.Phase is UntamedOperationPhase.Prepared or
            UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)
            throw new InvalidOperationException("UM-FRESH-STORE-API-OPERATION-PENDING");
        var state=cluster.Check(map);
        if(!state.ConfirmedStopped||state.Running||
           state.Identity!=null||state.LoaderIdentity!=null)
            throw new InvalidOperationException("UM-FRESH-STORE-API-PROCESS-NOT-STOPPED");
        string binary=Path.Combine(Path.GetFullPath(map.InstallationPath),
            "ShooterGame","Binaries","Win64");
        string loader=Path.Combine(binary,"AsaApiLoader.exe");
        if(!string.IsNullOrWhiteSpace(map.ApiLoaderPath)&&
           !SamePath(map.ApiLoaderPath,loader))
            throw new InvalidDataException("UM-FRESH-STORE-API-LOADER-PATH-CHANGED");
        if(!string.IsNullOrWhiteSpace(map.ApiLoaderArguments)&&
           map.ApiLoaderArguments!=map.Arguments)
            throw new InvalidDataException("UM-FRESH-STORE-API-ARGS-CHANGED");

        // Verify the EXACT mode that would be saved; do not use a supplied
        // renderer path or arguments. A matching cache key does not certify
        // native API initialization; the official Loader owns acquisition.
        var copy=JsonSerializer.Deserialize<LocalMap>(JsonSerializer.Serialize(map))
            ??throw new InvalidDataException("UM-FRESH-STORE-API-CLONE");
        copy.StartWithAsaApi=useApi;
        copy.ApiLoaderPath=loader;
        copy.ApiLoaderArguments=map.Arguments;
        var plan=AsaApiLaunchPolicy.Resolve(copy);
        if(plan.UseApi!=useApi||
           !SamePath(plan.ExecutablePath,useApi?loader:map.ExecutablePath)||
           plan.Arguments!=map.Arguments)
            throw new InvalidDataException("UM-FRESH-STORE-API-PLAN-CHANGED");
        string cache=useApi?
            AsaApiCacheProbe.Inspect(map.InstallationPath,map.ExecutablePath).State:
            "UM-API-CACHE-NOT-APPLICABLE";
        return(loader,cache);
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3072)return Fail(null,"UM-FRESH-STORE-API-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var req=doc.RootElement;
            if(req.ValueKind!=JsonValueKind.Object||
               !ReadGuid(req,"id",out var requestId)||
               !req.TryGetProperty("method",out var methodElement)||
               methodElement.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-FRESH-STORE-API-REQUEST");
            id=requestId.ToString("D");
            string? method=methodElement.GetString();
            if(method is not ("freshStore.loaderModeStatus" or
                "freshStore.loaderModePreview" or "freshStore.loaderModeApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="freshStore.loaderModeStatus")
                {
                    if(!ReadGuid(req,"mapId",out var mapId))
                        return Fail(id,"UM-FRESH-STORE-API-MAP");
                    var map=Owned(mapId);
                    if(map==null||map.HistoricalStoreProtected||
                       !FreshStoreEnrollmentPolicy.IsEnrolled(map))
                        return Fail(id,"UM-FRESH-STORE-API-NOT-ENROLLED");
                    bool eligible=false;
                    string cache="UM-API-CACHE-NOT-APPLICABLE";
                    try
                    {
                        (_,cache)=Preflight(map,map.StartWithAsaApi);
                        eligible=true;
                    }
                    catch(Exception ex) when(ex is InvalidDataException or
                        InvalidOperationException or IOException or UnauthorizedAccessException){}
                    return Ok(id,new {
                        mapId,mapName=map.Name,
                        useApi=map.StartWithAsaApi,
                        mode=map.StartWithAsaApi?"asaApiLoader":"directAsa",
                        confirmedStopped=eligible,
                        canChangeMode=eligible,
                        cacheState=cache,
                        cacheRuntimeCertified=false,
                        serverStarted=false,
                        originalStoreTouched=false,
                        evidence="NEW_STORE_LOCAL_LAUNCH_MODE_READ_ONLY"
                    });
                }
                if(method=="freshStore.loaderModePreview")
                {
                    pending=null;
                    if(!ReadGuid(req,"mapId",out var mapId)||
                       !req.TryGetProperty("useApi",out var choice)||
                       choice.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Fail(id,"UM-FRESH-STORE-API-SELECTION");
                    var map=Owned(mapId);
                    bool useApi=choice.GetBoolean();
                    if(map==null||map.StartWithAsaApi==useApi)
                        return Fail(id,"UM-FRESH-STORE-API-UNCHANGED");
                    var (loader,cache)=await Task.Run(()=>Preflight(map,useApi));
                    pending=new Approval(Guid.NewGuid(),map.Id,
                        JsonSerializer.Serialize(map),useApi,loader,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pending.Token,mapId,mapName=map.Name,useApi,
                        mode=useApi?"asaApiLoader":"directAsa",
                        cacheState=cache,cacheRuntimeCertified=false,
                        startsServer=false,changesStore=false,
                        requiresStoppedProcess=true,
                        evidence="NEW_STORE_LOCAL_API_MODE_PREVIEW_NO_WRITE"
                    });
                }

                if(!ReadGuid(req,"token",out var token)||pending==null||
                   pending.Token!=token||
                   DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-FRESH-STORE-API-APPROVAL-EXPIRED");
                if(!req.TryGetProperty("confirm",out var confirmed)||
                   confirmed.ValueKind!=JsonValueKind.True||
                   !req.TryGetProperty("typedApproval",out var phrase)||
                   phrase.ValueKind!=JsonValueKind.String||
                   phrase.GetString()!="FRESH STORE API MODE")
                    return Fail(id,"UM-FRESH-STORE-API-APPROVAL-REQUIRED");
                var approval=pending;
                pending=null; // one-shot, even when something changed
                var original=Owned(approval.MapId);
                if(original==null||original.StartWithAsaApi==approval.UseApi||
                   JsonSerializer.Serialize(original)!=approval.Profile)
                    return Fail(id,"UM-FRESH-STORE-API-PROFILE-CHANGED");
                var (loaderPath,_)=await Task.Run(()=>Preflight(original,approval.UseApi));
                if(!SamePath(loaderPath,approval.Loader))
                    return Fail(id,"UM-FRESH-STORE-API-LOADER-CHANGED");
                using var lease=cluster.Operations.Acquire(
                    original.InstallationPath,UntamedOperationKind.Configuration);
                if(!ReferenceEquals(Owned(approval.MapId),original)||
                   JsonSerializer.Serialize(original)!=approval.Profile)
                    return Fail(id,"UM-FRESH-STORE-API-LEASE-PROFILE-CHANGED");
                var (lockedPath,_cache)=Preflight(original,approval.UseApi);
                if(!SamePath(lockedPath,approval.Loader))
                    return Fail(id,"UM-FRESH-STORE-API-LEASE-CHANGED");
                var updated=JsonSerializer.Deserialize<LocalMap>(
                    JsonSerializer.Serialize(original))
                    ??throw new InvalidDataException("UM-FRESH-STORE-API-CLONE");
                updated.StartWithAsaApi=approval.UseApi;
                // Retain the original Loader path and exact launch grammar
                // even after opting out so any surviving Loader child remains
                // observable and a direct Start cannot bypass family checks.
                updated.ApiLoaderPath=lockedPath;
                updated.ApiLoaderArguments=updated.Arguments;
                lease.MarkMutationStarted();
                cluster.ReplaceMap(original,updated);
                lease.Complete();
                return Ok(id,new{
                    mapId=updated.Id,mapName=updated.Name,
                    useApi=updated.StartWithAsaApi,
                    mode=updated.StartWithAsaApi?"asaApiLoader":"directAsa",
                    serverStarted=false,originalStoreTouched=false,
                    requiresManualStart=true,
                    evidence="NEW_STORE_LOCAL_API_MODE_CONFIG_ONLY"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            // Never expose RCON passwords, physical transfer paths or
            // launcher command lines to WebView.
            return Fail(id,"UM-FRESH-STORE-API-SAFETY-FAILED");
        }
    }

    private static string Ok(string? id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>serial.Dispose();
}
