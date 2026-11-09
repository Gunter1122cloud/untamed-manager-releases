using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

/// <summary>
/// Synthetic native bridge contract for the REAL production orchestration:
/// exercise approvals, SHA-pinned source, durable stage receipts, interrupted
/// Manager restart and fail closed after a phase fails. No SteamCMD or RCON
/// is ever executed by this Windows test fixture.
/// </summary>
internal static class LiveSteamMaintenanceBridgeTests
{
    private static void Check(bool value,string label)
    {
        if(!value)throw new InvalidOperationException("LIVE STEAM CYCLE TEST: "+label);
        Console.WriteLine("PASS guarded SaveWorld-Stop-SteamCMD-Start: "+label);
    }
    private static async Task<JsonElement> Send(DesktopLiveSteamMaintenanceBridge bridge,
        string method,object? payload=null)
    {
        var values=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(payload!=null)
        {
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(payload));
            foreach(var entry in doc.RootElement.EnumerateObject())
                values[entry.Name]=entry.Value.Clone();
        }
        using var result=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(values)));
        return result.RootElement.Clone();
    }
    private static async Task<JsonElement> Poll(
        DesktopLiveSteamMaintenanceBridge bridge,string jobId)
    {
        JsonElement status=default;
        for(int i=0;i<250;i++)
        {
            status=await Send(bridge,"steam.liveStatus",new{jobId});
            string state=status.GetProperty("data").GetProperty("state").GetString()!;
            if(state=="completed"||state=="needsOperator")break;
            await Task.Delay(20);
        }
        return status;
    }

    public static async Task Run(string tempRoot)
    {
        string root=Path.Combine(tempRoot,"isolated-real-live-steam-cycle");
        string profile=Path.Combine(root,"manager-profile");
        string mapRoot=Path.Combine(root,"fake-map-install");
        Directory.CreateDirectory(mapRoot);
        ProcessIdentity identity=new(){
            Pid=42424,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-7),
            ExecutablePath=Path.Combine(mapRoot,"FakeAsa.exe"),
            Arguments="TheIsland_WP?SessionName=synthetic -port=7799"
        };
        var observed=new ProcessCheck(true,"Synthetic running fake ASA only",
            identity,false);
        await using var cluster=new ClusterService("synthetic-live-maintenance",profile,
            _=>observed);
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",
            InstallationPath=mapRoot,GamePort=7799,QueryPort=27799,
            RconPort=37111,RconHost="127.0.0.1",
            RconPasswordProtected="TEST_ONLY_CIPHERTEXT",
            Arguments=identity.Arguments,AutoStart=false
        };
        cluster.AddMap(map);
        using var stop=new DesktopFreshAsaStopBridge(cluster);
        using var steam=new DesktopStoppedSteamUpdateBridge(cluster);
        using var start=new DesktopFreshAsaStartBridge(cluster);
        int fakeRuns=0;
        Func<LocalMap,Action<string>,Task> full=async (_,progress)=>{
            fakeRuns++;
            progress("SAVEWORLD_STOP_PREFLIGHT");
            await Task.Delay(4);
            progress("SAVEWORLD_STABLE_ARK_DOEXIT");
            await Task.Delay(4);
            progress("STOP_VERIFIED");
            progress("STEAMCMD_BACKUP_DOWNLOAD_VERIFY");
            await Task.Delay(4);
            progress("UPDATE_VERIFIED");
            progress("ASA_PROCESS_START_REQUESTED");
            progress("ASA_PRODUCTION_READY");
        };
        using var bridge=new DesktopLiveSteamMaintenanceBridge(
            cluster,stop,steam,start,_=>observed,_=>true,full,_=>"123456");
        var empty=await Send(bridge,"steam.liveCurrent");
        Check(!empty.GetProperty("data").GetProperty("exists").GetBoolean(),
            "new native Manager has no invented running live-update job");

        // Explicitly reject original Kova Store ownership even if a fixture
        // tries to provide a happy-path process and stage executor.
        var historical=new LocalMap{
            Id=Guid.NewGuid(),Name="Historical Store",
            HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('A',64),
            InstallationPath=Path.Combine(root,"historical"),
            GamePort=8000,QueryPort=28000,RconPort=37300
        };
        cluster.AddMap(historical);
        var denied=await Send(bridge,"steam.livePreview",new{mapId=historical.Id});
        Check(!denied.GetProperty("ok").GetBoolean(),
            "original Kova Store-v1 cannot use standalone automatic Steam maintenance");

        var preview=await Send(bridge,"steam.livePreview",new{mapId=map.Id});
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("pid").GetInt32()==identity.Pid,
            "original local map identity/PID is pinned in real native preflight");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var noConsent=await Send(bridge,"steam.liveApply",
            new{token,confirm=false});
        Check(!noConsent.GetProperty("ok").GetBoolean()&&fakeRuns==0,
            "no Stop/RCON/SteamCMD process begins without operator confirmation");
        string previousArguments=map.Arguments;
        map.Arguments+=" -suspicious-change";
        var altered=await Send(bridge,"steam.liveApply",new{token,confirm=true});
        Check(!altered.GetProperty("ok").GetBoolean()&&fakeRuns==0,
            "changed original map config invalidates approved one-use native token");
        map.Arguments=previousArguments;

        preview=await Send(bridge,"steam.livePreview",new{mapId=map.Id});
        token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var started=await Send(bridge,"steam.liveApply",new{token,confirm=true});
        Check(started.GetProperty("ok").GetBoolean()&&
              started.GetProperty("data").GetProperty("started").GetBoolean(),
            "one approval queues one native orchestrated SaveWorld-Stop-Steam-Start job");
        string jobId=started.GetProperty("data").GetProperty("jobId").GetString()!;
        var result=await Poll(bridge,jobId);
        var record=result.GetProperty("data");
        Check(record.GetProperty("state").GetString()=="completed"&&
              record.GetProperty("stopVerified").GetBoolean()&&
              record.GetProperty("steamUpdateVerified").GetBoolean()&&
              record.GetProperty("startRequested").GetBoolean()&&
              record.GetProperty("productionReady").GetBoolean()&&
              fakeRuns==1,
            "completion requires ALL four independently acknowledged native phases exactly once");
        Check(!record.GetProperty("originalStoreTouched").GetBoolean()&&
              !record.GetProperty("remoteHostTouched").GetBoolean()&&
              !record.GetProperty("automaticRetry").GetBoolean(),
            "no shared Store, cross-host lock or automatic replay authorization is exported");
        var replay=await Send(bridge,"steam.liveApply",
            new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean()&&fakeRuns==1,
            "one-use approval cannot repeat DoExit or Process.Start");

        var saved=SteamLiveCycleJournal.Load(cluster.Store.Root)!;
        Check(saved.JobId==Guid.Parse(jobId)&&
              saved.Stage=="verified-asa-production-ready"&&
              saved.State=="completed",
            "real on-disk SHA-256 durable operation journal records verified completion");
        using var reopened=new DesktopLiveSteamMaintenanceBridge(
            cluster,stop,steam,start,_=>observed,_=>true,full,_=>"123456");
        var afterRestart=await Send(reopened,"steam.liveCurrent");
        Check(afterRestart.GetProperty("data").GetProperty("exists").GetBoolean()&&
              afterRestart.GetProperty("data").GetProperty("state").GetString()=="completed"&&
              fakeRuns==1,
            "Manager UI restart recovers completed journal but performs no new RCON or process launch");

        // A Manager crash after a real Stop/Steam stage is NEEDS_OPERATOR,
        // never an automatic transition to Update or Start.
        SteamLiveCycleJournal.Save(cluster.Store.Root,
            saved with{State="running",Stage="STEAMCMD_BACKUP_DOWNLOAD_VERIFY"});
        using var interrupted=new DesktopLiveSteamMaintenanceBridge(
            cluster,stop,steam,start,_=>observed,_=>true,full,_=>"123456");
        var interruptedStatus=await Send(interrupted,"steam.liveCurrent");
        Check(interruptedStatus.GetProperty("data").GetProperty("state").GetString()=="needsOperator"&&
              fakeRuns==1,
            "crash after stop/update becomes operator review, no background auto-resume");

        // A new operator must get a FRESH preflight and new token, with
        // original completed/uncertain journal retained in history.
        preview=await Send(interrupted,"steam.livePreview",new{mapId=map.Id});
        Check(preview.GetProperty("ok").GetBoolean(),
            "new operator can explicitly preflight again if server is independently running");
        token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var rerun=await Send(interrupted,"steam.liveApply",new{token,confirm=true});
        Check(rerun.GetProperty("ok").GetBoolean(),
            "approved recovery archives former operation and starts a new job");
        string rerunId=rerun.GetProperty("data").GetProperty("jobId").GetString()!;
        result=await Poll(interrupted,rerunId);
        Check(result.GetProperty("data").GetProperty("state").GetString()=="completed"&&
              fakeRuns==2,
            "freshly approved job must produce new independent verified completion");
        string historicalReceipt=Path.Combine(cluster.Store.Root,
            "maintenance-journals","history",jobId.Replace("-","")+".json");
        Check(File.Exists(historicalReceipt),
            "durable crash journal is archived byte-for-byte before a new cycle");

        // Tampered on-disk Manager journal cannot be implicitly discarded or
        // authorize an ASA/RCON job without repair; original data untouched.
        string journal=Path.Combine(cluster.Store.Root,
            "maintenance-journals","steam-live-cycle-v1.json");
        File.AppendAllText(journal,"CORRUPTION");
        using var corrupt=new DesktopLiveSteamMaintenanceBridge(
            cluster,stop,steam,start,_=>observed,_=>true,full,_=>"123456");
        var deniedCorruption=await Send(corrupt,"steam.livePreview",new{mapId=map.Id});
        Check(!deniedCorruption.GetProperty("ok").GetBoolean()&&fakeRuns==2,
            "tampered Manager-local hash-sealed journal fails closed without touching ASA");
    }
}
