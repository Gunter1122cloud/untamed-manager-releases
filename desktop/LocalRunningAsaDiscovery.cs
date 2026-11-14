using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// One-shot Windows-only, read-only ASA/ASA API process discovery. Results are
/// observations, NOT ownership, process adoption, SaveWorld or Store grants.
/// PIDs are returned only to the local renderer; no identity is persisted, and raw executable paths, credentials, launch lines and UNC paths never leave the native host.
/// </summary>
public static class LocalRunningAsaDiscovery
{
    public sealed record Sample(int Pid,int ParentPid,DateTimeOffset? StartedUtc,
        string Name,string? ExecutablePath,string? CommandLine);
    public sealed record Observation(int Pid,int ParentPid,DateTimeOffset? StartedUtc,
        string ProcessKind,string MapKey,string MatchState,string? ProfileMapId,
        string? ProfileMapName,bool LoaderParentObserved,
        bool Controllable=false,bool StoreWriteAuthorized=false,
        LocalAsaLaunchFacts.Facts? LaunchFacts=null,
        bool ObservedReadOnlyImported=false);

    // ASA's native map URL may include an explicitly quoted SessionName,
    // including spaces. Splitting at whitespace first makes valid launched
    // servers impossible to identify or adopt. Only this known harmless
    // URL option is accepted; extra URL switches remain unverified.
    private static readonly Regex LaunchPrefix=new(
        @"\A(?<map>[A-Za-z0-9_]{1,100})\?listen(?:\?SessionName=""[^""\r\n]{1,75}"")?(?=\s|\z)",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    internal static bool TryReadLaunchArguments(string? value,
        out string mapKey,out string flags)
    {
        mapKey="";flags="";
        if(string.IsNullOrWhiteSpace(value)||value.Length>8192||
           value.Any(char.IsControl))return false;
        string source=value.TrimStart();
        var match=LaunchPrefix.Match(source);
        if(!match.Success)return false;
        mapKey=match.Groups["map"].Value;
        flags=source[match.Length..].TrimStart();
        return true;
    }

    private static bool SamePath(string? first,string? second)
    {
        if(string.IsNullOrWhiteSpace(first)||string.IsNullOrWhiteSpace(second))
            return false;
        try
        {
            return Path.IsPathFullyQualified(first)&&
                Path.IsPathFullyQualified(second)&&
                string.Equals(Path.GetFullPath(first),Path.GetFullPath(second),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception ex) when(ex is ArgumentException or NotSupportedException
            or PathTooLongException){return false;}
    }

    // Shared sanitized parser entry point. An unexpected executable identity,
    // control character or excessive command line is not trusted.
    internal static string? CommandArguments(string? command,string? expectedExe)
    {
        if(string.IsNullOrWhiteSpace(command)||command.Length>8192||
           command.Any(char.IsControl)||
           string.IsNullOrWhiteSpace(expectedExe))return null;
        string line=command.Trim(), executable;
        if(line.StartsWith('"'))
        {
            int close=line.IndexOf('"',1);
            if(close<2 || (close+1<line.Length && !char.IsWhiteSpace(line[close+1])))
                return null;
            executable=line[1..close];
            line=line[(close+1)..].TrimStart();
        }
        else
        {
            if(!line.StartsWith(expectedExe,StringComparison.OrdinalIgnoreCase)||
               (line.Length>expectedExe.Length &&
                !char.IsWhiteSpace(line[expectedExe.Length])))return null;
            executable=expectedExe;
            line=line[expectedExe.Length..].TrimStart();
        }
        return SamePath(executable,expectedExe)?line:null;
    }

    private static string? LaunchMap(string? command,string? expectedExe)
    {
        string? line=CommandArguments(command,expectedExe);
        if(string.IsNullOrWhiteSpace(line))return null;
        return TryReadLaunchArguments(line,out var mapKey,out _)?
            mapKey:null;
    }

    private static string? ProfileMap(LocalMap map)
    {
        if(map.ObservedReadOnlyImported &&
           Regex.IsMatch(map.ObservedMapKey??"",
               @"\A[A-Za-z0-9_]{1,100}\z",
               RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
            return map.ObservedMapKey;
        if(!string.IsNullOrWhiteSpace(map.HistoricalMapKey))
            return Regex.IsMatch(map.HistoricalMapKey,
                @"\A[A-Za-z0-9_]{1,100}\z")?map.HistoricalMapKey:null;
        return TryReadLaunchArguments(map.Arguments,out var mapKey,out _)?
            mapKey:null;
    }

    private static bool PossibleLoaderProfile(LocalMap map,string? path)
    {
        // Original Kova may have been launched through ASA API before Obsidian
        // registered the StartWithAsaApi switch. Discovery must not hide that
        // loader, but it also must not turn the observation into authority.
        if(SamePath(map.ApiLoaderPath,path))return true;
        try
        {
            if(string.IsNullOrWhiteSpace(map.ExecutablePath))return false;
            string? directory=Path.GetDirectoryName(map.ExecutablePath);
            return !string.IsNullOrWhiteSpace(directory)&&
                SamePath(Path.Combine(directory,"AsaApiLoader.exe"),path);
        }
        catch(Exception ex) when(ex is ArgumentException or NotSupportedException
            or PathTooLongException){return false;}
    }

    /// <summary>
    /// Pure classification accepts only an immutable local snapshot; suitable
    /// for synthetic regression tests with no actual ASA processes.
    /// A profile match is explicitly a *candidate*, not verified ownership.
    /// </summary>
    public static IReadOnlyList<Observation> Classify(
        IEnumerable<Sample> snapshot,IEnumerable<LocalMap> localMaps)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(localMaps);
        var maps=localMaps.Where(m=>m!=null&&m.Id!=Guid.Empty).ToArray();
        var samples=snapshot.Where(p=>p!=null&&p.Pid>0&&
            (p.Name.Equals("ArkAscendedServer.exe",StringComparison.OrdinalIgnoreCase)||
             p.Name.Equals("AsaApiLoader.exe",StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var result=new List<Observation>();
        foreach(var p in samples)
        {
            bool isLoader=p.Name.Equals("AsaApiLoader.exe",StringComparison.OrdinalIgnoreCase);
            string kind=isLoader?"apiLoader":"asaServer";
            bool validPath=p.ExecutablePath!=null&&
                string.Equals(Path.GetFileName(p.ExecutablePath),p.Name,
                    StringComparison.OrdinalIgnoreCase);
            string? directKey=validPath?LaunchMap(p.CommandLine,p.ExecutablePath):null;
            bool stable=p.StartedUtc!=null&&p.StartedUtc.Value.Year>=2020;
            // ASA API can start an ASA child whose WMI command line omits
            // the map token. Its single verified parent loader can supply a
            // DISPLAY-ONLY hint; it must never prove identity or ownership.
            var parents=!isLoader&&validPath&&stable? samples.Where(loader=>
                loader.Pid==p.ParentPid&&loader.Pid!=p.Pid&&
                loader.StartedUtc!=null&&loader.StartedUtc<=p.StartedUtc&&
                loader.Name.Equals("AsaApiLoader.exe",StringComparison.OrdinalIgnoreCase)&&
                !string.IsNullOrWhiteSpace(loader.ExecutablePath)&&
                string.Equals(Path.GetDirectoryName(loader.ExecutablePath),
                    Path.GetDirectoryName(p.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase)&&
                LaunchMap(loader.CommandLine,loader.ExecutablePath)!=null).Take(2).ToArray():
                Array.Empty<Sample>();
            string? parentKey=parents.Length==1?
                LaunchMap(parents[0].CommandLine,parents[0].ExecutablePath):null;
            bool hasLoaderParent=parentKey!=null&&
                (directKey==null||
                 string.Equals(parentKey,directKey,StringComparison.OrdinalIgnoreCase));
            string? key=directKey??(hasLoaderParent?parentKey:null);
            // A loader-derived key is never enough for a profileCandidate,
            // registration, process reattachment, or any lifecycle authority.
            var matches=directKey==null?Array.Empty<LocalMap>():maps.Where(m=>
                string.Equals(ProfileMap(m),directKey,StringComparison.OrdinalIgnoreCase)&&
                (isLoader?PossibleLoaderProfile(m,p.ExecutablePath):
                    SamePath(m.ExecutablePath,p.ExecutablePath))).ToArray();
            string matchState=!validPath||!stable||directKey==null?"unverified":
                matches.Length>1?"ambiguous":
                matches.Length==1?"profileCandidate":"unregistered";
            var selected=matchState=="profileCandidate"?matches[0]:null;
            result.Add(new Observation(p.Pid,p.ParentPid,p.StartedUtc,kind,
                key??"unknown",matchState,selected?.Id.ToString("D"),
                selected?.Name,hasLoaderParent,false,false,
                LocalAsaLaunchFacts.Parse(p.CommandLine,p.ExecutablePath),
                selected?.ObservedReadOnlyImported??false));
        }
        return result.OrderBy(x=>x.Pid).ToArray();
    }

    /// <summary>Enumerates Windows WMI processes without altering any process.</summary>
    internal static IReadOnlyList<Sample> ReadSamples()
    {
        var samples=new List<Sample>();
        // Narrow WQL filter: never enumerate unrelated processes, usernames
        // or shell commands. A failure in one WMI process row must not hide
        // another ASA/Loader PID.
        try
        {
            using var query=new ManagementObjectSearcher(
                "SELECT ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine "+
                "FROM Win32_Process WHERE Name='ArkAscendedServer.exe' "+
                "OR Name='AsaApiLoader.exe'");
            using var rows=query.Get();
            foreach(ManagementObject item in rows)
            {
                try
                {
                    string name=Convert.ToString(item["Name"])??"";
                    if(!name.Equals("ArkAscendedServer.exe",StringComparison.OrdinalIgnoreCase)&&
                       !name.Equals("AsaApiLoader.exe",StringComparison.OrdinalIgnoreCase))
                        continue;
                    int pid=Convert.ToInt32(item["ProcessId"]);
                    if(pid<=0)continue;
                    DateTimeOffset? started=null;
                    try
                    {
                        using var process=Process.GetProcessById(pid);
                        if(!process.HasExited)
                            started=new DateTimeOffset(process.StartTime.ToUniversalTime());
                    }
                    catch(Exception) { /* identity remains unverified */ }
                    samples.Add(new Sample(pid,Convert.ToInt32(item["ParentProcessId"]),
                        started,name,Convert.ToString(item["ExecutablePath"]),
                        Convert.ToString(item["CommandLine"])));
                }
                catch(Exception)
                {
                    // Next ASA process may be accessible. Do not convert one
                    // unreadable Win32_Process row into "no servers running".
                }
            }
        }
        catch(Exception)
        {
            // A denied or temporarily unhealthy WMI provider must not erase
            // every visible ASA process: PID-only enumeration below remains
            // read-only, with no process ownership / RCON authorization.
        }
        var known=samples.Select(x=>x.Pid).ToHashSet();
        foreach(var name in new[]{"ArkAscendedServer","AsaApiLoader"})
        {
            Process[] processes;
            try{processes=Process.GetProcessesByName(name);}
            catch(Exception){continue;}
            foreach(var process in processes)
            {
                using(process)
                {
                    try
                    {
                        if(process.HasExited||!known.Add(process.Id))continue;
                        DateTimeOffset? started=null;
                        try{started=new DateTimeOffset(process.StartTime.ToUniversalTime());}
                        catch(Exception){/* inaccessible PID is still visible */ }
                        // No WMI ancestry or command-line proof: display only.
                        // Never let a fallback match become an adopted owner.
                        samples.Add(new Sample(process.Id,0,started,name+".exe",null,null));
                    }
                    catch(Exception){/* process exited while enumerating */ }
                }
            }
        }
        return samples.OrderBy(x=>x.Pid).ToArray();
    }

    public static IReadOnlyList<Observation> Inspect(IEnumerable<LocalMap> maps)=>
        Classify(ReadSamples(),maps);
}
