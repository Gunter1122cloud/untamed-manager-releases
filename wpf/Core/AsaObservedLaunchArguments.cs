using System;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

/// <summary>
/// Exact, display-safe ASA map URL parsing shared by Windows discovery,
/// explicit non-Store adoption and final Core WMI ownership handoff.
/// A quoted native SessionName may contain spaces or harmless words like
/// "Store"; actual protected switches and secrets remain forbidden.
/// Neither preview nor parsing proves process ownership or grants RCON.
/// </summary>
public static class AsaObservedLaunchArguments
{
    private static readonly Regex LaunchPrefix=new(
        @"\A(?<map>[A-Za-z0-9_]{1,100})\?listen(?:\?SessionName=""[^""\r\n]{1,75}"")?(?=\s|\z)",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly string[] SensitivePrefixes={
        "-password","-serveradminpassword","-secret","-token","-auth",
        "-credential","-key","-rconpassword"
    };

    public static bool TryRead(string? value,out string mapKey,out string flags)
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

    // Shared narrow Loader-child grammar. Physical ancestry must be
    // independently verified by the caller. Never grants Store ownership.
    internal static string? ExactArguments(string? command,string? exe)
    {
        if(string.IsNullOrWhiteSpace(command)||string.IsNullOrWhiteSpace(exe)||
           command.Length>8192||command.Any(char.IsControl))return null;
        try
        {
            string line=command.Trim(),actual;
            if(line.StartsWith('"'))
            {
                int end=line.IndexOf('"',1);
                if(end<2||(line.Length>end+1&&!char.IsWhiteSpace(line[end+1])))return null;
                actual=line[1..end];line=line[(end+1)..].Trim();
            }
            else
            {
                if(!line.StartsWith(exe,StringComparison.OrdinalIgnoreCase)||
                   (line.Length>exe.Length&&!char.IsWhiteSpace(line[exe.Length])))return null;
                actual=exe;line=line[exe.Length..].Trim();
            }
            return Path.IsPathFullyQualified(actual)&&Path.IsPathFullyQualified(exe)&&
                string.Equals(Path.GetFullPath(actual),Path.GetFullPath(exe),
                    StringComparison.OrdinalIgnoreCase)?line:null;
        }
        catch(Exception e) when(e is ArgumentException or NotSupportedException or IOException)
        {return null;}
    }
    internal static bool NonStoreLoaderArguments(string? arguments)
    {
        if(!TryRead(arguments,out _,out var flags)||ContainsProtectedOrSecretSwitch(flags))
            return false;
        return !flags.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Any(token=>
            token.Contains("store",StringComparison.OrdinalIgnoreCase)||
            token.StartsWith("-cluster",StringComparison.OrdinalIgnoreCase)||
            token.StartsWith("-convert",StringComparison.OrdinalIgnoreCase));
    }
    internal static bool MatchesNonStoreLoaderChild(string? command,string server,
        string loader,string launch)
    {
        if(!NonStoreLoaderArguments(launch))return false;
        string? child=ExactArguments(command,server);
        if(child==null)
            return string.Equals(ExactArguments(command,loader),launch,StringComparison.Ordinal);
        if(child.Equals(launch,StringComparison.Ordinal))return true;
        return child.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).All(token=>
            token.Equals("-log",StringComparison.OrdinalIgnoreCase)||
            token.Equals("-NoBattlEye",StringComparison.OrdinalIgnoreCase)||
            token.Equals("-NoHangDetection",StringComparison.OrdinalIgnoreCase));
    }
    public static bool ContainsProtectedOrSecretSwitch(string flags)
    {
        if(flags==null)return true;
        if(AsaStoreArgumentSwitches.ContainsProtectedSwitch(flags))
            return true;
        return flags.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)
            .Any(token=>SensitivePrefixes.Any(prefix=>token.StartsWith(
                prefix,StringComparison.OrdinalIgnoreCase)));
    }
}
