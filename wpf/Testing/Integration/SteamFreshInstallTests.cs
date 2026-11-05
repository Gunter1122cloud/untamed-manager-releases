using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class SteamFreshInstallTests
{
    private static void Check(bool ok,string message)
    {
        if(!ok)throw new Exception("STEAM FRESH INSTALL: "+message);
        Console.WriteLine("PASS steam fresh install: "+message);
    }
    public static async Task Run(string root,string? fakeSteam)
    {
        if(string.IsNullOrWhiteSpace(fakeSteam)||!File.Exists(fakeSteam))
            throw new InvalidOperationException("CI FakeSteamCMD.exe must be published");
        // Keep SteamCMD outside the map installation tree, like the
        // actual Manager defaults (SteamCMD/ vs ASA-Servers/<map ID>).
        string cmd=Path.Combine(root,"steam-tools","steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(cmd)!);
        if(!File.Exists(cmd))File.WriteAllText(cmd,"synthetic steam command placeholder");
        var mapId=Guid.NewGuid();
        string destination=Path.Combine(root,"steam-new-map-"+Guid.NewGuid().ToString("N"));
        var req=new SteamFreshInstallRequest(mapId,"The Island","",
            destination,cmd,false,MinimumFreeBytes:5L*1024*1024*1024);
        var receipt=await SteamFreshInstall.RunFakeFixtureAsync(
            req,Array.Empty<LocalMap>(),fakeSteam);
        Check(receipt.MapId==mapId&&receipt.MapLevel=="TheIsland_WP" &&
            receipt.BuildId=="123456" &&
            receipt.ServerExecutable.EndsWith("ArkAscendedServer.exe",
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(receipt.ServerExecutable),
            "fresh ASA download accepts an empty session name and verifies the EXE/build before port setup");
        Check(!File.Exists(Path.Combine(destination,"ShooterGame","Saved","SavedArks","TheIsland_WP.ark")) &&
            !Directory.Exists(Path.Combine(destination,"ShooterGame","Saved")),
            "new Steam install does not invent/overwrite saves, INIs or Store state");

        bool refusedExisting=false;
        try{await SteamFreshInstall.RunFakeFixtureAsync(
            req,Array.Empty<LocalMap>(),fakeSteam);}
        catch(InvalidOperationException){refusedExisting=true;}
        Check(refusedExisting,"completed installation cannot be reinstalled using new-server mode");

        var occupied=Path.Combine(root,"occupied-new-install");
        Directory.CreateDirectory(occupied);
        var valuable=Path.Combine(occupied,"KEEP-ME.txt");
        File.WriteAllText(valuable,"operator's existing important file");
        bool refusedUnknown=false;
        try{await SteamFreshInstall.RunFakeFixtureAsync(
            req with {MapId=Guid.NewGuid(),InstallRoot=occupied},
            Array.Empty<LocalMap>(),fakeSteam);}
        catch(IOException){refusedUnknown=true;}
        Check(refusedUnknown&&File.ReadAllText(valuable)=="operator's existing important file",
            "unknown nonempty installation is never overwritten or imported as new");

        var owned=new LocalMap{Id=Guid.NewGuid(),Name="existing map",InstallationPath=occupied};
        bool refusedShared=false;
        try{await SteamFreshInstall.RunFakeFixtureAsync(
            req with {MapId=Guid.NewGuid(),InstallRoot=occupied},
            new[]{owned},fakeSteam);}
        catch(InvalidOperationException){refusedShared=true;}
        Check(refusedShared,"existing shared installation is not mutated by fresh installer");

        bool refusedRemote=false;
        try{await SteamFreshInstall.RunFakeFixtureAsync(req with {
            MapId=Guid.NewGuid(),InstallRoot=@"\\hostname\asa\game"
        },Array.Empty<LocalMap>(),fakeSteam);}
        catch(UnauthorizedAccessException){refusedRemote=true;}
        Check(refusedRemote,"fresh installer never runs on a remote/UNC location");
        // A failed real child launch leaves a durable installation journal.
        // After a Manager restart, retry is only possible with the original
        // map GUID and explicit human approval; no blind re-run.
        string incompleteRoot=Path.Combine(root,"interrupted-install-"+Guid.NewGuid().ToString("N"));
        var pending=new SteamFreshInstallRequest(Guid.NewGuid(),"The Island",
            "Untamed Island",incompleteRoot,cmd,false,
            MinimumFreeBytes:5L*1024*1024*1024);
        bool interrupted=false;
        try
        {
            await SteamFreshInstall.RunFakeFixtureAsync(pending,
                Array.Empty<LocalMap>(),Path.Combine(root,"FakeSteamCMD.exe"));
        }
        catch(Exception ex) when(ex is IOException or
            System.ComponentModel.Win32Exception){interrupted=true;}
        var pendingJournal=SteamFreshInstall.InspectIncomplete(incompleteRoot);
        Check(interrupted && pendingJournal!=null &&
            pendingJournal.MapId==pending.MapId &&
            pendingJournal.MapName=="The Island" &&
            pendingJournal.Phase=="mutating",
            "failed installation records recoverable original map identity");
        bool wrongIdentityBlocked=false;
        try
        {
            await SteamFreshInstall.RunFakeFixtureAsync(
                pending with {MapId=Guid.NewGuid(),ExplicitRetry=true},
                Array.Empty<LocalMap>(),fakeSteam);
        }
        catch(InvalidOperationException){wrongIdentityBlocked=true;}
        Check(wrongIdentityBlocked,
            "manual restart refuses an unexpected map ID in the incomplete journal");
        bool wrongMapBlocked=false;
        try
        {
            await SteamFreshInstall.RunFakeFixtureAsync(
                pending with {MapName="Astraeos",ExplicitRetry=true},
                Array.Empty<LocalMap>(),fakeSteam);
        }
        catch(InvalidOperationException){wrongMapBlocked=true;}
        Check(wrongMapBlocked,
            "interrupted ASA install cannot silently change to a different map");
        var retryReceipt=await SteamFreshInstall.RunFakeFixtureAsync(
            pending with {ExplicitRetry=true},Array.Empty<LocalMap>(),fakeSteam);
        Check(retryReceipt.MapId==pending.MapId &&
            SteamFreshInstall.InspectIncomplete(incompleteRoot)==null,
            "explicit retry with original identity completes once and closes journal");
        foreach(var entry in new[]{
            ("The Island","TheIsland_WP"),("The Center","TheCenter_WP"),
            ("Scorched Earth","ScorchedEarth_WP"),("Ragnarok","Ragnarok_WP"),
            ("Aberration","Aberration_WP"),("Extinction","Extinction_WP"),
            ("Valguero","Valguero_WP"),("Genesis Part 1","Genesis_WP"),
            ("Astraeos","Astraeos_WP"),("Lost Colony","LostColony_WP")
        })
            Check(AsaSteamMapCatalog.LevelFor(entry.Item1)==entry.Item2,
                "official ASA level token "+entry.Item1);

        var args=AsaSteamMapCatalog.LaunchArguments("Astraeos","Untamed Kingdom",7777,27015);
        Check(args.StartsWith("Astraeos_WP?listen?SessionName=\"Untamed Kingdom\"") &&
            args.Contains("-port=7777")&&args.Contains("-QueryPort=27015") &&
            !args.Contains("RCONEnabled=False")&&!args.Contains("ServerAdminPassword"),
            "generated launch args use actual map and do not expose RCON admin credentials");

        bool rejectedInjection=false;
        try{AsaSteamMapCatalog.LaunchArguments("Astraeos","test?ServerAdminPassword=x",7777,27015);}
        catch(InvalidDataException){rejectedInjection=true;}
        Check(rejectedInjection,"launch argument injection rejected");
    }
}
