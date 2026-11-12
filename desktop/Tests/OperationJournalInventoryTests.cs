using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

internal static class OperationJournalInventoryTests
{
    private static void Check(bool condition,string label)
    {
        if(!condition)throw new InvalidOperationException(
            "OPERATION JOURNAL INVENTORY: "+label);
        Console.WriteLine("PASS journal inventory: "+label);
    }
    public static void Run(string testsRoot)
    {
        string root=Path.Combine(testsRoot,"inventory-fixture");
        string journalRoot=Path.Combine(root,"operation-journals");
        string known=Path.Combine(root,"known-asa-install");
        string unknown=Path.Combine(root,"unregistered-synthetic-test-install");
        Directory.CreateDirectory(known);
        Directory.CreateDirectory(unknown);
        using var scope=UntamedOperationCoordinator.UseIsolatedRootForTests(
            journalRoot);
        var localMap=new LocalMap{Id=Guid.NewGuid(),Name="Aberration",
            InstallationPath=known};
        var operations=new UntamedOperationCoordinator();
        using(var lease=operations.Acquire(known,UntamedOperationKind.Backup))
            lease.MarkMutationStarted();
        var restart=new AsaManualRestartJournal(unknown);
        var began=restart.Begin(Guid.NewGuid(),45678);
        restart.Change(began.Id,AsaRestartPhase.NeedsOperator);
        string localJournal=Path.Combine(journalRoot,
            UntamedOperationCoordinator.ResourceKey(known)+".json");
        byte[] before=File.ReadAllBytes(localJournal);
        var report=OperationJournalInventory.Inspect(new[]{localMap});
        Check(report.RecordCount==2&&report.RegisteredRecords==1&&
              report.UnattributedResources==1&&
              report.UnattributedPendingResources==1&&report.PendingRecords==2&&
              report.CorruptRecords==0&&!report.Truncated,
            "paired journal formats are counted without treating an unknown key as a live-map failure");
        Check(report.Entries.Any(e=>e.Registered&&
                e.RegisteredMapNames=="Aberration"&&e.Journal=="Operations-v1"&&
                e.Phase=="NeedsOperator")&&
              report.Entries.Any(e=>!e.Registered&&
                e.Journal=="Restart-v1"&&e.Phase=="NeedsOperator"),
            "known map and unregistered resource retain distinct operator-review classifications");
        Check(before.SequenceEqual(File.ReadAllBytes(localJournal)),
            "read-only scan never alters an existing operation journal");
        string invalid=Path.Combine(journalRoot,new string('A',64)+".json");
        File.WriteAllText(invalid,"{corrupt");
        var damaged=OperationJournalInventory.Inspect(new[]{localMap});
        Check(damaged.RecordCount==3&&damaged.CorruptRecords==1&&
              damaged.PendingRecords==3&&damaged.UnattributedResources==2&&
              damaged.UnattributedPendingResources==2,
            "corrupt unknown journal remains visible without any repair or deletion");
        Check(File.ReadAllText(invalid)=="{corrupt"&&
              before.SequenceEqual(File.ReadAllBytes(localJournal)),
            "corrupt and valid original bytes are preserved after diagnostics");

        // Different real resource keys may share the 16-character displayed
        // fingerprint. Never undercount genuinely separate unregistered keys.
        string samePrefix=new string('B',16);
        string collision1=Path.Combine(journalRoot,
            samePrefix+new string('C',48)+".json");
        string collision2=Path.Combine(journalRoot,
            samePrefix+new string('D',48)+".json");
        File.WriteAllText(collision1,"{}");
        File.WriteAllText(collision2,"{}");
        var collisions=OperationJournalInventory.Inspect(new[]{localMap});
        Check(collisions.RecordCount==5&&
              collisions.UnattributedResources==4&&
              collisions.UnattributedPendingResources==4,
            "different full resource hashes are counted independently despite equal displayed fingerprints");

        // A directory full of corrupt historical artifacts must not trigger
        // an unbounded eager sort or unbounded WebView response.
        for(int i=0;i<260;i++)
            File.WriteAllText(Path.Combine(journalRoot,i.ToString("X64")+".json"),"{}");
        var capped=OperationJournalInventory.Inspect(new[]{localMap});
        Check(capped.RecordCount==256&&capped.Truncated&&
              capped.Entries.Length==256,
            "more than 256 records are capped and explicitly declared incomplete");
        Check(before.SequenceEqual(File.ReadAllBytes(localJournal))&&
              File.ReadAllText(invalid)=="{corrupt",
            "bounded inventory does not alter pre-existing journal bytes");
    }
}
