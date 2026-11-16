using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedDesktop;

/// <summary>
/// Non-authoritative, read-only interpretation of a running ASA command line.
/// Return only bounded public port/mod identifiers and harmless flag presence.
/// Never expose launch source, raw UNC paths, secrets, cluster IDs or strings.
/// No facts from this parser are eligible for process adoption or Store grants.
/// </summary>
public static class LocalAsaLaunchFacts
{
    public sealed record Facts(
        int? GamePort,int? QueryPort,
        IReadOnlyList<string> ActiveModIds,IReadOnlyList<string> PassiveModIds,
        string ActiveModsState,string PassiveModsState,
        bool StoreFlagPresent,bool ClusterSwitchPresent,
        bool TransferOverridePresent,bool ConversionFlagPresent,
        bool ArgumentsValidated);

    private static Facts Unverified()=>new(null,null,Array.Empty<string>(),
        Array.Empty<string>(),"unverified","unverified",
        false,false,false,false,false);

    private static int? ReadPort(string[] tokens,string prefix)
    {
        var fields=tokens.Where(x=>x.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if(fields.Length!=1)return null;
        string raw=fields[0][prefix.Length..];
        if(raw.Length is <1 or >5||!raw.All(char.IsAsciiDigit)||
           !int.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out int port)||
           port is <1024 or >65535)return null;
        return port;
    }

    private static (IReadOnlyList<string> Ids,string State) ReadMods(
        string[] tokens,string prefix)
    {
        var fields=tokens.Where(x=>x.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if(fields.Length==0)return(Array.Empty<string>(),"notDeclared");
        if(fields.Length!=1)return(Array.Empty<string>(),"conflict");
        string raw=fields[0][prefix.Length..];
        if(string.IsNullOrWhiteSpace(raw)||raw.Length>2048)
            return(Array.Empty<string>(),"unverified");
        var mods=raw.Split(',');
        if(mods.Length is <1 or >80||
           mods.Any(x=>x.Length is <1 or >20||!x.All(char.IsAsciiDigit))||
           mods.Distinct(StringComparer.Ordinal).Count()!=mods.Length)
            return(Array.Empty<string>(),"unverified");
        return(mods,"parsed");
    }

    public static Facts Parse(string? observedCommand,string? processExecutable)
    {
        // Shared parser refuses an altered executable path before reading
        // any switch. Even a valid source is only an untrusted observation.
        string? arguments=LocalRunningAsaDiscovery.CommandArguments(
            observedCommand,processExecutable);
        if(string.IsNullOrWhiteSpace(arguments))return Unverified();
        // The map URL is a single Windows argument even if its quoted
        // SessionName contains spaces. Share the exact discovery grammar.
        if(!LocalRunningAsaDiscovery.TryReadLaunchArguments(
            arguments,out _,out var flags))return Unverified();
        string[] fields=flags.Length==0?Array.Empty<string>():
            flags.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        var active=ReadMods(fields,"-mods=");
        var passive=ReadMods(fields,"-passivemods=");
        bool One(string prefix,bool strict=false)=>
            fields.Count(x=>x.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&
                (!strict||x.Length>prefix.Length))==1 &&
            fields.Count(x=>x.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))==1;
        var clusters=fields.Where(x=>x.StartsWith("-clusterid=",
            StringComparison.OrdinalIgnoreCase)).ToArray();
        bool cluster=clusters.Length==1&&clusters[0].Length>11&&
            Regex.IsMatch(clusters[0][11..],@"\A[A-Za-z0-9_-]{1,64}\z",
                RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        bool conversion=fields.Any(x=>x.Equals("-converttostore",
            StringComparison.OrdinalIgnoreCase)||
            x.Equals("-convertfromstore",StringComparison.OrdinalIgnoreCase));
        return new(ReadPort(fields,"-Port="),ReadPort(fields,"-QueryPort="),
            active.Ids,passive.Ids,active.State,passive.State,
            fields.Count(x=>x.Equals("-usestore",StringComparison.OrdinalIgnoreCase))==1,
            cluster,One("-ClusterDirOverride=",true),conversion,true);
    }
}
