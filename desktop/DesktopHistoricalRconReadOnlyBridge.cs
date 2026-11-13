using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Real RCON READ-ONLY channel for an already-running original two-host
/// ACTIVE Store-v1 map. Completely separate from fresh-map RCON/write
/// paths; never changes survivor, tribe, world, original session, lock,
/// shared transfer, INI, or Manager map configuration.
/// </summary>
public sealed class DesktopHistoricalRconReadOnlyBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly Func<LocalMap,ProcessCheck> inspect;
    private readonly Func<LocalMap,string> sourceEpoch;
    private readonly Func<int,int,bool> owner;
    private readonly Func<string,int,string,string,CancellationToken,Task<string>> send;
    private readonly SemaphoreSlim serial=new(1,1);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopHistoricalRconReadOnlyBridge(ClusterService service):
        this(service,null,null,null,null){}

    // Only the isolated native Windows test assembly injects these verifiers;
    // ordinary Manager production always revalidates the real old source
    // session, WMI process, local Windows TCP listener and original ASA INI.
    internal DesktopHistoricalRconReadOnlyBridge(ClusterService service,
        Func<LocalMap,ProcessCheck>? processInspector,
        Func<LocalMap,string>? sourceEpochProof,
        Func<int,int,bool>? portOwner,
        Func<string,int,string,string,CancellationToken,Task<string>>? dispatch)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        inspect=processInspector??(map=>LegacyStoreV1ProcessObserver.Observe(
            map,cluster.Config.LocalMaps));
        // After a verified Obsidian source import, local RCON reads bind
        // to that Manager-owned source, not a retired Legacy profile or a
        // switched-off peer's old ACTIVE/DONE marker. Unmigrated profiles
        // retain their original strict handoff check.
        sourceEpoch=sourceEpochProof??(
            map=>LegacyStoreV1GracefulStop.OriginalSource(cluster,map));
        owner=portOwner??RconPortOwnership.IsOwnedListener;
        send=dispatch??RconProtocol.Execute;
    }

    private static bool ReadGuid(JsonElement item,string property,out Guid id)
    {
        id=Guid.Empty;
        return item.TryGetProperty(property,out var raw)&&
            raw.ValueKind==JsonValueKind.String&&
            Guid.TryParse(raw.GetString(),out id)&&id!=Guid.Empty;
    }
    private static bool Identical(ProcessIdentity a,ProcessIdentity b)=>
        a.Pid>0&&a.Pid==b.Pid&&a.StartedUtc==b.StartedUtc&&
        string.Equals(a.ExecutablePath,b.ExecutablePath,StringComparison.OrdinalIgnoreCase);
    private (LocalMap Map,ProcessIdentity Process,string Epoch) Require(Guid mapId)
    {
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId)
            ??throw new UnauthorizedAccessException("UM-ORIGINAL-RCON-LOCAL-MAP");
        if(!map.HistoricalStoreProtected||map.Maintenance||
           map.RconHost!="127.0.0.1"||map.RconPort is <1024 or >65535 ||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected)||
           !string.IsNullOrWhiteSpace(map.Arguments)||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||
           !AsaRconConfigurationEvidence.Matches(map))
            throw new InvalidOperationException("UM-ORIGINAL-RCON-CONFIGURATION");
        // RCON admin password remains in DPAPI. Never return it to renderer.
        // The SHA binds local four metadata files, both shared DONE peers,
        // operation/attempted records and the original Store lock/session.
        string epoch=sourceEpoch(map);
        if(epoch==null||epoch.Length!=64||epoch.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidDataException("UM-ORIGINAL-RCON-SOURCE-EPOCH-INVALID");
        var observed=inspect(map);
        if(!observed.Running||observed.ConfirmedStopped||
           observed.Identity is not {Pid:>0} identity||
           !owner(map.RconPort,identity.Pid))
            throw new InvalidOperationException("UM-ORIGINAL-RCON-PID-PORT-UNVERIFIED");
        return(map,identity,epoch);
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)
                return Error(null,"UM-ORIGINAL-RCON-REQUEST");
            using var document=JsonDocument.Parse(raw);
            var item=document.RootElement;
            if(item.ValueKind!=JsonValueKind.Object||
               !ReadGuid(item,"id",out var rid)||
               !item.TryGetProperty("method",out var methodValue)||
               methodValue.ValueKind!=JsonValueKind.String||
               !ReadGuid(item,"mapId",out var mapId))
                return Error(null,"UM-ORIGINAL-RCON-REQUEST");
            id=rid.ToString("D");
            string? method=methodValue.GetString();
            if(method is not ("historical.rconLiveStatus" or
                "historical.rconPlayers" or "historical.rconGameLog" or
                "historical.rconChatLog"))
                return Error(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="historical.rconLiveStatus")
                {
                    var status=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
                    if(status==null||!status.HistoricalStoreProtected)
                        return Error(id,"UM-ORIGINAL-RCON-LOCAL-MAP");
                    try
                    {
                        var (map,identity,_)=Require(mapId);
                        return Ok(id,new{
                            mapId,mapName=map.Name,configured=true,
                            processVerified=true,portOwned=true,
                            pid=identity.Pid,permitted=true,
                            allowedCommands=new[]{"ListPlayers","GetGameLog","GetChat"},
                            storeTouched=false,saveCompleteProven=false,
                            evidence="ORIGINAL_ACTIVE_SOURCE_WMI_PID_TCP_OWNER_READY"
                        });
                    }
                    catch(Exception)
                    {
                        return Ok(id,new{
                            mapId,mapName=status.Name,
                            configured=status.RconPort>0&&
                                !string.IsNullOrWhiteSpace(status.RconPasswordProtected),
                            processVerified=false,portOwned=false,pid=(int?)null,
                            permitted=false,
                            allowedCommands=Array.Empty<string>(),
                            storeTouched=false,saveCompleteProven=false,
                            evidence="ORIGINAL_RCON_NOT_VERIFIED_NO_COMMAND"
                        });
                    }
                }
                var (ownedMap,expected,approvedEpoch)=Require(mapId);
                string command=method switch{
                    "historical.rconPlayers"=>"ListPlayers",
                    "historical.rconGameLog"=>"GetGameLog",
                    "historical.rconChatLog"=>"GetChat",
                    _=>throw new InvalidDataException("UM-ORIGINAL-RCON-WRONG-ACTION")
                };
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(9));
                // Source/ownership can change between preview and dispatch.
                // Recheck before any RCON call; a later source change cannot
                // turn this read-only channel into a mutating command.
                var (current,now,dispatchEpoch)=Require(mapId);
                if(!ReferenceEquals(current,ownedMap)||!Identical(expected,now))
                    return Error(id,"UM-ORIGINAL-RCON-PROCESS-CHANGED");
                if(!string.Equals(approvedEpoch,dispatchEpoch,StringComparison.Ordinal))
                    return Error(id,"UM-ORIGINAL-RCON-SOURCE-EPOCH-CHANGED");
                string password=SecureStore.Unprotect(ownedMap.RconPasswordProtected);
                string response=await send("127.0.0.1",ownedMap.RconPort,
                    password,command,deadline.Token);
                var (after,observed,completedEpoch)=Require(mapId);
                if(!ReferenceEquals(after,ownedMap)||!Identical(expected,observed))
                    return Error(id,"UM-ORIGINAL-RCON-POST-DISPATCH-CHANGED");
                if(!string.Equals(approvedEpoch,completedEpoch,StringComparison.Ordinal))
                    return Error(id,"UM-ORIGINAL-RCON-SOURCE-EPOCH-CHANGED");
                if(response==null)
                    return Error(id,"UM-ORIGINAL-RCON-NO-RESPONSE");
                if(response.Length>32_768)response=response[..32_768];
                // ASA logs are untrusted; never forward the current original
                // DPAPI credential even when a plugin has echoed it in a log.
                response=UntamedPresentation.Redact(response).Replace(
                    password,"[REDACTED]",StringComparison.Ordinal);
                return Ok(id,new{
                    mapId=ownedMap.Id,mapName=ownedMap.Name,
                    command,response,observedUtc=DateTimeOffset.UtcNow,
                    readOnly=true,storeTouched=false,saveCompleteProven=false,
                    evidence="ORIGINAL_STORE_V1_VERIFIED_LOCAL_READ_ONLY_RCON"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            return Error(id,"UM-ORIGINAL-RCON-SAFETY-CHECK-FAILED");
        }
    }

    private static string Ok(string id,object result)=>
        JsonSerializer.Serialize(new{id,ok=true,data=result},Json);
    private static string Error(string? id,string reason)=>
        JsonSerializer.Serialize(new{id,ok=false,error=reason},Json);
    public void Dispose()=>serial.Dispose();
}
