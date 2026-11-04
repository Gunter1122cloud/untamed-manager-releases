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

// The ASA -mods switch includes both passive and active projects. -passivemods
// is a subset of -mods; disabled projects never enter launch arguments.
public static class ModStateController
{
    private static readonly Regex Param=new(
        @"(?<!\S)-(?<kind>mods|passivemods)=(?:""(?<quoted>[^""]*)""|(?<raw>[^\s""]+))(?=\s|$)",
        RegexOptions.IgnoreCase|RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex LooksLikeParam=new(
        @"(?<!\S)-(?:mods|passivemods)=",
        RegexOptions.IgnoreCase|RegexOptions.Compiled|RegexOptions.CultureInvariant);

    // Locate complete command-line tokens, not substrings inside a quoted
    // SessionName/password/unknown argument. Preserve all original text outside
    // the two owned switches, including spaces and escaped quotation marks.
    private static IReadOnlyList<(int Start,int Length,Match Value)> Switches(string args)
    {
        if(args.Length>32767||args.Contains('\0')||args.Contains('\r')||args.Contains('\n'))
            throw new InvalidDataException("Invalid mod launch argument frame");
        var result=new List<(int,int,Match)>();int cursor=0;
        while(cursor<args.Length)
        {
            if(char.IsWhiteSpace(args[cursor])){cursor++;continue;}
            int start=cursor;bool quoted=false;
            while(cursor<args.Length)
            {
                char c=args[cursor];
                if(c=='"')
                {
                    int slashes=0;
                    for(int at=cursor-1;at>=start&&args[at]=='\\';at--)slashes++;
                    if(slashes%2==0)quoted=!quoted;
                }
                if(char.IsWhiteSpace(c)&&!quoted)break;
                cursor++;
            }
            if(quoted)throw new InvalidDataException("Unterminated quoted launch argument");
            string token=args[start..cursor];
            if(token.Length>1&&token[0]=='"'&&token[^1]=='"')token=token[1..^1];
            if(token.Equals("-mods",StringComparison.OrdinalIgnoreCase)||
               token.Equals("-passivemods",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mod launch switch requires an explicit list");
            var looks=LooksLikeParam.Match(token);
            if(!looks.Success||looks.Index!=0)
            {
                string joined=token.Replace("\"","");
                if(joined.StartsWith("-mods=",StringComparison.OrdinalIgnoreCase)||
                   joined.StartsWith("-passivemods=",StringComparison.OrdinalIgnoreCase)||
                   joined.Equals("-mods",StringComparison.OrdinalIgnoreCase)||
                   joined.Equals("-passivemods",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Fragmented quoted mod switch is ambiguous");
                continue;
            }
            var match=Param.Match(token);
            if(!match.Success||match.Index!=0||match.Length!=token.Length)
                throw new InvalidDataException("Malformed mod launch switch");
            result.Add((start,cursor-start,match));
        }
        return result;
    }

    public static ModSet Read(LocalMap map)
    {
        if(map==null || map.Id==Guid.Empty)throw new InvalidDataException("Local map identity missing");
        var args=map.Arguments??"";
        var matches=Switches(args).Select(x=>x.Value).ToArray();
        if(matches.GroupBy(m=>m.Groups["kind"].Value,StringComparer.OrdinalIgnoreCase)
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
        var enabled=ReadSwitch("mods");
        var passive=ReadSwitch("passivemods");
        var disabled=CheckList((map.DisabledModIds??new()).ToArray());
        var enabledSet=new HashSet<string>(enabled,StringComparer.Ordinal);
        if(passive.Any(id=>!enabledSet.Contains(id)) ||
           disabled.Any(id=>enabledSet.Contains(id)))
            throw new InvalidDataException(
                "Passive mods must be a subset of -mods and disabled mods cannot be enabled");
        var passiveSet=new HashSet<string>(passive,StringComparer.Ordinal);
        var active=enabled.Where(id=>!passiveSet.Contains(id)).ToArray();
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
        string remaining=arg;
        foreach(var token in Switches(arg).Reverse())
            remaining=remaining.Remove(token.Start,token.Length);
        remaining=remaining.Trim();
        // A passive project stays in the normal load list and is also tagged
        // passive. Passive projects are first, preserving each state's order.
        var enabled=passive.Concat(active).ToArray();
        var next=remaining+
            (enabled.Length>0?(remaining.Length>0?" ":"")+"-mods="+string.Join(",",enabled):"");
        next+=(passive.Count>0?(next.Length>0?" ":"")+"-passivemods="+
            string.Join(",",passive):"");
        bool changed=!string.Equals(next,arg,StringComparison.Ordinal) ||
            !disabled.SequenceEqual(before.Disabled);
        return new(map.Id,map.Arguments??"",next,before,
            new(active,passive,disabled,before.Pending||changed),changed);
    }

    public static IReadOnlyList<ModPreview> ApplyLocalBatch(ClusterService cluster,
        IEnumerable<ModEdit> edits,
        IReadOnlyDictionary<Guid,ModEditBaseline>? expectedBaselines=null)
    {
        if(cluster==null||edits==null)throw new ArgumentNullException();
        var requests=edits.ToArray();
        if(requests.Length==0 || requests.Length>100)throw new InvalidDataException("Invalid batch size");
        var originals=new Dictionary<Guid,(string Args,List<string> Disabled,bool Pending)>();
        var previews=new List<ModPreview>();
        using var operation=cluster.Operations.AcquireMany(new[]{cluster.Store.Root}.Concat(
            requests.Select(e=>cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==e.Map))
                .Where(m=>m!=null&&!string.IsNullOrWhiteSpace(m.InstallationPath))
                .Select(m=>m!.InstallationPath)),UntamedOperationKind.Configuration);
        try
        {
            foreach(var edit in requests)
            {
                var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==edit.Map);
                if(map==null)throw new UnauthorizedAccessException("Cannot edit a peer map");
                if(!originals.ContainsKey(map.Id))
                {
                    // Verify the editor snapshot while the configuration lease is
                    // held, before any staging or disk write. Never silently
                    // overwrite a concurrent INI/mod editor change.
                    if(expectedBaselines!=null &&
                       (!expectedBaselines.TryGetValue(map.Id,out var expected) ||
                        !expected.Matches(map)))
                        throw new InvalidOperationException(
                            "Mod configuration changed outside this draft; reload is required");
                    originals.Add(map.Id,(map.Arguments,
                        (map.DisabledModIds??new()).ToList(),map.ModChangesPending));
                }
                // Staged in-memory edits; no server restart and no file writes yet.
                var preview=Preview(map,edit);
                map.Arguments=preview.NewArguments;
                map.DisabledModIds=preview.Updated.Disabled.ToList();
                map.ModChangesPending=preview.Updated.Pending;
                previews.Add(preview);
            }
            operation.MarkMutationStarted();
            cluster.Save();
            operation.Complete();
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

