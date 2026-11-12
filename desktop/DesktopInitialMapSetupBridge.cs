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
/// First-time local map profile only: after ASA files were installed or imported,
/// initialize session and conflict-free UDP ports. Never edits INI, RCON password,
/// player saves, Store-v1 or starts a real ASA process.
/// </summary>
public sealed class DesktopInitialMapSetupBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly Dictionary<Guid,Prepared> pending = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Prepared(Guid Token,Guid MapId,string Session,int Game,int Query,
        string BaselineMapName,string Level,string BaselineArguments,int BaselineGame,int BaselineQuery,
        string BaselineExe,DateTimeOffset CreatedUtc);

    public DesktopInitialMapSetupBridge(ClusterService service) =>
        cluster=service ?? throw new ArgumentNullException(nameof(service));

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Fail(null,"UM-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var args=doc.RootElement;
            if(args.ValueKind!=JsonValueKind.Object ||
               !GuidField(args,"id",out var requestId) ||
               !args.TryGetProperty("method",out var methodValue) ||
               methodValue.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-INVALID-MESSAGE");
            id=requestId.ToString("D");
            string? method=methodValue.GetString();
            if(method is not ("servers.initialStatus" or
                "servers.initialPreview" or "servers.initialApply"))
                return Fail(id,"UM-METHOD-DENIED");

            await gate.WaitAsync();
            try
            {
                foreach(var expired in pending.Where(x=>
                    DateTimeOffset.UtcNow-x.Value.CreatedUtc>TimeSpan.FromMinutes(10))
                    .Select(x=>x.Key).ToArray())pending.Remove(expired);

                if(method=="servers.initialStatus")
                {
                    if(!GuidField(args,"mapId",out var mapId))
                        return Fail(id,"UM-INVALID-MAP");
                    var map=Owned(mapId);
                    if(map==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    var process=cluster.Check(map);
                    var suggested=LocalMapPortPolicy.Suggest(cluster.Config.LocalMaps);
                    bool initial=Initial(map);
                    string? level=null;
                    try { level=AsaSteamMapCatalog.LevelFor(map.Name); }
                    catch(InvalidDataException)
                    {
                        if(!string.IsNullOrWhiteSpace(map.FreshInstallMapLevel))
                        {
                            try { level=AsaSteamMapCatalog.ValidateCustomLevel(
                                map.FreshInstallMapLevel); }
                            catch(InvalidDataException)
                            { return Fail(id,"UM-INITIAL-INSTALLED-LEVEL-INVALID"); }
                        }
                    }
                    bool executable=ServerExePresent(map);
                    bool stopped=process.ConfirmedStopped && !process.Running;
                    string blocker=!initial ? "PROFILE_ALREADY_CONFIGURED_OR_PROTECTED" :
                        !executable ? "SERVER_EXECUTABLE_MISSING" :
                        process.Running ? "PROCESS_RUNNING" :
                        !stopped ? "PROCESS_STOP_NOT_VERIFIED" : "NONE";
                    return Success(id,new{
                        mapId=map.Id,mapName=map.Name,level,
                        requiresCustomLevel=level==null,
                        initialSetupAvailable=blocker=="NONE",
                        blocker,processStatus=process.Status,
                        confirmedStopped=stopped,
                        hasServerExecutable=executable,
                        gamePort=map.GamePort,queryPort=map.QueryPort,
                        suggestion=new{game=suggested.Game,query=suggested.Query},
                        evidence="LOCAL_MANAGER_PROFILE_FIRST_SETUP_STATUS"
                    });
                }

                if(method=="servers.initialPreview")
                {
                    if(!GuidField(args,"mapId",out var mapId) ||
                       !TextField(args,"sessionName",75,out var session) ||
                       !IntField(args,"gamePort",out var game) ||
                       !IntField(args,"queryPort",out var query))
                        return Fail(id,"UM-INITIAL-INVALID-FIELDS");
                    var map=Owned(mapId);
                    if(map==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    if(!Initial(map))
                        return Fail(id,"UM-INITIAL-ALREADY-CONFIGURED");
                    if(!ServerExePresent(map))
                        return Fail(id,"UM-INITIAL-EXECUTABLE-INVALID");
                    var observed=cluster.Check(map);
                    if(!observed.ConfirmedStopped || observed.Running || map.Maintenance)
                        return Fail(id,"UM-INITIAL-STOP-REQUIRED");
                    // Official maps cannot be overridden. An unknown,
                    // manually imported map requires a single explicit safe
                    // Unreal level token, never a raw command-line string.
                    string level;
                    bool official=true;
                    try { level=AsaSteamMapCatalog.LevelFor(map.Name); }
                    catch(InvalidDataException) { official=false;level=""; }
                    if(official)
                    {
                        if(args.TryGetProperty("customLevel",out var supplied) &&
                           (supplied.ValueKind!=JsonValueKind.String ||
                            !string.IsNullOrWhiteSpace(supplied.GetString())))
                            return Fail(id,"UM-INITIAL-OFFICIAL-LEVEL-OVERRIDE-DENIED");
                    }
                    else
                    {
                        // A level entered during the verified SteamCMD install
                        // is already an explicit operator choice. Reuse it,
                        // but never allow another level to override this
                        // installed profile without a separate migration.
                        string requested="";
                        if(args.TryGetProperty("customLevel",out var custom))
                        {
                            if(custom.ValueKind!=JsonValueKind.String)
                                return Fail(id,"UM-INITIAL-CUSTOM-LEVEL-INVALID");
                            requested=custom.GetString()??"";
                        }
                        if(string.IsNullOrWhiteSpace(requested))
                            requested=map.FreshInstallMapLevel;
                        if(string.IsNullOrWhiteSpace(requested))
                            return Fail(id,"UM-INITIAL-CUSTOM-LEVEL-REQUIRED");
                        if(!string.IsNullOrWhiteSpace(map.FreshInstallMapLevel)&&
                           !string.Equals(requested,map.FreshInstallMapLevel,
                               StringComparison.Ordinal))
                            return Fail(id,"UM-INITIAL-INSTALLED-LEVEL-MISMATCH");
                        try{level=AsaSteamMapCatalog.ValidateCustomLevel(requested);}
                        catch(InvalidDataException)
                        {return Fail(id,"UM-INITIAL-CUSTOM-LEVEL-INVALID");}
                    }
                    string launch=AsaSteamMapCatalog.LaunchArgumentsForLevel(level,session,game,query);
                    LocalMapPortPolicy.RequireNoConflicts(new LocalMap{
                        Id=map.Id,Name=map.Name,GamePort=game,QueryPort=query
                    },cluster.Config.LocalMaps);
                    // Profile-level suggestions do not check the operating
                    // system. An unrelated process can already own the UDP
                    // listener even when no Manager map is registered.
                    var availability=AsaLocalPortPreflight.Inspect(new LocalMap{
                        GamePort=game,QueryPort=query,RconPort=0
                    });
                    if(availability!=AsaLocalPortAvailability.Available)
                        return Fail(id,availability==AsaLocalPortAvailability.InUse?
                            "UM-INITIAL-PORT-IN-USE":"UM-INITIAL-PORT-UNVERIFIED");
                    if(pending.Count>=6)pending.Clear();
                    var token=Guid.NewGuid();
                    pending[token]=new(token,map.Id,session,game,query,map.Name,level,
                        map.Arguments,map.GamePort,map.QueryPort,map.ExecutablePath,DateTimeOffset.UtcNow);
                    return Success(id,new{
                        token,mapId=map.Id,mapName=map.Name,sessionName=session,
                        mapLevel=level,requiresCustomLevel=!official,
                        gamePort=game,queryPort=query,
                        rconEnabled=false,serverStartRequested=false,
                        // Avoid sending arbitrary launch arguments into the UI.
                        configuredArgumentsLength=launch.Length,
                        evidence="INITIAL_MAP_PROFILE_PREVIEW_NO_WRITES"
                    });
                }

                if(!GuidField(args,"token",out var approvalToken) ||
                   !pending.TryGetValue(approvalToken,out var saved))
                    return Fail(id,"UM-INITIAL-PREVIEW-EXPIRED");
                if(!args.TryGetProperty("confirm",out var confirmation) ||
                   confirmation.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-INITIAL-CONFIRMATION-REQUIRED");
                // A negative/missing confirmation must not consume a valid preview.
                // Commit remains one-time: a successful save removes its token.
                var owned=Owned(saved.MapId);
                if(owned==null)return Fail(id,"UM-MAP-NOT-OWNED");
                if(!Initial(owned) || !ServerExePresent(owned) ||
                   !string.Equals(owned.Name,saved.BaselineMapName,StringComparison.Ordinal) ||
                   owned.Arguments!=saved.BaselineArguments ||
                   owned.GamePort!=saved.BaselineGame ||
                   owned.QueryPort!=saved.BaselineQuery ||
                   !string.Equals(owned.ExecutablePath,saved.BaselineExe,
                       StringComparison.OrdinalIgnoreCase))
                    return Fail(id,"UM-INITIAL-CONFLICT");
                var final=cluster.Check(owned);
                if(!final.ConfirmedStopped || final.Running || owned.Maintenance)
                    return Fail(id,"UM-INITIAL-STOP-REQUIRED");
                LocalMapPortPolicy.RequireNoConflicts(new LocalMap{
                    Id=owned.Id,Name=owned.Name,GamePort=saved.Game,QueryPort=saved.Query
                },cluster.Config.LocalMaps);
                // A free port during preview can be occupied by another
                // process before approval; no partial profile write is allowed.
                var availability=AsaLocalPortPreflight.Inspect(new LocalMap{
                    GamePort=saved.Game,QueryPort=saved.Query,RconPort=0
                });
                if(availability!=AsaLocalPortAvailability.Available)
                    return Fail(id,availability==AsaLocalPortAvailability.InUse?
                        "UM-INITIAL-PORT-IN-USE":"UM-INITIAL-PORT-UNVERIFIED");
                var copy=JsonSerializer.Deserialize<LocalMap>(JsonSerializer.Serialize(owned))
                    ?? throw new InvalidDataException("Could not prepare local map profile");
                copy.GamePort=saved.Game;
                copy.QueryPort=saved.Query;
                copy.Arguments=AsaSteamMapCatalog.LaunchArgumentsForLevel(
                    saved.Level,saved.Session,saved.Game,saved.Query);
                // No RCON admin password means RCON remains disabled.
                copy.RconPort=0;
                copy.AutoStart=false;
                cluster.ReplaceMap(owned,copy);
                pending.Remove(approvalToken);
                return Success(id,new{
                    mapId=copy.Id,mapName=copy.Name,configured=true,
                    gamePort=copy.GamePort,queryPort=copy.QueryPort,
                    rconEnabled=false,serverStarted=false,
                    evidence="ATOMIC_MANAGER_PROFILE_INITIAL_SETUP_ONLY"
                });
            }
            finally {gate.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-INITIAL-OPERATION-FAILED");
        }
    }

    private LocalMap? Owned(Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id);
    private static bool Initial(LocalMap map)=>
        !map.HistoricalStoreProtected && !map.ObservedReadOnlyImported &&
        string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256) &&
        string.IsNullOrWhiteSpace(map.StoreTestRoot) &&
        string.IsNullOrWhiteSpace(map.FreshStoreClusterId) &&
        map.Arguments.Length==0 && map.GamePort==0 && map.QueryPort==0 &&
        map.RconPort==0 && !map.AutoStart && !map.Maintenance;
    // The binary must belong to the registered local ASA installation.
    // File.Exists alone could accept an unrelated map's EXE and configure
    // a profile whose root and executable do not agree.
    private static bool ServerExePresent(LocalMap map)=>
        AsaReadyProbe.IsRegisteredServerBinary(map);
    private static bool GuidField(JsonElement root,string key,out Guid id)
    {
        id=Guid.Empty;
        return root.TryGetProperty(key,out var value) &&
            value.ValueKind==JsonValueKind.String &&
            Guid.TryParse(value.GetString(),out id) && id!=Guid.Empty;
    }
    private static bool TextField(JsonElement root,string key,int max,out string value)
    {
        value="";
        if(!root.TryGetProperty(key,out var prop)||prop.ValueKind!=JsonValueKind.String)return false;
        value=prop.GetString()?.Trim()??"";
        return value.Length is >0 and <=75 && value.Length<=max &&
            !value.Any(char.IsControl);
    }
    private static bool IntField(JsonElement root,string key,out int value)
    {
        value=0;
        return root.TryGetProperty(key,out var prop) &&
            prop.ValueKind==JsonValueKind.Number &&
            prop.TryGetInt32(out value) && value is >=1 and <=65535;
    }
    private static string Success(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>gate.Dispose();
}
