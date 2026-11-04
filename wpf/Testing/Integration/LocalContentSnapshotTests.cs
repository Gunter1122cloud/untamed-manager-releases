using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

public static class LocalContentSnapshotTests
{
    public static void Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"local-content-snapshot");
        string first=Path.Combine(root,"first");
        string second=Path.Combine(root,"second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        string api=Path.Combine(first,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(Path.Combine(api,"ArkApi","Plugins","Alpha"));
        Directory.CreateDirectory(Path.Combine(api,"UntamedDisabledPlugins","Beta"));

        var a=new LocalMap{
            Name="Astraeos",Id=Guid.NewGuid(),InstallationPath=first,
            Arguments="Astraeos?listen -mods=928548,942024 -log"
        };
        var b=new LocalMap{
            Name="Future Map",Id=Guid.NewGuid(),InstallationPath=second,
            Arguments="FutureMap?listen -mods=928548"
        };
        var snapshot=LocalContentSnapshotReader.Inspect(new[]{a,b});
        Check(snapshot.LocalMapCount==2 && snapshot.Mods.Complete &&
            snapshot.Mods.UniqueIdCount==2 &&
            snapshot.KnownModAssignments==3 &&
            snapshot.Mods.CommonIds.SequenceEqual(new[]{"928548"}),
            "Local mod overview uses registered maps and verified configured IDs, not audit constants");
        Check(snapshot.ReadablePluginMaps==2 && snapshot.PluginInventoryComplete &&
            snapshot.Plugins.Count==2 &&
            snapshot.Plugins.Single(x=>x.PluginName=="Alpha").Enabled &&
            !snapshot.Plugins.Single(x=>x.PluginName=="Beta").Enabled,
            "Local plugin overview reads active and disabled disk folders without runtime claims");

        b.Arguments="FutureMap?listen -mods=928548,928548";
        b.InstallationPath="";
        var partial=LocalContentSnapshotReader.Inspect(new[]{a,b});
        Check(!partial.Mods.Complete && partial.Mods.CommonIds.Count==0 &&
            partial.KnownModAssignments==2 &&
            partial.ReadablePluginMaps==1 &&
            partial.UnreadablePluginMaps.SequenceEqual(new[]{"Future Map"}) &&
            !partial.PluginInventoryComplete,
            "Unreadable mod and plugin data remain explicitly partial, not zero or healthy");

        b.Arguments="FutureMap?listen -mods=928548";
        b.InstallationPath=Path.Combine(root,"unmounted-map");
        var absent=LocalContentSnapshotReader.Inspect(new[]{a,b});
        Check(absent.Mods.Complete && !absent.PluginInventoryComplete &&
            absent.UnreadablePluginMaps.SequenceEqual(new[]{"Future Map"}),
            "Absent or disconnected local installation never appears as an empty healthy plugin directory");

        var empty=LocalContentSnapshotReader.Inspect(Array.Empty<LocalMap>());
        Check(empty.LocalMapCount==0 && empty.Plugins.Count==0 &&
            empty.ReadablePluginMaps==0 && empty.PluginInventoryComplete,
            "Empty profile overview is valid and does not invent a ten-map cluster");
    }

    private static void Check(bool success,string description)
    {
        if(!success)throw new InvalidOperationException(description);
        Console.WriteLine("PASS "+description);
    }
}
