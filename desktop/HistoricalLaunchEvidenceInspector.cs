using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedDesktop;

/// <summary>
/// Read-only assessment of an OPERATOR-SUPPLIED historical launch line.
/// It is not authenticated provenance, and must never unlock a historical map.
/// No command line, password, cluster name or path is retained or returned.
/// </summary>
public static class HistoricalLaunchEvidenceInspector
{
    private static readonly TimeSpan MatchTimeout=TimeSpan.FromMilliseconds(100);
    private static MatchCollection Matches(string source,string pattern)=>
        Regex.Matches(source,pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,MatchTimeout);

    private static string[] Values(string source,string name,bool query=false)
    {
        string prefix=query?@"\?":@"(?:^|\s)-";
        string pattern=prefix+Regex.Escape(name)+@"=(?:""(?<value>[^""]*)""|(?<value>[^\s?]*))";
        return Matches(source,pattern).Cast<Match>().Select(m=>m.Groups["value"].Value).ToArray();
    }

    private static bool Flag(string source,string name)=>
        Matches(source,@"(?:^|\s)-"+Regex.Escape(name)+@"(?=\s|$)").Count>0;

    private static string PortStatus(string[] observed,int archived)
    {
        if(observed.Length==0)return "UNKNOWN";
        if(observed.Length!=1)return "CONFLICT";
        return int.TryParse(observed[0],out int port)&&port==archived?"MATCH":"MISMATCH";
    }

    public static object Inspect(string source,string expectedMap,int gamePort,int queryPort,
        IReadOnlyList<string> expectedMods)
    {
        if(string.IsNullOrWhiteSpace(source)||source.Length>3072||
           source.Any(c=>char.IsControl(c)&&c!='\t'))
            throw new ArgumentException("Invalid historical command input");

        // The mandatory map token follows the optional executable name.
        // A textual match is NOT proof of an original process command line.
        bool mapFound=Matches(source,@"(?:^|\s)"+Regex.Escape(expectedMap)+@"(?=\?|\s|$)").Count==1;
        // ASA installations can place these options in the map query string
        // or as process switches. Duplicate/conflicting declarations fail closed.
        var game=Values(source,"port").Concat(Values(source,"port",true)).ToArray();
        var query=Values(source,"QueryPort").Concat(Values(source,"QueryPort",true)).ToArray();
        var mods=Values(source,"mods");
        var deprecatedMods=Values(source,"GameModIds",true);
        var cluster=Values(source,"clusterid");
        // Verified Untamed Kingdom Store-v1 internal identity. Never rename this
        // to the product/display name while retaining historical data.
        const string historicalClusterId="KovaCluster";
        string clusterStatus=cluster.Length==0?"UNKNOWN":
            cluster.Length!=1?"CONFLICT":
            string.Equals(cluster[0],historicalClusterId,StringComparison.Ordinal)?"MATCH":"MISMATCH";
        var transfer=Values(source,"ClusterDirOverride");
        var useStore=Flag(source,"usestore");
        var convert=Flag(source,"converttostore");
        string modStatus;
        if(mods.Length==0) modStatus=deprecatedMods.Length>0?"LEGACY_SYNTAX":"UNKNOWN";
        else if(mods.Length!=1)modStatus="CONFLICT";
        else
        {
            var found=mods[0].Split(',',StringSplitOptions.TrimEntries);
            modStatus=found.SequenceEqual(expectedMods,StringComparer.Ordinal)?"MATCH":"MISMATCH";
        }
        var codes=new List<string>();
        if(!mapFound)codes.Add("MAP_NOT_CONFIRMED");
        if(PortStatus(game,gamePort)!="MATCH")codes.Add("GAME_PORT_NOT_CONFIRMED");
        if(PortStatus(query,queryPort)!="MATCH")codes.Add("QUERY_PORT_NOT_CONFIRMED");
        if(modStatus!="MATCH")codes.Add("MOD_ORDER_NOT_CONFIRMED");
        if(clusterStatus!="MATCH")codes.Add("CLUSTER_ID_NOT_CONFIRMED");
        if(transfer.Length!=1||string.IsNullOrWhiteSpace(transfer[0]))codes.Add("CLUSTER_PATH_NOT_CONFIRMED");
        if(!useStore)codes.Add("USESTORE_NOT_CONFIRMED");
        if(convert)codes.Add("CONVERSION_FLAG_PRESENT");
        // Return only classifications, never original input, original IDs,
        // admin credentials, SessionName or any source path.
        return new {
            mapStatus=mapFound?"MENTIONED":"UNKNOWN",
            gamePortStatus=PortStatus(game,gamePort),
            queryPortStatus=PortStatus(query,queryPort),
            modOrderStatus=modStatus,
            clusterIdPresent=cluster.Length==1&&!string.IsNullOrWhiteSpace(cluster[0]),
            clusterIdStatus=clusterStatus,
            clusterPathPresent=transfer.Length==1&&!string.IsNullOrWhiteSpace(transfer[0]),
            clusterPathVerified=false,
            useStorePresent=useStore,conversionFlagPresent=convert,
            findings=codes.ToArray(),sourceAuthenticated=false,
            fullLaunchArgumentsVerified=false,peerWriterVerified=false,
            productionUseAllowed=false,serverStartAllowed=false,
            evidence="OPERATOR_TEXT_READ_ONLY_UNAUTHENTICATED"
        };
    }
}
