using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit two-step take-over of an already registered, running, NON-STORE
/// ASA process. Preview is read-only; apply only persists Manager configuration.
/// The Core independently checks the real Windows process under a machine-wide
/// installation lock. Historical Kova Store, loader processes and ambiguous
/// identities cannot use this path. No RCON, SaveWorld or process mutation here.
/// </summary>
public sealed class DesktopRunningAsaAdoptionBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,int Pid,
        DateTimeOffset StartedUtc,string CommandHash,DateTimeOffset ExpiresUtc);
    private readonly ClusterService cluster;
    private readonly Func<IReadOnlyList<LocalRunningAsaDiscovery.Sample>> observe;
    private readonly SemaphoreSlim serial=new(1,1);
    private readonly Dictionary<Guid,Approval> pending=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopRunningAsaAdoptionBridge(ClusterService owner,
        Func<IReadOnlyList<LocalRunningAsaDiscovery.Sample>>? observer=null)
    {
        cluster=owner??throw new ArgumentNullException(nameof(owner));
        observe=observer??LocalRunningAsaDiscovery.ReadSamples;
    }

    private static string Hash(string value)=>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    private static bool GuidField(JsonElement j,string field,out Guid value)
    {
        value=Guid.Empty;
        return j.TryGetProperty(field,out var el)&&
            el.ValueKind==JsonValueKind.String&&
            Guid.TryParse(el.GetString(),out value)&&value!=Guid.Empty;
    }

    private (LocalMap Map,LocalRunningAsaDiscovery.Sample Server,
        string Arguments) Candidate(Guid mapId,
            IReadOnlyList<LocalRunningAsaDiscovery.Sample> snapshot)
    {
        var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId);
        if(map==null||!map.ObservedReadOnlyImported||
           map.HistoricalStoreProtected ||
           !string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256) ||
           !string.IsNullOrWhiteSpace(map.FreshStoreClusterId) ||
           map.StartWithAsaApi||!string.IsNullOrWhiteSpace(map.ApiLoaderPath)||
           map.LastProcess!=null||map.LastLoader!=null)
            throw new InvalidDataException("UM-ADOPT-NOT-OBSERVED-NONSTORE");
        var matching=LocalRunningAsaDiscovery.Classify(snapshot,
            cluster.Config.LocalMaps).Where(x=>
                x.ProcessKind=="asaServer"&&
                x.MatchState=="profileCandidate"&&
                x.ObservedReadOnlyImported&&
                x.ProfileMapId==mapId.ToString("D")&&
                !x.LoaderParentObserved&&
                x.StartedUtc is {Year:>=2020}&&
                x.Pid>0).ToArray();
        if(matching.Length!=1)
            throw new InvalidDataException("UM-ADOPT-AMBIGUOUS-OR-MISSING");
        // Never accept two ASA processes claiming the same map, even if one
        // has unreadable WMI fields and classification failed.
        var match=matching[0];
        var server=snapshot.SingleOrDefault(p=>p.Pid==match.Pid);
        if(server==null || snapshot.Count(p=>p.Pid==match.Pid)!=1 ||
           server.StartedUtc!=match.StartedUtc ||
           !string.Equals(server.Name,"ArkAscendedServer.exe",
               StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(server.ExecutablePath,map.ExecutablePath,
               StringComparison.OrdinalIgnoreCase) ||
           snapshot.Any(p=>p.Pid!=server.Pid &&
               p.Name.Equals("ArkAscendedServer.exe",
                   StringComparison.OrdinalIgnoreCase)&&
               string.Equals(p.ExecutablePath,map.ExecutablePath,
                   StringComparison.OrdinalIgnoreCase)&&
               LocalRunningAsaDiscovery.CommandArguments(
                   p.CommandLine,p.ExecutablePath)?.StartsWith(
                   map.ObservedMapKey+"?listen",
                   StringComparison.OrdinalIgnoreCase)==true))
            throw new InvalidDataException("UM-ADOPT-DUPLICATE-PROCESS");
        string? args=LocalRunningAsaDiscovery.CommandArguments(
            server.CommandLine,server.ExecutablePath);
        if(string.IsNullOrWhiteSpace(args)||args.Length>2048 ||
           args.Any(char.IsControl) ||
           !LocalRunningAsaDiscovery.TryReadLaunchArguments(
               args,out var observedKey,out var launchFlags) ||
           !observedKey.Equals(map.ObservedMapKey,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-ADOPT-LAUNCH-UNVERIFIED");
        var facts=LocalAsaLaunchFacts.Parse(server.CommandLine,
            server.ExecutablePath);
        if(!facts.ArgumentsValidated||facts.StoreFlagPresent||
           facts.ClusterSwitchPresent||facts.TransferOverridePresent||
           facts.ConversionFlagPresent||
           facts.GamePort!=map.GamePort||facts.QueryPort!=map.QueryPort||
           map.GamePort is <1024 or >65535||
           map.QueryPort is <1024 or >65535 ||
           map.GamePort==map.QueryPort ||
           facts.ActiveModsState is "conflict" or "unverified"||
           facts.PassiveModsState is "conflict" or "unverified")
            throw new InvalidDataException("UM-ADOPT-PORTS-STORE-OR-MODS-UNVERIFIED");
        // Classify actual native launch switches, not harmless words inside
        // a quoted SessionName such as "Store Celebration".
        if(AsaStoreArgumentSwitches.ContainsProtectedSwitch(launchFlags))
            throw new InvalidDataException("UM-ADOPT-SENSITIVE-LAUNCH");
        string[] forbidden={"-password","-serveradminpassword","-secret",
            "-token","-auth","-credential","-key","-rconpassword"};
        if(launchFlags.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)
            .Any(token=>forbidden.Any(prefix=>token.StartsWith(prefix,
                StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("UM-ADOPT-SENSITIVE-LAUNCH");
        if(!AsaReadyProbe.IsRegisteredServerBinary(map) ||
           KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map) ||
           KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
            throw new InvalidDataException("UM-ADOPT-STORE-OR-RECOVERY-LOCKED");
        return(map,server,args);
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >1600)
                return Fail(null,"UM-ADOPT-INVALID-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var j=doc.RootElement;
            if(j.ValueKind!=JsonValueKind.Object ||
               !GuidField(j,"id",out var rid) ||
               !j.TryGetProperty("method",out var method) ||
               method.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-ADOPT-INVALID-REQUEST");
            id=rid.ToString("D");
            string? op=method.GetString();
            if(op is not ("servers.runningAdoptPreview" or
                "servers.runningAdoptApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                foreach(var stale in pending.Where(x=>x.Value.ExpiresUtc<
                    DateTimeOffset.UtcNow).Select(x=>x.Key).ToArray())
                    pending.Remove(stale);
                if(op=="servers.runningAdoptPreview")
                {
                    if(!GuidField(j,"mapId",out var mapId))
                        return Fail(id,"UM-ADOPT-MAP-ID");
                    var (map,server,args)=Candidate(mapId,observe());
                    if(pending.Count>12)pending.Clear();
                    var token=Guid.NewGuid();
                    pending[token]=new(token,mapId,server.Pid,
                        server.StartedUtc!.Value,Hash(server.CommandLine!),
                        DateTimeOffset.UtcNow.AddMinutes(3));
                    return Ok(id,new{
                        token,mapId=map.Id,mapName=map.Name,
                        mapKey=map.ObservedMapKey,pid=server.Pid,
                        gamePort=map.GamePort,queryPort=map.QueryPort,
                        noProcessMutation=true,storeWriterAuthorized=false,
                        requiresExternalControllerStopped=true,
                        requiresRconSetup=true,
                        evidence="EXACT_NONSTORE_RUNNING_ADOPTION_PREVIEW_NO_WRITE"
                    });
                }
                if(!GuidField(j,"token",out var approval)||
                   !pending.Remove(approval,out var pinned))
                    return Fail(id,"UM-ADOPT-TOKEN-EXPIRED");
                if(!j.TryGetProperty("confirm",out var yes) ||
                   yes.ValueKind!=JsonValueKind.True ||
                   !j.TryGetProperty("externalControllerStopped",out var other)||
                   other.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-ADOPT-EXPLICIT-APPROVAL-REQUIRED");
                var (chosen,active,launchArgs)=Candidate(pinned.MapId,observe());
                if(active.Pid!=pinned.Pid ||
                   active.StartedUtc!=pinned.StartedUtc ||
                   Hash(active.CommandLine!)!=pinned.CommandHash ||
                   chosen.Id!=pinned.MapId)
                    return Fail(id,"UM-ADOPT-PROCESS-CHANGED");
                var witness=new ProcessIdentity {
                    Pid=active.Pid,StartedUtc=active.StartedUtc!.Value,
                    ExecutablePath=chosen.ExecutablePath,Arguments=launchArgs
                };
                // Core's fresh physical WMI inspection + installation lease
                // are independent of this UI and synthetic observation.
                cluster.AdoptObservedNonStore(chosen.Id,launchArgs,witness);
                return Ok(id,new{
                    mapId=chosen.Id,mapName=chosen.Name,pid=active.Pid,
                    lifecycleAuthorized=true,storeWriterAuthorized=false,
                    rconRequiresSeparateConfiguration=true,
                    processStarted=false,processStopped=false,
                    evidence="NATIVE_WMI_EXACT_PROCESS_ADOPTED_LOCAL_MANAGER_PROFILE"
                });
            }
            finally {serial.Release();}
        }
        catch(InvalidDataException e)
        {
            return Fail(id,e.Message.StartsWith("UM-",StringComparison.Ordinal)?
                e.Message:"UM-ADOPT-VALIDATION-FAILED");
        }
        catch(UnauthorizedAccessException e)
        {
            return Fail(id,e.Message.StartsWith("UM-",StringComparison.Ordinal)?
                e.Message:"UM-ADOPT-UNAUTHORIZED");
        }
        catch(Exception){return Fail(id,"UM-ADOPT-PROCESS-OR-CONFIG-CHANGED");}
    }
    public void Dispose()=>serial.Dispose();
}
