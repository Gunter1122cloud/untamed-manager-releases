using System;
using System.IO;
using UntamedDesktop;

internal static class DesktopLoginStartupTests
{
    private static void Check(bool condition,string description)
    {
        if(!condition)throw new InvalidOperationException("LOGIN STARTUP: "+description);
        Console.WriteLine("PASS login startup: "+description);
    }
    public static void Run()
    {
        string path=@"C:\Synthetic Obsidian\UntamedDesktop.exe";
        string command=DesktopLoginStartup.CommandFor(path);
        Check(command=="\""+path+"\"","quotes full executable path; no injected arguments");
        Check(DesktopLoginStartup.Plan(null,command,true)==DesktopLoginStartup.Action.WriteOwn,
            "opt-in writes exactly one own value");
        Check(DesktopLoginStartup.Plan(command,command,true)==DesktopLoginStartup.Action.NoChange,
            "repeated opt-in is idempotent");
        Check(DesktopLoginStartup.Plan(command,command,false)==DesktopLoginStartup.Action.DeleteOwn,
            "opt-out deletes only its own value");
        Check(DesktopLoginStartup.Plan(null,command,false)==DesktopLoginStartup.Action.NoChange,
            "already disabled does not write");
        foreach(bool enable in new[]{false,true})
        {
            bool refused=false;
            try{DesktopLoginStartup.Plan("\"C:\\Other\\UntamedDesktop.exe\"",command,enable);}
            catch(InvalidOperationException){refused=true;}
            Check(refused,"different installation cannot be overwritten or deleted");
        }
        bool invalid=false;
        try{DesktopLoginStartup.CommandFor(@"\\server\share\UntamedDesktop.exe");}
        catch(InvalidDataException){invalid=true;}
        Check(invalid,"network executable refused");
        var asa=new LocalRunningAsaDiscovery.Observation(101,0,null,"asaServer","A","unregistered",null,null,false);
        var loader=asa with {Pid=102,ProcessKind="asaApiLoader"};
        var fresh=new DesktopAutomaticAsaDiscoverySupervisor.State(DateTimeOffset.UtcNow,"ok",new[]{asa,asa,loader},true);
        Check(DesktopTrayStatus.Describe(fresh).EndsWith(": 1"),"fresh tray status counts distinct game PIDs, not Loader");
        Check(DesktopTrayStatus.Describe(fresh with {Fresh=false}).Contains("nem ismert"),
            "stale WMI data is unknown, never zero");
        // No real HKCU Run key is touched by CI.
    }
}
