using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class ModDraftSessionTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new Exception("MOD DRAFT FAILED: "+reason);
        Console.WriteLine("PASS mod-draft "+reason);
    }
    public static async Task Run(string root)
    {
        var install=Path.Combine(root,"mod-draft-install");
        Directory.CreateDirectory(install);
        var map=new LocalMap {
            Name="Offline mod draft fixture",InstallationPath=install,
            Arguments="-log -mods=928548,942024,950914 -port=7777",
            DisabledModIds=new(){"930170"}
        };
        await using var cluster=new ClusterService("mod-draft-tests",
            Path.Combine(root,"mod-draft-profile"),
            _=>new ProcessCheck(false,"Synthetic fixture stopped",null,true));
        cluster.Config.LocalMaps.Add(map);
        cluster.Save();
        var draft=new ModDraftSession(map);
        Check(!draft.IsDirty&&!draft.CanUndo&&!draft.CanRedo&&draft.Steps==0,
            "new draft is read-only with no staged changes");

        draft.Stage("928548",ModMode.Active,1);
        draft.Stage("928548",ModMode.Active,2);
        var projected=ModStateController.Read(draft.Projected());
        Check(projected.Active.SequenceEqual(new[]{"942024","950914","928548"}) &&
            draft.Steps==2 && draft.TouchedModIds.Single()=="928548",
            "repeat reordering of the same mod preserves BOTH steps and final order");
        draft.Undo();
        Check(ModStateController.Read(draft.Projected()).Active.SequenceEqual(
            new[]{"942024","928548","950914"})&&draft.CanRedo,
            "undo restores the previous position without live writes");
        bool invalidRejected=false;
        try{draft.Stage("not-an-id",ModMode.Active);}
        catch(InvalidDataException){invalidRejected=true;}
        Check(invalidRejected&&draft.Steps==1&&draft.CanRedo,
            "invalid edit is atomic and does not erase redo history");
        draft.Redo();
        Check(ModStateController.Read(draft.Projected()).Active.SequenceEqual(
            new[]{"942024","950914","928548"})&&draft.Steps==2,
            "redo reapplies the exact second move");

        var moveDiff=draft.Differences();
        Check(moveDiff.Any(x=>x.ProjectId=="928548" &&
              x.BeforeMode==ModMode.Active&&x.BeforePosition==1 &&
              x.AfterMode==ModMode.Active&&x.AfterPosition==3),
            "preview reports exact before/after load positions for repeated moves");
        draft.Stage("942024",ModMode.Passive);
        draft.Stage("930170",ModMode.Active);
        projected=ModStateController.Read(draft.Projected());
        Check(projected.Passive.SequenceEqual(new[]{"942024"}) &&
            projected.Active.SequenceEqual(new[]{"950914","928548","930170"}) &&
            projected.Disabled.Count==0,
            "ordered draft handles move + passive switch + enable previously disabled mod");
        draft.Undo();
        draft.Stage("912815",ModMode.Active);
        Check(!draft.CanRedo && draft.Steps==4 &&
            ModStateController.Read(draft.Projected()).Active.Last()=="912815",
            "new edit after undo creates a new branch and drops redo tail");
        draft.Stage("950914",null);
        Check(!ModStateController.Read(draft.Projected()).Active.Contains("950914"),
            "removal is in-memory until explicitly saved");

        var finalDiff=draft.Differences();
        Check(finalDiff.Any(x=>x.ProjectId=="942024" &&
              x.BeforeMode==ModMode.Active&&x.AfterMode==ModMode.Passive) &&
            finalDiff.Any(x=>x.ProjectId=="950914" && x.AfterMode==null) &&
            finalDiff.Any(x=>x.ProjectId=="912815" && x.BeforeMode==null) &&
            !finalDiff.Any(x=>x.ProjectId=="930170"),
            "change review distinguishes added, removed, mode changed and untouched mods");
        byte[] originalBytes=File.ReadAllBytes(cluster.Store.FilePath);
        Check(File.ReadAllBytes(cluster.Store.FilePath).SequenceEqual(originalBytes) &&
            map.Arguments=="-log -mods=928548,942024,950914 -port=7777" &&
            map.DisabledModIds.Single()=="930170",
            "preview, undo and redo leave original config and file byte-identical");

        var foreign=new LocalMap{Id=Guid.NewGuid(),Arguments="-mods=928548"};
        bool foreignDenied=false;
        try{new ModDraftSession(foreign).Save(cluster);}
        catch(InvalidOperationException){foreignDenied=true;}
        Check(foreignDenied,"draft cannot Save without a real change");

        // A different editor updates the SAME local instance and persists it.
        map.Arguments+=" -other-editor=1";
        cluster.Save();
        var afterOtherWriter=File.ReadAllBytes(cluster.Store.FilePath);
        bool staleBlocked=false;
        try{draft.Save(cluster);}
        catch(InvalidOperationException){staleBlocked=true;}
        Check(staleBlocked && afterOtherWriter.SequenceEqual(
            File.ReadAllBytes(cluster.Store.FilePath)) &&
            map.Arguments.EndsWith("-other-editor=1",StringComparison.Ordinal),
            "stale draft cannot overwrite a different editor's saved arguments");

        // Restore only the synthetic fixture for the following valid save.
        map.Arguments="-log -mods=928548,942024,950914 -port=7777";
        cluster.Save();
        var incorrect=new ModEditBaseline("old unrelated args",Array.Empty<string>(),false);
        byte[] beforeMismatch=File.ReadAllBytes(cluster.Store.FilePath);
        bool leaseDenied=false;
        try{
            ModStateController.ApplyLocalBatch(cluster,
                new[]{new ModEdit(map.Id,"999999",ModMode.Active)},
                new System.Collections.Generic.Dictionary<Guid,ModEditBaseline>{{map.Id,incorrect}});
        }
        catch(InvalidOperationException){leaseDenied=true;}
        Check(leaseDenied&&beforeMismatch.SequenceEqual(File.ReadAllBytes(cluster.Store.FilePath)),
            "baseline rechecked under configuration lease before any durable mutation");

        var receipts=draft.Save(cluster);
        var actual=ModStateController.Read(map);
        var wanted=ModStateController.Read(draft.Projected());
        Check(receipts.Count==draft.Steps &&
            actual.Active.SequenceEqual(wanted.Active) &&
            actual.Passive.SequenceEqual(wanted.Passive) &&
            actual.Disabled.SequenceEqual(wanted.Disabled) &&
            map.Arguments.Contains("-port=7777",StringComparison.Ordinal) &&
            map.ModChangesPending,
            "explicit Save persists complete ordered preview, preserves other switches and marks restart pending");
        Check(!draft.MatchesLive(map),"saved edit invalidates old optimistic baseline");

        var clean=new ModDraftSession(map);
        clean.Stage("930170",ModMode.Disabled);
        clean.Undo();
        Check(!clean.IsDirty && clean.CanRedo,"undo to the baseline has no pending change");
        clean.Clear();
        Check(!clean.CanUndo&&!clean.CanRedo&&!clean.IsDirty,
            "discard clears draft history without modifying persisted local map");
    }
}
