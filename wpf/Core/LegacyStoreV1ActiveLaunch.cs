using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

/// <summary>
/// Independent C# implementation of the launch-argument contract observed in
/// the operator-supplied Kova agent.mjs. Existing Store-v1 is ACTIVE ONLY:
/// never prepare, convert, create a new session or rewrite shared metadata.
/// This builder creates ARGUMENTS, not a process. Production launch additionally
/// requires verified live two-peer writer ownership and ASA acceptance tests.
/// </summary>
public static class LegacyStoreV1ActiveLaunch
{
    private static readonly Regex Level=new(@"\A[A-Za-z0-9_]{1,100}\z",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly Regex Mod=new(@"\A[0-9]{1,12}\z",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly Regex ClusterName=new(@"\A[A-Za-z0-9_-]{1,80}\z",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));

    public sealed record Input(string MapKey,int GamePort,int QueryPort,
        IReadOnlyList<string> Mods,IReadOnlyList<string> PassiveMods,
        string ClusterId,string ClusterPath,string OriginalPhase,
        string GameUserSettingsIni,bool SourcePinnedClusterOnly=true);
    public sealed record Result(string[] Arguments,string MapKey,int GamePort,
        int QueryPort,string[] ModOrder,string[] PassiveModOrder,
        string StoreMode,bool ConversionRequested,string Evidence);

    /// <summary>
    /// Decode original ASA INI without rewriting a single source byte.
    /// Kova 1.8.3 accepts UTF-8 and BOM-marked UTF-16 LE/BE. Strict
    /// decoding makes a damaged or mislabeled INI a preflight error,
    /// rather than silently interpreting it as a blank configuration.
    /// </summary>
    public static string DecodeOriginalIni(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            Encoding encoding;
            int skip=0;
            if(bytes.Length>=3&&bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF)
            {
                encoding=new UTF8Encoding(false,true);
                skip=3;
            }
            else if(bytes.Length>=2&&bytes[0]==0xFF&&bytes[1]==0xFE)
            {
                encoding=new UnicodeEncoding(false,false,true);
                skip=2;
            }
            else if(bytes.Length>=2&&bytes[0]==0xFE&&bytes[1]==0xFF)
            {
                encoding=new UnicodeEncoding(true,false,true);
                skip=2;
            }
            else encoding=new UTF8Encoding(false,true);
            string decoded=encoding.GetString(bytes,skip,bytes.Length-skip);
            if(decoded.IndexOf('\\0')>=0)
                throw new InvalidDataException("UM-STORE-NATIVE-INI-NUL");
            return decoded;
        }
        catch(DecoderFallbackException ex)
        {
            throw new InvalidDataException("UM-STORE-NATIVE-INI-ENCODING",ex);
        }
    }

    public static Result Build(Input data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if(data.OriginalPhase!="active")
            throw new InvalidDataException("UM-STORE-V1-ACTIVE-PHASE-ONLY");
        if(string.IsNullOrWhiteSpace(data.MapKey)||!Level.IsMatch(data.MapKey))
            throw new InvalidDataException("UM-STORE-V1-MAP-KEY");
        if(data.GamePort is <1024 or >65535||data.QueryPort is <1024 or >65535||
           data.GamePort==data.QueryPort)
            throw new InvalidDataException("UM-STORE-V1-PORTS");
        if(!ClusterName.IsMatch(data.ClusterId)||
           (data.SourcePinnedClusterOnly&&data.ClusterId!="KovaCluster"))
            throw new InvalidDataException("UM-STORE-V1-CLUSTER-IDENTITY");
        if(!ValidWindowsClusterPath(data.ClusterPath))
            throw new InvalidDataException("UM-STORE-V1-CLUSTER-PATH");
        if(data.Mods==null||data.PassiveMods==null||
           data.Mods.Count>100||data.PassiveMods.Count>100||
           data.Mods.Any(x=>!ValidMod(x))||data.PassiveMods.Any(x=>!ValidMod(x))||
           data.PassiveMods.Distinct(StringComparer.Ordinal).Count()!=data.PassiveMods.Count)
            throw new InvalidDataException("UM-STORE-V1-MODS");
        if(data.PassiveMods.Except(data.Mods,StringComparer.Ordinal).Any())
            throw new InvalidDataException("UM-STORE-V1-PASSIVE-NOT-ACTIVE");
        ValidateIniPort(data.GameUserSettingsIni,"Port",data.GamePort);
        ValidateIniPort(data.GameUserSettingsIni,"QueryPort",data.QueryPort);
        var passive=data.PassiveMods.ToArray();
        // The -mods switch is an ordered list, not a passive-first group.
        // -passivemods selects a subset without reshuffling that list.
        var order=data.Mods.ToArray();
        if(order.Distinct(StringComparer.Ordinal).Count()!=order.Length)
            throw new InvalidDataException("UM-STORE-V1-DUPLICATE-MOD");
        var args=new List<string> {
            data.MapKey+"?listen",
            "-Port="+data.GamePort.ToString(CultureInfo.InvariantCulture),
            "-QueryPort="+data.QueryPort.ToString(CultureInfo.InvariantCulture)
        };
        if(order.Length>0)args.Add("-mods="+string.Join(",",order));
        if(passive.Length>0)args.Add("-passivemods="+string.Join(",",passive));
        args.Add("-clusterid="+data.ClusterId);
        args.Add("-ClusterDirOverride="+data.ClusterPath);
        // The operator's cluster has completed the original Store migration.
        // This must be present ONCE, with no second conversion attempt.
        args.Add("-usestore");
        args.Add("-log");
        if(args.Any(x=>x.Contains("converttostore",StringComparison.OrdinalIgnoreCase)||
                       x.Contains("convertfromstore",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UM-STORE-V1-RECONVERSION-DENIED");
        return new(args.ToArray(),data.MapKey,data.GamePort,data.QueryPort,
            order,passive,"active",false,"LEGACY_SOURCE_ACTIVE_LAUNCH_GRAMMAR_ONLY");
    }

    private static bool ValidMod(string? id)=>!string.IsNullOrWhiteSpace(id)&&Mod.IsMatch(id);

    /// <summary>
    /// Original source: a local drive or a UNC share, no spaces, quotes,
    /// traversal, device prefix or additional colon. Does NOT open the path.
    /// </summary>
    private static bool ValidWindowsClusterPath(string? path)
    {
        if(string.IsNullOrWhiteSpace(path)||path.Length>500||
           path.Any(c=>char.IsWhiteSpace(c)||c is '"' or '<' or '>' or '|' or '?' or '*' or '\0')||
           path.StartsWith(@"\\?\",StringComparison.Ordinal)||
           path.StartsWith(@"\\.\",StringComparison.Ordinal))
            return false;
        bool unc=path.StartsWith(@"\\",StringComparison.Ordinal);
        if(!unc&&!(path.Length>3&&char.IsAsciiLetter(path[0])&&
                    path[1]==':'&&path[2]=='\\'))
            return false;
        string remaining=unc?path[2..]:path[3..];
        if(remaining.Contains(':')||remaining.Contains('/')||
           remaining.Split('\\').Any(x=>x.Length==0||x is "." or ".."))
            return false;
        return !unc||remaining.Split('\\').Length>=2;
    }

    // INI comments and section names are compared without case sensitivity,
    // and duplicate properties follow the source's last-value-wins behavior.
    private static void ValidateIniPort(string ini,string key,int expected)
    {
        if(ini==null||ini.Length>4_000_000)
            throw new InvalidDataException("UM-STORE-V1-INI-INVALID");
        bool settings=false;
        string? observed=null;
        foreach(string original in ini.Split('\n'))
        {
            string line=original.TrimEnd('\r');
            string trim=line.Trim();
            if(trim.StartsWith('[')&&trim.EndsWith(']'))
            {
                settings=trim[1..^1].Equals("ServerSettings",StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if(!settings||trim.StartsWith(';')||trim.StartsWith('#'))continue;
            int at=line.IndexOf('=');
            if(at<0)continue;
            string name=line[..at].Trim();
            if(name.Equals(key,StringComparison.OrdinalIgnoreCase))
                observed=line[(at+1)..].Trim();
        }
        if(observed!=null&&
           (!int.TryParse(observed,NumberStyles.None,CultureInfo.InvariantCulture,out int actual)||
            actual!=expected))
            throw new InvalidDataException("UM-STORE-V1-INI-"+key.ToUpperInvariant()+"-MISMATCH");
    }
}
