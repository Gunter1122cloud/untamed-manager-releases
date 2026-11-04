using System;
using System.Linq;
using UntamedManager.Core;

public static class DemoWorkspaceTests
{
    private static void Check(bool ok,string description)
    {
        if(!ok)throw new Exception("DEMO workspace: "+description);
        Console.WriteLine("PASS demo "+description);
    }
    public static void Run()
    {
        var demo=new DemoWorkspace();
        Check(demo.Isolated && demo.Hosts.Count==3 && demo.Maps.Count==10,
            "offline DEMO is constructed entirely in memory with 3 hosts and 10 maps");
        var map=demo.Maps.First(x=>x.Name=="The Island");
        demo.SetRunning(map.Id,false);
        Check(!map.Running && map.Players==0,"simulated stop updates only DEMO state");
        demo.SetRunning(map.Id,true);
        Check(map.Running && map.Players>0,"simulated start restores synthetic players");
        Check(demo.Rcon(map.Id,"ListPlayers").Contains("[DEMO]"),
            "RCON response is explicitly synthetic");
        demo.SetMod(map.Id,"123456",DemoModMode.Passive);
        Check(map.Mods.Single(x=>x.ProjectId=="123456").Mode==DemoModMode.Passive,
            "mod mode changes only in-memory data");
        demo.TogglePlugin(map.Id,"ArkShop");
        Check(!map.Plugins.Single(x=>x.Name=="ArkShop").Enabled,
            "plugin toggle is a simulation");
        demo.SetIni(map.Id,"XPMultiplier","4.0");
        Check(map.Ini["XPMultiplier"]=="4.0","INI preview is editable without writing config files");
        var saved=demo.Backup(map.Id);
        Check(saved.StartsWith("DEMO-") && map.Backups.Contains(saved),
            "backup entries cannot be confused with genuine files");
        demo.Restart(map.Id);
        Check(demo.Events.Any(x=>x.Contains("SaveWorld")),"restart is recorded as synthetic operation");
        demo.SetHostOnline(map.Host,false);
        Check(demo.Status(map).Contains("OFFLINE"),"host outage visibly affects map status");
        bool blocked=false;
        try{demo.Rcon(map.Id,"DestroyWildDinos");}
        catch(InvalidOperationException){blocked=true;}
        Check(blocked,"arbitrary RCON commands cannot escape the demo");
        var independent=new DemoWorkspace();
        Check(independent.Maps.First(x=>x.Name=="The Island").Ini["XPMultiplier"]=="2.0",
            "closing the demo discards all changes without persistence");
    }
}
