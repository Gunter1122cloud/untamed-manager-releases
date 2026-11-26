using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    DateTimeOffset CheckedUtc,string Explanation)
{
    public string? DiagnosticCode {get;init;}
}

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
    public static bool IsOfficialLevel(string level)=>Official.Values.Contains(level,StringComparer.OrdinalIgnoreCase);
    public static string LevelFor(string name)
    {
        if(!Official.TryGetValue(name.Trim(),out var level))
            throw new InvalidDataException("Unknown ASA map: choose an official level before automatic installation");
        return level;
    }
    /// <summary>
    /// Resolve an explicit level for new SteamCMD installations. Steam app
    /// 2430930 is shared across official/custom maps. A custom map must have
    /// a deliberate display name and a separate, strict Unreal level token.
    /// The installer never guesses a token or accepts a token override for a
    /// built-in map name; the game's mod files are configured separately.
    /// </summary>
    public static string LevelForNewInstallation(string name,string? customLevel)
    {
        if(string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("UM-FRESH-MAP-NAME-INVALID");
        if(Official.TryGetValue(name.Trim(),out string? official))
        {
            if(!string.IsNullOrWhiteSpace(customLevel))
                throw new InvalidDataException("UM-FRESH-OFFICIAL-LEVEL-OVERRIDE");
            return official;
        }
        if(string.IsNullOrWhiteSpace(customLevel))
            throw new InvalidDataException("UM-FRESH-CUSTOM-LEVEL-REQUIRED");
        return ValidateCustomLevel(customLevel);
    }
    // A manually imported mod map can expose a level not present in this
    // official-name table. It may be entered ONLY by an operator during the
    // separate first-setup preflight, never inferred from display names or
    // accepted as extra command-line arguments.
    public static string ValidateCustomLevel(string level)
    {
        static bool Letter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        static bool Digit(char c) => c is >= '0' and <= '9';
        if(string.IsNullOrWhiteSpace(level) || level.Length>80 ||
           !Letter(level[0]) ||
           level.Any(c=>!Letter(c) && !Digit(c) && c!='_'))
            throw new InvalidDataException("UM-INITIAL-CUSTOM-LEVEL-INVALID");
        return level;
    }
    public static string LaunchArguments(string name,string session,int gamePort,int queryPort) =>
        LaunchArgumentsForLevel(LevelFor(name),session,gamePort,queryPort);

    public static string LaunchArgumentsForLevel(string level,string session,int gamePort,int queryPort)
    {
        level=ValidateCustomLevel(level);
        if(gamePort is <1 or >65535 || queryPort is <1 or >65535 || gamePort==queryPort)
            throw new InvalidDataException("Invalid Game/Query UDP port");
        if(string.IsNullOrWhiteSpace(session)||session.Length>75||
           session.Any(c=>char.IsControl(c)||c is '"' or '?' or '\\' or '/' or ';'))
            throw new InvalidDataException("Invalid ASA server session name");
        // Never set RCONEnabled=False on the command line: doing so can
        // override an explicit, password-protected RCONEnabled=True in
        // GameUserSettings.ini. The documented ASA default is False.
        // Passwords are configured in a newly created local INI, not in
        // a world-readable process command line.
        return level+"?listen?SessionName=\""+session+"\""+
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

    public const string PublicBuildEndpoint="https://api.steamcmd.net/v1/info/2430930";
    private static readonly HttpClient PublicBuildHttp=new(){Timeout=TimeSpan.FromSeconds(4)};
    private static readonly SemaphoreSlim PublicBuildGate=new(1,1);
    private static string? publicBuild;
    private static DateTimeOffset publicBuildChecked;
    private static readonly TimeSpan PublicBuildTtl=TimeSpan.FromMinutes(15);

    // Public-branch data must belong to the dedicated-server application,
    // never the client, a beta branch, a depot manifest or a game version.
    public static string? ParsePublicBuildJson(string raw)
    {
        if(string.IsNullOrWhiteSpace(raw)||raw.Length>2_000_000)return null;
        try
        {
            using var doc=JsonDocument.Parse(raw);
            var app=doc.RootElement;
            if(app.ValueKind!=JsonValueKind.Object)return null;
            if(app.TryGetProperty("data",out var data))app=data;
            else if(app.TryGetProperty("response",out var response))app=response;
            foreach(string key in new[]{"2430930","depots","branches","public"})
            {
                if(app.ValueKind!=JsonValueKind.Object||!app.TryGetProperty(key,out var next))return null;
                app=next;
            }
            if(app.ValueKind!=JsonValueKind.Object||
               (!app.TryGetProperty("buildid",out var id)&&!app.TryGetProperty("BuildID",out id)))return null;
            string? value=id.ValueKind==JsonValueKind.String?id.GetString():
                id.ValueKind==JsonValueKind.Number?id.GetRawText():null;
            value=value?.Trim();
            return value is {Length:>0 and <=20}&&value.All(char.IsAsciiDigit)?value:null;
        }
        catch(JsonException){return null;}
    }

    // Read-only network metadata. No SteamCMD executable, install lease,
    // app_update, process shutdown or server filesystem write is needed.
    public static async Task<AsaSteamBuildCheck> CheckPublicAsync(string install,
        bool force=true,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        string? local=InstalledBuild(install);
        await PublicBuildGate.WaitAsync(ct);
        try
        {
            var now=DateTimeOffset.UtcNow;
            if(!force&&publicBuild!=null&&now-publicBuildChecked<PublicBuildTtl)
                return Compare(install,"public",local,publicBuild,publicBuildChecked);
            try
            {
                using var reply=await PublicBuildHttp.GetAsync(PublicBuildEndpoint,ct);
                reply.EnsureSuccessStatusCode();
                string payload=await reply.Content.ReadAsStringAsync(ct);
                string? fresh=ParsePublicBuildJson(payload);
                if(fresh!=null)
                {
                    publicBuild=fresh;
                    publicBuildChecked=DateTimeOffset.UtcNow;
                    // Refresh the local manifest after the HTTP round trip.
                    return Compare(install,"public",InstalledBuild(install),fresh,publicBuildChecked);
                }
            }
            catch(Exception ex) when(!ct.IsCancellationRequested&&
                ex is HttpRequestException or OperationCanceledException or IOException)
            { /* No fresh authoritative comparison; retry on the next check. */ }
            return Compare(install,"public",InstalledBuild(install),null,DateTimeOffset.UtcNow) with
                {DiagnosticCode="UM-STEAM-PUBLIC-HTTP-UNAVAILABLE",
                 Explanation="A publikus Steam build nem elérhető; próbáld újra. Nincs igazolt frissítési igény."};
        }
        finally{PublicBuildGate.Release();}
    }

    // Parse only buildid within depots -> branches -> requested branch.
    // Never infer a build from change numbers, arbitrary output or game versions.
    public static string? ParseSteamAppInfo(string raw,string branch="public")
    {
        if(string.IsNullOrWhiteSpace(raw)||raw.Length>2_000_000||
           string.IsNullOrWhiteSpace(branch)||branch.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))
            return null;
        // SteamCMD can return logs and unrelated quoted VDF objects.
        // Keep the 2430930 context for EACH nested object; a global
        // "seen app" flag silently turned another app's public branch into
        // an apparent ASA update. Truncated/ambiguous responses fail closed.
        var tokens=Regex.Matches(raw,"\"(?:[^\"\\\\]|\\\\.)*\"|[{}]")
            .Select(m=>m.Value).ToArray();
        var path=new List<string>();
        string? pending=null;
        string? publishedBuild=null;
        bool sawAsaRoot=false;
        for(int i=0;i<tokens.Length;i++)
        {
            string token=tokens[i];
            if(token=="{")
            {
                if(pending==null)return null;
                if(path.Count==0&&pending=="2430930")sawAsaRoot=true;
                path.Add(pending);
                pending=null;
                continue;
            }
            if(token=="}")
            {
                if(path.Count==0||pending!=null)return null;
                path.RemoveAt(path.Count-1);
                continue;
            }
            if(!token.StartsWith('"')||token.Length<2)return null;
            string key=token[1..^1];
            if(i+1<tokens.Length&&tokens[i+1].StartsWith('"'))
            {
                string value=tokens[++i][1..^1];
                bool exactAsaBranch=path.Count==4&&
                    path[0]=="2430930"&&
                    path[1].Equals("depots",StringComparison.OrdinalIgnoreCase)&&
                    path[2].Equals("branches",StringComparison.OrdinalIgnoreCase)&&
                    path[3].Equals(branch,StringComparison.OrdinalIgnoreCase);
                if(exactAsaBranch&&key=="buildid")
                {
                    if(publishedBuild!=null||value.Length is <1 or >20||
                       !value.All(char.IsAsciiDigit))return null;
                    publishedBuild=value;
                }
                pending=null;
            }
            else pending=key;
        }
        // Closing all objects and a unique target-branch ID are mandatory.
        // A partial Steam response cannot authorize "update available".
        return sawAsaRoot&&path.Count==0&&pending==null
            ? publishedBuild:null;
    }

    public static AsaSteamBuildCheck Compare(string install,string branch,
        string? local,string? available,DateTimeOffset when)
    {
        static bool ReadBuild(string? raw,out System.Numerics.BigInteger number)
        {
            number=default;
            return raw is {Length:>0 and <=20} &&
                raw.All(char.IsAsciiDigit) &&
                System.Numerics.BigInteger.TryParse(raw,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,out number);
        }
        // Always evaluate both operands so the variables are initialized even
        // when the first Steam build is missing.
        bool localValid=ReadBuild(local,out var installed);
        bool remoteValid=ReadBuild(available,out var published);
        bool known=localValid&&remoteValid;
        // The public branch may lag a private / manually supplied install.
        // A different build ID is NOT sufficient to assert an upgrade exists.
        var state=!known?AsaSteamBuildState.Unknown:
            installed>=published?AsaSteamBuildState.Current:
            AsaSteamBuildState.UpdateAvailable;
        return new AsaSteamBuildCheck(install,branch,local,available,state,when,
            state==AsaSteamBuildState.Current?(installed>published?
                "Naprakész (helyi build újabb a publikus lekérdezésnél)":"Naprakész (azonos Steam build ID)"):
            state==AsaSteamBuildState.UpdateAvailable?"Újabb Steam-frissítés elérhető":
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
        using var hostLease=SteamHostLease.Acquire();
        return await CheckUnderHostLeaseAsync(exe,install,branch,timeout,ct);
    }
    internal static async Task<AsaSteamBuildCheck> CheckUnderHostLeaseAsync(string exe,string install,
        string branch="public",TimeSpan? timeout=null,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        SteamStoppedMapUpdater.DemandNoConcurrentSteamCmd();
        SteamCmdPreflight.Inspect(exe);
        string? local=InstalledBuild(install);
        if(branch!="public")
            throw new NotSupportedException("Private Steam branches require explicit branch credentials");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout??TimeSpan.FromMinutes(3));
        var psi=new ProcessStartInfo(Path.GetFullPath(exe))
        {
            WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(exe))!,
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        SteamCmdInvocation.Configure(psi,new[]{"+login","anonymous","+app_info_update","1",
            "+app_info_print","2430930","+quit"});
        AsaSteamBuildCheck Unknown(string code,string explanation)=>
            Compare(install,branch,local,null,DateTimeOffset.UtcNow) with
                {DiagnosticCode=code,Explanation=explanation};
        Process? process;
        try
        {
            process=Process.Start(psi);
        }
        catch(System.ComponentModel.Win32Exception)
        {
            ct.ThrowIfCancellationRequested();
            return Unknown("UM-STEAM-PUBLIC-LAUNCH","A SteamCMD nem indítható. Ellenőrizd a programot és a Windows jogosultságokat.");
        }
        if(process==null)
            return Unknown("UM-STEAM-PUBLIC-LAUNCH","A SteamCMD folyamat nem indult el.");
        using(process)
        {
            try
            {
                var stdout=process.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr=process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                var output=await stdout;
                var errors=await stderr;
                if(output.Length>2_000_000||errors.Length>2_000_000)
                    return Unknown("UM-STEAM-PUBLIC-OUTPUT-LIMIT","A SteamCMD válasza túl nagy; ismételd meg a verzióellenőrzést.");
                var remote=process.ExitCode==0?ParseSteamAppInfo(output,branch):null;
                if(remote!=null)return Compare(install,branch,local,remote,DateTimeOffset.UtcNow);
                // Only fixed diagnostic codes and messages leave the process boundary.
                var diagnostic=DiagnoseAppInfo(output,errors,process.ExitCode);
                return Unknown(diagnostic.Code,diagnostic.Explanation);
            }
            catch(Exception ex) when(!ct.IsCancellationRequested&&
                ex is OperationCanceledException or IOException or InvalidDataException)
            {
                if(!process.HasExited)
                    try{process.Kill(entireProcessTree:true);}catch{}
                return ex is OperationCanceledException?
                    Unknown("UM-STEAM-PUBLIC-TIMEOUT","A SteamCMD verziólekérdezés időtúllépés miatt megszakadt. Ellenőrizd a kapcsolatot és próbáld újra."):
                    Unknown("UM-STEAM-PUBLIC-OUTPUT","A SteamCMD válasza nem olvasható. Ellenőrizd az eszközt és próbáld újra.");
            }
            catch
            {
                if(!process.HasExited)
                    try{process.Kill(entireProcessTree:true);}catch{}
                throw;
            }
        }
    }

    internal static (string Code,string Explanation) DiagnoseAppInfo(string stdout,string stderr,int exitCode)
    {
        string all=stdout+"\n"+stderr;
        bool Has(params string[] patterns)=>patterns.Any(p=>all.Contains(p,StringComparison.OrdinalIgnoreCase));
        if(Has("no connection","connection failed","failed to connect","network error","ssl error","tls error","certificate verify","connection timeout"))
            return ("UM-STEAM-PUBLIC-NETWORK","A SteamCMD kapcsolat vagy TLS hibát jelzett. Ellenőrizd a hálózatot, DNS-t és az időbeállítást.");
        if(Has("login failure","logon failure","failed to log on","failed to login","not logged on","invalid password","access denied"))
            return ("UM-STEAM-PUBLIC-LOGIN","A Steam névtelen bejelentkezése nem sikerült. Ellenőrizd a SteamCMD kapcsolatát.");
        if(Has("failed to update app info","failed to load appinfo","app info cache","appinfo.vdf is corrupt"))
            return ("UM-STEAM-PUBLIC-CACHE","A Steam appinfo gyorsítótár frissítése hibát jelzett. Ellenőrizd a SteamCMD saját mappáját.");
        if(exitCode!=0)
            return ("UM-STEAM-PUBLIC-EXIT","A SteamCMD hibakóddal zárult. Ellenőrizd a helyi SteamCMD naplót.");
        if(!stdout.Contains("\"2430930\"",StringComparison.Ordinal))
            return ("UM-STEAM-PUBLIC-APPINFO-MISSING","A SteamCMD válaszában nincs ASA appinfo rekord. Próbáld újra a lekérdezést.");
        if(!stdout.Contains("\"public\"",StringComparison.OrdinalIgnoreCase)||
           !stdout.Contains("\"buildid\"",StringComparison.OrdinalIgnoreCase))
            return ("UM-STEAM-PUBLIC-BUILD-MISSING","A publikus ág buildazonosítója hiányzik. Ez nem bizonyít frissítési igényt.");
        return ("UM-STEAM-PUBLIC-VDF","A SteamCMD appinfo VDF válasza hiányos, kétértelmű vagy nem feldolgozható.");

    }
}
