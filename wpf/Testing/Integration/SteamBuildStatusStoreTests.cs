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
        // A SteamCMD exit alone must not set LastUpdatedUtc. Only a fully
        // verified save/backup/build/restart receipt may advance it.
        string appFolder=Path.Combine(map.InstallationPath,"steamapps");
        Directory.CreateDirectory(appFolder);
        File.WriteAllText(Path.Combine(appFolder,"appmanifest_2430930.acf"),
            "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"3001\"\n}\n");
        var success=new SteamMaintenanceReceipt(map.Id,"3000","3001",
            true,true,DateTimeOffset.UtcNow,new string('A',64));
        var updated=store.RecordVerifiedUpdate(map,success);
        Check(updated.LastUpdatedUtc==success.FinishedUtc &&
            updated.LocalBuild=="3001",
            "verified completed restart records successful Steam update");
        bool deniedUnready=false;
        try{store.RecordVerifiedUpdate(map,success with {Restarted=false});}
        catch(InvalidDataException){deniedUnready=true;}
        Check(deniedUnready &&
            store.Read(map).LastUpdatedUtc==success.FinishedUtc,
            "failed restart cannot advance last successful update");
        bool deniedFalseBuild=false;
        try{store.RecordVerifiedUpdate(map,success with {NewBuild="3999"});}
        catch(InvalidDataException){deniedFalseBuild=true;}
        Check(deniedFalseBuild &&
            store.Read(map).LocalBuild=="3001",
            "unverified manifest Build ID cannot masquerade as a successful update");
        bool rejectedDifferentMap=false;
        try{store.Write(other,restore);}
        catch(InvalidDataException){rejectedDifferentMap=true;}
        Check(rejectedDifferentMap,
            "a map cannot overwrite a different map's observation journal");
    }
}
