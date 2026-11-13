using System;
using System.Collections.Generic;
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
    // Only literal native safety codes are returned to WebView. Never forward
    // exception details, paths, credentials or original Legacy source records.
    private static readonly HashSet<string> SafeStartErrors = new(StringComparer.Ordinal)
    {
        "UM-STORE-LAUNCH-MODE-APPROVAL-REQUIRED",
        "UM-STORE-NATIVE-ACTIVE-IMPORT-ONLY",
        "UM-STORE-NATIVE-EXE-RCON-PARITY",
        "UM-STORE-NATIVE-BACKUP-RECOVERY",
        "UM-STORE-NATIVE-PORT-IN-USE",
        "UM-STORE-NATIVE-PROCESS-NOT-PROVEN-STOPPED",
        "UM-STORE-NATIVE-ORIGINAL-TWO-HOST-NOT-READY",
        "UM-STORE-NATIVE-RCON-CHANGED",
        "UM-STORE-RUNTIME-CLUSTER-INCOMPLETE",
        "UM-STORE-RUNTIME-RCON-INI-MISMATCH",
        "UM-STORE-RUNTIME-CONVERSION-DENIED",
        "UM-STORE-RUNTIME-ARGUMENTS-TOO-LONG",
        "UM-STORE-PIN-MANIFEST-MISSING",
        "UM-STORE-PIN-MANIFEST-INVALID",
        "UM-STORE-PIN-FILE-CHANGED",
        "UM-STORE-PIN-SOURCE-PARITY",
        "UM-STORE-PIN-NOT-A-DIRECTORY",
        "UM-STORE-PIN-MAP-IDENTITY",
        "UM-STORE-PIN-OWNER-CHANGED",
        "UM-STORE-PIN-MAP-ROOT-CHANGED",
        "UM-STORE-PIN-CLUSTER-METADATA",
        "UM-STORE-PIN-NOT-YET-APPROVED",
        "UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR"
    };
    private sealed record Pending(Guid Token,Guid MapId,string ProfileJson,
        string PlanFingerprint,int Game,int Query,int Rcon,bool UseApi,DateTimeOffset Created);
    private sealed record ModePending(Guid Token,Guid MapId,string Profile,
        bool UseApi,string LoaderPath,DateTimeOffset Created);
    private readonly ClusterService cluster;
    private readonly Func<LocalMap,LegacyStoreV1NativeStartPlan> prepare;
    private readonly Func<Guid,string,bool,ProcessIdentity> start;
    private readonly bool nativeLauncher;
    private readonly Func<LocalMap,bool,bool> modePreflight;
    private readonly SemaphoreSlim serial=new(1,1);
    private Pending? pending;
    private ModePending? pendingMode;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopHistoricalActiveStartBridge(ClusterService service):
        this(service,null,null){}

    // Dependency injection only for synthetic native Windows tests; never
    // exposed to WebView, runtime plugins or the remote LAN status protocol.
    internal DesktopHistoricalActiveStartBridge(ClusterService service,
        Func<LocalMap,LegacyStoreV1NativeStartPlan>? preparation,
        Func<Guid,string,bool,ProcessIdentity>? launcher,
        Func<LocalMap,bool,bool>? loaderModePreflight=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        prepare=preparation??(map=>LegacyStoreV1NativeStartPlanner.Prepare(cluster,map));
        nativeLauncher=launcher==null;
        start=launcher??((map,fingerprint,approved)=>
            cluster.LaunchOriginalActiveStoreAsa(map,fingerprint,approved));
        modePreflight=loaderModePreflight??PhysicalModePreflight;
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

    // Choosing the NEXT launch mode is independent of RCON attachment.
    // Start still retains all original RCON and Store/session preconditions.
    private static bool ModeOwned(LocalMap? map)=>
        map!=null&&map.Id!=Guid.Empty&&map.HistoricalStoreProtected&&
        !map.ObservedReadOnlyImported&&!map.Maintenance&&
        !string.IsNullOrWhiteSpace(map.HistoricalMapKey)&&
        map.HistoricalArchiveSha256?.Length==64&&
        map.HistoricalArchiveSha256.All(Uri.IsHexDigit)&&
        string.IsNullOrWhiteSpace(map.StoreTestRoot)&&
        string.IsNullOrWhiteSpace(map.Arguments)&&
        string.IsNullOrWhiteSpace(map.ApiLoaderArguments);

    private bool PhysicalModePreflight(LocalMap map,bool useApi)
    {
        if(!AsaReadyProbe.IsRegisteredServerBinary(map)||
           LegacyCompatibleWorldBackup.InstallationRecoveryRequired(map))
            return false;
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        var existing=cluster.Operations.Observe(map.InstallationPath);
        if(existing?.Phase is UntamedOperationPhase.Prepared or
            UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)
            return false;
        // A settings change may be committed ONLY while the registered local
        // process family is unequivocally stopped. No synthetic UI status.
        var stopped=LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(
            map,cluster.Config.LocalMaps);
        if(!stopped.ConfirmedStopped||stopped.Running||
           stopped.Identity!=null||stopped.LoaderIdentity!=null)
            return false;
        if(useApi)
        {
            var inspection=AsaApiInspector.Inspect(map.InstallationPath);
            if(!inspection.Complete)return false;
        }
        return true;
    }

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
                "historical.activeStartPreview" or "historical.activeStartApply" or
                "historical.loaderModeStatus" or "historical.loaderModePreview" or
                "historical.loaderModeApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="historical.loaderModeStatus")
                {
                    if(!ReadId(request,"mapId",out var mapId))
                        return Fail(id,"UM-STORE-LOADER-MODE-MAP");
                    var map=Owned(mapId);
                    if(!ModeOwned(map))
                        return Fail(id,"UM-STORE-LOADER-MODE-NOT-OWNED");
                    bool directReady=false,apiReady=false;
                    try
                    {
                        directReady=modePreflight(map!,false);
                        apiReady=modePreflight(map!,true);
                    }
                    catch(Exception ex) when(ex is IOException or
                        InvalidOperationException or UnauthorizedAccessException or
                        ArgumentException){}
                    bool canChange=map!.StartWithAsaApi?directReady:apiReady;
                    if(map.HistoricalLaunchModeNeedsApproval)
                        canChange|=directReady;
                    return Ok(id,new{
                        mapId,mapName=map.Name,useApi=map.StartWithAsaApi,
                        canChangeMode=canChange,
                        confirmedStopped=directReady||apiReady,
                        approvalRequired=map.HistoricalLaunchModeNeedsApproval,
                        mode=map.StartWithAsaApi?"asaApiLoader":"directAsa",
                        requiresStoppedProcess=true,
                        serverStarted=false,originalStoreTouched=false,
                        evidence="HISTORICAL_LOCAL_API_LAUNCH_PREFERENCE_STATUS"
                    });
                }
                if(method=="historical.loaderModePreview")
                {
                    pendingMode=null;pending=null;
                    if(!ReadId(request,"mapId",out var mapId)||
                       !request.TryGetProperty("useApi",out var setting)||
                       setting.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Fail(id,"UM-STORE-LOADER-MODE-SELECTION");
                    bool useApi=setting.GetBoolean();
                    var map=Owned(mapId);
                    if(!ModeOwned(map)||
                       (map!.StartWithAsaApi==useApi&&
                        !map.HistoricalLaunchModeNeedsApproval))
                        return Fail(id,"UM-STORE-LOADER-MODE-UNCHANGED-OR-NOT-OWNED");
                    string loader=Path.Combine(Path.GetDirectoryName(
                        Path.GetFullPath(map.ExecutablePath))!,"AsaApiLoader.exe");
                    if(map.ApiLoaderPath.Length>0&&
                       !Path.GetFullPath(map.ApiLoaderPath).Equals(loader,
                           StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-STORE-LOADER-MODE-PATH-MISMATCH");
                    if(!modePreflight(map,useApi))
                        return Fail(id,"UM-STORE-LOADER-MODE-PROCESS-OR-API-UNVERIFIED");
                    pendingMode=new ModePending(Guid.NewGuid(),map.Id,
                        JsonSerializer.Serialize(map),useApi,loader,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pendingMode.Token,mapId,mapName=map.Name,useApi,
                        mode=useApi?"asaApiLoader":"directAsa",
                        startsServer=false,changesStore=false,
                        requiresStoppedProcess=true,
                        evidence="LOCAL_HISTORICAL_API_LAUNCH_PREFERENCE_ONLY"
                    });
                }
                if(method=="historical.loaderModeApply")
                {
                    if(!ReadId(request,"token",out var modeToken)||
                       pendingMode==null||pendingMode.Token!=modeToken||
                       DateTimeOffset.UtcNow-pendingMode.Created>TimeSpan.FromMinutes(3)||
                       !request.TryGetProperty("confirm",out var modeConfirm)||
                       modeConfirm.ValueKind!=JsonValueKind.True)
                        return Fail(id,"UM-STORE-LOADER-MODE-APPROVAL");
                    var requested=pendingMode;pendingMode=null;pending=null;
                    var map=Owned(requested.MapId);
                    if(!ModeOwned(map)||
                       (map!.StartWithAsaApi==requested.UseApi&&
                        !map.HistoricalLaunchModeNeedsApproval)||
                       JsonSerializer.Serialize(map)!=requested.Profile)
                        return Fail(id,"UM-STORE-LOADER-MODE-PROFILE-CHANGED");
                    if(!modePreflight(map,requested.UseApi))
                        return Fail(id,"UM-STORE-LOADER-MODE-STATE-CHANGED");
                    using var operation=cluster.Operations.Acquire(
                        map.InstallationPath,UntamedOperationKind.Configuration);
                    if(JsonSerializer.Serialize(map)!=requested.Profile||
                       !modePreflight(map,requested.UseApi))
                        return Fail(id,"UM-STORE-LOADER-MODE-LEASE-CHANGED");
                    bool before=map.StartWithAsaApi;
                    bool neededApproval=map.HistoricalLaunchModeNeedsApproval;
                    string priorPath=map.ApiLoaderPath;
                    operation.MarkMutationStarted();
                    map.StartWithAsaApi=requested.UseApi;
                    map.HistoricalLaunchModeNeedsApproval=false;
                    if(requested.UseApi)map.ApiLoaderPath=requested.LoaderPath;
                    try{cluster.Save();}
                    catch
                    {
                        map.StartWithAsaApi=before;
                        map.HistoricalLaunchModeNeedsApproval=neededApproval;
                        map.ApiLoaderPath=priorPath;
                        throw;
                    }
                    operation.Complete();
                    return Ok(id,new{
                        mapId=map.Id,mapName=map.Name,
                        useApi=map.StartWithAsaApi,
                        mode=map.StartWithAsaApi?"asaApiLoader":"directAsa",
                        serverStarted=false,originalStoreTouched=false,
                        evidence="LOCAL_HISTORICAL_API_LAUNCH_PREFERENCE_SAVED"
                    });
                }
                if(method=="historical.activeStartStatus")
                {
                    if(!ReadId(request,"mapId",out var mapId))
                        return Fail(id,"UM-STORE-ACTIVE-MAP");
                    var map=Owned(mapId);
                    if(map==null||!map.HistoricalStoreProtected)
                        return Fail(id,"UM-STORE-ACTIVE-LOCAL-ONLY");
                    var observed=cluster.ObserveLocalProcessesReadOnly(map);
                    bool confirmedStopped=observed.ConfirmedStopped;
                    if(!observed.Running&&!confirmedStopped&&
                       observed.Identity==null&&observed.LoaderIdentity==null)
                    {
                        // Ordinary original Store observation is intentionally
                        // conservative and may report "unknown" even when no
                        // ASA process exists. Only the DEDICATED native
                        // stopped-family check can enable mode selection.
                        // Missing WMI/EXE metadata still denies permission.
                        var stopped=LegacyStoreV1ProcessObserver.
                            ObserveStoppedForNativeStart(map,cluster.Config.LocalMaps);
                        confirmedStopped=stopped.ConfirmedStopped&&
                            !stopped.Running&&stopped.Identity==null&&
                            stopped.LoaderIdentity==null;
                    }
                    return Ok(id,new{
                        mapId,mapName=map.Name,running=observed.Running,
                        confirmedStopped,
                        pid=observed.Identity?.Pid,
                        serverPid=observed.Identity?.Pid,
                        loaderPid=observed.LoaderIdentity?.Pid,
                        requestedMode=map.StartWithAsaApi?"asaApiLoader":"directAsa",
                        launchModeApprovalRequired=map.HistoricalLaunchModeNeedsApproval,
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
                    if(map!.HistoricalLaunchModeNeedsApproval)
                        return Fail(id,"UM-STORE-LAUNCH-MODE-APPROVAL-REQUIRED");
                    var plan=await Task.Run(()=>prepare(map));
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
                        apiCacheState=plan.ApiCacheState,
                        apiCacheRuntimeCertified=false,
                        // Local pinned starts must work with the other host
                        // offline. Never claim its obsolete DONE marker was
                        // inspected when the native plan skipped that check.
                        originalTwoPeersVerified=plan.OriginalTwoPeersVerified,
                        originalPeerCheckRequiredForLocalStart=plan.OriginalTwoPeersVerified,
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
                if(approved.UseApi&&nativeLauncher)
                {
                    // Non-mutating, bounded observation after approved spawn.
                    // Persist child identity ONLY after the actual Windows
                    // parent-child relationship and child-owned RCON port
                    // are independently reobserved. No RCON is dispatched.
                    // Failed/unknown observation NEVER retries Process.Start.
                    _=Task.Run(async()=>{
                        try
                        {
                            var until=DateTimeOffset.UtcNow.AddMinutes(3);
                            while(DateTimeOffset.UtcNow<until)
                            {
                                var seen=LegacyStoreV1ProcessObserver.Observe(
                                    live,cluster.Config.LocalMaps);
                                if(seen.Running&&
                                   seen.Identity is {Pid:>0} child&&
                                   seen.LoaderIdentity is {Pid:>0} parent&&
                                   parent.Pid==identity.Pid&&
                                   parent.StartedUtc==identity.StartedUtc&&
                                   RconPortOwnership.IsOwnedListener(live.RconPort,child.Pid))
                                {
                                    cluster.RecordOriginalStoreApiPair(live.Id,seen);
                                    return;
                                }
                                await Task.Delay(850);
                            }
                        }
                        catch
                        {
                            // Keep the unverified state and operation evidence;
                            // never infer success or start another process.
                        }
                    });
                }
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
        catch(Exception e) when(e is InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or IOException)
        {
            return Fail(id,SafeStartErrors.Contains(e.Message)?e.Message:
                "UM-STORE-ACTIVE-START-SAFETY-FAILED");
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
