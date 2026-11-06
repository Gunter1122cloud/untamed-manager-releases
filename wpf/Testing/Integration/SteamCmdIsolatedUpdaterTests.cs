using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class SteamCmdIsolatedUpdaterTests
{
    private static void Check(bool yes,string description)
    {
        if(!yes)throw new Exception("T11 isolated SteamCMD failure: "+description);
        Console.WriteLine("PASS steamcmd-isolated "+description);
    }
    private static bool IsRefused(Func<Task> action)
    {
        try{action().GetAwaiter().GetResult();return false;}
        catch(Exception ex) when(ex is InvalidDataException or UnauthorizedAccessException
            or IOException or InvalidOperationException or OperationCanceledException)
        {return true;}
    }
    public static async Task Run(string root,string? fixture)
    {
        if(string.IsNullOrWhiteSpace(fixture)||!File.Exists(fixture))
            throw new InvalidDataException("CI must supply a compiled FakeSteamCMD.exe");
        string placeholder=Path.Combine(root,"steamcmd.exe");
        if(!File.Exists(placeholder))File.WriteAllText(placeholder,"Synthetic SteamCMD preflight file");

        (string Install,string Profile,LocalMap Map,Func<LocalMap,ProcessCheck> Inspect)
            Setup(string name)
        {
            string baseRoot=Path.Combine(root,"t11-isolated-steam",name);
            string install=Path.Combine(baseRoot,"ASA");
            string profile=Path.Combine(baseRoot,"Manager");
            Directory.CreateDirectory(profile);
            string bin=Path.Combine(install,"ShooterGame","Binaries","Win64");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin,"ArkAscendedServer.exe"),"CI fixture only, never run");
            string save=Path.Combine(install,"ShooterGame","Saved");
            Directory.CreateDirectory(Path.Combine(save,"SavedArks"));
            File.WriteAllText(Path.Combine(save,"SavedArks","TheIsland_WP.ark"),"original world");
            string ini=Path.Combine(save,"Config","WindowsServer");
            Directory.CreateDirectory(ini);
            File.WriteAllText(Path.Combine(ini,"Game.ini"),"[ServerSettings]\nXPMultiplier=2");
            File.WriteAllText(Path.Combine(ini,"GameUserSettings.ini"),"[SessionSettings]\nTest=1");
            string plugin=Path.Combine(bin,"ArkApi","Plugins","SamplePlugin");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin,"data.db"),"protected plugin state");
            string steamApps=Path.Combine(install,"steamapps");
            Directory.CreateDirectory(steamApps);
            File.WriteAllText(Path.Combine(steamApps,"appmanifest_2430930.acf"),
                "\"buildid\" \"12345\"\n");
            var map=new LocalMap{Id=Guid.NewGuid(),Name="Isolated T11 test map",
                InstallationPath=install,ExecutablePath=Path.Combine(bin,"ArkAscendedServer.exe")};
            return(install,profile,map,_=>new ProcessCheck(false,"confirmed CI stopped",null,true));
        }

        var ok=Setup("ok");
        var status=await SteamCmdIsolatedUpdater.RunFakeFixtureAsync(
            placeholder,fixture,ok.Install,ok.Profile,new[]{ok.Map},
            ok.Inspect,1_048_576,TimeSpan.FromSeconds(8));
        Check(status.SteamAcknowledged&&status.ProtectedFilesPreserved&&
            status.ExitCode==0 && !status.ProductionUpdateCertified &&
            status.BuildBefore=="12345"&&status.BuildAfter=="12345",
            "same guarded OS process path invokes harmless fixture with exact app ID and restores no real ASA data");
        Check(File.ReadAllText(Path.Combine(ok.Install,"ShooterGame","Saved",
            "SavedArks","TheIsland_WP.ark"))=="original world" &&
            File.ReadAllText(Path.Combine(ok.Install,"ShooterGame","Binaries",
            "Win64","ArkApi","Plugins","SamplePlugin","data.db"))==
            "protected plugin state",
            "Game.ini, SavedArks world and plugin persistence remain byte-for-byte intact");
        var journal=Directory.GetFiles(Path.Combine(ok.Profile,"steam-lab-results"),"*.json");
        Check(journal.Length==1&&!File.ReadAllText(journal[0]).Contains(
            "FAKE_STEAM_",StringComparison.Ordinal),
            "successful isolated update writes one redacted durable receipt");
        Check(File.Exists(Path.Combine(ok.Install,"synthetic-steam-success.marker")),
            "the FakeSteamCMD process actually ran, not a simulated update preview");

        var stoppedWrong=Setup("wrong-owner");
        var foreign=new LocalMap{Id=Guid.NewGuid(),Name="Foreign",InstallationPath=ok.Install};
        Check(IsRefused(()=>SteamCmdIsolatedUpdater.RunFakeFixtureAsync(
            placeholder,fixture,stoppedWrong.Install,stoppedWrong.Profile,
            new[]{stoppedWrong.Map,foreign},stoppedWrong.Inspect,1024,TimeSpan.FromSeconds(8))),
            "mixed install roots cannot be used to update other maps");

        var active=Setup("active");
        Func<LocalMap,ProcessCheck> running=_=>
            new ProcessCheck(true,"running",new ProcessIdentity {
                Pid=9911,StartedUtc=DateTimeOffset.UtcNow,
                ExecutablePath=active.Map.ExecutablePath,Arguments=""
            });
        Check(IsRefused(()=>SteamCmdIsolatedUpdater.RunFakeFixtureAsync(
            placeholder,fixture,active.Install,active.Profile,new[]{active.Map},
            running,1024,TimeSpan.FromSeconds(8))) &&
            !File.Exists(Path.Combine(active.Install,"synthetic-steam-success.marker")),
            "running ASA fixture blocks child process launch");

        var fail=Setup("nonzero");
        Environment.SetEnvironmentVariable("FAKE_STEAM_FAIL","1");
        try
        {
            Check(IsRefused(()=>SteamCmdIsolatedUpdater.RunFakeFixtureAsync(
                placeholder,fixture,fail.Install,fail.Profile,new[]{fail.Map},
                fail.Inspect,1024,TimeSpan.FromSeconds(8))),
                "nonzero SteamCMD process exit cannot publish success");
        }
        finally{Environment.SetEnvironmentVariable("FAKE_STEAM_FAIL",null);}
        Check(IsRefused(()=>SteamCmdIsolatedUpdater.RunFakeFixtureAsync(
            placeholder,fixture,fail.Install,fail.Profile,new[]{fail.Map},
            fail.Inspect,1024,TimeSpan.FromSeconds(8))),
            "failed mutation retains operation lock record and cannot auto retry");

        var changed=Setup("changed-ini");
        Environment.SetEnvironmentVariable("FAKE_STEAM_TOUCH_INI","1");
        try
        {
            Check(IsRefused(()=>SteamCmdIsolatedUpdater.RunFakeFixtureAsync(
                placeholder,fixture,changed.Install,changed.Profile,new[]{changed.Map},
                changed.Inspect,1024,TimeSpan.FromSeconds(8))),
                "modified Game.ini prevents update from being marked successful");
        }
        finally{Environment.SetEnvironmentVariable("FAKE_STEAM_TOUCH_INI",null);}

        var cancelled=Setup("cancelled");
        using var cancel=new CancellationTokenSource();
        cancel.Cancel();
        Check(IsRefused(()=>SteamCmdIsolatedUpdater.RunFakeFixtureAsync(
            placeholder,fixture,cancelled.Install,cancelled.Profile,
            new[]{cancelled.Map},cancelled.Inspect,1024,TimeSpan.FromSeconds(8),
            cancel.Token)) &&
            !File.Exists(Path.Combine(cancelled.Install,"synthetic-steam-success.marker")),
            "pre-cancelled update never executes a child process");

        var timeout=Setup("timeout");
        Environment.SetEnvironmentVariable("FAKE_STEAM_TIMEOUT","1");
        try
        {
            Check(IsRefused(()=>SteamCmdIsolatedUpdater.RunFakeFixtureAsync(
                placeholder,fixture,timeout.Install,timeout.Profile,new[]{timeout.Map},
                timeout.Inspect,1024,TimeSpan.FromSeconds(2.1))),
                "timed-out child process cannot produce a successful update");
        }
        finally{Environment.SetEnvironmentVariable("FAKE_STEAM_TIMEOUT",null);}

        var live=ReleaseCapabilityGate.Check(ManagerOperation.LiveSteamUpdate,true,
            confirmedStopped:true);
        Check(!live.Allowed,
            "isolated SteamCMD adapter never unlocks production update gate");
        Check(File.ReadAllText(placeholder)=="offline synthetic steamcmd file" ||
              File.ReadAllText(placeholder)=="Synthetic SteamCMD preflight file",
            "operator-provided SteamCMD test placeholder remains unchanged");
    }
}
