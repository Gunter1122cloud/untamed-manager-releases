using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class SteamStoppedMapUpdaterTests
{
    private static void Check(bool result,string message)
    {
        if(!result)throw new InvalidOperationException("STOPPED ASA STEAM UPDATE: "+message);
        Console.WriteLine("PASS stopped Steam update: "+message);
    }
    private static readonly Func<LocalMap,ProcessCheck> IsStopped=map=>
        new ProcessCheck(false,"CI confirmed stopped process",null,true);
    internal static void Prepare(string install,bool withSave=true)
    {
        string exe=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe,"CI placeholder executable only");
        string apps=Path.Combine(install,"steamapps");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps,"appmanifest_2430930.acf"),
            "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123456\"\n}\n");
        if(withSave)
        {
            string saved=Path.Combine(install,"ShooterGame","Saved",
                "SavedArks","TheIsland_WP");
            Directory.CreateDirectory(saved);
            File.WriteAllBytes(Path.Combine(saved,"TheIsland_WP.ark"),
                new byte[]{1,2,3,4,5,6,7,8});
            string ini=Path.Combine(install,"ShooterGame","Saved",
                "Config","WindowsServer");
            Directory.CreateDirectory(ini);
            File.WriteAllText(Path.Combine(ini,"Game.ini"),
                "[/Script/ShooterGame.ShooterGameMode]\nXPMultiplier=2.0\n");
            File.WriteAllText(Path.Combine(ini,"GameUserSettings.ini"),
                "[ServerSettings]\nRCONPort=37015\n");
            string plugin=Path.Combine(install,"ShooterGame","Binaries",
                "Win64","ArkApi","Plugins","Inventory");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin,"config.json"),
                "{\"enabled\":true}");
        }
    }
    public static async Task Run(string sandbox,string? fakeSteam)
    {
        if(string.IsNullOrWhiteSpace(fakeSteam)||!File.Exists(fakeSteam))
            throw new InvalidDataException("Synthetic Windows FakeSteamCMD.exe is required");
        string test=Path.Combine(sandbox,"stopped-steam-"+Guid.NewGuid().ToString("N"));
        string fakeSteamPath=Path.Combine(test,"steamcmd","steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeSteamPath)!);
        File.WriteAllText(fakeSteamPath,"SteamCMD placeholder, never launched");
        string install=Path.Combine(test,"asa-island");
        Prepare(install);
        string beforeWorld=File.ReadAllText(Path.Combine(install,"ShooterGame",
            "Saved","SavedArks","TheIsland_WP","TheIsland_WP.ark"));
        string beforeIni=File.ReadAllText(Path.Combine(install,"ShooterGame",
            "Saved","Config","WindowsServer","Game.ini"));
        string beforePlugin=File.ReadAllText(Path.Combine(install,"ShooterGame",
            "Binaries","Win64","ArkApi","Plugins","Inventory","config.json"));

        await using var manager=new ClusterService("CI-stopped-update",test,IsStopped);
        var map=new LocalMap{
            Name="The Island",InstallationPath=install,ExecutablePath=Path.Combine(
                install,"ShooterGame","Binaries","Win64","ArkAscendedServer.exe"),
            GamePort=7777,QueryPort=27015,RconPort=37015
        };
        manager.AddMap(map);
        string storeSentinel=Path.Combine(test,"kova-store-v1-external-stays-unchanged.txt");
        File.WriteAllText(storeSentinel,"NO_STORE_V1_WRITES");
        var receipt=await SteamStoppedMapUpdater.RunFakeFixtureAsync(manager,map,
            fakeSteamPath,fakeSteam,1024L*1024*1024,TimeSpan.FromMinutes(2));

        Check(receipt.MapId==map.Id && receipt.OldBuild=="123456" &&
            receipt.NewBuild=="123457" && receipt.ProtectedFilesIntact,
            "real updater pipeline executes harmless Steam child and verifies changed build");
        Check(File.ReadAllText(Path.Combine(install,"ShooterGame",
            "Saved","SavedArks","TheIsland_WP","TheIsland_WP.ark"))==beforeWorld &&
              File.ReadAllText(Path.Combine(install,"ShooterGame",
                  "Saved","Config","WindowsServer","Game.ini"))==beforeIni &&
              File.ReadAllText(Path.Combine(install,"ShooterGame","Binaries",
                  "Win64","ArkApi","Plugins","Inventory","config.json"))==beforePlugin &&
              File.ReadAllText(storeSentinel)=="NO_STORE_V1_WRITES",
            "world, players, INI, ArkApi plugins and external Store-v1 sentinel unchanged");
        Check(Directory.Exists(receipt.BackupDirectory) &&
            File.Exists(Path.Combine(receipt.BackupDirectory,"complete")) &&
            File.Exists(Path.Combine(receipt.BackupDirectory,"manifest.json")) &&
            new SteamBuildStatusStore(manager.Store.Root).Read(map).LastUpdatedUtc!=null,
            "verified pre-update backup and durable last successful update receipt exist");
        Check(manager.Check(map).ConfirmedStopped &&
            new SteamSerialMaintenanceQueue(manager.Store.Root,
                manager.Config.LocalHostId).Observe()?.Phase==SteamQueuePhase.Completed,
            "originally stopped server remains stopped and local queue completes");

        // A map without any SavedArks/SaveGames data does not silently
        // mutate the Steam manifest: the protected backup gate blocks first.
        var noSave=Path.Combine(test,"asa-nosave");
        Prepare(noSave,false);
        var other=new LocalMap{Name="No save",
            InstallationPath=noSave,
            ExecutablePath=Path.Combine(noSave,"ShooterGame","Binaries",
                "Win64","ArkAscendedServer.exe"),GamePort=7797,QueryPort=27025,
            RconPort=37035};
        manager.AddMap(other);
        bool blockedNoSave=false;
        try
        {
            await SteamStoppedMapUpdater.RunFakeFixtureAsync(manager,other,
                fakeSteamPath,fakeSteam,1024L*1024*1024,TimeSpan.FromMinutes(2));
        }
        catch(InvalidDataException){blockedNoSave=true;}
        Check(blockedNoSave && AsaSteamBuildCatalog.InstalledBuild(noSave)=="123456",
            "missing verified world save prevents SteamCMD file mutation");

        // A remotely owned (not registered) map must not be sent through this
        // Manager's queue, even if the path itself is readable.
        var remote=new LocalMap{Name="Remote Map",InstallationPath=install,
            ExecutablePath=map.ExecutablePath};
        bool deniedRemote=false;
        try{await SteamStoppedMapUpdater.RunFakeFixtureAsync(manager,remote,
            fakeSteamPath,fakeSteam,1024L*1024*1024,TimeSpan.FromMinutes(2));}
        catch(UnauthorizedAccessException){deniedRemote=true;}
        Check(deniedRemote,
            "a local Manager cannot update a remote/unregistered map");
    }
}
