using System;
using UntamedManager.Core;

public static class LocalHostResourceTests
{
    private static void Check(bool ok,string description)
    {
        if(!ok)throw new Exception("Host resources: "+description);
        Console.WriteLine("PASS resources "+description);
    }
    public static void Run()
    {
        var moment=new DateTimeOffset(2026,11,4,19,0,0,TimeSpan.Zero);
        var valid=new LocalHostResourceSnapshot(25,2048*1024UL,8192*1024UL,moment);
        var message=valid.Display("LOCAL-TEST");
        Check(message.Contains("CPU: 25%")&&message.Contains("RAM: 2.0/8.0 GiB"),
            "grounded CPU and RAM figures use actual observation values");
        var disk=new LocalHostResourceSnapshot(25,2048*1024UL,8192*1024UL,moment,6.5,16.0);
        Check(disk.Display("LOCAL-TEST").Contains("Manager-lemez: 6.5/16.0 GiB"),
            "disk measurement explicitly names the Manager profile drive");
        var missing=new LocalHostResourceSnapshot(null,null,null,moment);
        Check(missing.Display("LOCAL-TEST").Contains("CPU: nem mérhető") &&
            missing.Display("LOCAL-TEST").Contains("RAM: nem mérhető"),
            "missing WMI measurements are never represented as zero or estimated usage");
    }
}
