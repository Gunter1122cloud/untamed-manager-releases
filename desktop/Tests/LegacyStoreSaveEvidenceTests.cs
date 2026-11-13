using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UntamedManager.Core;

// Native Windows regression for the additional MANAGER-LOCAL SaveComplete
// witness. No real ASA executable/RCON, Legacy original directory, or Store
// writer is accessed. Snapshot/restore/backup engines are unchanged.
internal static class LegacyStoreSaveEvidenceTests
{
    private static void Check(bool pass,string message)
    {
        if(!pass)throw new InvalidOperationException(
            "DURABLE STORE SAVECOMPLETE TEST FAILED: "+message);
        Console.WriteLine("PASS durable Store SaveComplete: "+message);
    }
    private static bool Rejects(Action action)
    {
        try{action();return false;}
        catch(Exception ex) when(ex is InvalidDataException or
            InvalidOperationException or IOException or UnauthorizedAccessException)
        {return true;}
    }

    public static Task Run(string sandbox)
    {
        string root=Path.Combine(sandbox,"original-store-save-evidence-lab");
        Directory.CreateDirectory(root);
        var id=Guid.NewGuid();
        var operation=Guid.NewGuid();
        const int pid=48031;
        var started=DateTimeOffset.UtcNow.AddMinutes(-4);
        const string sourceSha="AABBCCDDEEFF00112233445566778899"+
            "00112233445566778899AABBCCDDEEFF";
        string immutableOriginal=Path.Combine(root,"Legacy-shaped-unchanged-original.bin");
        byte[] original=SHA256.HashData(Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(immutableOriginal,original);

        Check(LegacyStoreV1SaveEvidenceJournal.Inspect(root,id)==null,
            "never fabricates SaveComplete before an approved attempt");
        var prepared=LegacyStoreV1SaveEvidenceJournal.Begin(root,operation,
            id,pid,started,sourceSha);
        Check(prepared.Phase=="prepared" &&
              !prepared.LocalSaveCompleteObserved &&
              !prepared.SharedStoreWriterCertified &&
              !prepared.BothHostsSaveCompleteCertified &&
              !prepared.ProductionRestoreAllowed,
            "durable receipt is initially prepared, no SaveComplete or cross-host certificate");

        var recovered=LegacyStoreV1SaveEvidenceJournal.Inspect(root,id);
        Check(recovered?.OperationId==operation&&
              recovered.Phase=="prepared"&&
              !recovered.LocalSaveCompleteObserved,
            "independent read after restart recognizes pending durable journal");
        Check(Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Begin(root,
                Guid.NewGuid(),id,pid,started,sourceSha)),
            "pending receipt prevents second SaveWorld attempt without operator review");
        Check(Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Complete(root,
                Guid.NewGuid(),id,pid,started,sourceSha,DateTimeOffset.UtcNow,true,false))&&
              Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Complete(root,
                operation,id,pid+1,started,sourceSha,DateTimeOffset.UtcNow,true,false))&&
              Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Complete(root,
                operation,id,pid,started.AddSeconds(1),sourceSha,
                DateTimeOffset.UtcNow,true,false))&&
              Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Complete(root,
                operation,id,pid,started,sourceSha+"1",DateTimeOffset.UtcNow,true,false)),
            "foreign operation, PID reuse, changed start time and source identity cannot certify SaveComplete");
        Check(Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Complete(root,
                operation,id,pid,started,sourceSha,DateTimeOffset.UtcNow,false,false))&&
              Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Complete(root,
                operation,id,pid,started,sourceSha,DateTimeOffset.UtcNow,true,true)),
            "missing observed SaveComplete and any DoExit are never certified");
        var timestamp=DateTimeOffset.UtcNow;
        var done=LegacyStoreV1SaveEvidenceJournal.Complete(root,operation,
            id,pid,started,sourceSha,timestamp,true,false);
        Check(done.Phase=="completed"&&done.LocalSaveCompleteObserved&&
              !done.FsyncCertified&&!done.GameClientRejoinVerified&&
              !done.BothHostsSaveCompleteCertified&&
              !done.ProductionRestoreAllowed,
            "only exact successful native observation creates a local-only completed receipt");
        Check(LegacyStoreV1SaveEvidenceJournal.Inspect(root,id)?
            .SaveObservedUtc==timestamp,
            "completed SaveComplete timestamp survives independent read");

        string journal=Path.Combine(root,"historical-save-evidence-v1",
            id.ToString("N")+".json");
        byte[] originalJournal=File.ReadAllBytes(journal);
        File.WriteAllText(journal+".next","partial interrupted journal");
        Check(Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Inspect(root,id))&&
              Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Begin(root,
                  Guid.NewGuid(),id,pid,started,sourceSha)),
            "abandoned .next from process crash blocks reading success or dispatching another SaveWorld");
        File.Delete(journal+".next");
        File.WriteAllText(journal,"{}");
        Check(Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Inspect(root,id))&&
              Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Begin(root,
                  Guid.NewGuid(),id,pid,started,sourceSha)),
            "truncated or changed SHA-256 journal cannot be used to authorize SaveWorld");
        File.WriteAllBytes(journal,originalJournal);
        Check(LegacyStoreV1SaveEvidenceJournal.Inspect(root,id)?
            .Phase=="completed",
            "isolated journal fixture can explicitly restore verified prior bytes");

        var next=Guid.NewGuid();
        LegacyStoreV1SaveEvidenceJournal.Begin(root,next,id,pid,started,sourceSha);
        LegacyStoreV1SaveEvidenceJournal.MarkUncertain(root,next,id,
            pid,started,sourceSha);
        Check(LegacyStoreV1SaveEvidenceJournal.Inspect(root,id)?
            .Phase=="needsOperator"&&
              Rejects(()=>LegacyStoreV1SaveEvidenceJournal.Begin(root,
                  Guid.NewGuid(),id,pid,started,sourceSha)),
            "interrupted SaveWorld stays NeedsOperator across Manager restart; no automatic retry");
        Check(File.ReadAllBytes(immutableOriginal).SequenceEqual(original),
            "local receipt lifecycle leaves original Legacy-shaped bytes unchanged");

        // Real Windows local coordinator + receipt review. No Legacy share or
        // real server process is involved. A crashed Backup journal and its
        // uncertain SaveWorld receipt must BOTH be explicitly reviewed.
        string installation=Path.Combine(root,"synthetic-installation");
        Directory.CreateDirectory(installation);
        string operationRoot=Path.Combine(root,"synthetic-operations");
        var coordinator=new UntamedOperationCoordinator(operationRoot);
        using(var abandoned=coordinator.Acquire(installation,UntamedOperationKind.Backup))
            abandoned.MarkMutationStarted();
        var operationJournal=coordinator.Observe(installation);
        Check(operationJournal?.Phase==UntamedOperationPhase.NeedsOperator,
            "interrupted Backup operation requires durable operator review");
        Check(Rejects(()=>coordinator.ReviewStoppedBackup(installation,
                operationJournal!.Operation,()=>false,
                ()=>LegacyStoreV1SaveEvidenceJournal.ReviewUncertain(
                    root,id,next,sourceSha,()=>true))),
            "unverified stopped/source state cannot release an interrupted operation");
        Check(LegacyStoreV1SaveEvidenceJournal.Inspect(root,id)?.Phase=="needsOperator",
            "failed review keeps the earlier SaveWorld receipt unchanged");

        byte[] previousReceipt=File.ReadAllBytes(journal);
        string operationPath=Path.Combine(operationRoot,
            UntamedOperationCoordinator.ResourceKey(installation)+".json");
        byte[] previousOperation=File.ReadAllBytes(operationPath);
        coordinator.ReviewStoppedBackup(installation,operationJournal!.Operation,
            ()=>true,
            ()=>LegacyStoreV1SaveEvidenceJournal.ReviewUncertain(
                root,id,next,sourceSha,()=>true));
        Check(coordinator.Observe(installation)?.Phase==UntamedOperationPhase.OperatorReviewed&&
              LegacyStoreV1SaveEvidenceJournal.Inspect(root,id)?.Phase=="reviewed",
            "confirmed operator review clears only local journal blockers");
        string receiptArchive=Path.Combine(root,"historical-save-evidence-v1",
            "reviewed",id.ToString("N")+"-"+next.ToString("N")+".json");
        string operationArchive=Path.Combine(operationRoot,
            UntamedOperationCoordinator.ResourceKey(installation)+"-"+
            operationJournal.Operation.ToString("N")+".operator-reviewed.json");
        Check(File.ReadAllBytes(receiptArchive).SequenceEqual(previousReceipt)&&
              File.ReadAllBytes(operationArchive).SequenceEqual(previousOperation),
            "both original uncertain journals are preserved byte-for-byte for audit");
        var reviewedState=LegacyStoreV1SaveEvidenceJournal.Inspect(root,id);
        Check(!reviewedState!.LocalSaveCompleteObserved&&
              !reviewedState.ProductionRestoreAllowed,
            "reviewing a failed SaveWorld never fabricates SaveComplete or restore authority");
        using(var fresh=coordinator.Acquire(installation,UntamedOperationKind.Backup))
            fresh.Complete();
        var manuallyApprovedNew=LegacyStoreV1SaveEvidenceJournal.Begin(
            root,Guid.NewGuid(),id,pid,started,sourceSha);
        Check(manuallyApprovedNew.Phase=="prepared"&&
              File.ReadAllBytes(receiptArchive).SequenceEqual(previousReceipt),
            "a NEW separately approved SaveWorld may begin without erasing old uncertain evidence");
        Check(File.ReadAllBytes(immutableOriginal).SequenceEqual(original),
            "operator journal recovery never touches original Legacy-shaped bytes");
        return Task.CompletedTask;
    }
}
