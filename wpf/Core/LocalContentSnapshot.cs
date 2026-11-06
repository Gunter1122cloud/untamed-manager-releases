using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// A read-only local profile snapshot. No historical audit fixture is allowed to
// masquerade as a live mod or plugin inventory. File presence is not runtime ACK.
public sealed record LocalPluginAssignment(Guid MapId,string MapName,
    string PluginName,bool Enabled);

public sealed record LocalContentSnapshot(
    LocalModInventory Mods,
    IReadOnlyList<LocalPluginAssignment> Plugins,
    IReadOnlyList<string> UnreadablePluginMaps,
    int ReadablePluginMaps)
{
    public int LocalMapCount => Mods.Maps.Count;
    public int KnownModAssignments => Mods.Maps
        .Where(m=>m.Error==null).Sum(m=>m.OrderedIds.Count);
    public bool PluginInventoryComplete =>
        UnreadablePluginMaps.Count==0 && ReadablePluginMaps==LocalMapCount;
}

public static class LocalContentSnapshotReader
{
    public static LocalContentSnapshot Inspect(IEnumerable<LocalMap> localMaps)
    {
        ArgumentNullException.ThrowIfNull(localMaps);
        LocalMap[] maps=localMaps.ToArray();
        LocalModInventory mods=LocalModAssignments.Inspect(maps);
        var plugins=new List<LocalPluginAssignment>();
        var unreadable=new List<string>();
        int readable=0;
        foreach(LocalMap map in maps)
        {
            try
            {
                // Missing/offline installations are unknown, never reported as empty.
                if(string.IsNullOrWhiteSpace(map.InstallationPath) ||
                   !Directory.Exists(map.InstallationPath))
                    throw new DirectoryNotFoundException("Local installation unavailable");
                var rows=PluginStateManager.Inventory(map);
                plugins.AddRange(rows.Select(item=>new LocalPluginAssignment(
                    map.Id,map.Name,item.Name,item.Enabled)));
                readable++;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException or NotSupportedException)
            {
                // An unreadable installation is UNKNOWN, never an empty plugin list.
                unreadable.Add(map.Name);
            }
        }
        return new(mods,plugins,unreadable,readable);
    }
}
