using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

/// <summary>
/// Local-only, database-free Copy To / Merge / Replace planning for ASA mods.
/// This operates on Manager-owned launch configuration, NEVER live ASA files,
/// the shared Store-v1, remote profiles or the protected historical import.
/// Applying does NOT download mods, restart ASA or certify mod loading.
/// </summary>
public enum LocalModCopyMode { Merge, Replace }

public sealed record LocalModCopyTarget(Guid MapId,string MapName,ModSet Before,
    ModSet After,bool Changed);

public sealed record LocalModCopyPlan(Guid SourceMapId,LocalModCopyMode Mode,
    ModEditBaseline SourceBaseline,
    IReadOnlyDictionary<Guid,ModEditBaseline> Baselines,
    IReadOnlyList<ModEdit> Edits,
    IReadOnlyList<LocalModCopyTarget> Targets)
{
    public bool Changed=>Edits.Count>0;
}

public static class LocalModCopyPlanner
{
    private static void RequireEligible(LocalMap map)
    {
        if(map.Id==Guid.Empty || map.Maintenance ||
           map.HistoricalStoreProtected ||
           UntamedWorldBackup.HasLiveOrHistoricalStore(map))
            throw new UnauthorizedAccessException(
                "Historical, Store-v1 or maintenance map cannot be modified by mod copy");
        // The shared Store writer belongs to ASA and the existing legacy coordinator.
        // This module never touches files outside Manager config.
        _=ModStateController.Read(map);
    }

    public static LocalModCopyPlan Preview(ClusterService cluster,Guid sourceId,
        IEnumerable<Guid> targetIds,LocalModCopyMode mode)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(targetIds);
        if(!Enum.IsDefined(mode))throw new InvalidDataException("Unknown mod copy mode");
        var ids=targetIds.ToArray();
        if(ids.Length is <1 or >25 || ids.Any(id=>id==Guid.Empty || id==sourceId) ||
           ids.Distinct().Count()!=ids.Length)
            throw new InvalidDataException("A distinct list of local destination maps is required");
        var source=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==sourceId)
            ??throw new UnauthorizedAccessException("Source map is not owned locally");
        RequireEligible(source);
        var sourceMods=ModStateController.Read(source);
        var passive=new HashSet<string>(sourceMods.Passive,StringComparer.Ordinal);
        var baselines=new Dictionary<Guid,ModEditBaseline>
        {
            [sourceId]=ModEditBaseline.Capture(source)
        };
        var edits=new List<ModEdit>();
        var previews=new List<LocalModCopyTarget>();

        foreach(var id in ids)
        {
            var target=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id)
                ??throw new UnauthorizedAccessException("Destination map is not owned locally");
            RequireEligible(target);
            var prior=ModStateController.Read(target);
            baselines.Add(id,ModEditBaseline.Capture(target));
            var working=new LocalMap {
                Id=target.Id,Name=target.Name,Arguments=target.Arguments,
                DisabledModIds=target.DisabledModIds.ToList(),
                ModChangesPending=target.ModChangesPending
            };
            var candidateEdits=new List<ModEdit>();
            void Stage(string projectId,ModMode? targetMode)
            {
                var edit=new ModEdit(id,projectId,targetMode);
                var result=ModStateController.Preview(working,edit);
                working.Arguments=result.NewArguments;
                working.DisabledModIds=result.Updated.Disabled.ToList();
                working.ModChangesPending=result.Updated.Pending;
                candidateEdits.Add(edit);
            }
            if(mode==LocalModCopyMode.Replace)
            {
                // Remove all prior configurations before appending source order.
                foreach(var existing in prior.EnabledOrder.Concat(prior.Disabled))
                    Stage(existing,null);
            }
            var known=new HashSet<string>(
                ModStateController.Read(working).EnabledOrder.Concat(
                    ModStateController.Read(working).Disabled),StringComparer.Ordinal);
            foreach(var projectId in sourceMods.EnabledOrder)
            {
                if(mode==LocalModCopyMode.Merge && known.Contains(projectId))continue;
                Stage(projectId,passive.Contains(projectId)?ModMode.Passive:ModMode.Active);
                known.Add(projectId);
            }
            foreach(var projectId in sourceMods.Disabled)
            {
                if(mode==LocalModCopyMode.Merge && known.Contains(projectId))continue;
                Stage(projectId,ModMode.Disabled);
                known.Add(projectId);
            }
            var after=ModStateController.Read(working);
            bool changed=!baselines[id].Matches(working);
            if(changed)edits.AddRange(candidateEdits);
            previews.Add(new(id,target.Name,prior,after,changed));
        }
        if(edits.Count>2500)throw new InvalidDataException(
            "Too many mod operations for one atomic local copy");
        return new(sourceId,mode,baselines[sourceId],baselines,edits,previews);
    }

    public static IReadOnlyList<ModPreview> Apply(ClusterService cluster,
        LocalModCopyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(plan);
        if(!plan.Changed)throw new InvalidOperationException("Nothing changed");
        var source=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==plan.SourceMapId)
            ??throw new UnauthorizedAccessException("Source map no longer owned");
        RequireEligible(source);
        if(!plan.SourceBaseline.Matches(source))
            throw new IOException("Source mod configuration changed since preview");
        foreach(var target in plan.Targets)
        {
            var current=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==target.MapId)
                ??throw new UnauthorizedAccessException("Destination map no longer owned");
            RequireEligible(current);
        }
        // The existing operation coordinator, full baseline check and ConfigStore
        // optimistic CAS perform the whole batch as one Manager-config write.
        return ModStateController.ApplyLocalBatch(cluster,plan.Edits,plan.Baselines);
    }
}
