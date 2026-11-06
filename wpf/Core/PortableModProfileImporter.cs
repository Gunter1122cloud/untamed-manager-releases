using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

public sealed record PortableModProfileTarget(Guid MapId,string Name,
    ModSet Before,ModSet After,bool Changed);
public sealed record PortableModImportPlan(PortableModProfile Profile,
    LocalModCopyMode Mode,IReadOnlyDictionary<Guid,ModEditBaseline> Baselines,
    IReadOnlyList<ModEdit> Edits,IReadOnlyList<PortableModProfileTarget> Targets)
{
    public bool Changed=>Edits.Count>0;
}

/// <summary>Preview and one-transaction application on explicitly selected
/// local map configurations. It never updates the other Manager remotely.</summary>
public static class PortableModProfileImporter
{
    private static void Eligible(LocalMap map)
    {
        if(map.Id==Guid.Empty||map.Maintenance||map.HistoricalStoreProtected||
           UntamedWorldBackup.HasLiveOrHistoricalStore(map))
            throw new UnauthorizedAccessException("Store-v1 or maintenance map is not editable");
        _=ModStateController.Read(map);
    }
    public static PortableModImportPlan Preview(ClusterService cluster,
        PortableModProfile profile,IEnumerable<Guid> mapIds,LocalModCopyMode mode)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        PortableModProfileCodec.Validate(profile);
        if(!Enum.IsDefined(mode))throw new InvalidDataException("Unknown profile import mode");
        var ids=mapIds.ToArray();
        if(ids.Length is <1 or >25 || ids.Any(id=>id==Guid.Empty)||
           ids.Distinct().Count()!=ids.Length)
            throw new InvalidDataException("Distinct local targets required");
        var baselines=new Dictionary<Guid,ModEditBaseline>();
        var edits=new List<ModEdit>();
        var results=new List<PortableModProfileTarget>();
        var passive=profile.Passive.ToHashSet(StringComparer.Ordinal);
        foreach(var id in ids)
        {
            var target=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id)
                ??throw new UnauthorizedAccessException("Remote/unknown target cannot be written");
            Eligible(target);
            var before=ModStateController.Read(target);
            baselines.Add(id,ModEditBaseline.Capture(target));
            var working=new LocalMap{
                Id=target.Id,Name=target.Name,Arguments=target.Arguments,
                DisabledModIds=(target.DisabledModIds??new()).ToList(),
                ModChangesPending=target.ModChangesPending
            };
            var steps=new List<ModEdit>();
            void Stage(string project,ModMode? value)
            {
                var edit=new ModEdit(id,project,value);
                var staged=ModStateController.Preview(working,edit);
                working.Arguments=staged.NewArguments;
                working.DisabledModIds=staged.Updated.Disabled.ToList();
                working.ModChangesPending=staged.Updated.Pending;
                steps.Add(edit);
            }
            if(mode==LocalModCopyMode.Replace)
                foreach(var mod in before.EnabledOrder.Concat(before.Disabled))
                    Stage(mod,null);
            var current=ModStateController.Read(working);
            var known=current.EnabledOrder.Concat(current.Disabled)
                .ToHashSet(StringComparer.Ordinal);
            foreach(var mod in profile.EnabledOrder)
            {
                if(mode==LocalModCopyMode.Merge&&known.Contains(mod))continue;
                Stage(mod,passive.Contains(mod)?ModMode.Passive:ModMode.Active);
                known.Add(mod);
            }
            foreach(var mod in profile.Disabled)
            {
                if(mode==LocalModCopyMode.Merge&&known.Contains(mod))continue;
                Stage(mod,ModMode.Disabled);
                known.Add(mod);
            }
            var after=ModStateController.Read(working);
            bool changed=!baselines[id].Matches(working);
            if(changed)edits.AddRange(steps);
            results.Add(new(id,target.Name,before,after,changed));
        }
        if(edits.Count>2500)throw new InvalidDataException("Too many profile import operations");
        return new(profile,mode,baselines,edits,results);
    }
    public static IReadOnlyList<ModPreview> Apply(ClusterService cluster,
        PortableModImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(plan);
        PortableModProfileCodec.Validate(plan.Profile);
        if(!plan.Changed)throw new InvalidOperationException("No changes to apply");
        foreach(var result in plan.Targets)
        {
            var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==result.MapId)
                ??throw new UnauthorizedAccessException("Remote target not owned");
            Eligible(map);
        }
        return ModStateController.ApplyLocalBatch(cluster,plan.Edits,plan.Baselines);
    }
}
