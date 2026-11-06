using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// Immutable, exact in-memory baseline used for optimistic concurrency. This
// captures only the local Manager launch-mod fields, never Store or ASA data.
public sealed record ModEditBaseline(string Arguments,string[] DisabledIds,bool Pending)
{
    public static ModEditBaseline Capture(LocalMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new(map.Arguments??"", (map.DisabledModIds??new()).ToArray(),map.ModChangesPending);
    }
    public bool Matches(LocalMap map)=>
        map!=null && string.Equals(Arguments,map.Arguments??"",StringComparison.Ordinal) &&
        DisabledIds.SequenceEqual(map.DisabledModIds??new()) &&
        Pending==map.ModChangesPending;
}

// Explicit field-level preview of configured mode and order differences.
// A position refers to its own Active, Passive or Disabled group (1-based).
public sealed record ModDraftDifference(string ProjectId,ModMode? BeforeMode,
    int? BeforePosition,ModMode? AfterMode,int? AfterPosition);

// A reusable editor session: edits are ordered operations, not a dictionary
// keyed by project ID. Repeating a change, moving a mod twice and undo/redo
// therefore keep exactly the sequence the operator previewed.
public sealed class ModDraftSession
{
    private readonly Guid _mapId;
    private readonly ModEditBaseline _baseline;
    private readonly List<ModEdit> _history=new();
    private int _cursor;
    public Guid MapId=>_mapId;
    public ModEditBaseline Baseline=>new(_baseline.Arguments,_baseline.DisabledIds.ToArray(),_baseline.Pending);
    public int Steps=>_cursor;
    public bool CanUndo=>_cursor>0;
    public bool CanRedo=>_cursor<_history.Count;
    public IReadOnlyList<ModEdit> Edits=>_history.Take(_cursor).ToArray();
    public IReadOnlyCollection<string> TouchedModIds=>_history.Take(_cursor)
        .Select(x=>x.ModId).Distinct(StringComparer.Ordinal).ToArray();

    public ModDraftSession(LocalMap map)
    {
        if(map==null || map.Id==Guid.Empty)throw new InvalidDataException("Missing local map identity");
        // Reject ambiguous mod config up front. No background filesystem access.
        ModStateController.Read(map);
        _mapId=map.Id;
        _baseline=ModEditBaseline.Capture(map);
    }
    private LocalMap CopyBaseline()=>new()
    {
        Id=_mapId,
        Arguments=_baseline.Arguments,
        DisabledModIds=_baseline.DisabledIds.ToList(),
        ModChangesPending=_baseline.Pending
    };
    private static void ApplyToCopy(LocalMap map,ModEdit edit)
    {
        var result=ModStateController.Preview(map,edit);
        map.Arguments=result.NewArguments;
        map.DisabledModIds=result.Updated.Disabled.ToList();
        map.ModChangesPending=result.Updated.Pending;
    }
    public LocalMap Projected()
    {
        var copy=CopyBaseline();
        foreach(var edit in _history.Take(_cursor))ApplyToCopy(copy,edit);
        return copy;
    }
    public IReadOnlyList<ModDraftDifference> Differences()
    {
        static Dictionary<string,(ModMode Mode,int Position)> Index(ModSet set)
        {
            var result=new Dictionary<string,(ModMode Mode,int Position)>(StringComparer.Ordinal);
            void Add(IReadOnlyList<string> ids,ModMode mode)
            {
                for(int i=0;i<ids.Count;i++)result.Add(ids[i],(mode,i+1));
            }
            Add(set.Active,ModMode.Active);
            Add(set.Passive,ModMode.Passive);
            Add(set.Disabled,ModMode.Disabled);
            return result;
        }
        var before=Index(ModStateController.Read(CopyBaseline()));
        var after=Index(ModStateController.Read(Projected()));
        var ordered=before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).ToArray();
        return ordered.Select(id=>{
            var existed=before.TryGetValue(id,out var prior);
            var present=after.TryGetValue(id,out var current);
            return new ModDraftDifference(id,
                existed?prior.Mode:null,existed?prior.Position:null,
                present?current.Mode:null,present?current.Position:null);
        }).Where(x=>x.BeforeMode!=x.AfterMode ||
            x.BeforePosition!=x.AfterPosition).ToArray();
    }

    public bool IsDirty
    {
        get
        {
            var next=Projected();
            return !_baseline.Matches(next);
        }
    }
    public void Stage(string modId,ModMode? target,int? position=null)
    {
        var edit=new ModEdit(_mapId,modId?.Trim()??"",target,position);
        var test=Projected();
        ApplyToCopy(test,edit); // validate before changing history
        if(_cursor>=100)throw new InvalidDataException("Too many staged mod operations");
        if(_cursor<_history.Count)_history.RemoveRange(_cursor,_history.Count-_cursor);
        _history.Add(edit);
        _cursor++;
    }
    public void Undo()
    {
        if(!CanUndo)throw new InvalidOperationException("No staged change to undo");
        _cursor--;
    }
    public void Redo()
    {
        if(!CanRedo)throw new InvalidOperationException("No staged change to redo");
        _cursor++;
    }
    public void Clear()
    {
        _history.Clear();
        _cursor=0;
    }
    public bool MatchesLive(LocalMap map)=>map!=null && map.Id==_mapId && _baseline.Matches(map);

    public IReadOnlyList<ModPreview> Save(ClusterService cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if(!IsDirty)throw new InvalidOperationException("No changed mod configuration to save");
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==_mapId)
            ??throw new UnauthorizedAccessException("Map is not registered on this local Manager");
        if(!MatchesLive(map))
            throw new InvalidOperationException("Mod config changed since editing began. Reload before saving.");
        return ModStateController.ApplyLocalBatch(cluster,Edits,
            new Dictionary<Guid,ModEditBaseline>{{_mapId,_baseline}});
    }
}
