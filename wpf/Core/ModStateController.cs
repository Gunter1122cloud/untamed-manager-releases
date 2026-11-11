using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

public enum ModMode { Active, Passive, Disabled }
public sealed record ModSet(IReadOnlyList<string> Active,IReadOnlyList<string> Passive,
    IReadOnlyList<string> Disabled,bool Pending)
{
    // Preserve the original interleaved -mods sequence. Passive is a SUBSET
    // switch, not a separate group that must be moved to the front.
    public IReadOnlyList<string> EnabledOrder {get;init;}=Array.Empty<string>();
}
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

    private static string EffectiveModArguments(LocalMap map)
    {
        if(!map.HistoricalStoreProtected)return map.Arguments??"";
        // Imported original Kova profiles keep raw launch arguments blank.
        // The native Start planner consumes these original lists instead.
        string[] enabled=CheckList(map.HistoricalEnabledMods??new());
        string[] passive=CheckList(map.HistoricalPassiveMods??new());
        return (enabled.Length>0?"-mods="+string.Join(",",enabled):"")+
            (passive.Length>0?(enabled.Length>0?" ":"")+
                "-passivemods="+string.Join(",",passive):"");
    }

    public static ModSet Read(LocalMap map)
    {
        if(map==null || map.Id==Guid.Empty)throw new InvalidDataException("Local map identity missing");
        var args=EffectiveModArguments(map);
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
        return new(active,passive,disabled,map.ModChangesPending){
            EnabledOrder=enabled
        };
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
        var originalOrder=before.EnabledOrder.ToList();
        int formerLoadIndex=originalOrder.IndexOf(edit.ModId);
        ModMode? formerMode=before.Active.Contains(edit.ModId)?ModMode.Active:
            before.Passive.Contains(edit.ModId)?ModMode.Passive:
            before.Disabled.Contains(edit.ModId)?ModMode.Disabled:null;
        int oldModePosition=formerMode switch{
            ModMode.Active=>active.IndexOf(edit.ModId),
            ModMode.Passive=>passive.IndexOf(edit.ModId),
            ModMode.Disabled=>disabled.IndexOf(edit.ModId),
            _=>-1
        };
        active.Remove(edit.ModId);passive.Remove(edit.ModId);disabled.Remove(edit.ModId);
        IReadOnlyList<string>? selectedMode=null;
        int newModeIndex=-1;
        if(edit.Target.HasValue)
        {
            var selected=edit.Target.Value switch{
                ModMode.Active=>active,ModMode.Passive=>passive,
                ModMode.Disabled=>disabled,_=>throw new InvalidDataException("Invalid mod state")
            };
            if(edit.Position.HasValue &&
               (edit.Position<0 || edit.Position>selected.Count))
                throw new ArgumentOutOfRangeException(nameof(edit.Position));
            // An unchanged mode with no explicit position is a no-op for
            // that mode's ordering. New/moved mods append by default.
            newModeIndex=edit.Position ??
                (formerMode==edit.Target && oldModePosition>=0?
                    Math.Min(oldModePosition,selected.Count):selected.Count);
            selected.Insert(newModeIndex,edit.ModId);
            selectedMode=selected;
        }
        CheckList(active);CheckList(passive);CheckList(disabled);
        var arg=EffectiveModArguments(map).Trim();
        // Remove only the two canonical switches, keep cluster, port, loader
        // and every other unknown argument in original order and form.
        string remaining=arg;
        foreach(var token in Switches(arg).Reverse())
            remaining=remaining.Remove(token.Start,token.Length);
        remaining=remaining.Trim();
        // Preserve every untouched -mods item's global position. Moving a mod
        // between Active and Passive is a mode change, NOT a load-order change.
        var enabled=originalOrder.ToList();
        enabled.Remove(edit.ModId);
        if(edit.Target is ModMode.Active or ModMode.Passive)
        {
            int insertAt=formerLoadIndex>=0?
                Math.Min(formerLoadIndex,enabled.Count):enabled.Count;
            if(edit.Position.HasValue && selectedMode!=null)
            {
                // An EXPLICIT reorder is anchored against the next or previous
                // project in the same mode. Unrelated passive/active items
                // retain their relative ordering as much as possible.
                var nextModeItem=newModeIndex+1<selectedMode.Count?
                    selectedMode[newModeIndex+1]:null;
                var previousModeItem=newModeIndex>0?
                    selectedMode[newModeIndex-1]:null;
                if(nextModeItem!=null && enabled.Contains(nextModeItem))
                    insertAt=enabled.IndexOf(nextModeItem);
                else if(previousModeItem!=null && enabled.Contains(previousModeItem))
                    insertAt=enabled.IndexOf(previousModeItem)+1;
            }
            enabled.Insert(insertAt,edit.ModId);
        }
        var next=remaining+
            (enabled.Count>0?(remaining.Length>0?" ":"")+"-mods="+string.Join(",",enabled):"");
        next+=(passive.Count>0?(next.Length>0?" ":"")+"-passivemods="+
            string.Join(",",passive):"");
        bool changed=!string.Equals(next,arg,StringComparison.Ordinal) ||
            !disabled.SequenceEqual(before.Disabled);
        return new(map.Id,map.Arguments??"",next,before,
            new ModSet(active,passive,disabled,before.Pending||changed){
                EnabledOrder=enabled
            },changed);
    }

    // Compare the exact non-mod launch tokens (including -usestore,
    // -clusterid and -ClusterDirOverride) before permitting a Kova mod edit.
    // We ignore only whitespace created where mod switches were removed.
    private static string NonModArguments(string args)
    {
        string cleaned=args??"";
        foreach(var token in Switches(cleaned).OrderByDescending(x=>x.Start))
            cleaned=cleaned.Remove(token.Start,token.Length);
        return Regex.Replace(cleaned.Trim(),@"\s+"," ");
    }

    public static IReadOnlyList<ModPreview> ApplyLocalBatch(ClusterService cluster,
        IEnumerable<ModEdit> edits,
        IReadOnlyDictionary<Guid,ModEditBaseline>? expectedBaselines=null)
    {
        if(cluster==null||edits==null)throw new ArgumentNullException();
        var requests=edits.ToArray();
        // One local Manager may copy up to five ~500-mod map configurations
        // atomically. Never accept an unbounded batch from the UI.
        if(requests.Length==0 || requests.Length>2500)
            throw new InvalidDataException("Invalid batch size");
        var originals=new Dictionary<Guid,(string Args,string LoaderArgs,
            List<string> Enabled,List<string> Passive,List<string> Disabled,bool Pending)>();
        var previews=new List<ModPreview>();
        using var operation=cluster.Operations.AcquireMany(new[]{cluster.Store.Root}.Concat(
            requests.Select(e=>cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==e.Map))
                .Where(m=>m!=null&&!string.IsNullOrWhiteSpace(m.InstallationPath))
                .Select(m=>m!.InstallationPath)),UntamedOperationKind.Configuration);
        try
        {
            // Validate ALL baselines (including an optional source-only map)
            // under the same operation lease. A changed source must not
            // silently provide an obsolete multi-map replacement.
            if(expectedBaselines!=null)
            foreach(var (baselineId,baseline) in expectedBaselines)
            {
                var observed=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==baselineId);
                if(observed==null || !baseline.Matches(observed))
                    throw new InvalidOperationException("Mod copy baseline changed; reload required");
            }
            foreach(var edit in requests)
            {
                var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==edit.Map);
                if(map==null)throw new UnauthorizedAccessException("Cannot edit a peer map");
                if(map.ObservedReadOnlyImported||map.Maintenance||
                   (!map.HistoricalStoreProtected&&UntamedWorldBackup.HasLiveOrHistoricalStore(map)))
                    throw new UnauthorizedAccessException("Store/mod ownership is not locally editable");
                if(map.HistoricalStoreProtected)
                {
                    if(string.IsNullOrWhiteSpace(map.HistoricalMapKey)||
                       string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)||
                       map.HistoricalArchiveSha256.Length!=64)
                        throw new UnauthorizedAccessException("Original Kova map identity missing");
                    new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
                    var folder=Path.GetFullPath(map.InstallationPath);
                    foreach(var sibling in cluster.Config.LocalMaps.Where(m=>
                        !string.IsNullOrWhiteSpace(m.InstallationPath)&&
                        string.Equals(Path.GetFullPath(m.InstallationPath),folder,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        var state=cluster.Check(sibling);
                        if(sibling.Maintenance||sibling.ObservedReadOnlyImported||
                           state.Running||!state.ConfirmedStopped||
                           state.Identity!=null||state.LoaderIdentity!=null)
                            throw new InvalidOperationException(
                                "Original Kova mod edit requires all local ASA/Loader processes stopped");
                    }
                }
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
                    originals.Add(map.Id,(map.Arguments,map.ApiLoaderArguments,
                        (map.HistoricalEnabledMods??new()).ToList(),
                        (map.HistoricalPassiveMods??new()).ToList(),
                        (map.DisabledModIds??new()).ToList(),map.ModChangesPending));
                }
                // Staged in-memory edits; no server restart and no file writes yet.
                var preview=Preview(map,edit);
                // No mod editor may rewrite the Store, cluster ID/path, port
                // or any other launch parameter; own only -mods/-passivemods.
                if(map.HistoricalStoreProtected&&
                   !string.Equals(NonModArguments(map.Arguments),
                       NonModArguments(preview.NewArguments),StringComparison.Ordinal))
                    throw new InvalidDataException("Kova Store-v1 launch flags changed outside mod switches");
                if(map.HistoricalStoreProtected)
                {
                    // Update the EXACT fields consumed by the real Kova native
                    // Start planner. Never add -mods to raw Store args at rest.
                    map.HistoricalEnabledMods=preview.Updated.EnabledOrder.ToList();
                    map.HistoricalPassiveMods=preview.Updated.Passive.ToList();
                }
                else
                {
                    string oldArgs=map.Arguments;
                    map.Arguments=preview.NewArguments;
                    if(map.StartWithAsaApi&&map.ApiLoaderArguments==oldArgs)
                        map.ApiLoaderArguments=preview.NewArguments;
                }
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
                map.ApiLoaderArguments=original.LoaderArgs;
                map.HistoricalEnabledMods=original.Enabled;
                map.HistoricalPassiveMods=original.Passive;
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

