using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

// Windows TEMP only. No SteamCMD process, ASA installation, or Store files.
internal static class SteamProtectedFileCoverageTests
{
    private static void Check(bool passed,string reason)
    {
        if(!passed)throw new InvalidOperationException(
            "PROTECTED STEAM FILES REGRESSION FAILED: "+reason);
        Console.WriteLine("PASS protected SteamCMD files: "+reason);
    }

    public static void Run(string testRoot)
    {
        string install=Path.Combine(testRoot,"steam-protected-asa-api-files");
        string saved=Path.Combine(install,"ShooterGame","Saved","SavedArks",
            "TheIsland_WP.ark");
        string runtime=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "ArkApi","AsaApi.dll");
        string config=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "ArkApi","config.json");
        string disabled=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "UntamedDisabledPlugins","Example","config.json");
        string loader=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "AsaApiLoader.exe");
        string versionBin=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "Version.dll");
        string versionRoot=Path.Combine(install,"Version.dll");
        foreach(var pair in new[]{
            (saved,"world-bytes"),(runtime,"runtime-bytes"),
            (config,"runtime-config"),(disabled,"disabled-plugin-config"),
            (loader,"loader-binary"),(versionBin,"version-injection"),
            (versionRoot,"version-root-injection")})
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Item1)!);
            File.WriteAllText(pair.Item1,pair.Item2);
        }
        var first=SteamProtectedFiles.Snapshot(install);
        Check(first.Count==7 &&
              first.Keys.Any(x=>x.EndsWith("AsaApi.dll",StringComparison.OrdinalIgnoreCase)) &&
              first.Keys.Any(x=>x.EndsWith("AsaApiLoader.exe",StringComparison.OrdinalIgnoreCase)) &&
              first.Keys.Any(x=>x.EndsWith("Version.dll",StringComparison.OrdinalIgnoreCase)),
            "world, API DLL, config, disabled plugin and loader are all protected");
        foreach(string path in new[]{saved,runtime,config,disabled,loader,versionBin,versionRoot})
        {
            string original=File.ReadAllText(path);
            File.WriteAllText(path,original+"-altered");
            Check(!SteamProtectedFiles.Identical(first,
                SteamProtectedFiles.Snapshot(install)),
                "SteamCMD drift detected for "+Path.GetFileName(path));
            File.WriteAllText(path,original);
        }
        Check(SteamProtectedFiles.Identical(first,
            SteamProtectedFiles.Snapshot(install)),
            "unchanged ASA API and world files retain exact snapshot parity");
        File.Delete(loader);
        Check(!SteamProtectedFiles.Identical(first,
            SteamProtectedFiles.Snapshot(install)),
            "deleted ASA API loader is detected before update receipt");
    }
}
