using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

/// <summary>
/// Two independent, locally owned ASA server processes on the SAME Windows
/// Manager instance. The test never opens TCP, launches ASA, or reads live
/// Store/character files. It checks the real native WebView2 Stop bridge,
/// including its one-shot approval and cross-map job status endpoints.
/// </summary>
internal static class MapScopedNativeLifecycleJobsTests
{
    private static void Check(bool ok,string claim)
    {
        if(!ok)throw new InvalidOperationException(
            "MULTI-MAP NATIVE LIFECYCLE: "+claim);
        Console.WriteLine("PASS multi-map native lifecycle: "+claim);
    }
    private static async Task<JsonElement> Call(
        Func<string,Task<string>> handle,string method,object args)
    {
        using var parameters=JsonDocument.Parse(JsonSerializer.Serialize(args));
        var envelope=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        foreach(var field in parameters.RootElement.EnumerateObject())
            envelope[field.Name]=field.Value.Clone();
        var raw=await handle(JsonSerializer.Serialize(envelope));
        using var response=JsonDocument.Parse(raw);
        return response.RootElement.Clone();
    }
    private static JsonElement Data(JsonElement response,string why)
    {
        Check(response.GetProperty("ok").GetBoolean(),why);
        return response.GetProperty("data");
    }
    public static async Task Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"multi-map-stop-job-isolation");
        Directory.CreateDirectory(root);
        LocalMap[] maps=new LocalMap[2];
        ProcessIdentity[] processes=new ProcessIdentity[2];
        for(int i=0;i<2;i++)
        {
            string installation=Path.Combine(root,"native-asa-"+i);
            string binary=Path.Combine(installation,
                "ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllBytes(binary,new byte[]{0x4d,0x5a,0x90,0x00});
            maps[i]=new LocalMap{
                Name="Synthetic independent ASA map "+i,
                InstallationPath=installation,
                ExecutablePath=binary,
                Arguments=(i==0?"TheIsland_WP":"Ragnarok_WP")+
                    "?listen -port="+(7900+i)+" -QueryPort="+(27080+i),
                GamePort=7900+i,QueryPort=27080+i,RconPort=37570+i,
                RconHost="127.0.0.1",
                RconPasswordProtected=SecureStore.Protect(
                    "SYNTHETIC-MULTIMAP-LOCAL-CREDENTIAL")
            };
            processes[i]=new ProcessIdentity{
                Pid=44801+i,
                StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-5),
                ExecutablePath=binary,
                Arguments=maps[i].Arguments
            };
        }
        await using var cluster=new ClusterService("multi-map-native-isolation",
            Path.Combine(root,"manager-profile"),map=>{
                int index=map.Id==maps[0].Id?0:map.Id==maps[1].Id?1:-1;
                return index<0
                    ?new ProcessCheck(false,"unknown synthetic map",null,false)
                    :new ProcessCheck(true,"owned synthetic ASA",processes[index]);
            });
        foreach(var map in maps)cluster.AddMap(map);
        Check(maps.All(map=>AsaReadyProbe.IsRegisteredServerBinary(map)&&
              FreshAsaInstallationSafety.IsProvenFresh(cluster,map)),
            "both local ASA install roots are independent and eligible");
        var firstReleased=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched=new List<Guid>();
        Action<string>? lateStopProgress=null;
        using var bridge=new DesktopFreshAsaStopBridge(cluster,
            (port,pid)=>(port==37570&&pid==44801)||
                        (port==37571&&pid==44802),
            null,
            async (map,identity,stage)=>{
                if(map==maps[0].Id)lateStopProgress=stage;
                lock(dispatched)dispatched.Add(map);
                if(map==maps[0].Id)
                    await firstReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return new AsaGracefulStopReceipt(map,identity.Pid,
                    true,false,true,DateTimeOffset.UtcNow,
                    "SYNTHETIC_DISTINCT_MAP_GRACEFUL_STOP_NO_TCP");
            });
        string[] jobIds=new string[2];
        string[] tokens=new string[2];
        for(int i=0;i<2;i++)
        {
            var preview=Data(await Call(bridge.HandleAsync,
                "servers.gracefulStopPreview",new{mapId=maps[i].Id}),
                "map "+i+" receives an independent owned local Stop preview");
            Check(preview.GetProperty("mapId").GetGuid()==maps[i].Id,
                "preview is bound to its own local map");
            tokens[i]=preview.GetProperty("token").GetString()!;
        }
        Check(tokens[0]!=tokens[1],
            "Stop approvals are independent even before either is applied");
        for(int i=0;i<2;i++)
        {
            string token=tokens[i];
            var accepted=Data(await Call(bridge.HandleAsync,
                "servers.gracefulStopApply",new{token,confirm=true}),
                "map "+i+" queues exactly one native graceful Stop");
            jobIds[i]=accepted.GetProperty("jobId").GetString()!;
            Check(!string.IsNullOrWhiteSpace(jobIds[i]),
                "native Stop job has a real unique ID");
            var replay=await Call(bridge.HandleAsync,
                "servers.gracefulStopApply",new{token,confirm=true});
            Check(!replay.GetProperty("ok").GetBoolean(),
                "an already consumed Stop token cannot dispatch twice");
        }
        Check(jobIds[0]!=jobIds[1],
            "sibling ASA map jobs never reuse one job ID");
        var first=Data(await Call(bridge.HandleAsync,
            "servers.gracefulStopCurrent",new{mapId=maps[0].Id}),
            "original map Stop remains observable after second map starts");
        var second=Data(await Call(bridge.HandleAsync,
            "servers.gracefulStopCurrent",new{mapId=maps[1].Id}),
            "second map Stop has independent state");
        Check(first.GetProperty("jobId").GetString()==jobIds[0] &&
              first.GetProperty("active").GetBoolean() &&
              second.GetProperty("jobId").GetString()==jobIds[1],
            "a sibling map does not replace the first map's active receipt");
        var originalStatus=Data(await Call(bridge.HandleAsync,
            "servers.gracefulStopStatus",new{jobId=jobIds[0]}),
            "first job status remains accessible by exact ID");
        Check(originalStatus.GetProperty("jobId").GetString()==jobIds[0],
            "job status is never redirected to another map's operation");
        firstReleased.TrySetResult(true);
        for(int n=0;n<90;n++)
        {
            var old=Data(await Call(bridge.HandleAsync,
                "servers.gracefulStopStatus",new{jobId=jobIds[0]}),
                "first job remains readable until completion");
            if(old.GetProperty("state").GetString()=="completed")break;
            await Task.Delay(25);
        }
        var completed=Data(await Call(bridge.HandleAsync,
            "servers.gracefulStopStatus",new{jobId=jobIds[0]}),
            "first completed Stop remains readable after sibling dispatch");
        Check(completed.GetProperty("state").GetString()=="completed",
            "first native Stop completes independently");
        Check(lateStopProgress!=null,
            "first ASA Stop retained its exact injected progress callback");
        lateStopProgress!("DELAYED FRESH STOP PROGRESS");
        var stableStop=Data(await Call(bridge.HandleAsync,
            "servers.gracefulStopStatus",new{jobId=jobIds[0]}),
            "final Stop status remains queryable after delayed progress");
        Check(stableStop.GetProperty("state").GetString()=="completed"&&
              stableStop.GetProperty("step").GetString()==
                "Windows process verified stopped",
            "late ASA Stop callback cannot reopen a verified terminated process");
        lock(dispatched)Check(dispatched.Count==2 &&
            dispatched.Contains(maps[0].Id)&&
            dispatched.Contains(maps[1].Id),
            "both ASA processes receive only their own one approved action");
        await VerifyOriginalStoreSaveWorldIsolated(root);
        await VerifyFreshRestartJobs(root);

    }
    private static async Task VerifyOriginalStoreSaveWorldIsolated(string baseRoot)
    {
        string root=Path.Combine(baseRoot,"two-independent-original-store-maps");
        Directory.CreateDirectory(root);
        var maps=new LocalMap[2];
        var processes=new ProcessIdentity[2];
        for(int i=0;i<2;i++)
        {
            string install=Path.Combine(root,"historical-map-"+i);
            string binary=Path.Combine(install,
                "ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllBytes(binary,new byte[]{0x4d,0x5a,0x01,0x02});
            maps[i]=new LocalMap{
                Id=Guid.NewGuid(),Name="Isolated original Store map "+i,
                HistoricalStoreProtected=true,
                HistoricalMapKey=i==0?"TheIsland_WP":"Ragnarok_WP",
                HistoricalArchiveSha256=new string('A',64),
                InstallationPath=install,ExecutablePath=binary,
                GamePort=7930+i,QueryPort=27130+i,RconPort=37610+i,
                RconHost="127.0.0.1",
                RconPasswordProtected=SecureStore.Protect(
                    "SYNTHETIC-ORIGINAL-STORE-RCON")
            };
            processes[i]=new ProcessIdentity{
                Pid=44901+i,
                StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-15),
                ExecutablePath=binary,
                Arguments=(i==0?"TheIsland_WP":"Ragnarok_WP")+
                    "?listen -usestore"
            };
        }
        await using var cluster=new ClusterService(
            "multi-map-original-stop-isolation",
            Path.Combine(root,"isolated-manager-profile"),
            _=>new ProcessCheck(false,"synthetic no external ASA",null,true));
        foreach(var map in maps)cluster.AddMap(map);
        var sourceSha=new string('E',64);
        var releaseFirst=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saved=new List<Guid>();
        var lateSaveProgress=new Action<string>?[2];
        LegacyStoreV1GracefulStop.Owned Probe(Guid mapId)
        {
            int idx=mapId==maps[0].Id?0:mapId==maps[1].Id?1:-1;
            if(idx<0)throw new UnauthorizedAccessException(
                "UM-TEST-NO-FOREIGN-STORE-MAP");
            return new(maps[idx],processes[idx],sourceSha);
        }
        Task<AsaGracefulStopReceipt> ForbiddenStop(
            Guid mapId,int pid,DateTimeOffset started,string source,
            Action<string> progress)=>
            Task.FromException<AsaGracefulStopReceipt>(
                new InvalidOperationException("test never executes DoExit"));
        async Task<LegacyStoreV1GracefulStop.SaveOnlyReceipt> SaveOnly(
            Guid mapId,int pid,DateTimeOffset started,string source,
            Action<string> progress)
        {
            lateSaveProgress[mapId==maps[0].Id?0:1]=progress;
            lock(saved)saved.Add(mapId);
            if(mapId==maps[0].Id)
                await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new LegacyStoreV1GracefulStop.SaveOnlyReceipt(
                mapId,pid,true,DateTimeOffset.UtcNow,
                "SYNTHETIC_SAVEWORLD_LOCAL_LOG_WITH_NO_RCON");
        }
        using var bridge=new DesktopHistoricalGracefulStopBridge(
            cluster,Probe,ForbiddenStop,SaveOnly);
        string[] jobIds=new string[2];
        string[] tokens=new string[2];
        for(int i=0;i<2;i++)
        {
            var preview=Data(await Call(bridge.HandleAsync,
                "historical.saveWorldPreview",new{mapId=maps[i].Id}),
                "original Store map "+i+" receives a distinct SaveWorld preview");
            Check(preview.GetProperty("mapId").GetGuid()==maps[i].Id,
                "Store SaveWorld approval remains map-owned");
            tokens[i]=preview.GetProperty("token").GetString()!;
        }
        Check(tokens[0]!=tokens[1],
            "Store SaveWorld approvals for both maps coexist");
        for(int i=0;i<2;i++)
        {
            string token=tokens[i];
            var accepted=Data(await Call(bridge.HandleAsync,
                "historical.saveWorldApply",
                new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD"}),
                "original Store SaveWorld "+i+" queues without DoExit");
            jobIds[i]=accepted.GetProperty("jobId").GetString()!;
            var replay=await Call(bridge.HandleAsync,
                "historical.saveWorldApply",
                new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD"});
            Check(!replay.GetProperty("ok").GetBoolean(),
                "Store SaveWorld approval is one-shot even across maps");
        }
        Check(jobIds[0]!=jobIds[1],
            "Store SaveWorld jobs have unique IDs for different maps");
        var stillRunning=Data(await Call(bridge.HandleAsync,
            "historical.gracefulStopRecovery",new{mapId=maps[0].Id}),
            "first Store map remains tracked after sibling dispatch");
        Check(stillRunning.GetProperty("jobId").GetString()==jobIds[0]&&
              stillRunning.GetProperty("inMemoryRunning").GetBoolean()&&
              stillRunning.GetProperty("activeJobKind").GetString()=="SaveWorldOnly",
            "Store SaveWorld source map still reports the correct in-flight job");
        var secondJob=Data(await Call(bridge.HandleAsync,
            "historical.saveWorldStatus",new{jobId=jobIds[1]}),
            "second Store SaveWorld status remains separately addressable");
        Check(secondJob.GetProperty("jobId").GetString()==jobIds[1],
            "Store SaveWorld sibling receipt does not replace first receipt");
        releaseFirst.TrySetResult(true);
        for(int n=0;n<90;n++)
        {
            var current=Data(await Call(bridge.HandleAsync,
                "historical.saveWorldStatus",new{jobId=jobIds[0]}),
                "first SaveWorld native status continues after second started");
            if(current.GetProperty("state").GetString()=="completed")break;
            await Task.Delay(25);
        }
        var completed=Data(await Call(bridge.HandleAsync,
            "historical.saveWorldStatus",new{jobId=jobIds[0]}),
            "completed original SaveWorld survives sibling operation");
        Check(completed.GetProperty("state").GetString()=="completed",
            "original Store SaveWorld completed without losing its job ID");
        Check(lateSaveProgress[0]!=null,
            "original Store SaveWorld captured the exact map progress callback");
        lateSaveProgress[0]!("DELAYED ORIGINAL SAVEWORLD PROGRESS");
        var stableSave=Data(await Call(bridge.HandleAsync,
            "historical.saveWorldStatus",new{jobId=jobIds[0]}),
            "original SaveWorld final receipt survives delayed evidence");
        Check(stableSave.GetProperty("state").GetString()=="completed"&&
              stableSave.GetProperty("step").GetString()==
                "Store-v1 SaveWorld igazolva; az ASA tovább fut",
            "late Store SaveWorld RCON callback cannot erase verified final save");
        lock(saved)Check(saved.Count==2&&
            saved.Contains(maps[0].Id)&&saved.Contains(maps[1].Id),
            "exactly one synthetic SaveWorld action per original Store map");

        // The same original Store bridge must not cancel map A's Start
        // approval just because the administrator previews map B.
        var launches=new List<Guid>();
        using(var starts=new DesktopHistoricalActiveStartBridge(cluster,
            preparation:map=>new LegacyStoreV1NativeStartPlan(
                (map.Id==maps[0].Id?"TheIsland_WP":"Ragnarok_WP")+
                    "?listen -Port="+map.GamePort+" -usestore -log",
                new string('B',64),map.GamePort,map.QueryPort,map.RconPort,
                "ISOLATED_ORIGINAL_STORE_START_SOURCE",false),
            launcher:(mapId,fingerprint,confirmed)=>{
                Check(confirmed&&fingerprint==new string('B',64),
                    "Store Start uses the approved pinned fingerprint only");
                lock(launches)launches.Add(mapId);
                int idx=mapId==maps[0].Id?0:1;
                return new ProcessIdentity{
                    Pid=45101+idx,StartedUtc=DateTimeOffset.UtcNow,
                    ExecutablePath=maps[idx].ExecutablePath,
                    Arguments="SYNTHETIC_ORIGINAL_STORE_START_ONLY"
                };
            }))
        {
            var startTokens=new string[2];
            for(int i=0;i<2;i++)
            {
                var plan=Data(await Call(starts.HandleAsync,
                    "historical.activeStartPreview",new{mapId=maps[i].Id}),
                    "original Store Start preview for map "+i);
                Check(plan.GetProperty("mapId").GetGuid()==maps[i].Id,
                    "native Store Start approval is map-specific");
                startTokens[i]=plan.GetProperty("token").GetString()!;
            }
            for(int i=0;i<2;i++)
            {
                var result=Data(await Call(starts.HandleAsync,
                    "historical.activeStartApply",
                    new{token=startTokens[i],confirm=true,
                        typedApproval="STORE-V1 ACTIVE START"}),
                    "original Store Start token survives sibling preview");
                Check(result.GetProperty("mapId").GetGuid()==maps[i].Id,
                    "each exact original Store map Start returns its own PID receipt");
                var repeated=await Call(starts.HandleAsync,
                    "historical.activeStartApply",
                    new{token=startTokens[i],confirm=true,
                        typedApproval="STORE-V1 ACTIVE START"});
                Check(!repeated.GetProperty("ok").GetBoolean(),
                    "both Store Start tokens are consumed exactly once");
            }
        }
        lock(launches)Check(launches.Count==2&&
            launches.Contains(maps[0].Id)&&launches.Contains(maps[1].Id),
            "both original Store maps independently reached the injected launcher once");

        // Changing the NEXT Loader preference never launches the ASA server.
        // The two map approvals must coexist even when both initially use
        // the same direct-ASA mode and need their first explicit sign-off.
        foreach(var map in maps)map.HistoricalLaunchModeNeedsApproval=true;
        using(var modes=new DesktopHistoricalActiveStartBridge(cluster,
            preparation:null,launcher:null,
            loaderModePreflight:(map,useApi)=>true))
        {
            var modeApprovalTokens=new string[2];
            for(int i=0;i<2;i++)
            {
                var preview=Data(await Call(modes.HandleAsync,
                    "historical.loaderModePreview",
                    new{mapId=maps[i].Id,useApi=false}),
                    "historical Loader preference can be reviewed for each map");
                modeApprovalTokens[i]=preview.GetProperty("token").GetString()!;
            }
            for(int i=0;i<2;i++)
            {
                var applied=Data(await Call(modes.HandleAsync,
                    "historical.loaderModeApply",
                    new{token=modeApprovalTokens[i],confirm=true}),
                    "original Loader preference remains approved after sibling preview");
                Check(applied.GetProperty("mapId").GetGuid()==maps[i].Id&&
                      !applied.GetProperty("serverStarted").GetBoolean()&&
                      !applied.GetProperty("originalStoreTouched").GetBoolean(),
                    "Loader mode selection writes only its own local Manager profile");
            }
        }
        Check(maps.All(map=>!map.HistoricalLaunchModeNeedsApproval),
            "Loader mode approvals are separate and consumed per original ASA map");
    }

    private static async Task VerifyFreshRestartJobs(string baseRoot)
    {
        string root=Path.Combine(baseRoot,"two-independent-restart-maps");
        Directory.CreateDirectory(root);
        var maps=new LocalMap[2];
        var processes=new ProcessIdentity[2];
        for(int i=0;i<2;i++)
        {
            string install=Path.Combine(root,"restart-map-"+i);
            string binary=Path.Combine(install,
                "ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllBytes(binary,new byte[]{0x4d,0x5a,0x90,0x00});
            maps[i]=new LocalMap{
                Name="Independent restart ASA map "+i,
                InstallationPath=install,ExecutablePath=binary,
                Arguments=(i==0?"TheIsland_WP":"Ragnarok_WP")+
                    "?listen -port="+(7940+i)+" -QueryPort="+(27140+i),
                GamePort=7940+i,QueryPort=27140+i,RconPort=37620+i,
                RconHost="127.0.0.1",
                RconPasswordProtected=SecureStore.Protect(
                    "SYNTHETIC-MULTIMAP-RESTART-CREDENTIAL")
            };
            processes[i]=new ProcessIdentity{
                Pid=45001+i,
                StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-5),
                ExecutablePath=binary,Arguments=maps[i].Arguments
            };
            string manifest=Path.Combine(install,"steamapps","appmanifest_2430930.acf");
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            File.WriteAllText(manifest,
                "\"AppState\"\r\n{\r\n\"appid\" \"2430930\"\r\n"+
                "\"buildid\" \"123456\"\r\n}\r\n");
            AsaInitialRconSettings.CreateForNewServer(install,maps[i].RconPort,
                "SYNTHETIC-MULTIMAP-RESTART-CREDENTIAL");
        }
        await using var cluster=new ClusterService(
            "multi-map-native-restart-isolation",
            Path.Combine(root,"manager-profile"),map=>{
                int index=map.Id==maps[0].Id?0:map.Id==maps[1].Id?1:-1;
                return index<0
                    ?new ProcessCheck(false,"unregistered synthetic map",null,false)
                    :new ProcessCheck(true,"owned synthetic restart ASA",processes[index]);
            });
        foreach(var map in maps)cluster.AddMap(map);
        Check(maps.All(map=>AsaRconConfigurationEvidence.Matches(map)&&
            !string.IsNullOrWhiteSpace(
                AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath))),
            "both independent ASA restarts have real local RCON INI and Steam manifests");
        var releaseFirst=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched=new List<Guid>();
        var lateRestartProgress=new Action<string>?[2];
        using var bridge=new DesktopFreshAsaRestartBridge(cluster,
            (port,pid)=>(port==37620&&pid==45001)||
                        (port==37621&&pid==45002),
            async (mapId,old,restartId,progress)=>{
                lateRestartProgress[mapId==maps[0].Id?0:1]=progress;
                lock(dispatched)dispatched.Add(mapId);
                if(mapId==maps[0].Id)
                    await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
                new AsaManualRestartJournal(maps[mapId==maps[0].Id?0:1].
                    InstallationPath).Change(restartId,AsaRestartPhase.Starting);
                var stopped=new AsaGracefulStopReceipt(mapId,old.Pid,
                    true,false,true,DateTimeOffset.UtcNow,
                    "SYNTHETIC_SAVEWORLD_OLD_ASA_EXIT");
                var started=new ProcessIdentity{
                    Pid=old.Pid+20,
                    StartedUtc=old.StartedUtc.AddMinutes(1),
                    ExecutablePath=old.ExecutablePath,
                    Arguments=old.Arguments
                };
                return (stopped,started);
            });
        string[] ids=new string[2];
        string[] tokens=new string[2];
        for(int i=0;i<2;i++)
        {
            var preview=Data(await Call(bridge.HandleAsync,
                "servers.restartPreview",new{mapId=maps[i].Id}),
                "fresh ASA map "+i+" receives its own restart preview");
            Check(preview.GetProperty("mapId").GetGuid()==maps[i].Id,
                "restart approval cannot target a different map");
            tokens[i]=preview.GetProperty("token").GetString()!;
        }
        Check(tokens[0]!=tokens[1],
            "fresh ASA Restart approval tokens do not overwrite across map cards");
        for(int i=0;i<2;i++)
        {
            string token=tokens[i];
            var accepted=Data(await Call(bridge.HandleAsync,
                "servers.restartApply",new{token,confirm=true}),
                "fresh ASA map "+i+" receives exactly one approved restart job");
            ids[i]=accepted.GetProperty("jobId").GetString()!;
            Check(!string.IsNullOrWhiteSpace(ids[i]),
                "restart job has an actual native task ID");
        }
        Check(ids[0]!=ids[1],"two ASA Restart tasks have distinct IDs");
        var original=Data(await Call(bridge.HandleAsync,
            "servers.restartCurrent",new{mapId=maps[0].Id}),
            "map A Restart can reattach after map B already started");
        var sibling=Data(await Call(bridge.HandleAsync,
            "servers.restartCurrent",new{mapId=maps[1].Id}),
            "map B Restart can reattach independently");
        Check(original.GetProperty("inMemoryJobId").GetString()==ids[0]&&
              original.GetProperty("inMemoryRunning").GetBoolean()&&
              sibling.GetProperty("inMemoryJobId").GetString()==ids[1],
            "Restart statuses are not overwritten across local map boundaries");
        var status=Data(await Call(bridge.HandleAsync,
            "servers.restartStatus",new{jobId=ids[0]}),
            "old Restart job ID resolves independently of latest map action");
        Check(status.GetProperty("jobId").GetString()==ids[0],
            "previous Restart status is not replaced by newer map job");
        releaseFirst.TrySetResult(true);
        for(int n=0;n<90;n++)
        {
            var progress=Data(await Call(bridge.HandleAsync,
                "servers.restartStatus",new{jobId=ids[0]}),
                "map A native Restart status remains queryable");
            if(progress.GetProperty("state").GetString()=="completed")break;
            await Task.Delay(25);
        }
        var done=Data(await Call(bridge.HandleAsync,
            "servers.restartStatus",new{jobId=ids[0]}),
            "first completed Restart retained its own final receipt");
        Check(done.GetProperty("state").GetString()=="completed",
            "first synthetic ASA Restart finishes after second map started");
        Check(lateRestartProgress[0]!=null,
            "fresh ASA Restart captured the source map's native progress callback");
        lateRestartProgress[0]!("DELAYED FRESH RESTART PROGRESS");
        var stableRestart=Data(await Call(bridge.HandleAsync,
            "servers.restartStatus",new{jobId=ids[0]}),
            "completed ASA Restart remains observable after late progress");
        Check(stableRestart.GetProperty("state").GetString()=="completed"&&
              !stableRestart.GetProperty("stage").GetString()!
                .Contains("DELAYED",StringComparison.Ordinal),
            "late ASA Restart callback cannot revert verified PID outcome");
        lock(dispatched)Check(dispatched.Count==2&&
            dispatched.Contains(maps[0].Id)&&dispatched.Contains(maps[1].Id),
            "one native simulated Restart dispatch per independent local map");
    }

}
