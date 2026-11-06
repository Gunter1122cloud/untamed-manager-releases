using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

/// <summary>
/// Completely read-only cross-map report based on the manager's configured
/// -mods/-passivemods and DisabledModIds. It does NOT claim that ASA loaded,
/// installed, subscribed to or verified these mods at runtime.
/// The original launch string, -mods order and all Store-v1 data are untouched.
/// </summary>
public sealed record LocalModAssignment(Guid MapId,string MapName,ModMode Mode,int Position);
public sealed record LocalModUsage(string ProjectId,IReadOnlyList<LocalModAssignment> Assignments)
{
    public int EnabledMapCount=>Assignments.Where(a=>a.Mode!=ModMode.Disabled)
        .Select(a=>a.MapId).Distinct().Count();
    public bool Shared=>EnabledMapCount>1;
}
public sealed record LocalModInventoryResult(
    IReadOnlyList<LocalModUsage> Projects,
    IReadOnlyList<string> UnreadableMaps,int MapCount)
{
    public IReadOnlyList<LocalModUsage> SharedProjects=>
        Projects.Where(x=>x.Shared).ToArray();
}

public static class LocalModUsageInventory
{
    public static LocalModInventoryResult Read(IEnumerable<LocalMap> localMaps)
    {
        ArgumentNullException.ThrowIfNull(localMaps);
        var maps=localMaps.ToArray();
        if(maps.Length>25 || maps.Any(m=>m==null || m.Id==Guid.Empty) ||
           maps.Select(m=>m.Id).Distinct().Count()!=maps.Length)
            throw new InvalidDataException(
                "Local mod inventory requires unique, well-formed local map identities");
        var usage=new Dictionary<string,List<LocalModAssignment>>(StringComparer.Ordinal);
        var errors=new List<string>();
        foreach(var map in maps)
        {
            ModSet snapshot;
            try{snapshot=ModStateController.Read(map);}
            catch(InvalidDataException)
            {
                errors.Add(string.IsNullOrWhiteSpace(map.Name)?
                    map.Id.ToString("N"):map.Name);
                continue;
            }
            void Append(IReadOnlyList<string> ids,ModMode mode)
            {
                for(int i=0;i<ids.Count;i++)
                {
                    var project=ids[i];
                    if(!usage.TryGetValue(project,out var locations))
                        usage[project]=locations=new List<LocalModAssignment>();
                    locations.Add(new(map.Id,map.Name,mode,i));
                }
            }
            Append(snapshot.Active,ModMode.Active);
            Append(snapshot.Passive,ModMode.Passive);
            Append(snapshot.Disabled,ModMode.Disabled);
        }
        return new(usage.OrderBy(x=>x.Key,StringComparer.Ordinal)
            .Select(x=>new LocalModUsage(x.Key,x.Value.ToArray()))
            .ToArray(),errors,maps.Length);
    }
}
