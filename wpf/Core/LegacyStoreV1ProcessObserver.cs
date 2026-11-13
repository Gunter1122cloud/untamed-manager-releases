using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace UntamedManager.Core;

/// <summary>
/// Physical, READ-ONLY observation of an already-running original Store-v1
/// ASA process. Imported profiles intentionally have no launch arguments;
/// the generic inspector therefore cannot match them. This observer compares
/// original source-grammar switches without ever starting, stopping, killing,
/// reattaching as an owner, persisting a PID, altering a character, or touching
/// shared Store/SMB metadata. A positive process observation grants NO rights
/// to perform any ASA lifecycle or Store-v1 transaction.
/// </summary>
public static class LegacyStoreV1ProcessObserver
{
    private static string? ExtractArguments(string? commandLine,string expectedExe)
    {
        if(string.IsNullOrWhiteSpace(commandLine))return null;
        string text=commandLine.Trim();
        string exe;
        if(text.StartsWith('"'))
        {
            int close=text.IndexOf('"',1);
            if(close<=1)return null;
            exe=text.Substring(1,close-1);
            if(close+1<text.Length && !char.IsWhiteSpace(text[close+1]))return null;
            text=text[(close+1)..].Trim();
        }
        else
        {
            // An unquoted Windows executable path is safe to parse only if
            // its entire configured path occurs at a word boundary.
            if(!text.StartsWith(expectedExe,StringComparison.OrdinalIgnoreCase) ||
               (text.Length>expectedExe.Length &&
                !char.IsWhiteSpace(text[expectedExe.Length])))return null;
            exe=expectedExe;
            text=text[expectedExe.Length..].Trim();
        }
        if(!Path.GetFullPath(exe).Equals(Path.GetFullPath(expectedExe),
            StringComparison.OrdinalIgnoreCase))return null;
        return text;
    }

    public static bool MatchesOriginalActiveCommand(string? commandLine,
        string expectedExe,LocalMap map)
    {
        if(map==null||!map.HistoricalStoreProtected||
           string.IsNullOrWhiteSpace(map.HistoricalMapKey) ||
           map.GamePort is <1024 or >65535 ||
           map.QueryPort is <1024 or >65535 ||
           map.HistoricalEnabledMods==null||map.HistoricalPassiveMods==null ||
           string.IsNullOrWhiteSpace(expectedExe)||
           !Path.IsPathFullyQualified(expectedExe))return false;
        try
        {
            string? remaining=ExtractArguments(commandLine,expectedExe);
            if(string.IsNullOrWhiteSpace(remaining) ||
               remaining.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)return false;
            string[] supplied=remaining.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            // The original active source has a canonical, ordered argument
            // grammar. Other flags and any conversion requests are UNKNOWN
            // and must not be presented as a verified original process.
            string[] paths=supplied.Where(x=>x.StartsWith(
                "-ClusterDirOverride=",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(paths.Length!=1)return false;
            string sharedPath=paths[0]["-ClusterDirOverride=".Length..];
            if(!string.IsNullOrWhiteSpace(map.HistoricalRuntimeTransferDirectory)&&
               !sharedPath.Equals(map.HistoricalRuntimeTransferDirectory,
                   StringComparison.OrdinalIgnoreCase))
                return false;
            if(string.IsNullOrWhiteSpace(map.HistoricalRuntimeClusterId)!=
               string.IsNullOrWhiteSpace(map.HistoricalRuntimeTransferDirectory))
                return false;
            // After an approved Obsidian runtime edit, the saved map profile
            // (not the old Legacy peer marker) determines the exact expected
            // cluster and mod order for local PID reattachment.
            string runtimeCluster=string.IsNullOrWhiteSpace(
                map.HistoricalRuntimeClusterId)?
                VaultProtocol.ClusterId:map.HistoricalRuntimeClusterId;
            var mods=ModStateController.Read(map);
            var canonical=LegacyStoreV1ActiveLaunch.Build(
                new LegacyStoreV1ActiveLaunch.Input(
                    map.HistoricalMapKey,map.GamePort,map.QueryPort,
                    mods.EnabledOrder,mods.Passive,
                    runtimeCluster,sharedPath,"active",string.Empty,
                    SourcePinnedClusterOnly:string.IsNullOrWhiteSpace(
                        map.HistoricalRuntimeClusterId)));
            return supplied.Length==canonical.Arguments.Length &&
                   supplied.SequenceEqual(canonical.Arguments,
                       StringComparer.OrdinalIgnoreCase) &&
                   canonical.Arguments.Count(x=>x.Equals("-usestore",
                       StringComparison.OrdinalIgnoreCase))==1;
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidDataException
            or NotSupportedException or PathTooLongException)
        {return false;}
    }

    /// <summary>
    /// ASA API may launch an ASA CHILD with no map/cluster switches at all:
    /// the exact ORIGINAL map, Store and port grammar remains on its parent.
    /// The child can be attributed to that original map ONLY when Windows
    /// proves an exact unique Loader PID/creation-time/parent relationship,
    /// both executables live in that exact original installation, and the
    /// parent's complete source command matches. This never infers Store
    /// writer ownership, RCON access, or process lifecycle permission.
    /// Nonempty child source switches (including another map, port, cluster
    /// or conversion) are deliberately rejected.
    /// </summary>
    public static bool MatchesOriginalApiLoaderChild(
        string? childCommand,string expectedServer,int childPid,int childParentPid,
        DateTimeOffset childStarted,string? loaderCommand,string expectedLoader,
        int loaderPid,DateTimeOffset loaderStarted,LocalMap map)
    {
        if(map==null||!map.HistoricalStoreProtected||
           childPid<=0||loaderPid<=0||childPid==loaderPid||
           childParentPid!=loaderPid||
           childStarted.Year<2020||loaderStarted.Year<2020||
           loaderStarted>childStarted||
           string.IsNullOrWhiteSpace(expectedServer)||
           string.IsNullOrWhiteSpace(expectedLoader))
            return false;
        try
        {
            string expected=Path.GetFullPath(map.ExecutablePath);
            string loader=Path.Combine(Path.GetDirectoryName(expected)!,
                "AsaApiLoader.exe");
            if(!expected.Equals(Path.GetFullPath(expectedServer),
                    StringComparison.OrdinalIgnoreCase)||
               !loader.Equals(Path.GetFullPath(expectedLoader),
                    StringComparison.OrdinalIgnoreCase)||
               !MatchesOriginalActiveCommand(loaderCommand,loader,map))
                return false;
            string? childArgs=ExtractArguments(childCommand,expected);
            if(childArgs==null)
            {
                // Real ASA API Loader may copy ITS own complete command line
                // into Win32_Process.CommandLine for ArkAscendedServer.exe.
                // Process.Name/ExecutablePath must still be the genuine ASA
                // child; this fallback never trusts the argv[0] executable.
                // Only the EXACT SAME, complete original Loader launch is
                // accepted, after independently validating the real Loader
                // PID/creation-time/parent relationship above.
                string? aliasArgs=ExtractArguments(childCommand,loader);
                string? parentArgs=ExtractArguments(loaderCommand,loader);
                return aliasArgs!=null&&parentArgs!=null&&
                    aliasArgs.Equals(parentArgs,StringComparison.OrdinalIgnoreCase)&&
                    MatchesOriginalActiveCommand(childCommand,loader,map);
            }
            if(childArgs.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)return false;
            // For a true ASA argv[0], only harmless child-local flags are
            // accepted; source-affecting overrides must never be added.
            string[] tokens=childArgs.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            return tokens.All(token=>
                token.Equals("-log",StringComparison.OrdinalIgnoreCase)||
                token.Equals("-NoBattlEye",StringComparison.OrdinalIgnoreCase)||
                token.Equals("-NoHangDetection",StringComparison.OrdinalIgnoreCase));
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidDataException or
            NotSupportedException or PathTooLongException)
        {return false;}
    }

    private static bool MinimalOriginalApiChildArguments(
        string? commandLine,string expectedServer)
    {
        try
        {
            var args=ExtractArguments(commandLine,expectedServer);
            if(args==null||args.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)return false;
            return args.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)
                .All(token=>
                    token.Equals("-log",StringComparison.OrdinalIgnoreCase)||
                    token.Equals("-NoBattlEye",StringComparison.OrdinalIgnoreCase)||
                    token.Equals("-NoHangDetection",StringComparison.OrdinalIgnoreCase));
        }
        catch(Exception ex) when(ex is ArgumentException or NotSupportedException
            or PathTooLongException){return false;}
    }

    /// <summary>
    /// Multiple owned ASA maps may legitimately share ONE executable path.
    /// A process with an unequivocally DIFFERENT original MAP?listen token
    /// is not a candidate for this map and must not poison its evidence.
    /// An unknown/malformed command is still uncertain and fails closed.
    /// </summary>
    public static bool DefinitelyDifferentMap(string? commandLine,
        string expectedExe,string requestedMap)
    {
        if(string.IsNullOrWhiteSpace(requestedMap) ||
           requestedMap.Length>100 ||
           !requestedMap.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'))
            return false;
        try
        {
            string? args=ExtractArguments(commandLine,expectedExe);
            if(string.IsNullOrWhiteSpace(args))return false;
            string first=args.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??"";
            const string suffix="?listen";
            if(!first.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))return false;
            string map=first[..^suffix.Length];
            return map.Length is >=1 and <=100 &&
                   map.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_') &&
                   !map.Equals(requestedMap,StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception ex) when(ex is ArgumentException or NotSupportedException
            or PathTooLongException)
        {return false;}
    }

    private static bool CanonicalLocalExecutable(string exe)
    {
        if(string.IsNullOrWhiteSpace(exe)||!Path.IsPathFullyQualified(exe)||
           exe.StartsWith(@"\\",StringComparison.Ordinal)||
           !Path.GetFileName(exe).Equals("ArkAscendedServer.exe",
               StringComparison.OrdinalIgnoreCase)||
           !File.Exists(exe))return false;
        var info=new FileInfo(exe);
        if((info.Attributes&FileAttributes.ReparsePoint)!=0)return false;
        for(var d=info.Directory;d!=null;d=d.Parent)
            if(d.Exists && (d.Attributes&FileAttributes.ReparsePoint)!=0)
                return false;
        return true;
    }

    /// <summary>
    /// Exact executable-family proof used ONLY by the explicitly approved
    /// historical ACTIVE start. The ordinary dashboard observer intentionally
    /// remains untrusted for lifecycle authority. A running or unreadable
    /// ASA/loader candidate (including one from another map sharing a binary)
    /// prevents the fresh process spawn. No process is killed or modified.
    /// </summary>
    internal static bool PotentialOriginalWriter(string expectedServer,
        string processName,string? executable)
    {
        bool server=processName.Equals("ArkAscendedServer.exe",
            StringComparison.OrdinalIgnoreCase);
        bool loader=processName.Equals("AsaApiLoader.exe",
            StringComparison.OrdinalIgnoreCase);
        if(!server&&!loader)return false;
        if(string.IsNullOrWhiteSpace(executable))return true;
        try
        {
            string actual=Path.GetFullPath(executable);
            string expected=Path.GetFullPath(expectedServer);
            if(server)return actual.Equals(expected,StringComparison.OrdinalIgnoreCase);
            return actual.Equals(Path.Combine(Path.GetDirectoryName(expected)!,
                "AsaApiLoader.exe"),StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception){return true;}
    }

    /// <summary>
    /// One ORIGINAL ASA EXE may legitimately run several owned maps with
    /// different ?listen/port/mod switches. Ignore another process ONLY
    /// when it matches the EXACT original source grammar of a distinct,
    /// locally registered historical map. An unknown, third-party or
    /// malformed process using the same executable remains a hard blocker.
    /// This cannot be used as cross-host writer/session authority.
    /// </summary>
    internal static bool ProvenDistinctOwnedMap(string? commandLine,
        string expectedExe,LocalMap requested,IEnumerable<LocalMap> localMaps)
    {
        if(requested==null||localMaps==null||
           string.IsNullOrWhiteSpace(commandLine)||
           string.IsNullOrWhiteSpace(expectedExe))return false;
        try
        {
            string exe=Path.GetFullPath(expectedExe);
            return localMaps.Any(other=>other!=null&&other.Id!=requested.Id&&
                other.Id!=Guid.Empty&&other.HistoricalStoreProtected&&
                other.GamePort!=requested.GamePort&&
                other.QueryPort!=requested.QueryPort&&
                other.RconPort!=requested.RconPort&&
                Path.GetFullPath(other.ExecutablePath).Equals(
                    exe,StringComparison.OrdinalIgnoreCase)&&
                MatchesOriginalActiveCommand(commandLine,exe,other));
        }
        catch(Exception){return false;}
    }

    /// <summary>
    /// The legacy agent could launch an original ACTIVE Store map through
    /// AsaApiLoader.exe instead of ArkAscendedServer.exe. Its loader may share
    /// the same Win64 directory with a DIFFERENT original map. Ignore that
    /// running loader ONLY when the native registered other map explicitly
    /// selected ASA API, and its full WMI command line matches the ORIGINAL
    /// -usestore/map/port/mod/session grammar for that other map.
    /// A loader for the requested map, unknown process, missing opt-in,
    /// changed arguments or inaccessible WMI is never ignored.
    /// </summary>
    internal static bool ProvenDistinctOwnedApiLoader(string? commandLine,
        string expectedServer,LocalMap requested,IEnumerable<LocalMap> localMaps)
    {
        if(string.IsNullOrWhiteSpace(commandLine)||requested==null||
           localMaps==null||string.IsNullOrWhiteSpace(expectedServer))
            return false;
        try
        {
            string server=Path.GetFullPath(expectedServer);
            string loader=Path.Combine(Path.GetDirectoryName(server)!,"AsaApiLoader.exe");
            return localMaps.Any(other=>other!=null&&other.Id!=Guid.Empty&&
                other.Id!=requested.Id&&other.HistoricalStoreProtected&&
                other.StartWithAsaApi&&
                string.IsNullOrWhiteSpace(other.Arguments)&&
                string.IsNullOrWhiteSpace(other.ApiLoaderArguments)&&
                other.GamePort!=requested.GamePort&&
                other.QueryPort!=requested.QueryPort&&
                other.RconPort!=requested.RconPort&&
                Path.GetFullPath(other.ExecutablePath).Equals(
                    server,StringComparison.OrdinalIgnoreCase)&&
                !string.IsNullOrWhiteSpace(other.ApiLoaderPath)&&
                Path.GetFullPath(other.ApiLoaderPath).Equals(
                    loader,StringComparison.OrdinalIgnoreCase)&&
                MatchesOriginalActiveCommand(commandLine,loader,other));
        }
        catch(Exception){return false;}
    }

    /// <summary>
    /// An orphan ASA child is authorized for read-only reattach only when a
    /// previous exact parent/child observation was pinned to the local profile.
    /// Bare source arguments or a reused PID can never prove Loader ancestry.
    /// Pure function also exercised by isolated synthetic Windows tests.
    /// </summary>
    internal static bool PreviouslyVerifiedOriginalApiChild(
        ObservedProcess child,string expectedServer,string expectedLoader,
        ProcessIdentity? priorChild,ProcessIdentity? priorLoader)
    {
        if(priorChild==null||priorLoader==null||child.Pid<=0||
           priorLoader.Pid<=0||
           priorChild.Arguments!="HISTORICAL_STORE_V1_API_CHILD_VERIFIED"||
           priorLoader.Arguments!="HISTORICAL_STORE_V1_API_LOADER_VERIFIED"||
           priorChild.Pid!=child.Pid||priorChild.Pid==priorLoader.Pid||
           priorChild.StartedUtc!=child.StartedUtc||
           priorLoader.StartedUtc>child.StartedUtc)return false;
        try
        {
            return Path.GetFullPath(child.ExecutablePath).Equals(
                Path.GetFullPath(expectedServer),StringComparison.OrdinalIgnoreCase)&&
                Path.GetFullPath(priorChild.ExecutablePath).Equals(
                    Path.GetFullPath(expectedServer),StringComparison.OrdinalIgnoreCase)&&
                Path.GetFullPath(priorLoader.ExecutablePath).Equals(
                    Path.GetFullPath(expectedLoader),StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception){return false;}
    }

    public static ProcessCheck ObserveStoppedForNativeStart(LocalMap map)=>
        ObserveStoppedForNativeStart(map,Array.Empty<LocalMap>());

    public static ProcessCheck ObserveStoppedForNativeStart(LocalMap map,
        IEnumerable<LocalMap> locallyRegisteredMaps)
    {
        if(map==null||!map.HistoricalStoreProtected||map.Id==Guid.Empty||
           locallyRegisteredMaps==null)
            throw new UnauthorizedAccessException("Only an imported historical map can be launched");
        try
        {
            if(!CanonicalLocalExecutable(map.ExecutablePath))
                return new(false,"UM-HISTORICAL-PROCESS-BINARY-UNKNOWN",null,false);
            using var query=new ManagementObjectSearcher(
                "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");
            using var found=query.Get();
            foreach(ManagementObject item in found)
            {
                string name=Convert.ToString(item["Name"])??"";
                string? path=Convert.ToString(item["ExecutablePath"]);
                if(!PotentialOriginalWriter(map.ExecutablePath,name,path))
                    continue;
                // Inaccessible command lines never prove a different map.
                // The EXE, legacy ?listen token, ports, mod ordering,
                // session flags and locally REGISTERED map must all match.
                string? command=Convert.ToString(item["CommandLine"]);
                if(name.Equals("ArkAscendedServer.exe",StringComparison.OrdinalIgnoreCase)&&
                   ProvenDistinctOwnedMap(command,map.ExecutablePath,map,locallyRegisteredMaps))
                    continue;
                if(name.Equals("AsaApiLoader.exe",StringComparison.OrdinalIgnoreCase)&&
                   ProvenDistinctOwnedApiLoader(command,map.ExecutablePath,map,locallyRegisteredMaps))
                    continue;
                return new(false,"UM-HISTORICAL-PROCESS-FAMILY-NOT-STOPPED",null,false);
            }
            // OS process-family absence for THIS map; a different,
            // positively identified original ASA map may still be running.
            return new(false,"UM-HISTORICAL-MAP-STOPPED_OTHER_VERIFIED_MAPS_IGNORED",
                null,true);
        }
        catch(Exception)
        {
            return new(false,"UM-HISTORICAL-PROCESS-ENUMERATION-UNAVAILABLE",null,false);
        }
    }

    public static ProcessCheck Observe(LocalMap map)=>
        Observe(map,Array.Empty<LocalMap>());

    // The dashboard may ignore a different running map ONLY when that
    // process is proven to belong to another locally registered Store owner.
    // The map-token-only heuristic is insufficient for a trust decision.
    public static ProcessCheck Observe(LocalMap map,
        IEnumerable<LocalMap> locallyRegisteredMaps)
    {
        if(map==null||!map.HistoricalStoreProtected||map.Id==Guid.Empty||
           locallyRegisteredMaps==null)
            throw new UnauthorizedAccessException(
                "Only imported historical Store-v1 maps may be observed");
        try
        {
            if(!CanonicalLocalExecutable(map.ExecutablePath))
                return new(false,
                    "Eredeti Store-v1 futtatható fájl nem igazolható",null);
            string exe=Path.GetFullPath(map.ExecutablePath);
            string loader=Path.Combine(Path.GetDirectoryName(exe)!,"AsaApiLoader.exe");
            var serverProcesses=new List<ObservedProcess>();
            var loaderProcesses=new List<ObservedProcess>();
            var unparameterizedApiChildren=new List<(ObservedProcess Process,string Command)>();
            var loaderCommands=new Dictionary<int,string>();
            bool uncertain=false;
            using var searcher=new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");
            using var processes=searcher.Get();
            foreach(ManagementObject entry in processes)
            {
                try
                {
                    string name=Convert.ToString(entry["Name"])??"";
                    string? rawPath=Convert.ToString(entry["ExecutablePath"]);
                    if(!PotentialOriginalWriter(exe,name,rawPath))
                        continue;
                    // A missing path or unreadable source command is NEVER
                    // evidence that an API child belongs to another map.
                    if(string.IsNullOrWhiteSpace(rawPath))
                    {uncertain=true;continue;}
                    string path=Path.GetFullPath(rawPath);
                    bool api=path.Equals(loader,StringComparison.OrdinalIgnoreCase);
                    bool server=path.Equals(exe,StringComparison.OrdinalIgnoreCase);
                    if(!api&&!server)continue;
                    string command=Convert.ToString(entry["CommandLine"])??"";
                    if(server&&ProvenDistinctOwnedMap(
                        command,exe,map,locallyRegisteredMaps))
                        continue;
                    if(api&&ProvenDistinctOwnedApiLoader(
                        command,exe,map,locallyRegisteredMaps))
                        continue;
                    bool exact=MatchesOriginalActiveCommand(
                        command,api?loader:exe,map);
                    // A bare child command is not independently original:
                    // defer its classification until its exact original
                    // ASA API Loader parent has been verified separately.
                    bool childOnly=!exact&&server&&map.StartWithAsaApi&&
                        (MinimalOriginalApiChildArguments(command,exe)||
                         // The WMI child has ArkAscendedServer.exe as its
                         // actual executable, but inherited the Loader's
                         // exact argv[0] and original Legacy launch grammar.
                         MatchesOriginalActiveCommand(command,loader,map));
                    if(!exact&&!childOnly)
                    {uncertain=true;continue;}
                    int pid=Convert.ToInt32(entry["ProcessId"]);
                    int parentPid=Convert.ToInt32(entry["ParentProcessId"]);
                    using var process=Process.GetProcessById(pid);
                    if(process.HasExited)continue;
                    var started=new DateTimeOffset(process.StartTime.ToUniversalTime());
                    var persisted=api?map.LastLoader:map.LastProcess;
                    if(persisted!=null&&persisted.Pid==pid &&
                       Math.Abs((started-persisted.StartedUtc).TotalSeconds)>2)
                    {uncertain=true;continue;}
                    var observed=new ObservedProcess(pid,parentPid,started,path,
                        "HISTORICAL_STORE_V1_SOURCE_VERIFIED");
                    if(api)
                    {
                        loaderProcesses.Add(observed);
                        loaderCommands.Add(pid,command);
                    }
                    else if(childOnly)unparameterizedApiChildren.Add((observed,command));
                    else serverProcesses.Add(observed);
                }
                catch(Exception)
                {uncertain=true;}
            }
            foreach(var candidate in unparameterizedApiChildren)
            {
                bool pairedNow=loaderProcesses.Count==1&&
                    loaderCommands.TryGetValue(loaderProcesses[0].Pid,
                        out var parentCommand)&&
                    MatchesOriginalApiLoaderChild(candidate.Command,exe,
                        candidate.Process.Pid,candidate.Process.ParentPid,
                        candidate.Process.StartedUtc,parentCommand,loader,
                        loaderProcesses[0].Pid,loaderProcesses[0].StartedUtc,map);
                // A Loader is allowed to exit after ASA has spawned, but
                // only the exact previously persisted and verified pair can
                // preserve read-only observation. A new or recycled child
                // with no running Loader must remain unverified.
                bool pairedEarlier=loaderProcesses.Count==0&&
                    PreviouslyVerifiedOriginalApiChild(candidate.Process,
                        exe,loader,map.LastProcess,map.LastLoader);
                if(!pairedNow&&!pairedEarlier)uncertain=true;
                else serverProcesses.Add(candidate.Process);
            }
            if(uncertain||serverProcesses.Count>1||loaderProcesses.Count>1)
                return new(false,
                    "Eredeti Store-v1 folyamatcsalád nem azonosítható egyértelműen",null);
            if(!map.StartWithAsaApi)
            {
                // A loader for this map is never an authorized direct start.
                if(loaderProcesses.Count!=0||serverProcesses.Count!=1)
                    return new(false,
                        "Eredeti Store-v1 közvetlen ASA-folyamat nem igazolt",null);
                var direct=serverProcesses[0];
                return new(true,"Fut · eredeti Store-v1 ASA processz ellenőrizve",
                    new ProcessIdentity{Pid=direct.Pid,StartedUtc=direct.StartedUtc,
                        ExecutablePath=direct.ExecutablePath,
                        Arguments="HISTORICAL_STORE_V1_READ_ONLY_OBSERVATION"});
            }
            if(serverProcesses.Count==1&&loaderProcesses.Count==0)
            {
                var childOnly=serverProcesses[0];
                // The Launcher may exit after spawning ASA. A standalone
                // command-line match is NOT enough: require a PREVIOUSLY
                // persisted and independently verified exact parent/child
                // creation-time pair. Neither PID is inferred or guessed.
                var pinnedChild=map.LastProcess;
                var pinnedParent=map.LastLoader;
                if(PreviouslyVerifiedOriginalApiChild(
                    childOnly,exe,loader,pinnedChild,pinnedParent))
                    return new(true,
                        "Fut · korábban igazolt API gyermekfolyamat; Loader már kilépett",
                        new ProcessIdentity{
                            Pid=childOnly.Pid,StartedUtc=childOnly.StartedUtc,
                            ExecutablePath=exe,
                            Arguments="HISTORICAL_STORE_V1_API_CHILD_VERIFIED"
                        },false,pinnedParent);
            }
            if(serverProcesses.Count!=1||loaderProcesses.Count!=1)
                return new(false,
                    loaderProcesses.Count==1&&serverProcesses.Count==0?
                        "ASA API Loader fut, ASA gyermekfolyamat még nem igazolt":
                        "ASA API Loader és eredeti ASA gyermekfolyamat nem igazolt",
                    null,false,loaderProcesses.Count==1?new ProcessIdentity{
                        Pid=loaderProcesses[0].Pid,
                        StartedUtc=loaderProcesses[0].StartedUtc,
                        ExecutablePath=loaderProcesses[0].ExecutablePath,
                        Arguments="HISTORICAL_STORE_V1_API_LOADER_OBSERVATION"
                    }:null);
            var parent=loaderProcesses[0];
            var child=serverProcesses[0];
            var parentIdentity=new ProcessIdentity{
                Pid=parent.Pid,StartedUtc=parent.StartedUtc,
                ExecutablePath=parent.ExecutablePath,
                Arguments="HISTORICAL_STORE_V1_SOURCE_VERIFIED"};
            var childIdentity=new ProcessIdentity{
                Pid=child.Pid,StartedUtc=child.StartedUtc,
                ExecutablePath=child.ExecutablePath,
                Arguments="HISTORICAL_STORE_V1_SOURCE_VERIFIED"};
            if(!ProcessFamily.IsVerifiedPair(
                new[]{parent,child},parentIdentity,childIdentity))
                return new(false,
                    "ASA API Loader szülő–gyermek PID kapcsolat nem igazolt",null);
            // The child is the only ASA/RCON owner. The Loader PID must never
            // be reported as a running ASA server or used for SaveWorld/Stop.
            childIdentity.Arguments="HISTORICAL_STORE_V1_API_CHILD_VERIFIED";
            parentIdentity.Arguments="HISTORICAL_STORE_V1_API_LOADER_VERIFIED";
            return new(true,
                "Fut · eredeti Store-v1 ASA API Loader és gyermek PID igazolt",
                childIdentity,false,parentIdentity);
        }
        catch(Exception)
        {
            // Inaccessible WMI/NTFS metadata never means the process stopped.
            return new(false,
                "Eredeti Store-v1 folyamatállapot nem ellenőrizhető",null);
        }
    }
}
