using System;
using System.IO;
using UntamedManager.Core;

public static class AsaInitialRconSettingsTests
{
    private static void Check(bool good,string message)
    {
        if(!good)throw new InvalidOperationException("ASA INITIAL RCON: "+message);
        Console.WriteLine("PASS ASA initial RCON: "+message);
    }

    public static void Run(string sandbox)
    {
        string root=Path.Combine(sandbox,"initial-rcon-"+Guid.NewGuid().ToString("N"));
        string binaries=Path.Combine(root,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binaries);
        File.WriteAllText(Path.Combine(binaries,"ArkAscendedServer.exe"),
            "Synthetic EXE placeholder; not a real dedicated server");
        string password="CI-Secret-Password-2026";
        var ini=AsaInitialRconSettings.CreateForNewServer(root,37015,password);
        string contents=File.ReadAllText(ini);
        Check(contents.Contains("[ServerSettings]") &&
              contents.Contains("RCONEnabled=True") &&
              contents.Contains("RCONPort=37015") &&
              contents.Contains("ServerAdminPassword="+password) &&
              !contents.Contains("ActiveMods") &&
              !File.Exists(Path.Combine(root,"ShooterGame","Saved","Config",
                  "WindowsServer","Game.ini")),
              "new ASA install bootstraps only RCON settings; no mods, maps or extra INI");
        string args=AsaSteamMapCatalog.LaunchArguments("The Island","Untamed Kingdom",7777,27015);
        Check(!args.Contains(password) && !args.Contains("ServerAdminPassword") &&
              !args.Contains("RCONEnabled=False") && args.Contains("-port=7777"),
              "fresh launch arguments do not leak admin password or disable INI RCON");

        bool existingRefused=false;
        try{AsaInitialRconSettings.CreateForNewServer(root,37016,"DIFFERENT-SECRET-2026");}
        catch(IOException){existingRefused=true;}
        Check(existingRefused && File.ReadAllText(ini)==contents,
              "existing GameUserSettings.ini is never overwritten");

        bool injectionRejected=false;
        try{AsaInitialRconSettings.ValidateAdminPassword("SomePassword123\r\nRCONPort=1234");}
        catch(InvalidDataException){injectionRejected=true;}
        Check(injectionRejected,"INI injection through admin password rejected");

        bool shortRejected=false;
        try{AsaInitialRconSettings.ValidateAdminPassword("123456");}
        catch(InvalidDataException){shortRejected=true;}
        Check(shortRejected,"weak/short initial RCON password rejected before SteamCMD launch");

        string imported=Path.Combine(sandbox,"existing-asa-import-"+Guid.NewGuid().ToString("N"));
        string dir=Path.Combine(imported,"ShooterGame","Saved","Config","WindowsServer");
        Directory.CreateDirectory(Path.Combine(imported,"ShooterGame","Binaries","Win64"));
        File.WriteAllText(Path.Combine(imported,"ShooterGame","Binaries",
            "Win64","ArkAscendedServer.exe"),"synthetic");
        Directory.CreateDirectory(dir);
        string existing=Path.Combine(dir,"GameUserSettings.ini");
        var original="[ServerSettings]\r\nTamingSpeedMultiplier=5.0\r\nRCONEnabled=False\r\n";
        File.WriteAllText(existing,original);
        bool preservation=true;
        try{AsaInitialRconSettings.CreateForNewServer(imported,37015,password);}
        catch(IOException){preservation=File.ReadAllText(existing)==original;}
        Check(preservation && File.ReadAllText(existing)==original,
            "imported ASA INI retains original settings and bytes untouched");
    }
}
