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
/// Explicit per-map fixed RCON actions. The renderer cannot send arbitrary
/// commands, select host/port, retrieve passwords or reach the LAN peer.
/// Only an identified, running, locally owned ASA process may receive
/// ListPlayers and explicitly approved SaveWorld; port ownership is verified.
/// </summary>
public sealed class DesktopRconReadOnlyBridge
{
    private readonly ClusterService cluster;
    private readonly Func<int,int,bool> ownsPort;
    private readonly Func<string,int,string,string,Action,CancellationToken,Task<string>> send;
    private readonly SemaphoreSlim serial=new(1,1);
    private sealed record SaveCheck(Guid Token,Guid MapId,int Pid,
        DateTimeOffset StartedUtc,AsaWorldSaveEvidence.Boundary Bookmark,
        DateTimeOffset Expires,bool Completed=false);
    // One map cannot invalidate another map's SaveWorld evidence.
    private readonly Dictionary<Guid,SaveCheck> pendingSaves=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopRconReadOnlyBridge(ClusterService cluster,
        Func<int,int,bool>? portOwner=null,
        Func<string,int,string,string,CancellationToken,Task<string>>? dispatch=null)
    {
        this.cluster=cluster??throw new ArgumentNullException(nameof(cluster));
        ownsPort=portOwner??RconPortOwnership.IsOwnedListener;
        send=dispatch==null
            ?async(host,port,password,command,recheck,ct)=>{
                if(command=="SaveWorld")
                {
                    await RconProtocol.DispatchOwnedSaveWorldAsync(
                        port,password,recheck,ct);
                    return "SAVEWORLD_DISPATCHED_NOT_COMPLETED";
                }
                recheck();
                return await RconProtocol.Execute(host,port,password,command,ct);
            }
            :(host,port,password,command,recheck,ct)=>{
                recheck();
                return dispatch(host,port,password,command,ct);
            };
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
            if(method is not ("rcon.status" or "rcon.listPlayers" or "rcon.saveWorld" or
                "rcon.saveStatus" or "rcon.gameLog" or "rcon.chatLog"))
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
                if(map.ObservedReadOnlyImported)return Deny(id,"UM-LIVE-OBSERVATION-NO-RCON-AUTHORITY");
                // Store-v1 is checked BEFORE ordinary RCON preflight: a
                // changed historical command line may invalidate the owned
                // PID check, but it must never hide the stronger Store write
                // prohibition or permit a retry through another endpoint.
                bool newStore=FreshStoreEnrollmentPolicy.IsEnrolled(map);
                if(newStore)
                {
                    try{FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);}
                    catch(Exception){return Deny(id,"UM-FRESH-STORE-CLUSTER-STATE-CHANGED");}
                }
                if(method=="rcon.saveWorld" && !newStore &&
                   LegacyCompatibleWorldBackup.HasLiveOrHistoricalStore(map))
                    return Deny(id,"UM-HISTORICAL-STORE-LOCKED");
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
                        allowedCommands=new[]{"ListPlayers","GetGameLog","GetChat","SaveWorld"},
                        evidence="LOCAL_RCON_READ_ONLY_PREFLIGHT_NO_COMMAND"
                    });
                if(!safe)return Deny(id,"UM-RCON-READONLY-PREFLIGHT-DENIED");
                if(method=="rcon.saveStatus")
                {
                    if(!root.TryGetProperty("watchId",out var identifier) ||
                       identifier.ValueKind!=JsonValueKind.String ||
                       !Guid.TryParse(identifier.GetString(),out var watchId) ||
                       !pendingSaves.TryGetValue(map.Id,out var savedCheck)||
                       savedCheck.Token!=watchId ||
                       savedCheck.Pid!=check.Identity!.Pid||
                       savedCheck.StartedUtc!=check.Identity.StartedUtc ||
                       savedCheck.Expires<DateTimeOffset.UtcNow)
                        return Deny(id,"UM-RCON-SAVE-WITNESS-NOT-FOUND");
                    var requested=savedCheck;
                    bool Same() {
                        var current=cluster.Check(map);
                        return current.Running&&!current.ConfirmedStopped &&
                            current.Identity is {Pid:>0} active &&
                            active.Pid==requested.Pid &&
                            active.StartedUtc==requested.StartedUtc &&
                            ownsPort(map.RconPort,requested.Pid);
                    }
                    using var proofDeadline=new CancellationTokenSource(
                        TimeSpan.FromSeconds(3));
                    var witness=await AsaWorldSaveEvidence.ObserveAsync(
                        requested.Bookmark,Same,proofDeadline.Token);
                    // Keep the receipt readable, but permit a new explicitly
                    // approved SaveWorld after this one has real file evidence.
                    if(witness.CompletionObserved||witness.WorldMetadataObserved)
                        pendingSaves[map.Id]=requested with {Completed=true};
                    return Ok(id,new{
                        mapId=map.Id,watchId=requested.Token,
                        rconCommandDispatched=true,
                        logSavingStarted=witness.LogStarted,
                        logSaveCompleteObserved=witness.LogCompleted,
                        worldFileUpdated=witness.WorldUpdated,
                        worldFileStable=witness.WorldStable,
                        combinedSaveEvidenceObserved=witness.CompletionObserved,
                        worldMetadataObserved=witness.WorldMetadataObserved,
                        diskFsyncCertified=false,
                        gameClientRejoinVerified=false,
                        sourceIsLocalAsaLog=true,
                        stage=witness.Stage,
                        evidence="LOCAL_ASA_POST_DISPATCH_LOG_AND_WORLD_METADATA"
                    });
                }
                if(method=="rcon.saveWorld")
                {
                    // A running historical -usestore ASA process has shared
                    // character/tribe ownership. A local manual SaveWorld is
                    // a Store write; never treat read-only RCON authentication
                    // as authorization to mutate an unverified old session.
                    // A second SaveWorld while an approved DoExit/Restart is
                    // underway can race the server's final world save.
                    // Read-only log/chat/players remain available for diagnosis.
                    new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
                    if(pendingSaves.TryGetValue(map.Id,out var previous)&&
                       !previous.Completed&&previous.Expires>DateTimeOffset.UtcNow)
                        return Deny(id,"UM-RCON-SAVE-ALREADY-PENDING");
                    pendingSaves.Remove(map.Id);
                    if(!root.TryGetProperty("confirm",out var approve) ||
                       approve.ValueKind!=JsonValueKind.True)
                        return Deny(id,"UM-RCON-SAVE-EXPLICIT-CONFIRMATION-REQUIRED");
                    if(!ReleaseCapabilityGate.Check(ManagerOperation.LiveWorldSave,
                        true,map.Maintenance,check.ConfirmedStopped,check.Running).Allowed)
                        return Deny(id,"UM-RCON-SAVE-PREFLIGHT-DENIED");
                }
                var identity=check.Identity!;
                string password=SecureStore.Unprotect(map.RconPasswordProtected);
                if(string.IsNullOrEmpty(password))return Deny(id,"UM-RCON-CREDENTIAL-UNAVAILABLE");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(9));
                // The command string is server-owned and fixed. It is never
                // read from the untrusted WebView request.
                string command=method switch {
                    "rcon.saveWorld"=>"SaveWorld",
                    "rcon.gameLog"=>"GetGameLog",
                    "rcon.chatLog"=>"GetChat",
                    _=>"ListPlayers"
                };
                void VerifyOwned()
                {
                    var live=cluster.Check(map);
                    if(!live.Running||live.ConfirmedStopped||
                       live.Identity is not {Pid:>0} active||
                       active.Pid!=identity.Pid||
                       active.StartedUtc!=identity.StartedUtc||
                       !ownsPort(map.RconPort,identity.Pid))
                        throw new InvalidOperationException(
                            "UM-RCON-POST-AUTH-OWNER-CHANGED");
                }
                // Capture log and world-file boundary BEFORE SaveWorld bytes;
                // RCON success alone can never be a SaveComplete signal.
                AsaWorldSaveEvidence.Boundary? bookmark=null;
                if(method=="rcon.saveWorld")
                {
                    bookmark=AsaWorldSaveEvidence.Begin(map,identity);
                }
                string answer=await send("127.0.0.1",map.RconPort,password,command,
                    VerifyOwned,timeout.Token);
                var after=cluster.Check(map);
                if(!after.Running || after.Identity==null ||
                   after.Identity.Pid!=identity.Pid ||
                   after.Identity.StartedUtc!=identity.StartedUtc ||
                   !ownsPort(map.RconPort,after.Identity.Pid))
                    return Deny(id,"UM-RCON-PROCESS-CHANGED");
                if(answer.Length>32000)return Deny(id,"UM-RCON-OVERSIZED-REPLY");
                string sanitized=UntamedPresentation.Redact(answer);
                if(method=="rcon.saveWorld")
                {
                    if(bookmark!=null)
                        pendingSaves[map.Id]=new SaveCheck(Guid.NewGuid(),map.Id,
                            identity.Pid,identity.StartedUtc,bookmark,
                            DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id,new {
                        watchId=pendingSaves.TryGetValue(map.Id,out var checkSaved)
                            ?checkSaved.Token:(Guid?)null,
                        nativeWitnessAvailable=bookmark!=null,
                        mapId=map.Id,mapName=map.Name,
                        rconAcknowledged=false,rconCommandDispatched=true,
                        diskSaveCompletionProven=false,
                        worldFilesTouchedByManager=false,
                        response=sanitized,observedUtc=DateTimeOffset.UtcNow,
                        evidence="REAL_FIXED_RCON_SAVEWORLD_ACK_NOT_DISK_COMPLETION"
                    });
                }
                return Ok(id,new {
                    mapId=map.Id,mapName=map.Name,command,response=sanitized,
                    observedUtc=DateTimeOffset.UtcNow,readOnly=true,
                    saveCompleteProven=false,
                    evidence=method=="rcon.listPlayers"?
                        "LOCAL_VERIFIED_READ_ONLY_LISTPLAYERS":
                        "LOCAL_VERIFIED_READ_ONLY_RCON"
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
