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
/// Registers an already-running, non-Store ASA into the EXISTING Manager
/// ConfigStore only. The observed process is never killed, restarted, adopted
/// as an owned ProcessIdentity or given writer/RCON rights. A fresh WMI
/// snapshot is required on both preview and apply; no web-provided executable
/// path or command line is accepted.
/// </summary>
public sealed class DesktopRunningAsaRegistrationBridge : IDisposable
{
    private sealed record Pinned(Guid Token,int Pid,DateTimeOffset StartedUtc,
        string Executable,string CommandHash,string MapKey,string Root,
        string Name,int GamePort,int QueryPort,DateTimeOffset ExpiresUtc);
    private readonly ClusterService cluster;
    private readonly Func<IReadOnlyList<LocalRunningAsaDiscovery.Sample>> observe;
    private readonly SemaphoreSlim serial=new(1,1);
    private readonly Dictionary<Guid,Pinned> previews=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopRunningAsaRegistrationBridge(ClusterService service,
        Func<IReadOnlyList<LocalRunningAsaDiscovery.Sample>>? observer=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        observe=observer??LocalRunningAsaDiscovery.ReadSamples;
    }

    private static string Hash(string value)=>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool GuidField(JsonElement json,string field,out Guid value)
    {
        value=Guid.Empty;
        return json.TryGetProperty(field,out var element)&&
            element.ValueKind==JsonValueKind.String&&
            Guid.TryParse(element.GetString(),out value)&&value!=Guid.Empty;
    }

    private static int Port(JsonElement json,string name)
    {
        if(!json.TryGetProperty(name,out var field))return 0;
        if(field.ValueKind!=JsonValueKind.Number||
           !field.TryGetInt32(out int port)||
           port!=0 && (port<1024||port>65535))
            throw new InvalidDataException("UM-LIVE-PORT-INVALID");
        return port;
    }

    private static string Name(JsonElement json)
    {
        if(!json.TryGetProperty("name",out var field)||
           field.ValueKind!=JsonValueKind.String)
            throw new InvalidDataException("UM-LIVE-NAME-INVALID");
        return LocalMapNameRules.Normalize(field.GetString());
    }

    private static (string Root,string Executable,string MapKey,
        LocalAsaLaunchFacts.Facts Facts) Inspect(
        LocalRunningAsaDiscovery.Sample server,
        IReadOnlyList<LocalRunningAsaDiscovery.Sample> all,
        IEnumerable<LocalMap> maps)
    {
        if(server.Name!="ArkAscendedServer.exe"||
           server.StartedUtc is not {Year:>=2020}||
           server.ExecutablePath==null||server.CommandLine==null)
            throw new InvalidDataException("UM-LIVE-IDENTITY-UNVERIFIED");
        var state=LocalRunningAsaDiscovery.Classify(all,maps)
            .SingleOrDefault(p=>p.Pid==server.Pid&&p.ProcessKind=="asaServer");
        if(state==null||state.MatchState!="unregistered"||
           state.MapKey=="unknown"||
           all.Count(x=>x.Pid==server.Pid)!=1)
            throw new InvalidDataException("UM-LIVE-ALREADY-REGISTERED-OR-AMBIGUOUS");
        var facts=LocalAsaLaunchFacts.Parse(server.CommandLine,server.ExecutablePath);
        if(!facts.ArgumentsValidated)
            throw new InvalidDataException("UM-LIVE-ARGUMENTS-UNVERIFIED");
        // This path cannot bypass original Kova Store identity/session proofs.
        if(facts.StoreFlagPresent||facts.ClusterSwitchPresent||
           facts.TransferOverridePresent||facts.ConversionFlagPresent)
            throw new InvalidDataException("UM-LIVE-STORE-REQUIRES-HISTORICAL-IMPORT");
        var exe=Path.GetFullPath(server.ExecutablePath);
        var directory=Directory.GetParent(exe);
        if(directory==null||!directory.Name.Equals("Win64",StringComparison.OrdinalIgnoreCase)||
           directory.Parent==null||
           !directory.Parent.Name.Equals("Binaries",StringComparison.OrdinalIgnoreCase)||
           directory.Parent.Parent==null||
           !directory.Parent.Parent.Name.Equals("ShooterGame",StringComparison.OrdinalIgnoreCase)||
           directory.Parent.Parent.Parent==null)
            throw new InvalidDataException("UM-LIVE-EXE-LAYOUT-INVALID");
        string root=DesktopLocalMapRegistryBridge.ValidateFixedLocalRoot(
            directory.Parent.Parent.Parent.FullName);
        string exact=Path.Combine(root,"ShooterGame","Binaries","Win64",
            "ArkAscendedServer.exe");
        if(!exe.Equals(Path.GetFullPath(exact),StringComparison.OrdinalIgnoreCase)||
           !File.Exists(exe))
            throw new InvalidDataException("UM-LIVE-EXE-LAYOUT-INVALID");
        return(root,exe,state.MapKey,facts);
    }

    private static (LocalRunningAsaDiscovery.Sample Server,
        IReadOnlyList<LocalRunningAsaDiscovery.Sample> Snapshot) Select(
        IReadOnlyList<LocalRunningAsaDiscovery.Sample> all,int pid,
        DateTimeOffset started)
    {
        var matches=all.Where(p=>p.Pid==pid).ToArray();
        if(matches.Length!=1||matches[0].StartedUtc!=started)
            throw new InvalidDataException("UM-LIVE-PID-REUSED-OR-EXITED");
        return(matches[0],all);
    }

    private void Unique(string name,string exe,string mapKey,string root,
        int game,int query)
    {
        if(cluster.Config.LocalMaps.Any(m=>m.Name.Equals(name,
               StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UM-LIVE-NAME-ALREADY-REGISTERED");
        foreach(var map in cluster.Config.LocalMaps)
        {
            if(!string.IsNullOrWhiteSpace(map.InstallationPath)&&
               Path.GetFullPath(map.InstallationPath).TrimEnd(Path.DirectorySeparatorChar)
                   .Equals(root,StringComparison.OrdinalIgnoreCase) &&
               (map.HistoricalStoreProtected ||
                !string.IsNullOrWhiteSpace(map.FreshStoreClusterId)))
                throw new InvalidDataException("UM-LIVE-SHARED-STORE-INSTALLATION");
            if(!string.IsNullOrWhiteSpace(map.ExecutablePath) &&
               Path.GetFullPath(map.ExecutablePath).Equals(exe,
                   StringComparison.OrdinalIgnoreCase) &&
               (map.ObservedMapKey.Equals(mapKey,StringComparison.OrdinalIgnoreCase) ||
                map.HistoricalMapKey.Equals(mapKey,StringComparison.OrdinalIgnoreCase) ||
                map.Arguments.TrimStart().StartsWith(mapKey+"?listen",
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("UM-LIVE-DUPLICATE-MAP-IDENTITY");
            if(game>0&&(map.GamePort==game||map.QueryPort==game)||
               query>0&&(map.GamePort==query||map.QueryPort==query))
                throw new InvalidDataException("UM-LIVE-PORT-CONFLICT");
        }
    }

    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Denied(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2200)return Denied(null,"UM-LIVE-REQUEST-INVALID");
            using var doc=JsonDocument.Parse(raw);
            var j=doc.RootElement;
            if(j.ValueKind!=JsonValueKind.Object||
               !GuidField(j,"id",out var rid)||
               !j.TryGetProperty("method",out var method)||
               method.ValueKind!=JsonValueKind.String)
                return Denied(null,"UM-LIVE-REQUEST-INVALID");
            id=rid.ToString("D");
            if(method.GetString() is not ("servers.runningPreview" or
                "servers.runningRegister"))
                return Denied(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                foreach(var expired in previews.Where(x=>x.Value.ExpiresUtc<
                    DateTimeOffset.UtcNow).Select(x=>x.Key).ToArray())
                    previews.Remove(expired);
                if(method.GetString()=="servers.runningPreview")
                {
                    if(!j.TryGetProperty("pid",out var pidField)||
                       !pidField.TryGetInt32(out int pid)||pid<=0||
                       !j.TryGetProperty("startedUtc",out var startField)||
                       startField.ValueKind!=JsonValueKind.String||
                       !DateTimeOffset.TryParse(startField.GetString(),
                           CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,
                           out var started))
                        return Denied(id,"UM-LIVE-IDENTITY-INVALID");
                    string name=Name(j);
                    int manualGame=Port(j,"gamePort"),manualQuery=Port(j,"queryPort");
                    var all=observe();
                    var (selected,_)=Select(all,pid,started);
                    var (root,exe,key,facts)=Inspect(selected,all,cluster.Config.LocalMaps);
                    if(manualGame>0&&facts.GamePort is int observedGame &&
                       observedGame!=manualGame ||
                       manualQuery>0&&facts.QueryPort is int observedQuery &&
                       observedQuery!=manualQuery)
                        return Denied(id,"UM-LIVE-PORT-MISMATCH");
                    int game=facts.GamePort??manualGame;
                    int query=facts.QueryPort??manualQuery;
                    if(game>0&&game==query)
                        return Denied(id,"UM-LIVE-PORT-CONFLICT");
                    Unique(name,exe,key,root,game,query);
                    if(previews.Count>=12)previews.Clear();
                    var token=Guid.NewGuid();
                    previews[token]=new(token,pid,started,exe,
                        Hash(selected.CommandLine!),key,root,name,game,query,
                        DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id,new{
                        token,pid,startedUtc=started,mapKey=key,mapName=name,
                        gamePort=game,queryPort=query,
                        folderName=Path.GetFileName(root),
                        manuallyEnteredGamePort=facts.GamePort==null&&game!=0,
                        manuallyEnteredQueryPort=facts.QueryPort==null&&query!=0,
                        historicalStoreAllowed=false,readOnly=true,
                        lifecycleAuthorized=false,storeWriterAuthorized=false,
                        evidence="EXACT_LIVE_ASA_PROCESS_IMPORT_PREVIEW_NO_WRITES"
                    });
                }
                if(!GuidField(j,"token",out var approval)||
                   !previews.Remove(approval,out var pinned))
                    return Denied(id,"UM-LIVE-PREVIEW-EXPIRED");
                if(!j.TryGetProperty("confirm",out var confirmed)||
                   confirmed.ValueKind!=JsonValueKind.True)
                    return Denied(id,"UM-LIVE-APPROVAL-REQUIRED");
                var current=observe();
                var (active,_)=Select(current,pinned.Pid,pinned.StartedUtc);
                var (root2,exe2,key2,_facts)=Inspect(active,current,cluster.Config.LocalMaps);
                if(root2!=pinned.Root||exe2!=pinned.Executable||
                   key2!=pinned.MapKey||
                   Hash(active.CommandLine!)!=pinned.CommandHash)
                    return Denied(id,"UM-LIVE-PROCESS-CHANGED");
                Unique(pinned.Name,exe2,key2,root2,pinned.GamePort,pinned.QueryPort);
                var map=new LocalMap{
                    Id=Guid.NewGuid(),Name=pinned.Name,
                    ExecutablePath=exe2,InstallationPath=root2,
                    ObservedMapKey=key2,ObservedReadOnlyImported=true,
                    GamePort=pinned.GamePort,QueryPort=pinned.QueryPort,
                    RconPort=0,Arguments="",ApiLoaderArguments="",
                    AutoStart=false,StartWithAsaApi=false,MonitorRcon=false
                };
                cluster.AddMap(map); // Existing atomic ConfigStore CAS, no ASA files.
                return Ok(id,new{
                    mapId=map.Id,mapName=map.Name,mapKey=key2,pid=pinned.Pid,
                    readOnly=true,lifecycleAuthorized=false,
                    storeWriterAuthorized=false,reattachOnRefresh=true,
                    needsExplicitLifecycleEnrollment=true,
                    evidence="EXISTING_MANAGER_CONFIG_PROFILE_ONLY_LIVE_ASA_NOT_ADOPTED"
                });
            }
            finally {serial.Release();}
        }
        catch(InvalidDataException e)
        {
            return Denied(id,e.Message.StartsWith("UM-",StringComparison.Ordinal)
                ?e.Message:"UM-LIVE-IDENTITY-UNVERIFIED");
        }
        catch(Exception){return Denied(id,"UM-LIVE-VALIDATION-FAILED");}
    }
    public void Dispose()=>serial.Dispose();
}
