using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit per-map read-only RCON. The renderer cannot send arbitrary
/// commands, select host/port, retrieve passwords or reach the LAN peer.
/// Only an identified, running, locally owned ASA process may receive
/// ListPlayers; RCON TCP listener ownership is verified by the OS.
/// </summary>
public sealed class DesktopRconReadOnlyBridge
{
    private readonly ClusterService cluster;
    private readonly Func<int,int,bool> ownsPort;
    private readonly Func<string,int,string,string,CancellationToken,Task<string>> send;
    private readonly SemaphoreSlim serial=new(1,1);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopRconReadOnlyBridge(ClusterService cluster,
        Func<int,int,bool>? portOwner=null,
        Func<string,int,string,string,CancellationToken,Task<string>>? dispatch=null)
    {
        this.cluster=cluster??throw new ArgumentNullException(nameof(cluster));
        ownsPort=portOwner??RconPortOwnership.IsOwnedListener;
        send=dispatch??RconProtocol.Execute;
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)return Deny(null,"UM-RCON-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !root.TryGetProperty("id",out var idValue) ||
               idValue.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(idValue.GetString(),out var parsed) ||
               !root.TryGetProperty("method",out var methodValue) ||
               methodValue.ValueKind!=JsonValueKind.String)
                return Deny(null,"UM-RCON-INVALID-MESSAGE");
            id=parsed.ToString("D");
            string? method=methodValue.GetString();
            if(method is not ("rcon.status" or "rcon.listPlayers"))
                return Deny(id,"UM-METHOD-DENIED");
            if(!root.TryGetProperty("mapId",out var mapValue) ||
               mapValue.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(mapValue.GetString(),out var mapId) || mapId==Guid.Empty)
                return Deny(id,"UM-RCON-INVALID-MAP");

            await serial.WaitAsync();
            try
            {
                var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
                if(map==null)return Deny(id,"UM-MAP-NOT-OWNED");
                if(map.HistoricalStoreProtected)return Deny(id,"UM-HISTORICAL-STORE-LOCKED");
                bool configured=map.RconPort is >=1 and <=65535 &&
                    !string.IsNullOrWhiteSpace(map.RconPasswordProtected);
                var check=cluster.Check(map);
                bool observed=check.Running && check.Identity is {Pid:>0};
                bool safe=ReleaseCapabilityGate.Check(ManagerOperation.LocalReadOnlyRcon,
                    true,map.Maintenance,!check.Running,observed).Allowed &&
                    map.RconHost=="127.0.0.1" && configured && observed &&
                    ownsPort(map.RconPort,check.Identity!.Pid);
                if(method=="rcon.status")
                    return Ok(id,new {
                        mapId=map.Id,mapName=map.Name,configured,processVerified=observed,
                        permitted=safe,maintenance=map.Maintenance,
                        allowedCommands=new[]{"ListPlayers"},
                        evidence="LOCAL_RCON_READ_ONLY_PREFLIGHT_NO_COMMAND"
                    });
                if(!safe)return Deny(id,"UM-RCON-READONLY-PREFLIGHT-DENIED");
                var identity=check.Identity!;
                string password=SecureStore.Unprotect(map.RconPasswordProtected);
                if(string.IsNullOrEmpty(password))return Deny(id,"UM-RCON-CREDENTIAL-UNAVAILABLE");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(9));
                // The command string is server-owned and fixed. It is never
                // read from the untrusted WebView request.
                string answer=await send("127.0.0.1",map.RconPort,password,"ListPlayers",timeout.Token);
                var after=cluster.Check(map);
                if(!after.Running || after.Identity==null ||
                   after.Identity.Pid!=identity.Pid ||
                   after.Identity.StartedUtc!=identity.StartedUtc ||
                   !ownsPort(map.RconPort,after.Identity.Pid))
                    return Deny(id,"UM-RCON-PROCESS-CHANGED");
                if(answer.Length>32000)return Deny(id,"UM-RCON-OVERSIZED-REPLY");
                string sanitized=UntamedPresentation.Redact(answer);
                return Ok(id,new {
                    mapId=map.Id,mapName=map.Name,response=sanitized,
                    observedUtc=DateTimeOffset.UtcNow,readOnly=true,
                    saveCompleteProven=false,
                    evidence="LOCAL_VERIFIED_READ_ONLY_LISTPLAYERS"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidDataException or
            IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Net.Sockets.SocketException or TaskCanceledException or
            OperationCanceledException or JsonException)
        {
            return Deny(id,"UM-RCON-READONLY-UNAVAILABLE");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Deny(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
}
