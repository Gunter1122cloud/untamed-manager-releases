using System;
using System.IO;
using System.Linq;
using UntamedDesktop;
using UntamedManager.Core;

// Windows native WMI-only test; all files live in isolated TEMP.
// Does not spawn an ASA process or contact any live Kova installation.
internal static class HistoricalLocalConfigGuardTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new InvalidOperationException(
            "HISTORICAL CONFIG GUARD REGRESSION FAILED: "+reason);
        Console.WriteLine("PASS historical config guard: "+reason);
    }

    public static async System.Threading.Tasks.Task Run(string testRoot)
    {
        string install=Path.Combine(testRoot,"original-offline-config-wmi");
        string executable=Path.Combine(install,"ShooterGame","Binaries",
            "Win64","ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable,new byte[]{0x4d,0x5a,0,0});
        bool simulatedRunning=false;
        await using var cluster=new ClusterService("original-config-wmi-ci",
            Path.Combine(testRoot,"original-config-wmi-profile"),
            _=>simulatedRunning?
                new ProcessCheck(true,"synthetic running",new ProcessIdentity{
                    Pid=33448,StartedUtc=DateTimeOffset.UtcNow,
                    ExecutablePath=executable,Arguments="synthetic"},false):
                new ProcessCheck(false,"generic original argv unknown",null,false));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",
            InstallationPath=install,ExecutablePath=executable,
            HistoricalStoreProtected=true,HistoricalMapKey="TheIsland_WP",
            HistoricalArchiveSha256=new string('B',64),
            GamePort=7777,QueryPort=27015,
            Arguments="",ApiLoaderArguments=""
        };
        cluster.AddMap(map);
        var confirmed=HistoricalLocalConfigGuard.DemandStopped(cluster,map);
        Check(confirmed.ConfirmedStopped&&!confirmed.Running,
            "native historical WMI proof authorizes offline edit despite generic argv uncertainty");
        simulatedRunning=true;
        bool denied=false;
        try{HistoricalLocalConfigGuard.DemandStopped(cluster,map);}
        catch(InvalidOperationException){denied=true;}
        Check(denied,
            "known running ASA PID overrides a stale native stopped observation");
    }
}
