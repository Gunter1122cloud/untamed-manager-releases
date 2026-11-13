using System;
using System.Linq;
using System.Text.Json;
using UntamedDesktop;

internal static class LocalAsaLaunchFactsTests
{
    public static void Run()
    {
        const string exe=@"C:\isolated-asa-launch-test\ShooterGame\Binaries\Win64\ArkAscendedServer.exe";
        const string share=@"\\synthetic.example\PrivateStoreRoot";
        string launch="\""+exe+"\" TheIsland_WP?listen -Port=7777 -QueryPort=27015"+
            " -mods=100011,100012 -passivemods=100013 -usestore"+
            " -clusterid=KovaCluster -ClusterDirOverride="+share+
            " -SessionName=DO_NOT_RETURN_ADMIN_METADATA";
        static void Check(bool condition,string context)
        {
            if(!condition)throw new InvalidOperationException(
                "LOCAL ASA LAUNCH FACTS TEST FAILED: "+context);
            Console.WriteLine("PASS ASA launch facts: "+context);
        }

        var known=LocalAsaLaunchFacts.Parse(launch,exe);
        Check(known.ArgumentsValidated&&known.GamePort==7777&&
              known.QueryPort==27015,"verified command grammar yields Game and Query ports");
        Check(known.ActiveModsState=="parsed"&&
              known.ActiveModIds.SequenceEqual(new[]{"100011","100012"})&&
              known.PassiveModsState=="parsed"&&
              known.PassiveModIds.SequenceEqual(new[]{"100013"}),
            "mod ID order is preserved without downloading or installing mods");
        Check(known.StoreFlagPresent&&known.ClusterSwitchPresent&&
              known.TransferOverridePresent&&!known.ConversionFlagPresent,
            "Store-v1 and cluster flags are observed without writer authority");
        string sanitized=JsonSerializer.Serialize(known);
        Check(!sanitized.Contains("synthetic.example",StringComparison.Ordinal)&&
              !sanitized.Contains("PrivateStoreRoot",StringComparison.Ordinal)&&
              !sanitized.Contains("KovaCluster",StringComparison.Ordinal)&&
              !sanitized.Contains("SessionName",StringComparison.Ordinal)&&
              !sanitized.Contains("DO_NOT_RETURN",StringComparison.Ordinal),
            "sanitized facts never echo original UNC, cluster ID or session name");
        Check(LocalAsaLaunchFacts.Parse(launch+" -Port=8000",exe).GamePort==null,
            "duplicate game ports cannot provide authoritative telemetry");
        var invalidMod=LocalAsaLaunchFacts.Parse(
            launch.Replace("-mods=100011,100012","-mods=100011,bad",StringComparison.Ordinal),exe);
        Check(invalidMod.ActiveModsState=="unverified"&&invalidMod.ActiveModIds.Count==0,
            "malformed mod switches are not echoed to the WebView");
        var duplicateMod=LocalAsaLaunchFacts.Parse(launch+" -mods=100099",exe);
        Check(duplicateMod.ActiveModsState=="conflict"&&duplicateMod.ActiveModIds.Count==0,
            "duplicate mod declarations fail closed");
        var conversion=LocalAsaLaunchFacts.Parse(launch+" -converttostore",exe);
        Check(conversion.ConversionFlagPresent&&conversion.ArgumentsValidated,
            "conversion switches are detected but never executed");
        var wrongExe=LocalAsaLaunchFacts.Parse(
            launch.Replace(exe,exe+".spoof",StringComparison.Ordinal),exe);
        Check(!wrongExe.ArgumentsValidated&&wrongExe.GamePort==null,
            "spoofed command-line executable cannot yield process evidence");
        var control=LocalAsaLaunchFacts.Parse(launch+"\r\n -Port=9999",exe);
        Check(!control.ArgumentsValidated,"control-character injection is refused");
        var missing=LocalAsaLaunchFacts.Parse("",exe);
        Check(!missing.ArgumentsValidated&&missing.ActiveModsState=="unverified",
            "unavailable WMI source never implies an empty mod list");
    }
}
