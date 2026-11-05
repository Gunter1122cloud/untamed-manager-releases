using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class MapProfileAtomicityTests
{
    private static void Assert(bool check,string reason)
    {
        if(!check)throw new InvalidOperationException("MAP CONFIG ATOMICITY: "+reason);
        Console.WriteLine("PASS map config atomicity: "+reason);
    }

    public static async Task Run(string testRoot)
    {
        string folder=Path.Combine(testRoot,"map-config-CAS-fixture");
        Directory.CreateDirectory(folder);
        await using var current=new ClusterService("map-config-ci",folder);
        var original=new LocalMap {
            Name="The Island",GamePort=7777,QueryPort=27015,RconPort=37015,
            StoreTestRoot="synthetic-sandbox-only",
            LastProcess=new ProcessIdentity {
                Pid=12345,StartedUtc=DateTimeOffset.UtcNow,
                ExecutablePath="synthetic-fixture.exe",Arguments="-ci"
            }
        };
        current.AddMap(original);
        await using var stale=new ClusterService("map-config-ci",folder);
        var staleOriginal=stale.Config.LocalMaps.Single();
        Assert(staleOriginal.Name=="The Island","fixture instances read identical original map");

        current.AddMap(new LocalMap{Name="Extinction",GamePort=7817});
        bool rejectedAdd=false;
        try{stale.AddMap(new LocalMap{Name="Uncommitted",GamePort=7997});}
        catch(IOException){rejectedAdd=true;}
        Assert(rejectedAdd && stale.Config.LocalMaps.Count==1 &&
            stale.Config.LocalMaps[0]==staleOriginal,
            "stale AddMap rejects CAS and restores original list/object identity");

        bool rejectedRemove=false;
        try{stale.RemoveMap(staleOriginal.Id);}
        catch(IOException){rejectedRemove=true;}
        Assert(rejectedRemove && stale.Config.LocalMaps.Count==1 &&
            stale.Config.LocalMaps[0]==staleOriginal,
            "stale RemoveMap restores removed object to its original index");

        var rejectedEdit=JsonSerializer.Deserialize<LocalMap>(
            JsonSerializer.Serialize(staleOriginal))!;
        rejectedEdit.Name="Modified only in memory";
        bool rejectedReplace=false;
        try{stale.ReplaceMap(staleOriginal,rejectedEdit);}
        catch(IOException){rejectedReplace=true;}
        Assert(rejectedReplace &&
            ReferenceEquals(stale.Config.LocalMaps[0],staleOriginal) &&
            staleOriginal.Name=="The Island",
            "stale ReplaceMap restores existing object without modifying old state");

        var validEdit=JsonSerializer.Deserialize<LocalMap>(
            JsonSerializer.Serialize(original))!;
        validEdit.Name="The Island Updated";
        validEdit.WatchdogIntervalSeconds=45;
        current.ReplaceMap(original,validEdit);
        Assert(current.Config.LocalMaps[0]==validEdit &&
            validEdit.Id==original.Id &&
            validEdit.StoreTestRoot=="synthetic-sandbox-only" &&
            validEdit.LastProcess?.Pid==12345 &&
            validEdit.GamePort==7777,
            "successful staged edit preserves identity, process and Store test metadata");

        bool duplicateNameDenied=false;
        var duplicate=JsonSerializer.Deserialize<LocalMap>(
            JsonSerializer.Serialize(validEdit))!;
        duplicate.Name="Extinction";
        try{current.ReplaceMap(validEdit,duplicate);}
        catch(InvalidOperationException){duplicateNameDenied=true;}
        Assert(duplicateNameDenied &&
            ReferenceEquals(current.Config.LocalMaps[0],validEdit),
            "duplicate name cannot replace an existing map");

        current.RemoveMap(validEdit.Id);
        Assert(current.Config.LocalMaps.Count==1 &&
            current.Config.LocalMaps[0].Name=="Extinction",
            "successful remove persists only selected map and preserves survivor order");

        await using var reloaded=new ClusterService("map-config-ci",folder);
        Assert(reloaded.Config.LocalMaps.Count==1 &&
            reloaded.Config.LocalMaps[0].Name=="Extinction",
            "saved map transaction survives a fresh Manager instance");
    }
}
