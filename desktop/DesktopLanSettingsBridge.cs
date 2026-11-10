using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Two-stage local Manager-only N-host LAN pairing. Each peer has its own
/// pinned HostId, private IPv4 address and DPAPI-protected shared key. Preview
/// never probes the network, and the listener starts only after Manager restart.
/// </summary>
public sealed class DesktopLanSettingsBridge
{
    private sealed record Pending(Guid Token,string Action,int ListenPort,string HostId,
        LanPeerConfig? Peer,string? NewKey,string OriginalFingerprint,DateTimeOffset CreatedUtc);
    private readonly ClusterService cluster;
    private readonly System.Threading.SemaphoreSlim gate=new(1,1);
    private Pending? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopLanSettingsBridge(ClusterService service)=>
        cluster=service??throw new ArgumentNullException(nameof(service));

    private static bool PrivateIPv4(string text)
    {
        if(!IPAddress.TryParse(text,out var address)||address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)return false;
        var b=address.GetAddressBytes();
        return b[0]==10||(b[0]==172&&b[1] is >=16 and <=31)||(b[0]==192&&b[1]==168);
    }
    private static bool Integer(JsonElement root,string name,out int value)
    {
        value=0;
        return root.TryGetProperty(name,out var prop)&&prop.ValueKind==JsonValueKind.Number&&prop.TryGetInt32(out value);
    }
    private static bool Text(JsonElement root,string name,int maximum,out string value)
    {
        value="";
        if(!root.TryGetProperty(name,out var prop)||prop.ValueKind!=JsonValueKind.String)return false;
        value=prop.GetString()?.Trim()??"";
        return value.Length<=maximum&&!value.Any(char.IsControl);
    }
    private static bool Port(int value)=>value==0||value is >=1024 and <=65535;
    private List<LanPeerConfig> CurrentPeers()=>cluster.Config.LanPeers.Select(x=>new LanPeerConfig{
        HostId=x.HostId,HostName=x.HostName,Address=x.Address,Port=x.Port,
        PairingKeyProtected=x.PairingKeyProtected
    }).ToList();
    private static string Fingerprint(int listenPort,IEnumerable<LanPeerConfig> peers)
    {
        var canonical=new{listenPort,peers=peers.Select(x=>new{
            x.HostId,x.HostName,x.Address,x.Port,x.PairingKeyProtected
        }).OrderBy(x=>x.HostId,StringComparer.OrdinalIgnoreCase).ToArray()};
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }
    private object Status()
    {
        var observations=cluster.PeerObservations;
        // Select the exact original Kova peer among ALL authenticated N-host
        // observations. The first connected host may be an unrelated new
        // machine; it must not replace the historical 5+5 partner.
        var pair=LegacyStoreV1PairComparison.CompareAcrossPeers(
            cluster.ReadLegacyStoreV1PeerProof(),observations);
        var primary=observations.FirstOrDefault();
        return new{
            originalStorePair=new{
                originalPeerHostId=pair.OriginalPeerHostId,
                bothOriginalProfilesPresent=pair.BothOriginalProfilesPresent,
                sameSharedSession=pair.SameSharedSession,sameSharedPath=pair.SameSharedPath,
                distinctDeviceIds=pair.DistinctDeviceIds,bothHaveFiveActiveMaps=pair.BothHaveFiveActiveMaps,
                copiedMetadataPairMatches=pair.CopiedMetadataPairMatches,
                bothPhysicalWitnessesFresh=pair.BothPhysicalWitnessesFresh,
                samePhysicalTransferDirectory=pair.SamePhysicalTransferDirectory,
                bothHistoricalLocalSaveWitnessesFresh=pair.BothHistoricalLocalSaveWitnessesFresh,
                bothHaveFiveRecentHistoricalLocalSaveReceipts=pair.BothHaveFiveRecentHistoricalLocalSaveReceipts,
                liveWriterCertified=false,characterTravelVerified=false,statusCode=pair.StatusCode
            },
            hostName=cluster.Config.LocalHostName,localHostId=cluster.Config.LocalHostId,
            listenPort=cluster.Config.ListenPort,
            // Compatibility properties for older React bundles.
            expectedPeerHostId=primary?.HostId??"",peerAddress=primary?.Address??"",peerPort=primary?.Port??0,
            pairingKeyConfigured=observations.Any(x=>!string.IsNullOrWhiteSpace(
                cluster.Config.LanPeers.FirstOrDefault(p=>p.HostId==x.HostId)?.PairingKeyProtected)),
            remoteFresh=cluster.RemoteIsFresh,remoteState=cluster.RemoteMessage,
            peers=observations.Select(x=>new{
                hostId=x.HostId,hostName=x.HostName,address=x.Address,port=x.Port,
                keyConfigured=!string.IsNullOrWhiteSpace(cluster.Config.LanPeers.FirstOrDefault(p=>p.HostId==x.HostId)?.PairingKeyProtected),
                online=x.IsFresh,lastSuccessUtc=x.LastSuccessUtc,stateCode=x.StateCode,
                mapCount=x.Snapshot?.Maps.Count??0
            }).ToArray(),
            secretReturned=false,peerWritesAllowed=false,evidence="DPAPI_LOCAL_LAN_SETTINGS_READ_ONLY"
        };
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Fail(null,"UM-LAN-INVALID-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            if(data.ValueKind!=JsonValueKind.Object||!data.TryGetProperty("id",out var rid)||
               rid.ValueKind!=JsonValueKind.String||!Guid.TryParse(rid.GetString(),out var requestId)||requestId==Guid.Empty||
               !data.TryGetProperty("method",out var methodValue)||methodValue.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-LAN-INVALID-REQUEST");
            id=requestId.ToString("D");
            var method=methodValue.GetString();
            if(method is not("lan.status" or "lan.preview" or "lan.apply" or
                "lan.physicalStoreProbe" or "lan.historicalSaveProbe"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="lan.status")return Ok(id,Status());
                if(method=="lan.historicalSaveProbe")
                {
                    var witness=cluster.InspectLocalHistoricalSaveReceiptWitness();
                    return Ok(id,new{
                        localHistoricalReceiptsReadOnly=true,
                        observedUtc=witness.ObservedUtc,
                        recentCompletedMapReceipts=witness.RecentCompletedMapReceipts,
                        allFiveRecentLocalReceipts=witness.AllFiveRecentLocalReceipts,
                        crossHostSaveCompleteCertified=false,
                        sharedStoreWriterCertified=false,
                        productionRestoreAllowed=false,
                        originalStoreFilesWritten=false,
                        rconUsed=false,
                        evidence="ON_DEMAND_MANAGER_LOCAL_HISTORICAL_RECEIPTS_NO_WRITER_QUORUM"
                    });
                }
                if(method=="lan.physicalStoreProbe")
                {
                    var witness=cluster.InspectLocalPhysicalTransferWitness();
                    return Ok(id,new{
                        localPhysicalDirectoryIdVerified=true,
                        localHostId=witness.HostId,
                        observedUtc=witness.ObservedUtc,
                        originalMetadataUnchanged=witness.OriginalMetadataUnchanged,
                        // The fingerprint travels in the encrypted LAN status,
                        // never raw original UNC names or Kova session IDs.
                        rawUncPathReturned=false,originalStoreFilesWritten=false,
                        liveStoreWriterCertified=false,
                        productionRestoreAllowed=false,
                        evidence="EXPLICIT_READ_ONLY_WINDOWS_UNC_FILE_ID_LOCAL_ONLY"
                    });
                }
                if(method=="lan.preview")return Preview(id,data);
                return Apply(id,data);
            }
            finally{gate.Release();}
        }
        catch(Exception){return Fail(id,"UM-LAN-SAFE-UPDATE-FAILED");}
    }

    private string Preview(string id,JsonElement data)
    {
        pending=null;
        if(!Integer(data,"listenPort",out var listenPort)||!Port(listenPort))
            return Fail(id,"UM-LAN-INVALID-LISTEN-PORT");
        string action="upsert";
        if(data.TryGetProperty("peerAction",out var actionValue))
        {
            if(actionValue.ValueKind!=JsonValueKind.String)return Fail(id,"UM-LAN-INVALID-ACTION");
            action=actionValue.GetString()??"";
        }
        if(action is not("upsert" or "remove"))return Fail(id,"UM-LAN-INVALID-ACTION");
        if(action=="upsert"&&data.TryGetProperty("peerAddress",out var addressProbe)&&
           addressProbe.ValueKind==JsonValueKind.String&&
           !PrivateIPv4(addressProbe.GetString()?.Trim()??""))
            return Fail(id,"UM-LAN-INVALID-PRIVATE-PEER");
        string hostId="";
        if(!Text(data,"hostId",40,out hostId))
        {
            // Keep the former single-peer request field working during upgrade.
            if(!Text(data,"expectedPeerHostId",40,out hostId))return Fail(id,"UM-LAN-INVALID-HOST-ID");
        }
        if(!Guid.TryParse(hostId,out var parsedHost)||parsedHost==Guid.Empty||
           !Guid.TryParse(cluster.Config.LocalHostId,out var localHost)||parsedHost==localHost)
            return Fail(id,"UM-LAN-PEER-HOST-ID-REQUIRED");
        hostId=parsedHost.ToString("N");
        var peers=CurrentPeers();
        var existing=peers.SingleOrDefault(x=>StringComparer.OrdinalIgnoreCase.Equals(x.HostId,hostId));
        LanPeerConfig? peer=null;
        string? key=null;
        if(action=="remove")
        {
            if(existing==null)return Fail(id,"UM-LAN-PEER-NOT-FOUND");
            if(peers.Count==1&&listenPort>0)return Fail(id,"UM-LAN-LAST-PEER-LISTENER-MUST_DISABLE");
        }
        else
        {
            string name;
            if(data.TryGetProperty("hostName",out _))
            {
                if(!Text(data,"hostName",100,out name)||name.Length==0)return Fail(id,"UM-LAN-INVALID-HOST-NAME");
            }
            else name="Peer "+parsedHost.ToString("N")[..8];
            if(!Text(data,"peerAddress",100,out var address)||!PrivateIPv4(address)||
               !Integer(data,"peerPort",out var peerPort)||peerPort is <1024 or >65535||
               !Text(data,"pairingKey",256,out var suppliedKey))
                return Fail(id,"UM-LAN-INVALID-PRIVATE-PEER");
            if(!Guid.TryParse(cluster.Config.LocalHostId,out localHost)||parsedHost==localHost)
                return Fail(id,"UM-LAN-PEER-HOST-ID-REQUIRED");
            key=suppliedKey.Length>0?suppliedKey:null;
            if(key!=null&&(key.Length<24||key.Any(char.IsWhiteSpace)))return Fail(id,"UM-LAN-PAIRING-KEY-TOO-SHORT");
            if(key==null&&existing==null)return Fail(id,"UM-LAN-PAIRING-KEY-REQUIRED");
            peer=new LanPeerConfig{HostId=hostId,HostName=name,Address=IPAddress.Parse(address).ToString(),
                Port=peerPort,PairingKeyProtected=existing?.PairingKeyProtected??""};
        }
        if(peers.Count-(action=="remove"?1:0)+(action=="upsert"&&existing==null?1:0)>0&&listenPort==0)
        {
            // A host may be outbound-only. Its peer still needs an enabled listener.
        }
        var token=Guid.NewGuid();
        pending=new Pending(token,action,listenPort,hostId,peer,key,
            Fingerprint(cluster.Config.ListenPort,peers),DateTimeOffset.UtcNow);
        return Ok(id,new{
            token,action,listenPort,hostId,hostName=peer?.HostName,
            peerAddress=peer?.Address,peerPort=peer?.Port??0,
            pairingKeyWillChange=key!=null,requiresManagerRestart=true,
            remoteWritesAllowed=false,evidence="LOCAL_LAN_PAIRING_PREFLIGHT_NO_NETWORK_OR_FILE_WRITE"
        });
    }

    private string Apply(string id,JsonElement data)
    {
        if(!data.TryGetProperty("token",out var tokenValue)||tokenValue.ValueKind!=JsonValueKind.String||
           !Guid.TryParse(tokenValue.GetString(),out var approval)||
           !data.TryGetProperty("confirm",out var confirmed)||confirmed.ValueKind!=JsonValueKind.True||
           pending==null||pending.Token!=approval)return Fail(id,"UM-LAN-EXPLICIT-APPROVAL-REQUIRED");
        var snapshot=pending;pending=null;
        if(DateTimeOffset.UtcNow-snapshot.CreatedUtc>TimeSpan.FromMinutes(5))return Fail(id,"UM-LAN-PREVIEW-EXPIRED");
        var peers=CurrentPeers();
        if(Fingerprint(cluster.Config.ListenPort,peers)!=snapshot.OriginalFingerprint)
            return Fail(id,"UM-LAN-CONFIG-CHANGED");
        if(snapshot.Action=="remove")peers.RemoveAll(x=>StringComparer.OrdinalIgnoreCase.Equals(x.HostId,snapshot.HostId));
        else
        {
            var peer=snapshot.Peer??throw new InvalidDataException("Previewed peer is missing");
            var existing=peers.SingleOrDefault(x=>StringComparer.OrdinalIgnoreCase.Equals(x.HostId,snapshot.HostId));
            peer.PairingKeyProtected=snapshot.NewKey==null
                ?existing?.PairingKeyProtected??throw new InvalidDataException("Existing peer key is missing")
                :SecureStore.Protect(snapshot.NewKey);
            if(existing==null)peers.Add(peer);
            else peers[peers.IndexOf(existing)]=peer;
        }
        cluster.UpdateLanConfiguration(snapshot.ListenPort,peers);
        return Ok(id,new{
            saved=true,action=snapshot.Action,listenPort=snapshot.ListenPort,
            hostId=snapshot.HostId,peerCount=peers.Count,
            requiresManagerRestart=true,secretReturned=false,remoteWritesAllowed=false,
            evidence="ATOMIC_LOCAL_LAN_PAIRING_DPAPI_PROFILE_ONLY"
        });
    }

    private static string Ok(string id,object data)=>JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
}
