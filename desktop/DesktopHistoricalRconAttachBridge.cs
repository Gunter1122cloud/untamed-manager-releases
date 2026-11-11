using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Attaches the existing historical Store-v1 RCON listener to the NEW
/// Manager's local DPAPI profile. Never edits an original ASA INI, Store
/// session/lock, world, character, tribe or shared transfer record.
/// Source ACTIVE/DONE and original RCON INI are independently rechecked.
/// </summary>
public sealed class DesktopHistoricalRconAttachBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,string Profile,
        string OriginalMapKey,string Root,string Exe,string ArchiveSha,
        int Game,int Query,int Port,string ProtectedPassword,string IniSha,
        DateTimeOffset Created);
    private readonly ClusterService cluster;
    private readonly Func<LocalMap,bool> originalGuard;
    private readonly Func<LocalMap,bool> rconProof;
    private readonly Func<LocalMap,ProcessCheck> apiProcessObserver;
    private readonly Func<int,int,bool> ownsRconListener;
    private readonly SemaphoreSlim gate=new(1,1);
    private Approval? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopHistoricalRconAttachBridge(ClusterService service,
        Func<LocalMap,bool>? sourceGuard=null,
        Func<LocalMap,bool>? iniEvidence=null,
        Func<LocalMap,ProcessCheck>? apiObserver=null,
        Func<int,int,bool>? listenerOwner=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        originalGuard=sourceGuard??(map=>
            LegacyStoreV1LiveReadGate.InspectOriginalActive(map,
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData),"Kova"))
                .CompletedOriginalSourceGuard);
        rconProof=iniEvidence??AsaRconConfigurationEvidence.Matches;
        apiProcessObserver=apiObserver??(map=>
            LegacyStoreV1ProcessObserver.Observe(map,cluster.Config.LocalMaps));
        ownsRconListener=listenerOwner??RconPortOwnership.IsOwnedListener;
    }

    private static bool GuidValue(JsonElement item,string key,out Guid id)
    {
        id=Guid.Empty;
        return item.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String &&
            Guid.TryParse(p.GetString(),out id)&&id!=Guid.Empty;
    }
    private LocalMap? Owned(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==id);
    private static bool Historical(LocalMap? map)=>
        map!=null&&map.Id!=Guid.Empty&&map.HistoricalStoreProtected&&
        !map.Maintenance&&!string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)&&
        !string.IsNullOrWhiteSpace(map.HistoricalMapKey)&&
        string.IsNullOrWhiteSpace(map.StoreTestRoot)&&
        string.IsNullOrWhiteSpace(map.Arguments)&&
        string.IsNullOrWhiteSpace(map.ApiLoaderArguments)&&
        map.RconPort==0&&
        string.IsNullOrWhiteSpace(map.RconPasswordProtected)&&
        (!map.StartWithAsaApi ||
         (map.LastProcess is {Pid:>0,
             Arguments:"HISTORICAL_STORE_V1_API_CHILD_VERIFIED"} &&
          map.LastLoader is {Pid:>0,
             Arguments:"HISTORICAL_STORE_V1_API_LOADER_VERIFIED"} &&
          !string.IsNullOrWhiteSpace(map.ApiLoaderPath)))&&
        map.GamePort is >=1024 and <=65535 &&
        map.QueryPort is >=1024 and <=65535;

    private static string IniHash(LocalMap map)
    {
        string path=AsaInitialRconSettings.NewIniPath(map.InstallationPath);
        if(!File.Exists(path) || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-HISTORICAL-RCON-INI-MISSING");
        using var source=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.Read,65536,FileOptions.SequentialScan);
        if(source.Length is <8 or >131072)
            throw new InvalidDataException("UM-HISTORICAL-RCON-INI-BOUNDS");
        return Convert.ToHexString(SHA256.HashData(source));
    }
    // Read only the existing local ASA INI's unambiguous RCON port. Never
    // return ServerAdminPassword or turn an INI hint into authority to send.
    private static int SuggestedOriginalRconPort(LocalMap map)
    {
        try
        {
            string path=AsaInitialRconSettings.NewIniPath(map.InstallationPath);
            var dir=Path.GetDirectoryName(path)!;
            for(DirectoryInfo? cursor=new(dir);cursor!=null;cursor=cursor.Parent)
                if(cursor.Exists&&(cursor.Attributes&FileAttributes.ReparsePoint)!=0)
                    return 0;
            var file=new FileInfo(path);
            if(!file.Exists||file.Length is <8 or >131072||
               (file.Attributes&FileAttributes.ReparsePoint)!=0)
                return 0;
            int sections=0,enabled=0,ports=0,port=0;
            bool inside=false,isEnabled=false;
            foreach(string line in File.ReadLines(path))
            {
                string value=line.Trim();
                if(value.StartsWith("[",StringComparison.Ordinal))
                {
                    inside=value.Equals("[ServerSettings]",StringComparison.OrdinalIgnoreCase);
                    if(inside)sections++;
                    continue;
                }
                if(!inside||value.Length==0||value[0] is ';' or '#')continue;
                int equal=value.IndexOf('=');
                if(equal<1)continue;
                string key=value[..equal].Trim();
                string field=value[(equal+1)..].Trim();
                if(key.Equals("RCONEnabled",StringComparison.OrdinalIgnoreCase))
                {
                    enabled++;isEnabled=field.Equals("True",StringComparison.OrdinalIgnoreCase);
                }
                else if(key.Equals("RCONPort",StringComparison.OrdinalIgnoreCase))
                {
                    ports++;
                    if(!int.TryParse(field,out port))return 0;
                }
            }
            return sections==1&&enabled==1&&isEnabled&&ports==1&&
                port is >=1024 and <=65535?port:0;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException)
        {return 0;}
    }

    private static LocalMap WithRcon(LocalMap original,int port,string protectedPassword)
    {
        var copy=JsonSerializer.Deserialize<LocalMap>(JsonSerializer.Serialize(original))
            ??throw new InvalidDataException("UM-HISTORICAL-RCON-CLONE-FAILED");
        copy.RconHost="127.0.0.1";
        copy.RconPort=port;
        copy.RconPasswordProtected=protectedPassword;
        return copy;
    }
    private bool VerifiedApiRconOwner(LocalMap map,int requestedPort)
    {
        if(!map.StartWithAsaApi)return true;
        var pinnedChild=map.LastProcess;
        var pinnedLoader=map.LastLoader;
        if(pinnedChild is not {Pid:>0,
                Arguments:"HISTORICAL_STORE_V1_API_CHILD_VERIFIED"}||
           pinnedLoader is not {Pid:>0,
                Arguments:"HISTORICAL_STORE_V1_API_LOADER_VERIFIED"}||
           pinnedChild.Pid==pinnedLoader.Pid||
           pinnedLoader.StartedUtc>pinnedChild.StartedUtc||
           string.IsNullOrWhiteSpace(map.ApiLoaderPath))return false;
        try
        {
            string server=Path.GetFullPath(map.ExecutablePath);
            string loader=Path.Combine(Path.GetDirectoryName(server)!,
                "AsaApiLoader.exe");
            if(!Path.GetFullPath(map.ApiLoaderPath).Equals(loader,
                    StringComparison.OrdinalIgnoreCase)||
               !File.Exists(loader)||
               !Path.GetFullPath(pinnedChild.ExecutablePath).Equals(server,
                    StringComparison.OrdinalIgnoreCase)||
               !Path.GetFullPath(pinnedLoader.ExecutablePath).Equals(loader,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            var observed=apiProcessObserver(map);
            var child=observed.Identity;
            var parent=observed.LoaderIdentity;
            if(!observed.Running||observed.ConfirmedStopped||
               child is not {Pid:>0,
                   Arguments:"HISTORICAL_STORE_V1_API_CHILD_VERIFIED"}||
               parent is not {Pid:>0,
                   Arguments:"HISTORICAL_STORE_V1_API_LOADER_VERIFIED"}||
               child.Pid!=pinnedChild.Pid||
               child.StartedUtc!=pinnedChild.StartedUtc||
               parent.Pid!=pinnedLoader.Pid||
               parent.StartedUtc!=pinnedLoader.StartedUtc||
               !Path.GetFullPath(child.ExecutablePath).Equals(server,
                   StringComparison.OrdinalIgnoreCase)||
               !Path.GetFullPath(parent.ExecutablePath).Equals(loader,
                   StringComparison.OrdinalIgnoreCase))
                return false;
            return ownsRconListener(requestedPort,child.Pid);
        }
        catch(Exception)
        {
            // A stopped/changed process, inaccessible WMI or unknown TCP
            // listener does not become Manager write authority.
            return false;
        }
    }

    private void Demand(LocalMap map,int port,string protectedPassword,string iniHash)
    {
        if(!Historical(map)||port is <1024 or >65535 ||
           !AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidDataException("UM-HISTORICAL-RCON-UNSAFE-MAP");
        var candidate=WithRcon(map,port,protectedPassword);
        LocalMapPortPolicy.RequireNoConflicts(candidate,cluster.Config.LocalMaps);
        if(!rconProof(candidate)||IniHash(map)!=iniHash)
            throw new InvalidDataException("UM-HISTORICAL-RCON-INI-CHANGED");
        if(!originalGuard(map))
            throw new InvalidDataException("UM-HISTORICAL-RCON-ORIGINAL-STORE-INVALID");
        // An ORIGINAL Store ASA API child is never the loader itself.
        // #151 retains a verified original process pair when importing live
        // Kova maps. Re-check that exact pair and the CHILD-owned RCON port
        // before granting its Manager profile the existing RCON credentials.
        // The listener and Windows WMI are independently rechecked under
        // the native installation operation lease on Apply.
        if(map.StartWithAsaApi&&!VerifiedApiRconOwner(map,port))
            throw new InvalidDataException("UM-HISTORICAL-RCON-API-CHILD-UNVERIFIED");
        // The bridge is attaching credentials only; no TCP command is sent.
        // An already-running original ASA process may remain untouched.
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Fail(null,"UM-HISTORICAL-RCON-REQUEST");
            using var parsed=JsonDocument.Parse(raw);
            var item=parsed.RootElement;
            if(item.ValueKind!=JsonValueKind.Object||
               !GuidValue(item,"id",out var requestId)||
               !item.TryGetProperty("method",out var method)||
               method.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-HISTORICAL-RCON-REQUEST");
            id=requestId.ToString("D");
            string? action=method.GetString();
            if(action is not ("historical.rconStatus" or
                "historical.rconPreview" or "historical.rconApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(action=="historical.rconStatus")
                {
                    if(!GuidValue(item,"mapId",out var mapId))
                        return Fail(id,"UM-HISTORICAL-RCON-MAP");
                    var map=Owned(mapId);
                    if(map==null||!map.HistoricalStoreProtected)
                        return Fail(id,"UM-HISTORICAL-RCON-OWNED-ONLY");
                    bool attached=map.RconPort is >=1024 and <=65535&&
                        !string.IsNullOrWhiteSpace(map.RconPasswordProtected);
                    return Ok(id,new{
                        mapId,mapName=map.Name,configured=attached,
                        port=attached?map.RconPort:0,
                        suggestedPort=attached?map.RconPort:SuggestedOriginalRconPort(map),
                        verifiedIni=attached&&rconProof(map),
                        originalStoreTouched=false,
                        evidence="HISTORICAL_RCON_MANAGER_PROFILE_STATUS_ONLY"
                    });
                }
                if(action=="historical.rconPreview")
                {
                    pending=null;
                    if(!GuidValue(item,"mapId",out var mapId)||
                       !item.TryGetProperty("rconPort",out var portField)||
                       !portField.TryGetInt32(out int port)||
                       !item.TryGetProperty("password",out var passField)||
                       passField.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-HISTORICAL-RCON-PREVIEW-FIELDS");
                    var map=Owned(mapId);
                    if(!Historical(map))return Fail(id,"UM-HISTORICAL-RCON-ALREADY-CONFIGURED");
                    string password=passField.GetString()??"";
                    // The existing ASA secret is never regenerated or
                    // written into the original INI. Unlike fresh-server
                    // creation, short/legacy-symbol values are valid here if
                    // the read-only original INI and DPAPI evidence match.
                    try {AsaInitialRconSettings.ValidateExistingAdminPassword(password);}
                    catch(InvalidDataException)
                    {
                        return Fail(id,"UM-HISTORICAL-RCON-EXISTING-PASSWORD-INVALID");
                    }
                    string protectedPassword=SecureStore.Protect(password);
                    string hash=IniHash(map!);
                    Demand(map!,port,protectedPassword,hash);
                    var permit=new Approval(Guid.NewGuid(),map!.Id,
                        JsonSerializer.Serialize(map),map.HistoricalMapKey,
                        map.InstallationPath,map.ExecutablePath,
                        map.HistoricalArchiveSha256,map.GamePort,map.QueryPort,
                        port,protectedPassword,hash,DateTimeOffset.UtcNow);
                    pending=permit;
                    return Ok(id,new{
                        token=permit.Token,mapId=map.Id,mapName=map.Name,
                        rconPort=port,existingIniVerified=true,
                        originalActiveDoneVerified=true,
                        originalStoreTouched=false,serverStarted=false,
                        evidence="HISTORICAL_RCON_EXISTING_INI_PREVIEW_ONLY"
                    });
                }
                if(!GuidValue(item,"token",out var token)||pending==null||
                   pending.Token!=token||
                   DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-HISTORICAL-RCON-TOKEN-EXPIRED");
                if(!item.TryGetProperty("confirm",out var yes)||
                   yes.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-HISTORICAL-RCON-APPROVAL");
                Approval approved=pending;
                pending=null; // Never replay an attach approval.
                var original=Owned(approved.MapId);
                if(original==null||JsonSerializer.Serialize(original)!=approved.Profile||
                   original.HistoricalMapKey!=approved.OriginalMapKey||
                   original.HistoricalArchiveSha256!=approved.ArchiveSha||
                   original.InstallationPath!=approved.Root||
                   original.ExecutablePath!=approved.Exe||
                   original.GamePort!=approved.Game||
                   original.QueryPort!=approved.Query)
                    return Fail(id,"UM-HISTORICAL-RCON-PROFILE-CHANGED");
                Demand(original,approved.Port,approved.ProtectedPassword,approved.IniSha);
                using var lease=cluster.Operations.Acquire(original.InstallationPath,
                    UntamedOperationKind.Configuration);
                Demand(original,approved.Port,approved.ProtectedPassword,approved.IniSha);
                var configured=WithRcon(original,approved.Port,approved.ProtectedPassword);
                lease.MarkMutationStarted();
                cluster.ReplaceMap(original,configured);
                lease.Complete();
                return Ok(id,new{
                    mapId=configured.Id,mapName=configured.Name,
                    configured=true,rconPort=configured.RconPort,
                    originalStoreTouched=false,iniModified=false,
                    serverStarted=false,
                    evidence="HISTORICAL_RCON_DPAPI_PROFILE_ATTACHED"
                });
            }
            finally{gate.Release();}
        }
        catch(InvalidDataException ex) when(ex.Message is
            "UM-HISTORICAL-RCON-INI-CHANGED" or
            "UM-HISTORICAL-RCON-ORIGINAL-STORE-INVALID" or
            "UM-HISTORICAL-RCON-API-CHILD-UNVERIFIED")
        {
            // Only these static, credential-free native reasons are exposed.
            // In particular, INI mismatch does not imply the disk file
            // changed: a wrong supplied password gives the same result.
            return Fail(id,ex.Message);
        }
        catch(Exception)
        {
            // No credentials, original UNC path or Kova IDs are sent to UI.
            return Fail(id,"UM-HISTORICAL-RCON-SAFETY-CHECK-FAILED");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string message)=>
        JsonSerializer.Serialize(new{id,ok=false,error=message},Json);
    public void Dispose()=>gate.Dispose();
}
