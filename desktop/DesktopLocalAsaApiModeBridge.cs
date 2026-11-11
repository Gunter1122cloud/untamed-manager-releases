using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Per-map ASA API opt-in for ordinary, locally owned non-Store ASA maps.
/// Historical Kova and new Store maps retain their own existing native
/// approval bridges. This bridge changes only the Manager launch profile;
/// it never starts/stops ASA, edits INI, plugins or Store/session files.
/// </summary>
public sealed class DesktopLocalAsaApiModeBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,string Profile,
        bool UseApi,string Loader,DateTimeOffset CreatedUtc);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim serial=new(1,1);
    private Approval? pending;

    public DesktopLocalAsaApiModeBridge(ClusterService service)=>
        cluster=service??throw new ArgumentNullException(nameof(service));

    private LocalMap? Owned(Guid id)=>cluster.Config.LocalMaps
        .SingleOrDefault(m=>m.Id==id);

    private static bool ReadGuid(JsonElement obj,string field,out Guid id)
    {
        id=Guid.Empty;
        return obj.TryGetProperty(field,out var item)&&
            item.ValueKind==JsonValueKind.String&&
            Guid.TryParse(item.GetString(),out id)&&id!=Guid.Empty;
    }

    private static bool Same(string first,string second)=>
        string.Equals(Path.GetFullPath(first),Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);

    private string Preflight(LocalMap map,bool chooseApi,bool underLease=false)
    {
        if(map.Id==Guid.Empty||!cluster.Config.LocalMaps.Contains(map)||
           map.HistoricalStoreProtected||map.ObservedReadOnlyImported||
           FreshStoreEnrollmentPolicy.IsEnrolled(map)||map.Maintenance||
           !string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)||
           !string.IsNullOrWhiteSpace(map.HistoricalMapKey)||
           !string.IsNullOrWhiteSpace(map.FreshStoreClusterId)||
           !string.IsNullOrWhiteSpace(map.FreshStoreTransferDirectory)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map)||
           KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
            throw new UnauthorizedAccessException("UM-LOCAL-API-STORE-PROTECTED");

        if(string.IsNullOrWhiteSpace(map.InstallationPath)||
           string.IsNullOrWhiteSpace(map.ExecutablePath)||
           !Path.IsPathFullyQualified(map.InstallationPath)||
           !Path.IsPathFullyQualified(map.ExecutablePath)||
           map.InstallationPath.StartsWith(@"\\",StringComparison.Ordinal)||
           map.Arguments.Contains("-usestore",StringComparison.OrdinalIgnoreCase)||
           map.Arguments.Contains("-converttostore",StringComparison.OrdinalIgnoreCase)||
           map.Arguments.Contains("-convertfromstore",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-LOCAL-API-INSTALL-INVALID");

        var root=Path.GetFullPath(map.InstallationPath)
            .TrimEnd(Path.DirectorySeparatorChar);
        string expectedServer=Path.Combine(root,"ShooterGame","Binaries",
            "Win64","ArkAscendedServer.exe");
        string loader=Path.Combine(root,"ShooterGame","Binaries",
            "Win64","AsaApiLoader.exe");
        if(!Same(map.ExecutablePath,expectedServer)||
           (!string.IsNullOrWhiteSpace(map.ApiLoaderPath)&&
            !Same(map.ApiLoaderPath,loader))||
           (!string.IsNullOrWhiteSpace(map.ApiLoaderArguments)&&
            map.ApiLoaderArguments!=map.Arguments))
            throw new InvalidDataException("UM-LOCAL-API-LAUNCH-PARITY");

        for(DirectoryInfo? dir=new(root);dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-LOCAL-API-REPARSE");
        if(!AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidDataException("UM-LOCAL-API-SERVER-UNVERIFIED");

        if(!underLease)
        {
            var active=cluster.Operations.Observe(root);
            if(active?.Phase is (UntamedOperationPhase.Prepared or
                UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator))
                throw new InvalidOperationException("UM-LOCAL-API-PENDING-OPERATION");
        }
        new AsaManualRestartJournal(root).DemandNoPending();
        // SteamCMD updates a shared installation, but a launch-mode choice
        // remains *per map*. Any co-installed ASA/Loader must be stopped.
        var group=cluster.Config.LocalMaps.Where(m=>
            !string.IsNullOrWhiteSpace(m.InstallationPath)&&
            Same(m.InstallationPath,root)).ToArray();
        if(group.Length==0)throw new InvalidOperationException("UM-LOCAL-API-OWNER");
        foreach(var participant in group)
        {
            var observed=cluster.Check(participant);
            if(observed.Running||!observed.ConfirmedStopped||
               observed.Identity!=null||observed.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-LOCAL-API-ASA-LOADER-RUNNING");
        }
        var copy=JsonSerializer.Deserialize<LocalMap>(JsonSerializer.Serialize(map))
            ??throw new InvalidDataException("UM-LOCAL-API-CLONE");
        copy.StartWithAsaApi=chooseApi;
        copy.ApiLoaderPath=loader;
        copy.ApiLoaderArguments=copy.Arguments;
        var plan=AsaApiLaunchPolicy.Resolve(copy);
        if(plan.UseApi!=chooseApi||
           !Same(plan.ExecutablePath,chooseApi?loader:expectedServer)||
           plan.Arguments!=map.Arguments)
            throw new InvalidDataException("UM-LOCAL-API-PREVIEW-PLAN-MISMATCH");
        return loader;
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3072)return Fail(null,"UM-LOCAL-API-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var req=doc.RootElement;
            if(req.ValueKind!=JsonValueKind.Object||
               !ReadGuid(req,"id",out var requestId)||
               !req.TryGetProperty("method",out var method)||
               method.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-LOCAL-API-REQUEST");
            id=requestId.ToString("D");
            string? name=method.GetString();
            if(name is not ("servers.apiModeStatus" or
                "servers.apiModePreview" or "servers.apiModeApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(name=="servers.apiModeStatus")
                {
                    if(!ReadGuid(req,"mapId",out var mapId))
                        return Fail(id,"UM-LOCAL-API-MAP");
                    var map=Owned(mapId);
                    if(map==null||map.HistoricalStoreProtected||
                       map.ObservedReadOnlyImported||
                       FreshStoreEnrollmentPolicy.IsEnrolled(map))
                        return Fail(id,"UM-LOCAL-API-OWNERSHIP");
                    bool available=false;
                    try{Preflight(map,false);available=true;}
                    catch(Exception ex) when(ex is InvalidOperationException or
                        InvalidDataException or IOException or UnauthorizedAccessException){}
                    return Ok(id,new{
                        mapId,mapName=map.Name,useApi=map.StartWithAsaApi,
                        canChangeMode=available,requiresStoppedProcess=true,
                        mode=map.StartWithAsaApi?"asaApiLoader":"directAsa",
                        serverStarted=false,originalStoreTouched=false,
                        evidence="LOCAL_ASA_API_MANAGER_LAUNCH_MODE"
                    });
                }
                if(name=="servers.apiModePreview")
                {
                    pending=null;
                    if(!ReadGuid(req,"mapId",out var mapId)||
                       !req.TryGetProperty("useApi",out var field)||
                       field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Fail(id,"UM-LOCAL-API-SELECTION");
                    var map=Owned(mapId);
                    bool useApi=field.GetBoolean();
                    if(map==null||map.StartWithAsaApi==useApi)
                        return Fail(id,"UM-LOCAL-API-NO-CHANGE");
                    string loader=Preflight(map,useApi);
                    pending=new(Guid.NewGuid(),map.Id,JsonSerializer.Serialize(map),
                        useApi,loader,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pending.Token,mapId,mapName=map.Name,useApi,
                        mode=useApi?"asaApiLoader":"directAsa",
                        startsServer=false,changesStore=false,
                        requiresStoppedProcess=true,
                        evidence="LOCAL_ASA_API_MODE_PREVIEW_NO_WRITE"
                    });
                }
                if(!ReadGuid(req,"token",out var token)||pending==null||
                   token!=pending.Token||
                   DateTimeOffset.UtcNow-pending.CreatedUtc>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-LOCAL-API-APPROVAL-EXPIRED");
                if(!req.TryGetProperty("confirm",out var yes)||
                   yes.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-LOCAL-API-APPROVAL-REQUIRED");
                var approval=pending;
                pending=null;
                var owned=Owned(approval.MapId);
                if(owned==null||owned.StartWithAsaApi==approval.UseApi||
                   JsonSerializer.Serialize(owned)!=approval.Profile)
                    return Fail(id,"UM-LOCAL-API-PROFILE-CHANGED");
                string checkedLoader=Preflight(owned,approval.UseApi);
                if(!Same(checkedLoader,approval.Loader))
                    return Fail(id,"UM-LOCAL-API-LOADER-CHANGED");
                using var lease=cluster.Operations.Acquire(
                    owned.InstallationPath,UntamedOperationKind.Configuration);
                if(!ReferenceEquals(owned,Owned(approval.MapId))||
                   JsonSerializer.Serialize(owned)!=approval.Profile||
                   !Same(Preflight(owned,approval.UseApi,underLease:true),approval.Loader))
                    return Fail(id,"UM-LOCAL-API-LEASE-CHANGED");
                var updated=JsonSerializer.Deserialize<LocalMap>(
                    JsonSerializer.Serialize(owned))??
                    throw new InvalidDataException("UM-LOCAL-API-COPY");
                updated.StartWithAsaApi=approval.UseApi;
                updated.ApiLoaderPath=approval.Loader;
                updated.ApiLoaderArguments=updated.Arguments;
                lease.MarkMutationStarted();
                cluster.ReplaceMap(owned,updated);
                lease.Complete();
                return Ok(id,new{
                    mapId=updated.Id,mapName=updated.Name,useApi=updated.StartWithAsaApi,
                    mode=updated.StartWithAsaApi?"asaApiLoader":"directAsa",
                    serverStarted=false,originalStoreTouched=false,
                    requiresManualStart=true,evidence="LOCAL_ASA_API_MODE_SAVED"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-LOCAL-API-SAFETY-FAILED");
        }
    }

    private static string Ok(string? id,object value)=>
        JsonSerializer.Serialize(new{id,ok=true,data=value},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>serial.Dispose();
}
