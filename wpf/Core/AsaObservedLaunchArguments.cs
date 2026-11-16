using System;
using System.Linq;
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
