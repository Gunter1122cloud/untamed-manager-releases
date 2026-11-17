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

    private static async Task VerifyNativeOriginalStopMemoryRecovery(string root)
    {
        // The native worker may be queued/running before it writes the
        // cross-process coordinator journal. Recovery must not advertise a
        // free Store-v1 lifecycle slot during that window, or after an
        // in-memory failure whose durable outcome is not yet readable.
        string install=Path.Combine(root,"held-original-stop");
        string profile=Path.Combine(root,"held-stop-profile");
        Directory.CreateDirectory(install);
        await using var cluster=new ClusterService("held-original-stop",profile,
            _=>new ProcessCheck(true,"isolated synthetic running ASA",null,false));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="Isolated original Stop worker",
            HistoricalStoreProtected=true,HistoricalMapKey="TheIsland_WP",
            InstallationPath=install,RconPort=37179
        };
        cluster.AddMap(map);
        var owner=new ProcessIdentity{
            Pid=55179,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-4),
            ExecutablePath=Path.Combine(install,"synthetic-ArkAscendedServer.exe"),
            Arguments="TheIsland_WP?listen"
        };
        var started=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop=new DesktopHistoricalGracefulStopBridge(cluster,
            _=>new LegacyStoreV1GracefulStop.Owned(map,owner,SourceSha),
            async (_,_,_,_,_)=>{
                started.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException(
                    "isolated worker failed before coordinator journal");
            });
        var preview=await Call(stop,"historical.gracefulStopPreview",
            new{mapId=map.Id});
        Check(preview.GetProperty("ok").GetBoolean(),
            "synthetic original Stop offers an explicit owned-map approval");
        string token=preview.GetProperty("data").GetProperty("token")
            .GetString()!;
        var apply=await Call(stop,"historical.gracefulStopApply",
            new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD DOEXIT"});
        Check(apply.GetProperty("ok").GetBoolean(),
            "one original Stop request queues the isolated native job");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(cluster.Operations.Observe(install)==null,
            "synthetic Stop worker has no durable coordinator receipt yet");
        var pending=await Call(stop,"historical.gracefulStopRecovery",
            new{mapId=map.Id});
        var pendingData=pending.GetProperty("data");
        Check(pending.GetProperty("ok").GetBoolean()&&
              pendingData.GetProperty("blocked").GetBoolean()&&
              pendingData.GetProperty("inMemoryRunning").GetBoolean()&&
              !pendingData.GetProperty("requiresOperator").GetBoolean(),
            "original Stop recovery blocks while in-memory RCON worker runs");
        var duplicate=await Call(stop,"historical.gracefulStopPreview",
            new{mapId=map.Id});
        Check(!duplicate.GetProperty("ok").GetBoolean(),
            "a second original Store Stop preview is refused while work runs");
        release.TrySetResult(true);
        string jobId=apply.GetProperty("data").GetProperty("jobId").GetString()!;
        JsonElement result=default;
        for(int i=0;i<120;i++)
        {
            result=await Call(stop,"historical.gracefulStopStatus",
                new{jobId});
            if(result.GetProperty("data").GetProperty("state")
                .GetString()=="needsOperator")break;
            await Task.Delay(25);
        }
        Check(result.GetProperty("data").GetProperty("state")
            .GetString()=="needsOperator",
            "failed synthetic original Stop remains NeedsOperator");
        var after=await Call(stop,"historical.gracefulStopRecovery",
            new{mapId=map.Id});
        Check(after.GetProperty("ok").GetBoolean()&&
              after.GetProperty("data").GetProperty("blocked").GetBoolean()&&
              after.GetProperty("data").GetProperty("requiresOperator").GetBoolean()&&
              !after.GetProperty("data").GetProperty("inMemoryRunning").GetBoolean(),
            "original Store Stop recovery preserves failed in-memory state");
    }

    private static async Task VerifyRunningOwnedSaveReview(string root)
    {
        // P0 live-aberration regression with synthetic process/Store state.
        // Never launches an ASA process, connects to RCON or touches Kova.
        string installation=Path.Combine(root,"running-save-review-install");
        string profile=Path.Combine(root,"running-save-review-manager");
        Directory.CreateDirectory(installation);
        await using var cluster=new ClusterService("running-save-review-test",profile,
            _=>new ProcessCheck(true,"synthetic running ASA",null,false));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="Synthetic running Aberration",
            HistoricalStoreProtected=true,HistoricalMapKey="Aberration_WP",
            InstallationPath=installation,RconPort=37045
        };
        cluster.AddMap(map);
        const int pid=16392;
        var pidStarted=DateTimeOffset.UtcNow.AddMinutes(-6);
        var receiptOperation=Guid.NewGuid();
        LegacyStoreV1SaveEvidenceJournal.Begin(cluster.Store.Root,
            receiptOperation,map.Id,pid,pidStarted,SourceSha);
        LegacyStoreV1SaveEvidenceJournal.MarkUncertain(cluster.Store.Root,
            receiptOperation,map.Id,pid,pidStarted,SourceSha);
        using(var interrupted=cluster.Operations.Acquire(
            installation,UntamedOperationKind.Backup))
            interrupted.MarkMutationStarted();
        var originalCoordinator=cluster.Operations.Observe(installation)!;
        byte[] untouched=File.ReadAllBytes(Path.Combine(cluster.Store.Root,
            "historical-save-evidence-v1",map.Id.ToString("N")+".json"));
        bool exactOwner=false;
        int readOnlyChecks=0;
        using var bridge=new DesktopHistoricalGracefulStopBridge(cluster,
            null,null,reviewVerifier:(_,_)=>false,
            runningReviewVerifier:(id,observedPid,observedStart,sha)=>{
                readOnlyChecks++;
                return exactOwner&&id==map.Id&&observedPid==pid&&
                    observedStart==pidStarted&&sha==SourceSha;
            },isolatedRunningReviewMinimumAge:TimeSpan.Zero);
        var denied=await Call(bridge,"historical.saveWorldReviewPreview",
            new{mapId=map.Id});
        Check(!denied.GetProperty("ok").GetBoolean(),
            "running SaveWorld review denies an unverified original PID/source");

        exactOwner=true;
        var review=await Call(bridge,"historical.saveWorldReviewPreview",
            new{mapId=map.Id});
        Check(review.GetProperty("ok").GetBoolean()&&
              review.GetProperty("data").GetProperty("runningOwned").GetBoolean()&&
              !review.GetProperty("data").GetProperty("confirmedStopped").GetBoolean()&&
              review.GetProperty("data").GetProperty("previousOutcomeStillUnknown").GetBoolean(),
            "running original ASA can preview uncertain SaveWorld without Stop");
        string token=review.GetProperty("data").GetProperty("token").GetString()!;
        var rejected=await Call(bridge,"historical.saveWorldReviewApply",
            new{token=Guid.NewGuid()});
        Check(!rejected.GetProperty("ok").GetBoolean(),
            "unrecognized one-shot token cannot write any running-review journal");
        exactOwner=false;
        rejected=await Call(bridge,"historical.saveWorldReviewApply",
            new{token});
        Check(!rejected.GetProperty("ok").GetBoolean()&&
              cluster.Operations.Observe(installation)?.Phase==
                  UntamedOperationPhase.NeedsOperator &&
              LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id)?.Phase==
                  "needsOperator",
            "changed original PID/source denies apply without changing any journal");
        rejected=await Call(bridge,"historical.saveWorldReviewApply",
            new{token});
        Check(!rejected.GetProperty("ok").GetBoolean(),
            "failed running-review approval token cannot be replayed");

        exactOwner=true;
        review=await Call(bridge,"historical.saveWorldReviewPreview",
            new{mapId=map.Id});
        token=review.GetProperty("data").GetProperty("token").GetString()!;
        var resolved=await Call(bridge,"historical.saveWorldReviewApply",
            new{token});
        Check(resolved.GetProperty("ok").GetBoolean()&&
              resolved.GetProperty("data").GetProperty("ownerRemainedRunning").GetBoolean()&&
              resolved.GetProperty("data").GetProperty("previousOutcomeStillUnknown").GetBoolean()&&
              !resolved.GetProperty("data").GetProperty("rconSent").GetBoolean()&&
              !resolved.GetProperty("data").GetProperty("originalStoreFilesWritten").GetBoolean(),
            "running review is audited without RCON, Stop or false SaveComplete");
        var evidence=LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id);
        var coordinator=cluster.Operations.Observe(installation);
        Check(evidence?.Phase=="reviewed"&&!evidence.LocalSaveCompleteObserved&&
              coordinator?.Phase==UntamedOperationPhase.OperatorReviewed&&
              coordinator.Operation==originalCoordinator.Operation&&
              readOnlyChecks>=4,
            "running review preserves coordinator identity and UNKNOWN SaveWorld outcome");
        string archive=Path.Combine(cluster.Store.Root,"historical-save-evidence-v1",
            "reviewed",map.Id.ToString("N")+"-"+receiptOperation.ToString("N")+".json");
        Check(File.Exists(archive)&&File.ReadAllBytes(archive).SequenceEqual(untouched),
            "original uncertain receipt is archived byte-identically");
        var state=await Call(bridge,"historical.gracefulStopRecovery",
            new{mapId=map.Id});
        Check(state.GetProperty("ok").GetBoolean()&&
              !state.GetProperty("data").GetProperty("blocked").GetBoolean(),
            "reviewed running SaveWorld no longer blocks a new independent preflight");

        // Crash-injection: receipt reviewed and archived, but the common
        // Backup coordinator write was interrupted. A second approved
        // operator review resumes idempotently; no RCON replay occurs.
        var anotherReceipt=Guid.NewGuid();
        LegacyStoreV1SaveEvidenceJournal.Begin(cluster.Store.Root,
            anotherReceipt,map.Id,pid,pidStarted,SourceSha);
        LegacyStoreV1SaveEvidenceJournal.MarkUncertain(cluster.Store.Root,
            anotherReceipt,map.Id,pid,pidStarted,SourceSha);
        using(var second=cluster.Operations.Acquire(
            installation,UntamedOperationKind.Backup))
            second.MarkMutationStarted();
        LegacyStoreV1SaveEvidenceJournal.ReviewUncertain(cluster.Store.Root,
            map.Id,anotherReceipt,SourceSha,()=>true);
        Check(cluster.Operations.Observe(installation)?.Phase==
                  UntamedOperationPhase.NeedsOperator,
            "crash between receipt and coordinator leaves installation blocked");
        var resume=await Call(bridge,"historical.saveWorldReviewPreview",
            new{mapId=map.Id});
        Check(resume.GetProperty("ok").GetBoolean()&&
              resume.GetProperty("data").GetProperty("phase").GetString()=="reviewed",
            "partial two-journal review is presented for explicit completion");
        var resumed=await Call(bridge,"historical.saveWorldReviewApply",
            new{token=resume.GetProperty("data").GetProperty("token").GetString(),
                });
        Check(resumed.GetProperty("ok").GetBoolean()&&
              cluster.Operations.Observe(installation)?.Phase==
                  UntamedOperationPhase.OperatorReviewed,
            "second approval finishes interrupted paired review without sending command");

        // P1: three-journal crash. In a fully isolated installation,
        // a matching old-PID Restart and a Backup journal may both be
        // NeedsOperator. Review SaveEvidence first, Restart second, Backup
        // last; a synthetic exception between writes must remain resumable.
        var thirdReceipt=Guid.NewGuid();
        LegacyStoreV1SaveEvidenceJournal.Begin(cluster.Store.Root,
            thirdReceipt,map.Id,pid,pidStarted,SourceSha);
        LegacyStoreV1SaveEvidenceJournal.MarkUncertain(cluster.Store.Root,
            thirdReceipt,map.Id,pid,pidStarted,SourceSha);
        using(var third=cluster.Operations.Acquire(
            installation,UntamedOperationKind.Backup))
            third.MarkMutationStarted();
        var restartJournal=new AsaManualRestartJournal(installation);
        var pendingRestart=restartJournal.Begin(map.Id,pid);
        restartJournal.Change(pendingRestart.Id,AsaRestartPhase.NeedsOperator);
        // Synthetic fixture only: emulate a genuine abandoned Restart that
        // is older than the production three-minute manual-review threshold.
        string restartFile=Path.Combine(UntamedOperationCoordinator.DefaultRoot,
            "Restart-v1",UntamedOperationCoordinator.ResourceKey(installation)+".json");
        File.WriteAllText(restartFile,JsonSerializer.Serialize(
            restartJournal.Observe()! with{
                ChangedUtc=DateTimeOffset.UtcNow.AddMinutes(-6)
            },new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var pendingCoordinator=cluster.Operations.Observe(installation)!;
        bool injectedCrash=false;
        try
        {
            cluster.Operations.ReviewRunningBackup(installation,
                pendingCoordinator.Operation,()=>true,()=>{
                    LegacyStoreV1SaveEvidenceJournal.ReviewUncertain(
                        cluster.Store.Root,map.Id,thirdReceipt,SourceSha,()=>true);
                    throw new IOException("synthetic crash after first of three reviews");
                },pendingRestart.Id,map.Id,pid);
        }
        catch(IOException){injectedCrash=true;}
        Check(injectedCrash&&
              LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id)?.Phase=="reviewed"&&
              restartJournal.Observe()?.Phase==AsaRestartPhase.NeedsOperator&&
              cluster.Operations.Observe(installation)?.Phase==UntamedOperationPhase.NeedsOperator,
            "three-journal crash after SaveEvidence retains both pending recovery blockers");
        var paired=await Call(bridge,"historical.saveWorldReviewPreview",
            new{mapId=map.Id});
        Check(paired.GetProperty("ok").GetBoolean()&&
              paired.GetProperty("data").GetProperty("pairedRestartReview").GetBoolean(),
            "running review inventories and binds the matching interrupted Restart");
        var pairedReceipt=await Call(bridge,"historical.saveWorldReviewApply",
            new{token=paired.GetProperty("data").GetProperty("token").GetString(),
                });
        Check(pairedReceipt.GetProperty("ok").GetBoolean()&&
              pairedReceipt.GetProperty("data").GetProperty("pairedRestartReviewed").GetBoolean()&&
              !pairedReceipt.GetProperty("data").GetProperty("rconSent").GetBoolean()&&
              restartJournal.Observe()?.Phase==AsaRestartPhase.OperatorReviewed&&
              cluster.Operations.Observe(installation)?.Phase==UntamedOperationPhase.OperatorReviewed&&
              !LegacyStoreV1SaveEvidenceJournal.Inspect(cluster.Store.Root,map.Id)!.LocalSaveCompleteObserved,
            "three-journal review is resumed idempotently without Send/SaveComplete/Stop");
    }

    public static async Task Run(string testRoot)
    {
        await VerifyRunningOwnedSaveReview(testRoot);
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
        string secondInstallation=Path.Combine(root,"second-store-installation");
        Directory.CreateDirectory(secondInstallation);
        var secondMap=new LocalMap{Id=Guid.NewGuid(),Name="SyntheticStoreReview2",
            InstallationPath=secondInstallation,
            HistoricalStoreProtected=true,HistoricalMapKey="Ragnarok_WP",
            AutoStart=false,RconPort=0};
        cluster.AddMap(secondMap);
        Guid oldReceipt=Guid.NewGuid();
        var started=DateTimeOffset.UtcNow.AddMinutes(-4);
        LegacyStoreV1SaveEvidenceJournal.Begin(cluster.Store.Root,oldReceipt,
            map.Id,50121,started,SourceSha);
        LegacyStoreV1SaveEvidenceJournal.MarkUncertain(cluster.Store.Root,
            oldReceipt,map.Id,50121,started,SourceSha);
        using(var interrupted=cluster.Operations.Acquire(installation,
            UntamedOperationKind.Backup))
            interrupted.MarkMutationStarted();
        Guid secondReceipt=Guid.NewGuid();
        LegacyStoreV1SaveEvidenceJournal.Begin(cluster.Store.Root,secondReceipt,
            secondMap.Id,50123,started,SourceSha);
        LegacyStoreV1SaveEvidenceJournal.MarkUncertain(cluster.Store.Root,
            secondReceipt,secondMap.Id,50123,started,SourceSha);
        using(var secondInterrupted=cluster.Operations.Acquire(
            secondInstallation,UntamedOperationKind.Backup))
            secondInterrupted.MarkMutationStarted();
        var oldGlobal=cluster.Operations.Observe(installation);
        Check(oldGlobal?.Phase==UntamedOperationPhase.NeedsOperator,
            "crashed Backup writer is durably blocked");

        bool isStoppedAndOriginal=false;
        using var bridge=new DesktopHistoricalGracefulStopBridge(
            cluster,null,null,reviewVerifier:(id,sha)=>
                isStoppedAndOriginal&&
                (id==map.Id||id==secondMap.Id)&&sha==SourceSha);
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
        var invalidSecondPreview=await Call(bridge,
            "historical.saveWorldReviewPreview",new{mapId=Guid.NewGuid()});
        Check(!invalidSecondPreview.GetProperty("ok").GetBoolean(),
            "failed review preview for an unrelated map preserves first map token");
        var secondPreview=await Call(bridge,"historical.saveWorldReviewPreview",
            new{mapId=secondMap.Id});
        Check(secondPreview.GetProperty("ok").GetBoolean(),
            "separate original Store map has independent pending SaveWorld review");
        string secondToken=secondPreview.GetProperty("data")
            .GetProperty("token").GetString()!;
        Check(token!=secondToken,
            "independent pending operator reviews never share approval tokens");
        var noConsent=await Call(bridge,"historical.saveWorldReviewApply",
            new{token=Guid.NewGuid()});
        Check(!noConsent.GetProperty("ok").GetBoolean(),
            "review cannot happen with a mismatched or unknown operation token");
        var approved=await Call(bridge,"historical.saveWorldReviewApply",
            new{token});
        Check(approved.GetProperty("ok").GetBoolean()&&
            approved.GetProperty("data").GetProperty("reviewed").GetBoolean()&&
            approved.GetProperty("data").GetProperty("oldReceiptArchived").GetBoolean(),
            "scoped native token reviews both journals without sending RCON");
        var secondApproved=await Call(bridge,"historical.saveWorldReviewApply",
            new{token=secondToken});
        Check(secondApproved.GetProperty("ok").GetBoolean()&&
              secondApproved.GetProperty("data").GetProperty("mapId").GetGuid()==secondMap.Id,
            "second map's review succeeds after first map commit, without replay");
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
            new{token});
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

        // Simulate an interrupted local receipt publisher, not an ASA/Store
        // command. The strict mutating preflights must still fail closed,
        // while recovery pages must expose the exact affected map and retain
        // all local journal bytes for operator investigation.
        string receiptDirectory=Path.Combine(cluster.Store.Root,
            "historical-save-evidence-v1");
        string interruptedReceiptNext=Path.Combine(receiptDirectory,
            map.Id.ToString("N")+".json.next");
        byte[] incomplete={0x53,0x54,0x4f,0x52,0x45,0x2d,0x56,0x31};
        File.WriteAllBytes(interruptedReceiptNext,incomplete);
        byte[] originalEvidence=File.ReadAllBytes(Path.Combine(
            receiptDirectory,map.Id.ToString("N")+".json"));
        var deniedExport=await Call(bridge,"historical.saveWorldForensicExport",
            new{mapId=map.Id,confirm=false});
        Check(!deniedExport.GetProperty("ok").GetBoolean(),
            "unreadable receipt forensic copy requires explicit operator click");
        var exported=await Call(bridge,"historical.saveWorldForensicExport",
            new{mapId=map.Id,confirm=true});
        Check(exported.GetProperty("ok").GetBoolean()&&
              exported.GetProperty("data").GetProperty("originalUntouched").GetBoolean()&&
              !exported.GetProperty("data").GetProperty("grantsRecovery").GetBoolean()&&
              !exported.GetProperty("data").GetProperty("sendsRcon").GetBoolean()&&
              exported.GetProperty("data").GetProperty("bytes").GetInt64()==
                  incomplete.Length,
            "operator exports malformed next file without granting new ASA action");
        string archivedForensics=Path.Combine(cluster.Store.Root,
            "historical-save-evidence-forensic-v1",
            exported.GetProperty("data").GetProperty("fileName").GetString()!);
        Check(File.ReadAllBytes(archivedForensics).SequenceEqual(incomplete)&&
              File.ReadAllBytes(interruptedReceiptNext).SequenceEqual(incomplete)&&
              File.ReadAllBytes(Path.Combine(
                  receiptDirectory,map.Id.ToString("N")+".json"))
                  .SequenceEqual(originalEvidence),
            "forensic export is byte-identical and leaves original .next and receipt intact");
        var degradedEvidence=await Call(bridge,"historical.saveWorldEvidence",
            new{mapId=map.Id});
        Check(degradedEvidence.GetProperty("ok").GetBoolean()&&
              degradedEvidence.GetProperty("data").GetProperty("phase")
                .GetString()=="unreadable"&&
              degradedEvidence.GetProperty("data").GetProperty("unreadable")
                .GetBoolean()&&
              degradedEvidence.GetProperty("data").GetProperty("errorCode")
                .GetString()=="UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE"&&
              !degradedEvidence.GetProperty("data")
                .GetProperty("localSaveCompleteObserved").GetBoolean(),
            "SaveWorld evidence remains readable as an explicit corrupted-receipt blocker");
        var degradedRecovery=await Call(bridge,"historical.gracefulStopRecovery",
            new{mapId=map.Id});
        Check(degradedRecovery.GetProperty("ok").GetBoolean()&&
              degradedRecovery.GetProperty("data").GetProperty("blocked")
                .GetBoolean()&&
              degradedRecovery.GetProperty("data").GetProperty("requiresOperator")
                .GetBoolean()&&
              degradedRecovery.GetProperty("data").GetProperty("saveEvidenceUnreadable")
                .GetBoolean()&&
              degradedRecovery.GetProperty("data").GetProperty("saveEvidencePhase")
                .GetString()=="unreadable",
            "Stop recovery identifies unreadable SaveWorld on only the affected map");
        using(var restart=new DesktopHistoricalRestartBridge(cluster))
        {
            using var restartResponse=JsonDocument.Parse(await restart.HandleAsync(
                JsonSerializer.Serialize(new{id=Guid.NewGuid(),method=
                    "historical.restartRecovery",mapId=map.Id})));
            var restartResult=restartResponse.RootElement;
            Check(restartResult.GetProperty("ok").GetBoolean()&&
                  restartResult.GetProperty("data").GetProperty("blocked")
                    .GetBoolean()&&
                  restartResult.GetProperty("data").GetProperty("saveEvidencePending")
                    .GetBoolean()&&
                  restartResult.GetProperty("data").GetProperty("saveEvidenceUnreadable")
                    .GetBoolean()&&
                  restartResult.GetProperty("data").GetProperty("saveEvidenceErrorCode")
                    .GetString()=="UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE",
                "Restart recovery reports the corrupt local SaveWorld without retry");
            using var siblingResponse=JsonDocument.Parse(await restart.HandleAsync(
                JsonSerializer.Serialize(new{id=Guid.NewGuid(),method=
                    "historical.restartRecovery",mapId=secondMap.Id})));
            Check(siblingResponse.RootElement.GetProperty("ok").GetBoolean()&&
                  !siblingResponse.RootElement.GetProperty("data")
                    .GetProperty("blocked").GetBoolean(),
                "an unreadable SaveWorld for one map does not block another map");
            using var refusedRestart=JsonDocument.Parse(await restart.HandleAsync(
                JsonSerializer.Serialize(new{id=Guid.NewGuid(),method=
                    "historical.restartPreview",mapId=map.Id})));
            Check(!refusedRestart.RootElement.GetProperty("ok").GetBoolean(),
                "strict Restart preflight still refuses unreadable SaveWorld evidence");
        }
        var refusedSave=await Call(bridge,"historical.saveWorldPreview",
            new{mapId=map.Id});
        Check(!refusedSave.GetProperty("ok").GetBoolean()&&
              File.ReadAllBytes(interruptedReceiptNext).SequenceEqual(incomplete)&&
              File.ReadAllBytes(Path.Combine(receiptDirectory,
                map.Id.ToString("N")+".json")).SequenceEqual(originalEvidence)&&
              File.Exists(archived),
            "unreadable SaveWorld blocks new commands without deleting .next or any receipt");
        // A directory occupying the atomic .next slot is equally unsafe:
        // File.Exists returns false for directories, so Inspect must reject
        // this case instead of presenting the original receipt as valid.
        File.Delete(interruptedReceiptNext);
        Directory.CreateDirectory(interruptedReceiptNext);
        var directoryEvidence=await Call(bridge,"historical.saveWorldEvidence",
            new{mapId=map.Id});
        Check(directoryEvidence.GetProperty("ok").GetBoolean()&&
              directoryEvidence.GetProperty("data").GetProperty("unreadable")
                .GetBoolean()&&
              directoryEvidence.GetProperty("data").GetProperty("phase")
                .GetString()=="unreadable"&&
              Directory.Exists(interruptedReceiptNext)&&
              File.ReadAllBytes(Path.Combine(receiptDirectory,
                map.Id.ToString("N")+".json")).SequenceEqual(originalEvidence),
            "directory-shaped interrupted SaveWorld receipt also blocks without mutation");
        Directory.Delete(interruptedReceiptNext);
        string canonicalReceipt=Path.Combine(receiptDirectory,
            map.Id.ToString("N")+".json");
        string preservedReceipt=canonicalReceipt+".synthetic-saved";
        File.Move(canonicalReceipt,preservedReceipt);
        Directory.CreateDirectory(canonicalReceipt);
        var disguised=await Call(bridge,"historical.saveWorldEvidence",
            new{mapId=map.Id});
        Check(disguised.GetProperty("ok").GetBoolean()&&
              disguised.GetProperty("data").GetProperty("unreadable")
                .GetBoolean()&&
              disguised.GetProperty("data").GetProperty("phase")
                .GetString()=="unreadable"&&
              Directory.Exists(canonicalReceipt)&&
              File.ReadAllBytes(preservedReceipt).SequenceEqual(originalEvidence),
            "directory masquerading as canonical SaveWorld receipt never counts as absent");
        Directory.Delete(canonicalReceipt);
        File.Move(preservedReceipt,canonicalReceipt);
        await VerifyNativeOriginalStopMemoryRecovery(root);
    }
}
