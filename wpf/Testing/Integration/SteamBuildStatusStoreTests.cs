using System;
using System.IO;
using UntamedManager.Core;

public static class SteamBuildStatusStoreTests
{
    private static void Check(bool ok,string message)
    {
        if(!ok)throw new InvalidOperationException("STEAM BUILD STATUS: "+message);
        Console.WriteLine("PASS Steam status "+message);
    }
    public static void Run(string root)
    {
        var manager=Path.Combine(root,"steam-status-store-"+Guid.NewGuid().ToString("N"));
        var map=new LocalMap{Id=Guid.NewGuid(),Name="The Island",
            InstallationPath=Path.Combine(root,"status-asa-"+Guid.NewGuid().ToString("N"))};
        var other=new LocalMap{Id=Guid.NewGuid(),Name="Aberration",
            InstallationPath=Path.Combine(root,"status-different-asa")};
        var store=new SteamBuildStatusStore(manager);
        var initially=store.Read(map);
        Check(initially.State==AsaSteamBuildState.Unknown &&
            initially.LastCheckedUtc==null && initially.LastUpdatedUtc==null,
            "new map status is unknown rather than fabricated");
        var t0=DateTimeOffset.UtcNow.AddHours(-1);
        var initial=store.SaveCheck(map,AsaSteamBuildCatalog.Compare(
            map.InstallationPath,"public","1000","2000",t0));
        Check(initial.State==AsaSteamBuildState.UpdateAvailable &&
            initial.LastCheckedUtc==t0,"only a real newer remote Steam Build ID makes an update available");
        var failed=store.SaveCheck(map,AsaSteamBuildCatalog.Compare(
            map.InstallationPath,"public","1000",null,t0.AddMinutes(30)));
        Check(failed.State==AsaSteamBuildState.Unknown &&
            failed.LastCheckedUtc==t0,
            "inconclusive remote check preserves last successful observation timestamp");
        var stale=store.SaveCheck(map,AsaSteamBuildCatalog.Compare(
            map.InstallationPath,"public","3000","2000",t0.AddMinutes(50)));
        Check(stale.State==AsaSteamBuildState.Unknown &&
            stale.LastCheckedUtc==t0,
            "a newer local Steam build cannot be treated as out of date");
        var restore=new SteamBuildStatusStore(manager).Read(map);
        Check(restore.State==stale.State &&
            restore.LastCheckedUtc==t0 &&
            restore.LastUpdatedUtc==null,
            "saved map Build ID and timestamps survive a Manager restart");
        bool rejectedDifferentMap=false;
        try{store.Write(other,restore);}
        catch(InvalidDataException){rejectedDifferentMap=true;}
        Check(rejectedDifferentMap,
            "a map cannot overwrite a different map's observation journal");
    }
}
