using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// Aggregate only explicitly registered LOCAL map backup packages.
// Read-only integrity checks; does not access or restore ASA, Kova Store or SQL.
public sealed record LocalBackupMapSummary(Guid MapId,string MapName,
    int ValidPackages,int InvalidPackages,long VerifiedBytes,
    DateTimeOffset? LatestVerifiedUtc,IReadOnlyList<LocalBackupHistoryEntry> Packages);

public static class LocalBackupOverview
{
    public static IReadOnlyList<LocalBackupMapSummary> Inspect(
        string profileRoot,IEnumerable<LocalMap> localMaps,int recentPerMap=10)
    {
        if(localMaps==null)throw new ArgumentNullException(nameof(localMaps));
        if(recentPerMap<1 || recentPerMap>50)throw new ArgumentOutOfRangeException(nameof(recentPerMap));
        if(string.IsNullOrWhiteSpace(profileRoot) || !Path.IsPathFullyQualified(profileRoot)
            || profileRoot.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit fixed local Manager profile required");
        var maps=localMaps.ToArray();
        if(maps.Any(m=>m==null || m.Id==Guid.Empty || string.IsNullOrWhiteSpace(m.Name)) ||
           maps.Select(m=>m.Id).Distinct().Count()!=maps.Length)
            throw new InvalidDataException("Invalid or duplicate local map identities");
        var summaries=new List<LocalBackupMapSummary>();
        foreach(var map in maps)
        {
            string root=Path.Combine(profileRoot,"local-backups",map.Id.ToString("N"));
            var packages=LocalBackupCatalog.Inspect(root,map.Id,recentPerMap);
            var good=packages.Where(p=>p.Valid).ToArray();
            summaries.Add(new(map.Id,map.Name,good.Length,packages.Count-good.Length,
                good.Sum(p=>p.TotalBytes),
                good.Length>0?good.Max(p=>p.CreatedUtc):null,
                packages));
        }
        return summaries;
    }
}
