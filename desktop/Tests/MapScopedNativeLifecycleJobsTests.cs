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
        using var bridge=new DesktopFreshAsaStopBridge(cluster,
            (port,pid)=>(port==37570&&pid==44801)||
                        (port==37571&&pid==44802),
            async (map,identity)=>{
                lock(dispatched)dispatched.Add(map);
                if(map==maps[0].Id)
                    await firstReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return new AsaGracefulStopReceipt(map,identity.Pid,
                    true,false,true,DateTimeOffset.UtcNow,
                    "SYNTHETIC_DISTINCT_MAP_GRACEFUL_STOP_NO_TCP");
            });
        string[] jobIds=new string[2];
        for(int i=0;i<2;i++)
        {
            var preview=Data(await Call(bridge.HandleAsync,
                "servers.gracefulStopPreview",new{mapId=maps[i].Id}),
                "map "+i+" receives an independent owned local Stop preview");
            Check(preview.GetProperty("mapId").GetGuid()==maps[i].Id,
                "preview is bound to its own local map");
            string token=preview.GetProperty("token").GetString()!;
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
        lock(dispatched)Check(dispatched.Count==2 &&
            dispatched.Contains(maps[0].Id)&&
            dispatched.Contains(maps[1].Id),
            "both ASA processes receive only their own one approved action");
        await VerifyOriginalStoreSaveWorldIsolated(root);

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
        for(int i=0;i<2;i++)
        {
            var preview=Data(await Call(bridge.HandleAsync,
                "historical.saveWorldPreview",new{mapId=maps[i].Id}),
                "original Store map "+i+" receives a distinct SaveWorld preview");
            Check(preview.GetProperty("mapId").GetGuid()==maps[i].Id,
                "Store SaveWorld approval remains map-owned");
            string token=preview.GetProperty("token").GetString()!;
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
        lock(saved)Check(saved.Count==2&&
            saved.Contains(maps[0].Id)&&saved.Contains(maps[1].Id),
            "exactly one synthetic SaveWorld action per original Store map");
    }

}
