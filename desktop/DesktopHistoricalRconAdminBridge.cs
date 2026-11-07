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
/// Single-send, original ACTIVE Store-v1 local admin RCON. No browser-supplied
/// endpoint, no remote LAN write, no new original session/lock or conversion.
/// Requires the original two-peer source, exact PID/start and locally owned
/// TCP listener at preview, after native approval and after authentication.
/// </summary>
public sealed class DesktopHistoricalRconAdminBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,string Action,
        string Command,int Pid,DateTimeOffset StartedUtc,string SourceSha,
        string ProfileJson,DateTimeOffset Expires);
    private readonly ClusterService cluster;
    private readonly Func<Guid,LegacyStoreV1GracefulStop.Owned> inspect;
    private readonly Func<string,int,string,string,Action,CancellationToken,Task<string>> send;
    private readonly SemaphoreSlim gate=new(1,1);
    private Approval? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly Regex ForbiddenLifecycle=new(
        @"\A\s*(?:(?:cheat|admincheat)\s+)*(?:doexit|exit|quit|stop|restart|saveworld)\b",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public DesktopHistoricalRconAdminBridge(ClusterService service):
        this(service,null,null){}

    internal DesktopHistoricalRconAdminBridge(ClusterService service,
        Func<Guid,LegacyStoreV1GracefulStop.Owned>? proof,
        Func<string,int,string,string,Action,CancellationToken,Task<string>>? dispatch)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        inspect=proof??(mapId=>LegacyStoreV1GracefulStop.Preview(cluster,mapId));
        send=dispatch??RconProtocol.DispatchApprovedAdminAsync;
    }
    private static bool Id(JsonElement root,string name,out Guid id)
    {
        id=Guid.Empty;
        return root.TryGetProperty(name,out var prop)&&
            prop.ValueKind==JsonValueKind.String&&
            Guid.TryParse(prop.GetString(),out id)&&id!=Guid.Empty;
    }
    private static string Command(string action,string message)
    {
        if(message==null||message.Length>240||
           message.Any(char.IsControl)||message.Contains(';'))
            throw new InvalidDataException("UM-ORIGINAL-ADMIN-COMMAND-INVALID");
        string value=message.Trim();
        switch(action)
        {
            case "broadcast":
            case "serverChat":
                if(value.Length is <1 or >200)
                    throw new InvalidDataException("UM-ORIGINAL-ADMIN-MESSAGE-INVALID");
                return (action=="broadcast"?"Broadcast ":"ServerChat ")+value;
            case "wildWipe":
                if(value!="")throw new InvalidDataException("UM-ORIGINAL-ADMIN-WIPE-PARAMETER");
                return "DestroyWildDinos";
            case "custom":
                if(value.Length is <1 or >240||ForbiddenLifecycle.IsMatch(value))
                    throw new InvalidDataException("UM-ORIGINAL-ADMIN-LIFECYCLE-DENIED");
                return value;
            default:throw new InvalidDataException("UM-ORIGINAL-ADMIN-ACTION-DENIED");
        }
    }
    private static string ApprovalWord(string action)=>action switch{
        "wildWipe"=>"STORE-V1 DINÓTÖRLÉS",
        "broadcast" or "serverChat" or "custom"=>"STORE-V1 ADMIN",
        _=>throw new InvalidDataException("UM-ORIGINAL-ADMIN-ACTION-DENIED")
    };
    private static bool Identical(ProcessIdentity first,ProcessIdentity second)=>
        first.Pid>0&&first.Pid==second.Pid&&
        first.StartedUtc==second.StartedUtc&&
        string.Equals(first.ExecutablePath,second.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);

    private LegacyStoreV1GracefulStop.Owned Require(Guid mapId)
    {
        var original=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId)
            ??throw new UnauthorizedAccessException("UM-ORIGINAL-ADMIN-LOCAL-ONLY");
        if(!original.HistoricalStoreProtected||original.Maintenance||
           !string.IsNullOrWhiteSpace(original.Arguments))
            throw new InvalidOperationException("UM-ORIGINAL-ADMIN-HISTORICAL-ONLY");
        var journal=cluster.Operations.Observe(original.InstallationPath);
        if(journal?.Phase is UntamedOperationPhase.Prepared or
            UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator)
            throw new InvalidOperationException("UM-ORIGINAL-ADMIN-DURABLE-RECOVERY-REQUIRED");
        var proof=inspect(mapId);
        if(!ReferenceEquals(proof.Map,original)||
           proof.Process.Pid<=0||proof.Process.StartedUtc==default||
           proof.SourceSha.Length!=64||
           !proof.SourceSha.All(Uri.IsHexDigit))
            throw new InvalidDataException("UM-ORIGINAL-ADMIN-OWNER-PROOF");
        return proof;
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Fail(null,"UM-ORIGINAL-ADMIN-REQUEST");
            using var parsed=JsonDocument.Parse(raw);
            var root=parsed.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||
               !Id(root,"id",out var requestId)||
               !root.TryGetProperty("method",out var methodProp)||
               methodProp.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-ORIGINAL-ADMIN-REQUEST");
            id=requestId.ToString("D");
            string? method=methodProp.GetString();
            if(method is not ("historical.adminPreview" or "historical.adminSend"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="historical.adminPreview")
                {
                    pending=null;
                    if(!Id(root,"mapId",out var mapId)||
                       !root.TryGetProperty("action",out var a)||
                       a.ValueKind!=JsonValueKind.String||
                       !root.TryGetProperty("message",out var m)||
                       m.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-ORIGINAL-ADMIN-INVALID-SELECTION");
                    string action=a.GetString()??"";
                    string command=Command(action,m.GetString()??"");
                    var proof=await Task.Run(()=>Require(mapId));
                    pending=new Approval(Guid.NewGuid(),mapId,action,command,
                        proof.Process.Pid,proof.Process.StartedUtc,
                        proof.SourceSha,JsonSerializer.Serialize(proof.Map),
                        DateTimeOffset.UtcNow.AddMinutes(3));
                    return Ok(id,new{
                        token=pending.Token,mapId,mapName=proof.Map.Name,
                        action,command,approvalWord=ApprovalWord(action),
                        dangerous=action is "wildWipe" or "custom",
                        expiresUtc=pending.Expires,
                        originalStoreActive=true,dispatched=false,
                        gameClientVerified=false,originalStoreMetadataWritten=false,
                        evidence="ORIGINAL_STORE_ACTIVE_ADMIN_RCON_PREVIEW"
                    });
                }
                if(!Id(root,"token",out var token)||
                   !root.TryGetProperty("confirm",out var confirmed)||
                   confirmed.ValueKind!=JsonValueKind.True||
                   pending==null||pending.Token!=token)
                    return Fail(id,"UM-ORIGINAL-ADMIN-APPROVAL");
                var approved=pending;
                pending=null; // Never send twice, even on bad typed confirmation.
                if(approved.Expires<DateTimeOffset.UtcNow||
                   !root.TryGetProperty("typedApproval",out var typed)||
                   typed.ValueKind!=JsonValueKind.String||
                   typed.GetString()!=ApprovalWord(approved.Action))
                    return Fail(id,"UM-ORIGINAL-ADMIN-TYPED-APPROVAL");
                var first=await Task.Run(()=>Require(approved.MapId));
                if(!Identical(first.Process,new ProcessIdentity{
                    Pid=approved.Pid,StartedUtc=approved.StartedUtc,
                    ExecutablePath=first.Process.ExecutablePath})||
                   first.SourceSha!=approved.SourceSha||
                   JsonSerializer.Serialize(first.Map)!=approved.ProfileJson)
                    return Fail(id,"UM-ORIGINAL-ADMIN-PROCESS-OR-SOURCE-CHANGED");
                using var lease=cluster.Operations.Acquire(first.Map.InstallationPath,
                    UntamedOperationKind.Configuration);
                // Lease persists the fact this exact local installation
                // received a mutating command, without touching Store files.
                var locked=inspect(approved.MapId);
                if(!ReferenceEquals(locked.Map,first.Map)||
                   !Identical(locked.Process,first.Process)||
                   locked.SourceSha!=first.SourceSha||
                   JsonSerializer.Serialize(locked.Map)!=approved.ProfileJson)
                    throw new InvalidDataException("UM-ORIGINAL-ADMIN-LEASE-PROOF-CHANGED");
                void Recheck()
                {
                    var now=inspect(approved.MapId);
                    if(!ReferenceEquals(now.Map,locked.Map)||
                       !Identical(now.Process,locked.Process)||
                       now.SourceSha!=locked.SourceSha||
                       JsonSerializer.Serialize(now.Map)!=approved.ProfileJson)
                        throw new InvalidOperationException("UM-ORIGINAL-ADMIN-PID-OR-SESSION-CHANGED");
                }
                Recheck();
                string password=SecureStore.Unprotect(locked.Map.RconPasswordProtected);
                if(string.IsNullOrEmpty(password))
                    throw new InvalidDataException("UM-ORIGINAL-ADMIN-RCON-SECRET-MISSING");
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
                lease.MarkMutationStarted();
                string output=await send("127.0.0.1",locked.Map.RconPort,
                    password,approved.Command,Recheck,deadline.Token);
                Recheck();
                if(output==null||output.Length>32768)
                    throw new InvalidDataException("UM-ORIGINAL-ADMIN-REPLY-UNVERIFIED");
                lease.Complete();
                return Ok(id,new{
                    mapId=locked.Map.Id,mapName=locked.Map.Name,
                    action=approved.Action,command=approved.Command,
                    response=UntamedPresentation.Redact(output).Replace(
                        password,"[REDACTED]",StringComparison.Ordinal),
                    commandSent=true,oneShot=true,
                    originalStoreMetadataWritten=false,saveCompleteProven=false,
                    gameplayEffectVerified=false,
                    evidence="ORIGINAL_STORE_ACTIVE_ADMIN_RCON_SINGLE_SEND"
                });
            }
            finally{gate.Release();}
        }
        catch(Exception)
        {
            // The cross-process journal stays NEEDS_OPERATOR if anything
            // goes wrong after authenticated command dispatch may have begun.
            return Fail(id,"UM-ORIGINAL-ADMIN-SAFETY-CHECK-FAILED_NO_RETRY");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>gate.Dispose();
}
