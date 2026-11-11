using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Windows TEMP-only regression: a legacy Enabled=true schedule cannot trigger
// SteamCMD after the operator changes the policy to manual-only updates.
internal static class ManualOnlySteamScheduleTests
{
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(
            "MANUAL STEAM POLICY REGRESSION FAILED: "+reason);
        Console.WriteLine("PASS manual-only SteamCMD: "+reason);
    }

    public static async Task Run(string testRoot)
    {
        await using var cluster=new ClusterService("manual-only-steam-fixture",
            Path.Combine(testRoot,"manual-only-steam-profile"),
            _=>new ProcessCheck(false,"Synthetic confirmed stopped",null,true));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="Manual-only fixture",
            InstallationPath=Path.Combine(testRoot,"manual-only-steam-install"),
            ExecutablePath=Path.Combine(testRoot,"manual-only-steam-install",
                "ShooterGame","Binaries","Win64","ArkAscendedServer.exe")
        };
        cluster.AddMap(map);
        string journal=Path.Combine(cluster.Store.Root,"scheduled-stopped-steam-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        string historical=JsonSerializer.Serialize(new{
            Schedules=new[]{new ScheduledStoppedSteamUpdate(
                map.Id,60,new string('A',64),true,
                DateTimeOffset.UtcNow.AddHours(-3),null,"scheduled",null,null)}
        });
        File.WriteAllText(journal,historical);
        int updates=0;
        using var supervisor=new DesktopScheduledStoppedSteamSupervisor(cluster,
            (_,_)=>{
                updates++;
                return Task.FromException<SteamStoppedMapUpdater.Receipt>(
                    new InvalidOperationException("Retired callback must never execute"));
            });
        supervisor.Start();
        Check(supervisor.RuntimeState=="manual-only",
            "startup never starts the old periodic SteamCMD timer");
        var snapshots=supervisor.Snapshot();
        Check(snapshots.Length==1&&!snapshots[0].Enabled,
            "old Enabled=true JSON schedule is exposed as disabled");
        await supervisor.TickOnceAsync(DateTimeOffset.UtcNow.AddDays(2));
        Check(updates==0 && File.ReadAllText(journal)==historical,
            "even directly invoked legacy tick cannot mutate ASA or legacy JSON");

        bool denied=false;
        try{supervisor.Configure(map.Id,60);}
        catch(InvalidOperationException e){
            denied=e.Message=="UM-STEAM-SCHEDULE-MANUAL-ONLY";
        }
        Check(denied,"native schedule configuration refuses automatic updates");
        var bridge=new DesktopScheduledStoppedSteamBridge(cluster,supervisor);
        using var deniedRpc=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(new{
                id=Guid.NewGuid(),method="steam.scheduleSet",
                mapId=map.Id,confirm=true,
                typedApproval="AUTOMATIKUS LEÁLLÍTOTT FRISSÍTÉS",intervalMinutes=60
            })));
        Check(!deniedRpc.RootElement.GetProperty("ok").GetBoolean() &&
            deniedRpc.RootElement.GetProperty("error").GetString()==
                "UM-STEAM-SCHEDULE-MANUAL-ONLY",
            "WebView2 bridge cannot re-enable a retired schedule");
        using var status=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(new{id=Guid.NewGuid(),method="steam.scheduleStatus"})));
        var info=status.RootElement.GetProperty("data");
        Check(info.GetProperty("runnerState").GetString()=="manual-only" &&
            !info.GetProperty("schedules")[0].GetProperty("enabled").GetBoolean(),
            "UI gets authoritative manual-only runtime and disabled schedule");
        Check(updates==0&&File.ReadAllText(journal)==historical,
            "all synthetic tests preserve existing schedule bytes and never run update");
    }
}
