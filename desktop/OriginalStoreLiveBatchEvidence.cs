using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Read-only, exact-WMI reconstruction of five already RUNNING ORIGINAL Kova
/// Store-v1 processes. No source conversion, copied Store writer, new session,
/// RCON command, process launch or stop. Used only by the explicitly approved
/// historical five-map import; all real Store operations keep their independent
/// original ACTIVE/DONE/peer/port/PID/SaveComplete gates.
/// </summary>
public static class OriginalStoreLiveBatchEvidence
{
    public sealed record Result(LocalMap[] Maps,string ProcessSha256,
        int DirectProcesses,int ApiLoaderPairs);

    private static string Full(string path)=>Path.GetFullPath(path);
    private static bool Same(string? a,string? b)
    {
        if(string.IsNullOrWhiteSpace(a)||string.IsNullOrWhiteSpace(b))
            return false;
        try{return string.Equals(Full(a),Full(b),
            StringComparison.OrdinalIgnoreCase);}
        catch(Exception){return false;}
    }
    private static LocalMap CreateProfile(LegacyStoreV1OriginalMap m,
        string sourceSha)
    {
        string root=DesktopLocalMapRegistryBridge.ValidateFixedLocalRoot(
            m.OriginalInstallationRoot);
        var install=LocalAsaInstallLocator.Probe(root);
        if(!install.HasServerExecutable||
           string.IsNullOrWhiteSpace(install.ServerExecutable))
            throw new InvalidDataException("UM-ORIGINAL-LIVE-ASA-EXE-MISSING");
        return new LocalMap {
            Id=m.MapId,Name=MapName(m.MapKey),
            InstallationPath=root,ExecutablePath=install.ServerExecutable,
            HistoricalStoreProtected=true,HistoricalArchiveSha256=sourceSha,
            HistoricalMapKey=m.MapKey,GamePort=m.GamePort,QueryPort=m.QueryPort,
            HistoricalEnabledMods=m.EnabledMods.ToList(),
            HistoricalPassiveMods=m.PassiveMods.ToList(),
            Arguments="",ApiLoaderArguments="",StartWithAsaApi=false,
            AutoStart=false,MonitorRcon=false,RconPort=0
        };
    }
    private static string MapName(string key)=>key switch {
        "TheIsland_WP"=>"The Island","TheCenter_WP"=>"The Center",
        "ScorchedEarth_WP"=>"Scorched Earth",
        "Aberration_WP"=>"Aberration","Extinction_WP"=>"Extinction",
        "Genesis_WP"=>"Genesis Part 1","Valguero_WP"=>"Valguero",
        "Ragnarok_WP"=>"Ragnarok","Astraeos_WP"=>"Astraeos",
        "LostColony_WP"=>"Lost Colony",_=>LocalMapNameRules.Normalize(key)
    };

    // A pure classifier for synthetic regressions. Production always takes
    // a second physical WMI snapshot and revalidates the ORIGINAL shared
    // Store source under the native Manager's allowlisted method.
    public static Result Classify(LegacyStoreV1OriginalLocalSelection original,
        IReadOnlyList<LocalRunningAsaDiscovery.Sample> snapshot)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(snapshot);
        if(original.Maps==null||original.Maps.Length!=5 ||
           original.FourFilesSha256.Length!=64 ||
           original.LaunchProfileSha256.Length!=64 ||
           original.Maps.Select(x=>x.MapId).Distinct().Count()!=5)
            throw new InvalidDataException("UM-ORIGINAL-LIVE-ORIGINAL-FIVE-REQUIRED");
        var maps=original.Maps.Select(m=>CreateProfile(m,
            original.FourFilesSha256)).ToArray();
        // One original ASA installation per map (unchanged from stopped
        // batch importer). A shared/overlapping destination cannot be safely
        // registered as five separate lifecycle owners.
        for(int i=0;i<maps.Length;i++)
            for(int j=i+1;j<maps.Length;j++)
            {
                string a=Full(maps[i].InstallationPath).TrimEnd(Path.DirectorySeparatorChar);
                string b=Full(maps[j].InstallationPath).TrimEnd(Path.DirectorySeparatorChar);
                if(Same(a,b)||a.StartsWith(b+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)||
                   b.StartsWith(a+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("UM-ORIGINAL-LIVE-OVERLAPPING-ROOTS");
            }

        var relevant=new List<(LocalRunningAsaDiscovery.Sample Sample,
            LocalMap Map,bool Loader)>();
        var seen=new HashSet<int>();
        foreach(var p in snapshot)
        {
            if(p==null||p.Pid<=0||
               p.Name is not ("ArkAscendedServer.exe" or "AsaApiLoader.exe"))
                continue;
            // An inaccessible ASA process path cannot be ruled out as an
            // unknown second Store writer on the same Windows machine.
            if(string.IsNullOrWhiteSpace(p.ExecutablePath))
                throw new InvalidDataException("UM-ORIGINAL-LIVE-WMI-PATH-UNKNOWN");
            bool server=p.Name=="ArkAscendedServer.exe";
            var matchingPaths=maps.Where(m=>Same(p.ExecutablePath,
                server?m.ExecutablePath:Path.Combine(
                    Path.GetDirectoryName(m.ExecutablePath)!,
                    "AsaApiLoader.exe"))).ToArray();
            if(matchingPaths.Length==0)continue; // unrelated installation
            if(!seen.Add(p.Pid)||p.StartedUtc is not {Year:>=2020} ||
               string.IsNullOrWhiteSpace(p.CommandLine))
                throw new InvalidDataException("UM-ORIGINAL-LIVE-WMI-IDENTITY-UNKNOWN");
            var matching=matchingPaths.Where(m=>
                LegacyStoreV1ProcessObserver.MatchesOriginalActiveCommand(
                    p.CommandLine,p.ExecutablePath,m)).ToArray();
            if(matching.Length!=1)
                throw new InvalidDataException("UM-ORIGINAL-LIVE-UNRECOGNIZED-STORE-PROCESS");
            relevant.Add((p,matching[0],!server));
        }

        int apiPairs=0,direct=0;
        foreach(var map in maps)
        {
            var candidates=relevant.Where(x=>x.Map.Id==map.Id).ToArray();
            var servers=candidates.Where(x=>!x.Loader).ToArray();
            var loaders=candidates.Where(x=>x.Loader).ToArray();
            if(servers.Length!=1||loaders.Length>1)
                throw new InvalidDataException("UM-ORIGINAL-LIVE-MISSING-OR-DUPLICATE-ASA");
            var child=servers[0].Sample;
            var childIdentity=new ProcessIdentity {
                Pid=child.Pid,StartedUtc=child.StartedUtc!.Value,
                ExecutablePath=map.ExecutablePath,
                Arguments="HISTORICAL_STORE_V1_READ_ONLY_OBSERVATION"
            };
            if(loaders.Length==1)
            {
                var parent=loaders[0].Sample;
                if(parent.Pid==child.Pid||parent.Pid!=child.ParentPid||
                   parent.StartedUtc>child.StartedUtc)
                    throw new InvalidDataException("UM-ORIGINAL-LIVE-API-PAIR-UNVERIFIED");
                map.StartWithAsaApi=true;
                map.ApiLoaderPath=Path.Combine(
                    Path.GetDirectoryName(map.ExecutablePath)!,
                    "AsaApiLoader.exe");
                childIdentity.Arguments="HISTORICAL_STORE_V1_API_CHILD_VERIFIED";
                map.LastLoader=new ProcessIdentity{
                    Pid=parent.Pid,StartedUtc=parent.StartedUtc!.Value,
                    ExecutablePath=map.ApiLoaderPath,
                    Arguments="HISTORICAL_STORE_V1_API_LOADER_VERIFIED"
                };
                apiPairs++;
            }
            else direct++;
            map.LastProcess=childIdentity;
        }

        // The digest pins BOTH parents and children, full private command
        // lines, PID creation times and original source hashes. Only its
        // SHA-256 reaches the token; raw Kova paths and arguments remain native.
        var signature=new StringBuilder(original.FourFilesSha256).Append('|')
            .Append(original.LaunchProfileSha256);
        foreach(var m in maps.OrderBy(x=>x.Id))
        {
            signature.Append('|').Append(m.Id).Append('|')
                .Append(m.InstallationPath).Append('|')
                .Append(m.StartWithAsaApi);
        }
        foreach(var entry in relevant.OrderBy(x=>x.Sample.Pid))
        {
            var p=entry.Sample;
            signature.Append('|').Append(p.Pid).Append('|')
                .Append(p.ParentPid).Append('|').Append(p.StartedUtc!.Value.ToUnixTimeMilliseconds())
                .Append('|').Append(p.ExecutablePath).Append('|')
                .Append(p.CommandLine).Append('|').Append(entry.Map.Id);
        }
        return new(maps,Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()))),
            direct,apiPairs);
    }
}
