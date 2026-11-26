using System.Linq;
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
        bool invalidRejected=false;
        try
        {
            await AsaSteamBuildCatalog.CheckAsync(steam,install,"public",TimeSpan.FromSeconds(5));
        }
        catch(System.IO.InvalidDataException ex) when(ex.Message=="SteamCMD executable has an invalid size")
        {
            invalidRejected=true;
        }
        Check(invalidRejected&&AsaSteamBuildCatalog.InstalledBuild(install)=="123456",
            "invalid text executable rejected before launch, local build unchanged");

        // Header-only inert PE fixture passes the bounded file preflight but
        // has no executable image/sections or entry point for Windows to load.
        byte[] header=new byte[512];
        header[0]=(byte)'M';header[1]=(byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(header,0x3c);
        header[0x80]=(byte)'P';header[0x81]=(byte)'E';
        BitConverter.GetBytes((ushort)0x014c).CopyTo(header,0x84);
        BitConverter.GetBytes((ushort)1).CopyTo(header,0x86);
        BitConverter.GetBytes((ushort)0xe0).CopyTo(header,0x94);
        System.IO.File.WriteAllBytes(steam,header);
        Check(SteamCmdPreflight.Inspect(steam).LengthBytes==512,
            "inert header fixture passes only the structural preflight");
        var report=await AsaSteamBuildCatalog.CheckAsync(
            steam,install,"public",TimeSpan.FromSeconds(5));
        Check(report.InstalledBuild=="123456"&&
              report.AvailableBuild==null&&
              report.State==AsaSteamBuildState.Unknown&&
              report.DiagnosticCode=="UM-STEAM-PUBLIC-LAUNCH",
            "SteamCMD cannot launch: existing local build retained, public build unknown");
    }

    public static void Run()
    {
        const string installed=""" "AppState" { "appid" "2430930" "InstalledDepots" { "123" { "buildid" "999" } } "buildid" "123456" "name" "ARK: Survival Ascended" } """;
        Check(AsaSteamBuildCatalog.ParseInstalledBuildManifest(installed)=="123456",
            "installed manifest reads the direct AppState build, not a nested depot");
        Check(AsaSteamBuildCatalog.ParseInstalledBuildManifest(installed.Replace("\"appid\" \"2430930\"","\"appid\" \"376030\""))==null,
            "non-ASA AppState cannot certify installed build");
        Check(AsaSteamBuildCatalog.ParseInstalledBuildManifest(installed.Replace("\"buildid\" \"123456\"",""))==null,
            "nested build cannot substitute for missing AppState build");
        Check(AsaSteamBuildCatalog.ParseInstalledBuildManifest(installed.Replace("\"buildid\" \"123456\"","\"buildid\" \"123456\" \"buildid\" \"222\""))==null,
            "duplicate AppState build IDs remain unknown");
        Check(AsaSteamBuildCatalog.ParseInstalledBuildManifest(installed.TrimEnd()[..^1])==null&&
              AsaSteamBuildCatalog.ParseInstalledBuildManifest(installed+installed)==null&&
              AsaSteamBuildCatalog.ParseInstalledBuildManifest(installed+" extra")==null,
            "truncated, duplicated or malformed manifest roots remain unknown");
        Check(AsaSteamBuildCatalog.ParseInstalledBuildManifest(""" "AppState" { "appid" "2430930" "buildid" "123456" "name" "literal } and \"quote\" inside" } """)=="123456",
            "quoted brace and escaped quote are content, not structural delimiters");
        const string json="""{"data":{"2430930":{"depots":{"branches":{"public":{"buildid":"123457"},"beta":{"buildid":"999999"}}}}}}""";
        Check(AsaSteamBuildCatalog.ParsePublicBuildJson(json)=="123457",
            "HTTP metadata reads only dedicated ASA public branch");
        Check(AsaSteamBuildCatalog.ParsePublicBuildJson(json.Replace("2430930","2399830"))==null,
            "client app metadata is not dedicated server metadata");
        Check(AsaSteamBuildCatalog.ParsePublicBuildJson(json.Replace("123457","97.2"))==null,
            "ARK game version cannot authorize a Steam update");
        Check(AsaSteamBuildCatalog.ParsePublicBuildJson(json.Replace("public","private"))==null,
            "a private branch does not authorize a public update");
        Check(AsaSteamBuildCatalog.ParsePublicBuildJson(json[..^1])==null,
            "truncated HTTP metadata stays unknown");
        Check(AsaSteamBuildCatalog.Compare(@"C:\test","public","123458","123457",DateTimeOffset.UtcNow).State==AsaSteamBuildState.Current,
            "public API lag does not mark newer local builds outdated");
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
        var invocation=new System.Diagnostics.ProcessStartInfo("steamcmd.exe");
        invocation.Environment["STEAM_PASSWORD"]="fixture-secret";
        SteamCmdInvocation.Configure(invocation,SteamCmdInvocation.UpdateArguments(@"C:\ASA Test"));
        Check(invocation.ArgumentList.SequenceEqual(new[]{"-language","english","+force_install_dir",
            @"C:\ASA Test","+login","anonymous","+app_update","2430930","validate","+quit"})&&
            !invocation.Environment.ContainsKey("STEAM_PASSWORD"),
            "English anonymous argv preserves spaced path and removes credentials");
        string transient="ERROR! Failed to install app '2430930' (No connection)";
        Check(SteamCmdInvocation.CanRetryAppUpdate(0,1,transient,false)&&
            SteamCmdInvocation.CanRetryAppUpdate(1,1,transient,false)&&
            !SteamCmdInvocation.CanRetryAppUpdate(2,1,transient,false)&&
            !SteamCmdInvocation.CanRetryAppUpdate(0,0,transient,false)&&
            !SteamCmdInvocation.CanRetryAppUpdate(0,1,transient,true),
            "bounded retries only on recognized provider failure");
        Check(!SteamCmdInvocation.CanRetryAppUpdate(0,1,transient+" Disk write failure",false)&&
            !SteamCmdInvocation.CanRetryAppUpdate(0,1,"random connection timeout",false)&&
            SteamCmdInvocation.UpdateFailureCode("ERROR! Disk write failure")=="UM-STEAM-UPDATE-DISK",
            "permanent and unknown failures are not replayed");
        Check(SteamCmdInvocation.CanUseDirectFallback(new System.IO.IOException("UM-STEAM-CACHE-INSUFFICIENT-STAGING-SPACE"))&&
            !SteamCmdInvocation.CanUseDirectFallback(new System.IO.InvalidDataException("UM-STEAM-CACHE-MAP-OVERLAP")),
            "cache fallback only for a known safe pre-mutation condition");
        var unknown=AsaSteamBuildCatalog.Compare("C:\\test","public",
            "123456",AsaSteamBuildCatalog.ParseSteamAppInfo(
                "\"2430930\" {} "+foreign),DateTimeOffset.UtcNow);
        Check(unknown.State==AsaSteamBuildState.Unknown,
            "wrong-game appinfo stays Unknown rather than UpdateAvailable");
    }
}
