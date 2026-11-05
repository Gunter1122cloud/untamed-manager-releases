using System;
using System.IO;
using UntamedManager.Core;

public static class AsaSteamBuildCatalogTests
{
    private static void Check(bool ok,string desc)
    {
        if(!ok)throw new Exception("BUILD ID REGRESSION: "+desc);
        Console.WriteLine("PASS Steam build: "+desc);
    }
    public static void Run(string root)
    {
        string install=Path.Combine(root,"buildid-tests");
        Directory.CreateDirectory(Path.Combine(install,"steamapps"));
        string manifest=Path.Combine(install,"steamapps","appmanifest_2430930.acf");
        string content="\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123456\"\n}\n";
        File.WriteAllText(manifest,content);
        string? local=AsaSteamBuildCatalog.InstalledBuild(install);
        Check(local=="123456","local Steam build comes exclusively from ASA appmanifest");
        string sample="Loading Steam API...\n\"2430930\"\n{\n \"depots\"\n {\n"+
            " \"branches\"\n {\"public\" {\"buildid\" \"123457\" \"timeupdated\" \"10\"}"+
            " \"beta\" {\"buildid\" \"987654\"}}\n}\n \"changename\" \"456\" \n}";
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(sample)=="123457",
            "public branch build ID parsed from Steam app_info_print");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(sample,"beta")=="987654",
            "branch-specific depot IDs are not confused with public build");
        var latest=AsaSteamBuildCatalog.Compare(install,"public",local,"123457",
            DateTimeOffset.UtcNow);
        Check(latest.State==AsaSteamBuildState.UpdateAvailable,
            "differing real Steam build IDs signal update availability");
        var current=AsaSteamBuildCatalog.Compare(install,"public",local,"123456",
            DateTimeOffset.UtcNow);
        Check(current.State==AsaSteamBuildState.Current,
            "same build IDs suppress false positive updates");
        var noData=AsaSteamBuildCatalog.Compare(install,"public",local,null,
            DateTimeOffset.UtcNow);
        Check(noData.State==AsaSteamBuildState.Unknown,
            "missing authoritative public build is never classified as current/outdated");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(
            "\"2430930\" {\"depots\" {\"123\" {\"buildid\" \"22222\"}}}") is null,
            "depot manifest is not a published branch build ID");
        File.WriteAllText(manifest,content.Replace("2430930","376030"));
        Check(AsaSteamBuildCatalog.InstalledBuild(install)==null,
            "ASE 376030 manifest is never mistaken for ASA 2430930");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(
            "\"376030\" {\"depots\" {\"branches\" {\"public\" {\"buildid\" \"555\"}}}}")
            is null,"wrong Steam application output cannot trigger ASA update");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(
            "garbage published build 999888") is null,
            "game version strings and guessed numbers are not accepted as Steam build");
    }
}
