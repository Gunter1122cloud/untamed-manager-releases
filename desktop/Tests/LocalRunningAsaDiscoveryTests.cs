using System;
using System.IO;
using System.Linq;
using UntamedDesktop;
using UntamedManager.Core;

internal static class LocalRunningAsaDiscoveryTests
{
    public static void Run()
    {
        // No real process, RCON, ASA installation or SMB path is touched.
        var dir=Path.Combine(@"C:\synthetic-asa-process-only","ShooterGame","Binaries","Win64");
        string server=Path.Combine(dir,"ArkAscendedServer.exe");
        string loader=Path.Combine(dir,"AsaApiLoader.exe");
        var started=DateTimeOffset.UtcNow.AddMinutes(-12);
        var original=new LocalMap{
            Id=Guid.NewGuid(),Name="Synthetic The Island",HistoricalMapKey="TheIsland_WP",
            HistoricalStoreProtected=true,InstallationPath=@"C:\synthetic-asa-process-only",
            ExecutablePath=server,ApiLoaderPath=loader,StartWithAsaApi=true
        };
        LocalRunningAsaDiscovery.Sample[] sample={
            new(1001,33,started,"AsaApiLoader.exe",loader,
                "\""+loader+"\" TheIsland_WP?listen -Port=7777 -usestore"),
            new(1002,1001,started.AddSeconds(5),"ArkAscendedServer.exe",server,
                "\""+server+"\" TheIsland_WP?listen -Port=7777 -usestore"),
            new(1003,33,started,"ArkAscendedServer.exe",server,
                "\""+server+"\" Ragnarok_WP?listen -Port=7817"),
            new(1004,33,null,"ArkAscendedServer.exe",null,null)
        };
        var found=LocalRunningAsaDiscovery.Classify(sample,new[]{original}).ToArray();
        void Check(bool pass,string message)
        {
            if(!pass)throw new InvalidOperationException(
                "LOCAL RUNNING ASA DISCOVERY TEST FAILED: "+message);
            Console.WriteLine("PASS running ASA discovery: "+message);
        }
        Check(found.Length==4,"no running ASA candidate silently discarded");
        Check(found[0].MatchState=="profileCandidate"&&
              found[1].MatchState=="profileCandidate"&&
              found[1].LoaderParentObserved,
            "loader and ASA child are observed with the expected map, executable and parent");
        Check(found[2].MatchState=="unregistered"&&found[2].ProfileMapId==null,
            "another map sharing an executable is not adopted");
        Check(found[3].MatchState=="unverified"&&found[3].ProfileMapId==null,
            "unreadable process identity cannot be mistaken for a registered map");
        Check(found.All(x=>!x.Controllable&&!x.StoreWriteAuthorized),
            "even successful source-key candidates grant zero lifecycle or Store authority");
        var spoof=sample[1] with{CommandLine="\""+loader+
            "\" TheIsland_WP?listen -Port=7777"};
        Check(LocalRunningAsaDiscovery.Classify(new[]{spoof},new[]{original})
              .Single().MatchState=="unverified",
            "executable mismatch in a command line denies profile match");
        var importedButNotAdopted=new LocalMap{
            Id=Guid.NewGuid(),Name="Kova historical loader not adopted",
            HistoricalMapKey="TheIsland_WP",HistoricalStoreProtected=true,
            ExecutablePath=server,InstallationPath=original.InstallationPath,
            StartWithAsaApi=false,ApiLoaderPath=""
        };
        var passive=LocalRunningAsaDiscovery.Classify(
            new[]{sample[0]},new[]{importedButNotAdopted}).Single();
        Check(passive.MatchState=="profileCandidate"&&!passive.Controllable&&
              !passive.StoreWriteAuthorized,
            "original Kova API loader is discovered even before Manager API opt-in, without ownership");

        var duplicate=new LocalMap{
            Id=Guid.NewGuid(),Name="Duplicate synthetic profile",
            HistoricalMapKey="TheIsland_WP",HistoricalStoreProtected=true,
            ExecutablePath=server,InstallationPath=original.InstallationPath,
            StartWithAsaApi=true,ApiLoaderPath=loader
        };
        Check(LocalRunningAsaDiscovery.Classify(sample,new[]{original,duplicate})
              .Where(x=>x.Pid is 1001 or 1002)
              .All(x=>x.MatchState=="ambiguous"&&x.ProfileMapId==null),
            "duplicate Manager identity fails closed rather than selecting one owner");
        var wrongParent=sample[1] with{ParentPid=9999};
        Check(!LocalRunningAsaDiscovery.Classify(new[]{sample[0],wrongParent},
              new[]{original}).Last().LoaderParentObserved,
            "unrelated parent PID cannot imply loader ancestry");
        var pidCollision=sample[1] with{ParentPid=1002};
        Check(!LocalRunningAsaDiscovery.Classify(new[]{sample[0],pidCollision},
              new[]{original}).Last().LoaderParentObserved,
            "self-parent never proves loader ancestry");
    }
}
