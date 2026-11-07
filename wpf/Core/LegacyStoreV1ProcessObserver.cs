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
            var canonical=LegacyStoreV1ActiveLaunch.Build(
                new LegacyStoreV1ActiveLaunch.Input(
                    map.HistoricalMapKey,map.GamePort,map.QueryPort,
                    map.HistoricalEnabledMods,map.HistoricalPassiveMods,
                    VaultProtocol.ClusterId,sharedPath,"active",string.Empty));
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

    public static ProcessCheck Observe(LocalMap map)
    {
        if(map==null||!map.HistoricalStoreProtected||map.Id==Guid.Empty)
            throw new UnauthorizedAccessException(
                "Only imported historical Store-v1 maps may be observed");
        try
        {
            if(!CanonicalLocalExecutable(map.ExecutablePath))
                return new(false,
                    "Eredeti Store-v1 futtatható fájl nem igazolható",null);
            string exe=Path.GetFullPath(map.ExecutablePath);
            var matches=new List<ProcessIdentity>();
            bool uncertain=false;
            using var searcher=new ManagementObjectSearcher(
                "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");
            using var processes=searcher.Get();
            foreach(ManagementObject entry in processes)
            {
                try
                {
                    if(!Path.GetFileName(exe).Equals(
                        Convert.ToString(entry["Name"]),StringComparison.OrdinalIgnoreCase))
                        continue;
                    string path=Convert.ToString(entry["ExecutablePath"])??"";
                    if(string.IsNullOrWhiteSpace(path))
                    {uncertain=true;continue;}
                    if(!Path.GetFullPath(path).Equals(exe,
                        StringComparison.OrdinalIgnoreCase))continue;
                    string command=Convert.ToString(entry["CommandLine"])??"";
                    if(!MatchesOriginalActiveCommand(command,exe,map))
                    {uncertain=true;continue;}
                    int id=Convert.ToInt32(entry["ProcessId"]);
                    using var process=Process.GetProcessById(id);
                    if(process.HasExited)continue;
                    var started=new DateTimeOffset(process.StartTime.ToUniversalTime());
                    if(map.LastProcess!=null&&map.LastProcess.Pid==id &&
                       Math.Abs((started-map.LastProcess.StartedUtc).TotalSeconds)>2)
                    {uncertain=true;continue;}
                    matches.Add(new ProcessIdentity{
                        Pid=id,StartedUtc=started,ExecutablePath=exe,
                        // Never copy secrets or shared UNC path into an
                        // in-memory/persisted process identity.
                        Arguments="HISTORICAL_STORE_V1_READ_ONLY_OBSERVATION"
                    });
                }
                catch(Exception)
                {uncertain=true;}
            }
            if(uncertain||matches.Count!=1)
                return new(false,matches.Count>1?
                    "Több eredeti ASA-folyamat: manuális ellenőrzés szükséges":
                    "Eredeti Store-v1 folyamat nem azonosítható biztosan",null);
            return new(true,
                "Fut · eredeti Store-v1 ASA processz ellenőrizve (csak olvasás)",
                matches[0]);
        }
        catch(Exception)
        {
            // Inaccessible WMI/NTFS metadata never means the process stopped.
            return new(false,
                "Eredeti Store-v1 folyamatállapot nem ellenőrizhető",null);
        }
    }
}
