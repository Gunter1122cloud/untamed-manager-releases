using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

public enum ModMode { Active, Passive, Disabled }
public sealed record ModSet(IReadOnlyList<string> Active,IReadOnlyList<string> Passive,
    IReadOnlyList<string> Disabled,bool Pending);
public sealed record ModEdit(Guid Map,string ModId,ModMode? Target,int? Position=null);
public sealed record ModPreview(Guid Map,string PreviousArguments,string NewArguments,
    ModSet Previous,ModSet Updated,bool Changed);

// Active and passive are distinct ASA switches; disabled IDs are owned only by
// the local Manager and must never appear in its launch arguments.
public static class ModStateController
{
    private static readonly Regex Param=new(
        @"(?<!\S)-(?<kind>mods|passivemods)=(?:""(?<quoted>[^""]*)""|(?<raw>[^\s""]+))(?=\s|$)",
        RegexOptions.IgnoreCase|RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex LooksLikeParam=new(
        @"(?<!\S)-(?:mods|passivemods)=",
        RegexOptions.IgnoreCase|RegexOptions.Compiled|RegexOptions.CultureInvariant);

    public static ModSet Read(LocalMap map)
    {
        if(map==null || map.Id==Guid.Empty)throw new InvalidDataException("Local map identity missing");
        var args=map.Arguments??"";
        var matches=Param.Matches(args).Cast<Match>().ToArray();
        if(matches.Length!=LooksLikeParam.Matches(args).Count ||
           matches.GroupBy(m=>m.Groups["kind"].Value,StringComparer.OrdinalIgnoreCase)
               .Any(g=>g.Count()!=1))
            throw new InvalidDataException("Malformed or repeated -mods/-passivemods argument");
        string[] ReadSwitch(string kind)
        {
            var match=matches.SingleOrDefault(m=>m.Groups["kind"].Value.Equals(kind,
                StringComparison.OrdinalIgnoreCase));
            if(match==null)return Array.Empty<string>();
            string raw=match.Groups["quoted"].Success?
                match.Groups["quoted"].Value:match.Groups["raw"].Value;
            if(string.IsNullOrWhiteSpace(raw))
                throw new InvalidDataException("Mod launch parameter cannot be empty");
            return CheckList(raw.Split(',',StringSplitOptions.None));
        }
        var active=ReadSwitch("mods");
        var passive=ReadSwitch("passivemods");
        var disabled=CheckList((map.DisabledModIds??new()).ToArray());
        if(active.Concat(passive).Concat(disabled).Distinct(StringComparer.Ordinal).Count()!=
           active.Length+passive.Length+disabled.Length)
            throw new InvalidDataException("Mod ID cannot appear in multiple states");
        return new(active,passive,disabled,map.ModChangesPending);
    }

    private static string[] CheckList(IEnumerable<string> ids)
    {
        var items=ids.ToArray();
        if(items.Length>500 ||
           items.Any(id=>id==null || !Regex.IsMatch(id,@"\A[0-9]{5,12}\z")) ||
           items.Distinct(StringComparer.Ordinal).Count()!=items.Length)
            throw new InvalidDataException("Invalid or duplicate CurseForge project ID");
        return items;
    }
    public static ModPreview Preview(LocalMap map,ModEdit edit)
    {
        if(map==null || map.Id!=edit.Map || map.Id==Guid.Empty)
            throw new UnauthorizedAccessException("Mod edit is not owned by this map");
        if(edit.ModId==null || !Regex.IsMatch(edit.ModId,@"\A[0-9]{5,12}\z"))
            throw new InvalidDataException("Invalid project ID");
        var before=Read(map);
        var active=before.Active.ToList();
        var passive=before.Passive.ToList();
        var disabled=before.Disabled.ToList();
        active.Remove(edit.ModId);passive.Remove(edit.ModId);disabled.Remove(edit.ModId);
        if(edit.Target.HasValue)
        {
            var selected=edit.Target.Value switch{
                ModMode.Active=>active,ModMode.Passive=>passive,
                ModMode.Disabled=>disabled,_=>throw new InvalidDataException("Invalid mod state")
            };
            if(edit.Position.HasValue &&
               (edit.Position<0 || edit.Position>selected.Count))
                throw new ArgumentOutOfRangeException(nameof(edit.Position));
            selected.Insert(edit.Position??selected.Count,edit.ModId);
        }
        CheckList(active);CheckList(passive);CheckList(disabled);
        var arg=(map.Arguments??"").Trim();
        // Remove only the two canonical switches, keep cluster, port, loader
        // and every other unknown argument in original order and form.
        var remaining=Param.Replace(arg,"").Trim();
        remaining=Regex.Replace(remaining,@"[ \t]{2,}"," ");
        var next=remaining+
            (active.Count>0?(remaining.Length>0?" ":"")+"-mods="+string.Join(",",active):"");
        next+=(passive.Count>0?(next.Length>0?" ":"")+"-passivemods="+
            string.Join(",",passive):"");
        bool changed=!string.Equals(next,arg,StringComparison.Ordinal) ||
            !disabled.SequenceEqual(before.Disabled);
        return new(map.Id,map.Arguments??"",next,before,
            new(active,passive,disabled,before.Pending||changed),changed);
    }

    public static IReadOnlyList<ModPreview> ApplyLocalBatch(ClusterService cluster,
        IEnumerable<ModEdit> edits)
    {
        if(cluster==null||edits==null)throw new ArgumentNullException();
        var requests=edits.ToArray();
        if(requests.Length==0 || requests.Length>100)throw new InvalidDataException("Invalid batch size");
        var originals=new Dictionary<Guid,(string Args,List<string> Disabled,bool Pending)>();
        var previews=new List<ModPreview>();
        try
        {
            foreach(var edit in requests)
            {
                var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==edit.Map);
                if(map==null)throw new UnauthorizedAccessException("Cannot edit a peer map");
                if(!originals.ContainsKey(map.Id))
                    originals.Add(map.Id,(map.Arguments,
                        (map.DisabledModIds??new()).ToList(),map.ModChangesPending));
                // Staged in-memory edits; no server restart and no file writes yet.
                var preview=Preview(map,edit);
                map.Arguments=preview.NewArguments;
                map.DisabledModIds=preview.Updated.Disabled.ToList();
                map.ModChangesPending=preview.Updated.Pending;
                previews.Add(preview);
            }
            cluster.Save();
            return previews;
        }
        catch
        {
            foreach(var (id,original) in originals)
            {
                var map=cluster.Config.LocalMaps.Single(x=>x.Id==id);
                map.Arguments=original.Args;
                map.DisabledModIds=original.Disabled;
                map.ModChangesPending=original.Pending;
            }
            throw;
        }
    }

    public static ModPreview PreviewMove(LocalMap map,string id,ModMode mode,int newIndex)
        =>Preview(map,new(map.Id,id,mode,newIndex));

    public static void ConfirmAppliedAfterVerifiedRestart(ClusterService cluster,LocalMap map)
    {
        // Deliberately no automatic reset: real ASA readiness is still unproven.
        throw new InvalidOperationException(
            "A confirmed real ASA restart and loaded-mod reconciliation are not yet certified");
    }
}
