using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Disposable TEST-only owned-map and journal fixtures. No real ASA/Kova,
// Windows process or RCON calls: production proof is injected with a synthetic
// stopped-owner witness. This tests the actual shared coordinator/API path.
internal static class OriginalStoreStoppedJournalRecoveryTests
{
    private static void Check(bool value,string label)
    {
        if(!value)throw new InvalidOperationException(
            "STOPPED ORIGINAL STORE RECOVERY: "+label);
        Console.WriteLine("PASS stopped original Store recovery: "+label);
    }
    private static async Task<JsonElement> Call(
        DesktopAsaLifecycleJournalBridge bridge,string method,Guid mapId,
        object? extra=null)
    {
        var fields=new System.Collections.Generic.Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),
            ["method"]=method,["mapId"]=mapId.ToString("D")
        };
        if(extra!=null)
            foreach(var row in JsonSerializer.SerializeToElement(extra).EnumerateObject())
                fields[row.Name]=row.Value.Clone();
        using var doc=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(fields)));
        return doc.RootElement.Clone();
    }
    public static async Task Run(string testRoot)
    {
        string home=Path.Combine(testRoot,"stopped-original-paired-review");
        string installation=Path.Combine(home,"historical-asa-aberration");
        Directory.CreateDirectory(installation);
        await using var cluster=new ClusterService(
            "stopped-original-paired-review",
            Path.Combine(home,"manager-profile"),
            _=>new ProcessCheck(false,"synthetic stopped only",null,true));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="Aberration",
            InstallationPath=installation,HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('A',64),
            HistoricalMapKey="Aberration_WP",RconPort=37045,
            AutoStart=false,MonitorRcon=false
        };
        cluster.AddMap(map);
        using(var lease=cluster.Operations.Acquire(
            installation,UntamedOperationKind.Launch))
            lease.MarkMutationStarted();
        string key=UntamedOperationCoordinator.ResourceKey(installation);
        string common=Path.Combine(UntamedOperationCoordinator.DefaultRoot,
            key+".json");
        var before=cluster.Operations.Observe(installation)!;
        File.WriteAllText(common,JsonSerializer.Serialize(before with {
            UpdatedUtc=DateTimeOffset.UtcNow.AddMinutes(-12)
        }));
        string originalCommon=File.ReadAllText(common);
        const int oldPid=16796;
        var restarts=new AsaManualRestartJournal(installation);
        var created=restarts.Begin(map.Id,oldPid);
        restarts.Change(created.Id,AsaRestartPhase.NeedsOperator);
        string restartFile=Path.Combine(
            UntamedOperationCoordinator.DefaultRoot,"Restart-v1",key+".json");
        File.WriteAllText(restartFile,JsonSerializer.Serialize(
            restarts.Observe()! with{
                ChangedUtc=DateTimeOffset.UtcNow.AddMinutes(-12)
            }));
        string originalRestart=File.ReadAllText(restartFile);
        bool stopped=true;
        string fingerprint=new string('F',64);
        int stoppedCalls=0,runningCalls=0;
        var bridge=new DesktopAsaLifecycleJournalBridge(cluster,
            _=>{
                runningCalls++;
                throw new InvalidOperationException(
                    "UM-RECOVERY-RUNNING-PROOF-UNAVAILABLE");
            },
            (mapId,pid)=>{
                stoppedCalls++;
                if(!stopped||mapId!=map.Id||pid!=oldPid)
                    throw new InvalidDataException("UM-RECOVERY-STOPPED-NOT-VERIFIED");
                return new DesktopAsaLifecycleJournalBridge.StoppedProof(
                    map,pid,fingerprint);
            });
        var preview=await Call(bridge,"servers.lifecycleRecoveryPreview",map.Id);
        Check(preview.GetProperty("ok").GetBoolean()&&
            preview.GetProperty("data").GetProperty("reviewMode").GetString()=="stopped"&&
            preview.GetProperty("data").GetProperty("stoppedWindowsProcessVerified").GetBoolean()&&
            !preview.GetProperty("data").GetProperty("rconListenerOwnerVerified").GetBoolean()&&
            preview.GetProperty("data").GetProperty("rconListenerAbsent").GetBoolean()&&
            preview.GetProperty("data").GetProperty("pairedJournals").GetInt32()==2&&
            preview.GetProperty("data").GetProperty("pid").GetInt32()==oldPid,
            "paired historical journals offer explicit stopped-owner preview without live RCON proof");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var refused=await Call(bridge,"servers.lifecycleRecoveryApply",map.Id,new{
            token,confirm=true,ackUnknownOutcome=true,typedApproval="WRONG"
        });
        Check(!refused.GetProperty("ok").GetBoolean()&&
            File.ReadAllText(common)==originalCommon&&
            File.ReadAllText(restartFile)==originalRestart,
            "wrong operator approval preserves both original journal bytes");
        preview=await Call(bridge,"servers.lifecycleRecoveryPreview",map.Id);
        token=preview.GetProperty("data").GetProperty("token").GetString()!;
        stopped=false;
        var lost=await Call(bridge,"servers.lifecycleRecoveryApply",map.Id,new{
            token,confirm=true,ackUnknownOutcome=true,
            typedApproval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS"
        });
        Check(!lost.GetProperty("ok").GetBoolean()&&
            File.ReadAllText(common)==originalCommon&&
            File.ReadAllText(restartFile)==originalRestart,
            "lost stopped process proof fails closed with no journal mutation");
        stopped=true;
        preview=await Call(bridge,"servers.lifecycleRecoveryPreview",map.Id);
        token=preview.GetProperty("data").GetProperty("token").GetString()!;
        fingerprint=new string('E',64);
        var changed=await Call(bridge,"servers.lifecycleRecoveryApply",map.Id,new{
            token,confirm=true,ackUnknownOutcome=true,
            typedApproval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS"
        });
        Check(!changed.GetProperty("ok").GetBoolean()&&
            File.ReadAllText(common)==originalCommon&&
            File.ReadAllText(restartFile)==originalRestart,
            "stale source fingerprint refuses stopped-owner approval");
        preview=await Call(bridge,"servers.lifecycleRecoveryPreview",map.Id);
        token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var complete=await Call(bridge,"servers.lifecycleRecoveryApply",map.Id,new{
            token,confirm=true,ackUnknownOutcome=true,
            typedApproval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS"
        });
        Check(complete.GetProperty("ok").GetBoolean()&&
            complete.GetProperty("data").GetProperty("reviewMode").GetString()=="stopped"&&
            complete.GetProperty("data").GetProperty("oldStoppedPid").GetInt32()==oldPid&&
            complete.GetProperty("data").GetProperty("pairedJournalsReviewed").GetInt32()==2&&
            !complete.GetProperty("data").GetProperty("rconCommandSent").GetBoolean()&&
            !complete.GetProperty("data").GetProperty("processStarted").GetBoolean()&&
            !complete.GetProperty("data").GetProperty("restartMarkedSuccessful").GetBoolean(),
            "stopped-paired review is explicit and never invents restart success or sends commands");
        string commonAudit=Path.Combine(UntamedOperationCoordinator.DefaultRoot,
            key+"-"+before.Operation.ToString("N")+".operator-reviewed.json");
        string restartAudit=Path.Combine(
            UntamedOperationCoordinator.DefaultRoot,"Restart-v1",
            key+"-"+created.Id.ToString("N")+".operator-reviewed.json");
        Check(File.ReadAllText(commonAudit)==originalCommon&&
            File.ReadAllText(restartAudit)==originalRestart&&
            cluster.Operations.Observe(installation)?.Phase==
                UntamedOperationPhase.OperatorReviewed&&
            restarts.Observe()?.Phase==AsaRestartPhase.OperatorReviewed&&
            stoppedCalls>4&&runningCalls>0,
            "original two journal byte streams retained under audit with independently reviewed phases");
        restarts.DemandNoPending();
        bool oldTokenDenied=false;
        try{restarts.DemandAuthorizedStart(created.Id);}
        catch(InvalidOperationException){oldTokenDenied=true;}
        Check(oldTokenDenied,
            "new Start may be separately approved but old restart token can never replay");
        var replay=await Call(bridge,"servers.lifecycleRecoveryApply",map.Id,new{
            token,confirm=true,ackUnknownOutcome=true,
            typedApproval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS"
        });
        Check(!replay.GetProperty("ok").GetBoolean(),
            "consumed stopped-owner approval cannot be replayed");
    }
}
