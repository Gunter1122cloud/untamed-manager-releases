using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Real, single-send local ASA admin RCON with a short-lived preview token.
/// All untrusted commands originate in WebView2, but only a locally owned
/// OS-verified ASA process, its exact RCON listener and a DPAPI-managed
/// credential can receive one. Mutating commands require native approval;
/// no automatic replay occurs after timeout, disconnect or PID change.
/// Lifecycle commands are intentionally kept on the separate safe stop path.
/// </summary>
public sealed class DesktopRconAdminBridge
{
    private sealed record Pending(Guid Token,Guid MapId,string Action,string Command,
        int Pid,DateTimeOffset ProcessStartUtc,DateTimeOffset Expires);
    private readonly ClusterService cluster;
    private readonly Func<int,int,bool> ownsPort;
    private readonly Func<string,int,string,string,Action,CancellationToken,Task<string>> send;
    private readonly SemaphoreSlim gate=new(1,1);
    private Pending? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly Regex ForbiddenLifecycle=new(
        @"\A\s*(?:(?:cheat|admincheat)\s+)*(?:doexit|exit|quit|stop|restart|saveworld)\b",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public DesktopRconAdminBridge(ClusterService local,
        Func<int,int,bool>? owner=null,
        Func<string,int,string,string,CancellationToken,Task<string>>? dispatch=null)
    {
        cluster=local??throw new ArgumentNullException(nameof(local));
        ownsPort=owner??RconPortOwnership.IsOwnedListener;
        send=dispatch==null
            ?RconProtocol.DispatchApprovedAdminAsync
            :(host,port,password,command,recheck,ct)=>{
                recheck(); // injected synthetic tests still exercise ownership
                return dispatch(host,port,password,command,ct);
            };
    }
    private static string BuildCommand(string action,string message)
    {
        if(message.Length>240||message.Any(char.IsControl)||
           message.Contains(';')||message.Contains('\0'))
            throw new InvalidDataException("UM-RCON-UNSAFE-COMMAND");
        string text=message.Trim();
        if(action=="broadcast"||action=="serverChat")
        {
            if(text.Length is <1 or >200)
                throw new InvalidDataException("UM-RCON-INVALID-MESSAGE");
            return (action=="broadcast"?"Broadcast ":"ServerChat ")+text;
        }
        if(action=="wildWipe")
        {
            if(text.Length!=0)throw new InvalidDataException("UM-RCON-INVALID-PARAMETER");
            return "DestroyWildDinos";
        }
        if(action=="custom")
        {
            if(text.Length is <1 or >240||ForbiddenLifecycle.IsMatch(text))
                throw new InvalidDataException("UM-RCON-LIFECYCLE-SEPARATE");
            return text;
        }
        throw new InvalidDataException("UM-RCON-INVALID-ACTION");
    }
    private (LocalMap Map,ProcessIdentity Identity) RequireOwned(Guid mapId)
    {
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
        if(map==null)throw new UnauthorizedAccessException("UM-RCON-MAP-NOT-OWNED");
        if(map.HistoricalStoreProtected||map.ObservedReadOnlyImported||map.Maintenance)
            throw new InvalidOperationException("UM-RCON-MAP-PROTECTED");
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        if(FreshStoreEnrollmentPolicy.IsEnrolled(map))
            FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);
        else if(KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map))
            throw new InvalidOperationException("UM-RCON-MAP-PROTECTED");
        if(map.RconHost!="127.0.0.1"||map.RconPort is <1 or >65535||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected))
            throw new InvalidOperationException("UM-RCON-LOCAL-CONFIG-REQUIRED");
        var state=cluster.Check(map);
        if(!state.Running||state.ConfirmedStopped||state.Identity is not {Pid:>0})
            throw new InvalidOperationException("UM-RCON-PROCESS-UNVERIFIED");
        var identity=state.Identity;
        if(!ownsPort(map.RconPort,identity.Pid))
            throw new InvalidOperationException("UM-RCON-LISTENER-UNVERIFIED");
        return(map,identity);
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Fail(null,"UM-RCON-INVALID-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||
               !root.TryGetProperty("id",out var rid)||
               rid.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(rid.GetString(),out var request)||request==Guid.Empty||
               !root.TryGetProperty("method",out var rawMethod)||
               rawMethod.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-RCON-INVALID-REQUEST");
            id=request.ToString("D");
            string? method=rawMethod.GetString();
            if(method is not ("rcon.adminPreview" or "rcon.adminSend"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="rcon.adminPreview")
                {
                    pending=null;
                    if(!root.TryGetProperty("mapId",out var rawMap)||
                       rawMap.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(rawMap.GetString(),out var mapId)||
                       !root.TryGetProperty("action",out var rawAction)||
                       rawAction.ValueKind!=JsonValueKind.String||
                       !root.TryGetProperty("message",out var rawMessage)||
                       rawMessage.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-RCON-INVALID-PREVIEW");
                    string action=rawAction.GetString()??"";
                    string message=rawMessage.GetString()??"";
                    string command=BuildCommand(action,message);
                    var (map,identity)=RequireOwned(mapId);
                    pending=new(Guid.NewGuid(),map.Id,action,command,identity.Pid,
                        identity.StartedUtc,DateTimeOffset.UtcNow.AddMinutes(3));
                    return Ok(id,new{
                        token=pending.Token,mapId=map.Id,mapName=map.Name,
                        action,command,requiresApproval=true,
                        dangerous=action is "wildWipe" or "custom",
                        approvalWord=action=="wildWipe"?"DINÓTÖRLÉS":
                                     action=="custom"?"ADMIN":null,
                        expiresUtc=pending.Expires,
                        dispatched=false,credentialExposed=false,
                        evidence="LOCAL_OS_VERIFIED_ADMIN_RCON_PREVIEW"
                    });
                }
                if(!root.TryGetProperty("token",out var rawToken)||
                   rawToken.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(rawToken.GetString(),out var token)||
                   !root.TryGetProperty("confirm",out var yes)||
                   yes.ValueKind!=JsonValueKind.True||
                   pending==null||pending.Token!=token)
                    return Fail(id,"UM-RCON-ADMIN-APPROVAL-REQUIRED");
                var job=pending;
                // Claim before dispatch. A timeout cannot silently resend
                // a non-idempotent command to a later process.
                pending=null;
                if(job.Expires<DateTimeOffset.UtcNow)
                    return Fail(id,"UM-RCON-ADMIN-PREVIEW-EXPIRED");
                string approval=job.Action=="wildWipe"?"DINÓTÖRLÉS":
                                job.Action=="custom"?"ADMIN":"";
                if(approval!="")
                {
                    if(!root.TryGetProperty("typedApproval",out var typed)||
                       typed.ValueKind!=JsonValueKind.String||
                       typed.GetString()!=approval)
                        return Fail(id,"UM-RCON-ADMIN-TYPED-APPROVAL-REQUIRED");
                }
                var (owned,identityNow)=RequireOwned(job.MapId);
                if(identityNow.Pid!=job.Pid||
                   identityNow.StartedUtc!=job.ProcessStartUtc)
                    return Fail(id,"UM-RCON-ADMIN-PROCESS-CHANGED");
                string password=SecureStore.Unprotect(owned.RconPasswordProtected);
                if(string.IsNullOrEmpty(password))
                    return Fail(id,"UM-RCON-ADMIN-PASSWORD-UNAVAILABLE");
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(11));
                void RecheckAfterAuthentication()
                {
                    var (live,process)=RequireOwned(job.MapId);
                    if(process.Pid!=job.Pid ||
                       process.StartedUtc!=job.ProcessStartUtc ||
                       live.RconPort!=owned.RconPort)
                        throw new InvalidOperationException(
                            "UM-RCON-ADMIN-POST-AUTH-PID-CHANGED");
                }
                string output=await send("127.0.0.1",owned.RconPort,password,job.Command,
                    RecheckAfterAuthentication,deadline.Token);
                var check=cluster.Check(owned);
                if(!check.Running||check.Identity==null||
                   check.Identity.Pid!=job.Pid||
                   check.Identity.StartedUtc!=job.ProcessStartUtc||
                   !ownsPort(owned.RconPort,job.Pid))
                    return Fail(id,"UM-RCON-ADMIN-POST-SEND-UNVERIFIED_NO_RETRY");
                if(output.Length>32000)
                    return Fail(id,"UM-RCON-ADMIN-OUTPUT-TOO-LARGE");
                return Ok(id,new{
                    mapId=owned.Id,mapName=owned.Name,action=job.Action,
                    command=job.Command,response=UntamedPresentation.Redact(output),
                    commandSent=true,observedUtc=DateTimeOffset.UtcNow,
                    oneShot=true,saveCompleteProven=false,
                    storeWriterTouched=false,
                    evidence="LIVE_LOCAL_ADMIN_RCON_SINGLE_SEND"
                });
            }
            finally{gate.Release();}
        }
        catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or IOException or JsonException or ArgumentException or
            System.Net.Sockets.SocketException or TaskCanceledException or
            OperationCanceledException or System.Security.Cryptography.CryptographicException or
            TimeoutException or RegexMatchTimeoutException)
        {
            return Fail(id,"UM-RCON-ADMIN-SAFETY-CHECK-FAILED_NO_AUTO_RETRY");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
}
