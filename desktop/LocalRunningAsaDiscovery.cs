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
        bool Controllable=false,bool StoreWriteAuthorized=false);

    private static readonly Regex MapToken=new(
        @"\A(?<map>[A-Za-z0-9_]{1,100})\?listen\z",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

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
        var first=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if(first==null)return null;
        var match=MapToken.Match(first);
        return match.Success?match.Groups["map"].Value:null;
    }

    private static string? ProfileMap(LocalMap map)
    {
        if(!string.IsNullOrWhiteSpace(map.HistoricalMapKey))
            return Regex.IsMatch(map.HistoricalMapKey,
                @"\A[A-Za-z0-9_]{1,100}\z")?map.HistoricalMapKey:null;
        string? first=map.Arguments?.TrimStart().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first==null?null:(MapToken.Match(first) is {Success:true} match
            ?match.Groups["map"].Value:null);
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
        if(samples.Length>64)throw new InvalidDataException("Too many ASA process observations");
        var result=new List<Observation>();
        foreach(var p in samples)
        {
            bool isLoader=p.Name.Equals("AsaApiLoader.exe",StringComparison.OrdinalIgnoreCase);
            string kind=isLoader?"apiLoader":"asaServer";
            bool validPath=p.ExecutablePath!=null&&
                string.Equals(Path.GetFileName(p.ExecutablePath),p.Name,
                    StringComparison.OrdinalIgnoreCase);
            string? key=validPath?LaunchMap(p.CommandLine,p.ExecutablePath):null;
            var matches=key==null?Array.Empty<LocalMap>():maps.Where(m=>
                string.Equals(ProfileMap(m),key,StringComparison.OrdinalIgnoreCase)&&
                (isLoader?PossibleLoaderProfile(m,p.ExecutablePath):
                    SamePath(m.ExecutablePath,p.ExecutablePath))).ToArray();
            bool stable=p.StartedUtc!=null&&p.StartedUtc.Value.Year>=2020;
            string matchState=!validPath||!stable||key==null?"unverified":
                matches.Length>1?"ambiguous":
                matches.Length==1?"profileCandidate":"unregistered";
            var selected=matchState=="profileCandidate"?matches[0]:null;
            bool hasLoaderParent=!isLoader&&stable&&key!=null&&
                samples.Any(loader=>loader.Pid==p.ParentPid&&
                    loader.Pid!=p.Pid&&loader.StartedUtc!=null&&
                    loader.StartedUtc<=p.StartedUtc&&
                    loader.Name.Equals("AsaApiLoader.exe",StringComparison.OrdinalIgnoreCase)&&
                    string.Equals(LaunchMap(loader.CommandLine,loader.ExecutablePath),
                        key,StringComparison.OrdinalIgnoreCase)&&
                    string.Equals(Path.GetDirectoryName(loader.ExecutablePath),
                        Path.GetDirectoryName(p.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase));
            result.Add(new Observation(p.Pid,p.ParentPid,p.StartedUtc,kind,
                key??"unknown",matchState,selected?.Id.ToString("D"),
                selected?.Name,hasLoaderParent));
        }
        return result.OrderBy(x=>x.Pid).ToArray();
    }

    /// <summary>Enumerates Windows WMI processes without altering any process.</summary>
    public static IReadOnlyList<Observation> Inspect(IEnumerable<LocalMap> maps)
    {
        var samples=new List<Sample>();
        using var query=new ManagementObjectSearcher(
            "SELECT ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine FROM Win32_Process");
        using var rows=query.Get();
        foreach(ManagementObject item in rows)
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
            catch(Exception) { /* an unreadable identity stays unverified */ }
            samples.Add(new Sample(pid,Convert.ToInt32(item["ParentProcessId"]),
                started,name,Convert.ToString(item["ExecutablePath"]),
                Convert.ToString(item["CommandLine"])));
            if(samples.Count>64)
                throw new InvalidDataException("Too many ASA process observations");
        }
        return Classify(samples,maps);
    }
}
