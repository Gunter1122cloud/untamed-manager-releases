using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public enum AsaSteamBuildState
{
    Unknown, Checking, Current, UpdateAvailable, Installing, Restarting, Failed
}
public sealed record AsaSteamBuildCheck(string InstallationRoot,string Branch,
    string? InstalledBuild,string? AvailableBuild,AsaSteamBuildState State,
    DateTimeOffset CheckedUtc,string Explanation);

/// <summary>
/// ASA map names are launch LEVELS, not ten independent Steam applications.
/// The chosen level is the first launch argument. Steam app 2430930 supplies
/// the same server files for every level, and each fresh instance has its own root.
/// </summary>
public static class AsaSteamMapCatalog
{
    private static readonly Dictionary<string,string> Official=new(StringComparer.OrdinalIgnoreCase)
    {
        ["The Island"]="TheIsland_WP",["The Center"]="TheCenter_WP",
        ["Scorched Earth"]="ScorchedEarth_WP",["Ragnarok"]="Ragnarok_WP",
        ["Aberration"]="Aberration_WP",["Extinction"]="Extinction_WP",
        ["Valguero"]="Valguero_WP",["Genesis Part 1"]="Genesis_WP",
        ["Genesis: Part 1"]="Genesis_WP",["Astraeos"]="Astraeos_WP",
        ["Lost Colony"]="LostColony_WP"
    };
    public static string LevelFor(string name)
    {
        if(!Official.TryGetValue(name.Trim(),out var level))
            throw new InvalidDataException("Unknown ASA map: choose an official level before automatic installation");
        return level;
    }
    public static string LaunchArguments(string name,string session,int gamePort,int queryPort)
    {
        string level=LevelFor(name);
        if(gamePort is <1 or >65535 || queryPort is <1 or >65535 || gamePort==queryPort)
            throw new InvalidDataException("Invalid Game/Query UDP port");
        if(string.IsNullOrWhiteSpace(session)||session.Length>75||
           session.Any(c=>char.IsControl(c)||c is '"' or '?' or '\\' or '/' or ';'))
            throw new InvalidDataException("Invalid ASA server session name");
        // RCON is OFF until an explicit admin password is configured; it is
        // unsafe to enable remote administration with an empty password.
        return level+"?listen?SessionName=\""+session+"\"?RCONEnabled=False"+
            " -port="+gamePort+" -QueryPort="+queryPort+" -log";
    }
}
public static class AsaSteamBuildCatalog
{
    private static readonly Regex ManifestId=new(
        "(?m)^\\s*\"buildid\"\\s*\"(?<n>[0-9]{1,20})\"\\s*$",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex AppId=new(
        "(?m)^\\s*\"appid\"\\s*\"2430930\"\\s*$",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);

    public static string? InstalledBuild(string root)
    {
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root) ||
           root.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("Explicit local ASA installation required");
        string manifest=Path.Combine(Path.GetFullPath(root),"steamapps","appmanifest_2430930.acf");
        if(!File.Exists(manifest))return null;
        if(new FileInfo(manifest).Length>1024*1024 ||
           (File.GetAttributes(manifest)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Invalid local Steam appmanifest");
        string content=File.ReadAllText(manifest);
        if(!AppId.IsMatch(content))return null;
        return ManifestId.Match(content) is var match && match.Success
            ? match.Groups["n"].Value:null;
    }

    // Parse only buildid within depots -> branches -> requested branch.
    // Never infer a build from change numbers, arbitrary output or game versions.
    public static string? ParseSteamAppInfo(string raw,string branch="public")
    {
        if(string.IsNullOrWhiteSpace(raw)||raw.Length>2_000_000||
           string.IsNullOrWhiteSpace(branch)||branch.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))
            return null;
        var tokens=Regex.Matches(raw,"\"(?:[^\"\\\\]|\\\\.)*\"|[{}]")
            .Select(m=>m.Value).ToArray();
        var path=new List<string>();
        string? pending=null;
        bool containsApp=false;
        for(int i=0;i<tokens.Length;i++)
        {
            string token=tokens[i];
            if(token=="{")
            {
                path.Add(pending??"");
                pending=null;continue;
            }
            if(token=="}")
            {
                if(path.Count>0)path.RemoveAt(path.Count-1);
                pending=null;continue;
            }
            if(!token.StartsWith('"')||token.Length<2)continue;
            string key=token[1..^1];
            if(key=="2430930" && path.Count==0)containsApp=true;
            if(i+1<tokens.Length && tokens[i+1].StartsWith('"'))
            {
                string value=tokens[++i][1..^1];
                if(containsApp && key=="buildid" && path.Count>=3 &&
                   path[^1].Equals(branch,StringComparison.OrdinalIgnoreCase) &&
                   path.Contains("branches",StringComparer.OrdinalIgnoreCase) &&
                   path.Contains("depots",StringComparer.OrdinalIgnoreCase) &&
                   value.Length<=20 && value.All(char.IsAsciiDigit))
                    return value;
                pending=null;
            }
            else pending=key;
        }
        return null;
    }
    public static AsaSteamBuildCheck Compare(string install,string branch,
        string? local,string? available,DateTimeOffset when)
    {
        bool known=!string.IsNullOrWhiteSpace(local)&&!string.IsNullOrWhiteSpace(available)
            &&local.All(char.IsAsciiDigit)&&available.All(char.IsAsciiDigit);
        var state=!known?AsaSteamBuildState.Unknown:
            local==available?AsaSteamBuildState.Current:AsaSteamBuildState.UpdateAvailable;
        return new AsaSteamBuildCheck(install,branch,local,available,state,when,
            state==AsaSteamBuildState.Current?"Naprakész (azonos Steam build ID)":
            state==AsaSteamBuildState.UpdateAvailable?"Steam-frissítés elérhető":
            "A Steam Build ID nem ellenőrizhető hitelesen");
    }
    /// <summary>
    /// SteamCMD app_info_print reads published depot metadata only. It never
    /// runs app_update nor touches the ASA install directory.
    /// </summary>
    public static async Task<AsaSteamBuildCheck> CheckAsync(string exe,string install,
        string branch="public",TimeSpan? timeout=null,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        SteamCmdPreflight.Inspect(exe);
        string? local=InstalledBuild(install);
        if(branch!="public")
            throw new NotSupportedException("Private Steam branches require explicit branch credentials");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout??TimeSpan.FromMinutes(3));
        var psi=new ProcessStartInfo(exe)
        {
            WorkingDirectory=Path.GetDirectoryName(exe)!,
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        foreach(var arg in new[]{"+login","anonymous","+app_info_update","1",
            "+app_info_print","2430930","+quit"})
            psi.ArgumentList.Add(arg);
        using var process=Process.Start(psi)??throw new IOException("SteamCMD could not start");
        var stdout=process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr=process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var output=await stdout;
            _=await stderr;
            if(output.Length>2_000_000)throw new InvalidDataException("Steam appinfo exceeds bounded output");
            string? remote=process.ExitCode==0?ParseSteamAppInfo(output,branch):null;
            return Compare(install,branch,local,remote,DateTimeOffset.UtcNow);
        }
        catch
        {
            if(!process.HasExited)
            {
                try{process.Kill(entireProcessTree:true);}catch{}
            }
            throw;
        }
    }
}
