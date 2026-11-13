using System;
using System.IO;
using System.Linq;
using UntamedDesktop;
using UntamedManager.Core;

// Windows native WMI-only test; all files live in isolated TEMP.
// Does not spawn an ASA process or contact any live Legacy installation.
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
        // The strict preflight must reject someone else's or an in-flight
        // local operation. The relaxed observer is valid only after the
        // Steam updater has acquired this exact installation lease.
        using(var held=cluster.Operations.Acquire(install,UntamedOperationKind.Update))
        {
            bool pendingRefused=false;
            try{HistoricalLocalConfigGuard.DemandStopped(cluster,map);}
            catch(InvalidOperationException){pendingRefused=true;}
            Check(pendingRefused,
                "historical Steam preflight rejects pending mutation journal");
            var inOwnLease=HistoricalLocalConfigGuard.DemandStopped(cluster,map,
                underOwnOperation:true);
            Check(inOwnLease.ConfirmedStopped,
                "historical stopped family can be checked under owned update lease");
            held.Complete();
        }
        // ClusterService.Check intentionally reports historical Store as
        // uncertain regardless of a synthetic inspector. Do not pretend a
        // fixture PID is a genuine WMI ArkAscendedServer process. Test the
        // independent fail-closed native identity/maintenance gates instead.
        map.Maintenance=true;
        bool denied=false;
        try{HistoricalLocalConfigGuard.DemandStopped(cluster,map);}
        catch(UnauthorizedAccessException){denied=true;}
        Check(denied,"historical maintenance blocks offline file mutation");
        map.Maintenance=false;
        map.HistoricalArchiveSha256="INVALID";
        denied=false;
        try{HistoricalLocalConfigGuard.DemandStopped(cluster,map);}
        catch(UnauthorizedAccessException){denied=true;}
        Check(denied,"changed original Store archive identity blocks native edit");
    }
}
