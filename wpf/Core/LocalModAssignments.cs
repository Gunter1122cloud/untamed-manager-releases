using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// Read-only, local Manager configuration inventory. These are configured launch
// arguments, NOT a claim that a server has loaded or installed the given mods.
public sealed record LocalModMapStatus(Guid MapId,string Name,
    IReadOnlyList<string> OrderedIds,string? Error);

public sealed record LocalModInventory(
    IReadOnlyList<LocalModMapStatus> Maps,
    IReadOnlyList<string> CommonIds,
    IReadOnlyList<string> SingleMapIds,
    int UniqueIdCount,
    bool Complete);

public static class LocalModAssignments
{
    public static LocalModInventory Inspect(IEnumerable<LocalMap> ownedMaps)
    {
        if(ownedMaps==null)throw new ArgumentNullException(nameof(ownedMaps));
        var rows=new List<LocalModMapStatus>();
        var seen=new HashSet<Guid>();
        foreach(var map in ownedMaps)
        {
            if(map==null || map.Id==Guid.Empty || !seen.Add(map.Id))
                throw new InvalidDataException("Local map identities must be unique");
            if(string.IsNullOrWhiteSpace(map.Name))
                throw new InvalidDataException("Local map name missing");
            try
            {
                rows.Add(new(map.Id,map.Name,LocalContentOperations.ReadMods(map.Arguments),null));
            }
            catch(InvalidDataException ex)
            {
                // Do not silently interpret corrupt launch parameters as an empty list.
                rows.Add(new(map.Id,map.Name,Array.Empty<string>(),ex.Message));
            }
        }
        bool complete=rows.All(x=>x.Error==null);
        var frequencies=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var row in rows.Where(x=>x.Error==null))
            foreach(var id in row.OrderedIds)
                frequencies[id]=frequencies.GetValueOrDefault(id)+1;
        // With even one unreadable map, the "global common" claim is unproven.
        var common=complete && rows.Count>0
            ? frequencies.Where(x=>x.Value==rows.Count).Select(x=>x.Key)
                .OrderBy(x=>x,StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        var single=complete
            ? frequencies.Where(x=>x.Value==1).Select(x=>x.Key)
                .OrderBy(x=>x,StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        return new(rows,common,single,frequencies.Count,complete);
    }
}
