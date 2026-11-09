using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UntamedManager.Core;

// Native Windows regression for the additional MANAGER-LOCAL SaveComplete
// witness. No real ASA executable/RCON, Kova original directory, or Store
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
        string immutableOriginal=Path.Combine(root,"Kova-shaped-unchanged-original.bin");
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
            "local receipt lifecycle leaves original Kova-shaped bytes unchanged");
        return Task.CompletedTask;
    }
}
