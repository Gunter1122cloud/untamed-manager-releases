using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Two-stage, local Manager-only LAN pairing. Only RFC1918 IPv4 targets are
/// accepted. The shared secret is stored through Windows DPAPI, never echoed
/// to WebView2, logged or sent to GitHub. The listener starts only after the
/// operator restarts the desktop app; no remote mutation route exists.
/// </summary>
public sealed class DesktopLanSettingsBridge
{
    private sealed record Pending(Guid Token,int ListenPort,string PeerAddress,int PeerPort,
        string? NewKey,int OriginalListen,string OriginalAddress,int OriginalPeerPort,
        string OriginalKey,DateTimeOffset CreatedUtc);
    private readonly ClusterService cluster;
    private readonly System.Threading.SemaphoreSlim gate=new(1,1);
    private Pending? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopLanSettingsBridge(ClusterService service)=>
        cluster=service??throw new ArgumentNullException(nameof(service));

    private static bool PrivateIPv4(string text)
    {
        if(!IPAddress.TryParse(text,out var address) ||
           address.AddressFamily!=AddressFamily.InterNetwork)return false;
        var b=address.GetAddressBytes();
        return b[0]==10 || (b[0]==172 && b[1] is >=16 and <=31) ||
            (b[0]==192 && b[1]==168);
    }
    private static bool Integer(JsonElement root,string name,out int value)
    {
        value=0;
        return root.TryGetProperty(name,out var prop) &&
            prop.ValueKind==JsonValueKind.Number && prop.TryGetInt32(out value);
    }
    private static bool Text(JsonElement root,string name,int maximum,out string value)
    {
        value="";
        if(!root.TryGetProperty(name,out var prop) ||
           prop.ValueKind!=JsonValueKind.String)return false;
        value=prop.GetString()?.Trim()??"";
        return value.Length<=maximum && !value.Any(char.IsControl);
    }
    private static bool Port(int value)=>value==0 || value is >=1024 and <=65535;
    private object Status()
    {
        var pair=LegacyStoreV1PairComparison.Compare(
            LegacyStoreV1PeerProof.FromCurrentUserLegacyProfile(),
            cluster.RemoteIsFresh?cluster.RemoteSnapshot?.OriginalStoreV1:null,
            cluster.RemoteIsFresh);
        return new {
        originalStorePair=new{
            bothOriginalProfilesPresent=pair.BothOriginalProfilesPresent,
            sameSharedSession=pair.SameSharedSession,
            sameSharedPath=pair.SameSharedPath,
            distinctDeviceIds=pair.DistinctDeviceIds,
            bothHaveFiveActiveMaps=pair.BothHaveFiveActiveMaps,
            copiedMetadataPairMatches=pair.CopiedMetadataPairMatches,
            liveWriterCertified=false,characterTravelVerified=false,
            statusCode=pair.StatusCode
        },
        hostName=cluster.Config.LocalHostName,
        listenPort=cluster.Config.ListenPort,
        peerAddress=cluster.Config.PeerAddress,
        peerPort=cluster.Config.PeerPort,
        pairingKeyConfigured=!string.IsNullOrWhiteSpace(cluster.Config.PairingKeyProtected),
        remoteFresh=cluster.RemoteIsFresh,
        remoteState=cluster.RemoteIsFresh?"Hitelesített LAN-peer elérhető":
            "Nem érhető el friss, hitelesített peer-állapot",
        secretReturned=false,
        peerWritesAllowed=false,
        evidence="DPAPI_LOCAL_LAN_SETTINGS_READ_ONLY"
        };
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)return Fail(null,"UM-LAN-INVALID-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !data.TryGetProperty("id",out var rid)||
               rid.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(rid.GetString(),out var requestId)||
               requestId==Guid.Empty ||
               !data.TryGetProperty("method",out var action)||
               action.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-LAN-INVALID-REQUEST");
            id=requestId.ToString("D");
            string? method=action.GetString();
            if(method is not ("lan.status" or "lan.preview" or "lan.apply"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="lan.status")return Ok(id,Status());
                if(method=="lan.preview")
                {
                    pending=null;
                    if(!Integer(data,"listenPort",out var listenPort)||
                       !Integer(data,"peerPort",out var peerPort)||
                       !Text(data,"peerAddress",100,out var address)||
                       !Text(data,"pairingKey",256,out var suppliedKey) ||
                       !Port(listenPort)||!Port(peerPort) ||
                       ((address.Length==0)!=(peerPort==0)) ||
                       (address.Length>0&&!PrivateIPv4(address)))
                        return Fail(id,"UM-LAN-INVALID-PRIVATE-PEER");
                    string? key=suppliedKey.Length>0?suppliedKey:null;
                    if(key!=null && (key.Length<24 || key.Any(char.IsWhiteSpace)))
                        return Fail(id,"UM-LAN-PAIRING-KEY-TOO-SHORT");
                    bool enabled=listenPort>0||peerPort>0;
                    if(enabled && key==null &&
                       string.IsNullOrWhiteSpace(cluster.Config.PairingKeyProtected))
                        return Fail(id,"UM-LAN-PAIRING-KEY-REQUIRED");
                    // Preview never touches Manager config or starts a listener.
                    var cfg=cluster.Config;
                    var token=Guid.NewGuid();
                    pending=new(token,listenPort,address,peerPort,key,
                        cfg.ListenPort,cfg.PeerAddress,cfg.PeerPort,
                        cfg.PairingKeyProtected,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token,listenPort,peerAddress=address,peerPort,
                        pairingKeyWillChange=key!=null,
                        requiresManagerRestart=true,
                        remoteWritesAllowed=false,
                        evidence="LOCAL_LAN_PAIRING_PREFLIGHT_NO_NETWORK_OR_FILE_WRITE"
                    });
                }
                if(!data.TryGetProperty("token",out var tokenValue)||
                   tokenValue.ValueKind!=JsonValueKind.String ||
                   !Guid.TryParse(tokenValue.GetString(),out var approval)||
                   !data.TryGetProperty("confirm",out var confirmed)||
                   confirmed.ValueKind!=JsonValueKind.True ||
                   pending==null||pending.Token!=approval)
                    return Fail(id,"UM-LAN-EXPLICIT-APPROVAL-REQUIRED");
                var snapshot=pending;
                pending=null; // No replay even after a failed ConfigStore write.
                if(DateTimeOffset.UtcNow-snapshot.CreatedUtc>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-LAN-PREVIEW-EXPIRED");
                var current=cluster.Config;
                if(current.ListenPort!=snapshot.OriginalListen ||
                   current.PeerAddress!=snapshot.OriginalAddress ||
                   current.PeerPort!=snapshot.OriginalPeerPort ||
                   current.PairingKeyProtected!=snapshot.OriginalKey)
                    return Fail(id,"UM-LAN-CONFIG-CHANGED");
                string protectedKey=snapshot.NewKey==null?snapshot.OriginalKey:
                    SecureStore.Protect(snapshot.NewKey);
                cluster.UpdateSettings(cfg=>{
                    cfg.ListenPort=snapshot.ListenPort;
                    cfg.PeerAddress=snapshot.PeerAddress;
                    cfg.PeerPort=snapshot.PeerPort;
                    cfg.PairingKeyProtected=protectedKey;
                });
                return Ok(id,new{
                    saved=true,listenPort=snapshot.ListenPort,
                    peerAddress=snapshot.PeerAddress,peerPort=snapshot.PeerPort,
                    requiresManagerRestart=true,
                    secretReturned=false,remoteWritesAllowed=false,
                    evidence="ATOMIC_LOCAL_LAN_PAIRING_DPAPI_PROFILE_ONLY"
                });
            }
            finally {gate.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-LAN-SAFE-UPDATE-FAILED");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
}
