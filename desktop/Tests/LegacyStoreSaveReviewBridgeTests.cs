using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Fully isolated native bridge regression: no live Kova path, shared SMB,
// server binary, user RCON or production ASA process is ever accessed.
internal static class LegacyStoreSaveReviewBridgeTests
{
    private const string SourceSha="AABBCCDDEEFF00112233445566778899"+
        "00112233445566778899AABBCCDDEEFF";

    private static void Check(bool pass,string label)
    {
        if(!pass)throw new InvalidOperationException("STORE REVIEW BRIDGE TEST: "+label);
        Console.WriteLine("PASS Store SaveWorld reviewed recovery: "+label);
    }
    private static async Task<JsonElement> Call(
        DesktopHistoricalGracefulStopBridge bridge,string method,object? fields=null)
    {
        var request=new System.Collections.Generic.Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(fields!=null)
        {
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(fields));
            foreach(var field in doc.RootElement.EnumerateObject())
                request[field.Name]=field.Value.Clone();
        }
        string response=await bridge.HandleAsync(JsonSerializer.Serialize(request));
        using var parsed=JsonDocument.Parse(response);
        return parsed.RootElement.Clone();
    }

    public static async Task Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"synthetic-reviewed-store-save");
        string profile=Path.Combine(root,"manager");
        string installation=Path.Combine(root,"synthetic-server");
        Directory.CreateDirectory(installation);
        await using var cluster=new ClusterService("save-review-test",profile,
            _=>new ProcessCheck(false,"isolated stopped",null,true));
        var map=new LocalMap{Id=Guid.NewGuid(),Name="SyntheticStoreReview",
            InstallationPath=installation,
            HistoricalStoreProtected=true,HistoricalMapKey="TheIsland_WP",
            AutoStart=false,RconPort=0};
        cluster.AddMap(map);
        Guid oldReceipt=Guid.NewGuid();
        var started=DateTimeOffset.UtcNow.AddMinutes(-4);
        LegacyStoreV1SaveEvidenceJournal.Begin(cluster.Store.Root,oldReceipt,
            map.Id,50121,started,SourceSha);
        LegacyStoreV1SaveEvidenceJournal.MarkUncertain(cluster.Store.Root,
            oldReceipt,map.Id,50121,started,SourceSha);
        using(var interrupted=cluster.Operations.Acquire(installation,
            UntamedOperationKind.Backup))
            interrupted.MarkMutationStarted();
        var oldGlobal=cluster.Operations.Observe(installation);
        Check(oldGlobal?.Phase==UntamedOperationPhase.NeedsOperator,
            "crashed Backup writer is durably blocked");

        bool isStoppedAndOriginal=false;
        using var bridge=new DesktopHistoricalGracefulStopBridge(
            cluster,null,null,reviewVerifier:(id,sha)=>
                isStoppedAndOriginal&&id==map.Id&&sha==SourceSha);
        var denied=await Call(bridge,"historical.saveWorldReviewPreview",
            new{mapId=map.Id});
        Check(!denied.GetProperty("ok").GetBoolean(),
            "unverified stopped/original Store state cannot produce an approval");
        isStoppedAndOriginal=true;
        var preview=await Call(bridge,"historical.saveWorldReviewPreview",
            new{mapId=map.Id});
        Check(preview.GetProperty("ok").GetBoolean()&&
            preview.GetProperty("data").GetProperty("confirmedStopped").GetBoolean(),
            "fresh verified stopped source creates one-time review preview");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var noConsent=await Call(bridge,"historical.saveWorldReviewApply",
            new{token,confirm=true,confirmation="WRONG"});
        Check(!noConsent.GetProperty("ok").GetBoolean(),
            "review cannot happen without exact typed confirmation");
        var approved=await Call(bridge,"historical.saveWorldReviewApply",
            new{token,confirm=true,confirmation="REVIEW STORE SAVE"});
        Check(approved.GetProperty("ok").GetBoolean()&&
            approved.GetProperty("data").GetProperty("reviewed").GetBoolean()&&
            approved.GetProperty("data").GetProperty("oldReceiptArchived").GetBoolean(),
            "explicit consent reviews both native journals without sending RCON");
        var evidence=LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id);
        Check(evidence?.Phase=="reviewed"&&!evidence.LocalSaveCompleteObserved&&
              !evidence.ProductionRestoreAllowed&&
              cluster.Operations.Observe(installation)?.Phase==
                  UntamedOperationPhase.OperatorReviewed,
            "reviewed receipts never fabricate SaveComplete or production restore");
        string archived=Path.Combine(cluster.Store.Root,"historical-save-evidence-v1",
            "reviewed",map.Id.ToString("N")+"-"+oldReceipt.ToString("N")+".json");
        Check(File.Exists(archived)&&new FileInfo(archived).Length>64,
            "original uncertain local receipt remains durably archived");
        var replay=await Call(bridge,"historical.saveWorldReviewApply",
            new{token,confirm=true,confirmation="REVIEW STORE SAVE"});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "review token cannot be replayed");
        var recovered=await Call(bridge,"historical.gracefulStopRecovery",
            new{mapId=map.Id});
        Check(recovered.GetProperty("ok").GetBoolean()&&
              !recovered.GetProperty("data").GetProperty("blocked").GetBoolean(),
            "native recovery now allows a newly approved SaveWorld");
        var newAttempt=LegacyStoreV1SaveEvidenceJournal.Begin(cluster.Store.Root,
            Guid.NewGuid(),map.Id,50122,started,SourceSha);
        Check(newAttempt.Phase=="prepared"&&File.Exists(archived),
            "new SaveWorld receipt starts without deleting original crash evidence");
    }
}
