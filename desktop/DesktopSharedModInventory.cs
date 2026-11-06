using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Cross-map, read-only mod configuration inventory. Uses the existing Core
/// ModStateController for normal maps, but historical Store-v1 maps are read
/// strictly from their preserved archive metadata rather than their empty
/// (deliberately quarantined) launch arguments. No ASA/Store/config writes.
/// </summary>
public static class DesktopSharedModInventory
{
    public sealed record Assignment(string MapId,string MapName,string Mode,int Position,
        bool HistoricalProtected);
    public sealed record Project(string ProjectId,int EnabledMapCount,bool Shared,
        Assignment[] Assignments);
    public sealed record Report(int MapCount,int ConfiguredMapCount,int SharedProjectCount,
        string[] UnreadableMaps,Project[] Projects,string Evidence);

    public static Report Read(IEnumerable<LocalMap> localMaps)
    {
        ArgumentNullException.ThrowIfNull(localMaps);
        var maps=localMaps.ToArray();
        if(maps.Length>25 || maps.Any(m=>m==null || m.Id==Guid.Empty) ||
            maps.Select(m=>m.Id).Distinct().Count()!=maps.Length)
            throw new InvalidDataException("Invalid local map list");

        var standard=LocalModUsageInventory.Read(maps.Where(m=>!m.HistoricalStoreProtected));
        var unreadable=standard.UnreadableMaps.ToList();
        var projects=new Dictionary<string,List<Assignment>>(StringComparer.Ordinal);
        var validMapIds=new HashSet<string>(StringComparer.Ordinal);

        void Add(string projectId,Assignment assignment)
        {
            if(!projects.TryGetValue(projectId,out var list))
                projects[projectId]=list=new List<Assignment>();
            list.Add(assignment);
            validMapIds.Add(assignment.MapId);
        }

        foreach(var project in standard.Projects)
        foreach(var item in project.Assignments)
            Add(project.ProjectId,new(item.MapId.ToString("D"),item.MapName,
                item.Mode switch {
                    ModMode.Active=>"active",ModMode.Passive=>"passive",
                    ModMode.Disabled=>"disabled",_=>"unknown"
                },item.Position,false));

        foreach(var map in maps.Where(m=>m.HistoricalStoreProtected))
        {
            // A protected profile has no active launch Arguments by design.
            // Its archive metadata is informational; never use it for launch.
            var active=map.HistoricalEnabledMods;
            var passive=map.HistoricalPassiveMods;
            bool Valid(IReadOnlyList<string>? ids)=>
                ids!=null && ids.Count<=501 &&
                ids.All(id=>id!=null && Regex.IsMatch(id,@"^\d{5,12}$",
                    RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)));
            if(string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256) ||
               string.IsNullOrWhiteSpace(map.HistoricalMapKey) ||
               !Valid(active) || !Valid(passive))
            {
                unreadable.Add(map.Name);
                continue;
            }
            for(int i=0;i<active.Count;i++)
                Add(active[i],new(map.Id.ToString("D"),map.Name,"active",i,true));
            for(int i=0;i<passive.Count;i++)
                Add(passive[i],new(map.Id.ToString("D"),map.Name,"passive",i,true));
        }

        var result=projects.OrderBy(p=>p.Key,StringComparer.Ordinal)
            .Select(p=>{
                var assignments=p.Value
                    .OrderBy(a=>a.MapName,StringComparer.OrdinalIgnoreCase)
                    .ThenBy(a=>a.Mode,StringComparer.Ordinal)
                    .ThenBy(a=>a.Position).ToArray();
                int enabled=assignments.Where(a=>a.Mode is "active" or "passive")
                    .Select(a=>a.MapId).Distinct(StringComparer.Ordinal).Count();
                return new Project(p.Key,enabled,enabled>1,assignments);
            }).ToArray();
        return new(maps.Length,validMapIds.Count,
            result.Count(p=>p.Shared),unreadable.ToArray(),result,
            "LOCAL_CONFIG_AND_QUARANTINED_ARCHIVE_METADATA_ONLY");
    }
}
