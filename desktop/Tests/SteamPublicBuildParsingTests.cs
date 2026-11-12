using System;
using UntamedManager.Core;

// Public Steam appinfo is untrusted. A malformed/unrelated/partial response
// must never become a trusted "new ASA build available" indication.
internal static class SteamPublicBuildParsingTests
{
    private static void Check(bool ok,string label)
    {
        if(!ok)throw new InvalidOperationException(
            "STEAM PUBLIC BUILD PARSER: "+label);
        Console.WriteLine("PASS Steam public build: "+label);
    }

    public static async System.Threading.Tasks.Task RunAsync(string testsRoot)
    {
        Run();
        string install=System.IO.Path.Combine(testsRoot,"steam-appinfo-unknown");
        string apps=System.IO.Path.Combine(install,"steamapps");
        System.IO.Directory.CreateDirectory(apps);
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(apps,"appmanifest_2430930.acf"),
            "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123456\"\n}\n");
        string bin=System.IO.Path.Combine(testsRoot,"fake-steamcmd-executable");
        System.IO.Directory.CreateDirectory(bin);
        string steam=System.IO.Path.Combine(bin,"steamcmd.exe");
        System.IO.File.WriteAllText(steam,"NOT A WINDOWS EXECUTABLE");
        var report=await AsaSteamBuildCatalog.CheckAsync(
            steam,install,"public",TimeSpan.FromSeconds(5));
        Check(report.InstalledBuild=="123456"&&
              report.AvailableBuild==null&&
              report.State==AsaSteamBuildState.Unknown,
            "SteamCMD cannot launch: existing local build retained, public build unknown");
    }

    public static void Run()
    {
        const string primary=
            "\"2430930\" {\"depots\" {\"branches\" {\"public\" "+
            "{\"buildid\" \"123457\"} \"beta\" {\"buildid\" \"456789\"}}}}";
        const string foreign=
            "\"999999\" {\"depots\" {\"branches\" {\"public\" "+
            "{\"buildid\" \"999999\"}}}}";
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(
            "SteamCMD loading...\n"+primary+"\n"+foreign)=="123457",
            "only the ASA application's public branch build is accepted");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(
            "\"2430930\" {} "+foreign)==null,
            "a previous ASA root never authorizes a foreign app's build");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(primary,"beta")=="456789",
            "the requested branch must match the exact nested path");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(
            primary[..^1])==null,
            "truncated Steam appinfo does not become an update");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(
            "\"2430930\" {\"depots\" {\"branches\" {\"public\" "+
            "{\"buildid\" \"123\" \"buildid\" \"456\"}}}}")==null,
            "duplicated build IDs are rejected even inside the target branch");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(
            "\"2430930\" {\"depots\" {\"branches\" {\"public\" "+
            "{\"buildid\" \"not-a-build\"}}}}")==null,
            "non-numeric Steam build strings never authorize an update");
        Check(AsaSteamBuildCatalog.ParseSteamAppInfo(
            "\"2430930\" {\"depots\" {\"branches\" {\"public\" "+
            "{\"buildid\" \"789\"}}}}}", "public")==null,
            "unbalanced closing braces reject otherwise plausible builds");
        var unknown=AsaSteamBuildCatalog.Compare("C:\\test","public",
            "123456",AsaSteamBuildCatalog.ParseSteamAppInfo(
                "\"2430930\" {} "+foreign),DateTimeOffset.UtcNow);
        Check(unknown.State==AsaSteamBuildState.Unknown,
            "wrong-game appinfo stays Unknown rather than UpdateAvailable");
    }
}
