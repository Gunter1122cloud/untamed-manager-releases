using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class AsaReadyProbeTests
{
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new Exception("ASA readiness probe failed: "+reason);
        Console.WriteLine("PASS asa-readiness "+reason);
    }

    public static async Task Run(string testRoot)
    {
        string installation=Path.Combine(testRoot,"readiness-local-install");
        string binaries=Path.Combine(installation,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binaries);
        string serverExe=Path.Combine(binaries,"ArkAscendedServer.exe");
        string loaderExe=Path.Combine(binaries,"AsaApiLoader.exe");
        File.WriteAllText(serverExe,"Isolated binary-path fixture; never executed");
        File.WriteAllText(loaderExe,"Isolated loader-path fixture; never executed");
        var map=new LocalMap {
            Id=Guid.NewGuid(),Name="Isolated ASA readiness",InstallationPath=installation,
            ExecutablePath=serverExe,Arguments="TheIsland?SessionName=Fixture -log",
            ApiLoaderPath=loaderExe,ApiLoaderArguments="--fixture-loader",
            StartWithAsaApi=true,
            RconHost="127.0.0.1",RconPort=27999,
            RconPasswordProtected="TEST_ONLY_ENCRYPTED_PLACEHOLDER"
        };
        var started=DateTimeOffset.UtcNow.AddMinutes(-5);
        var server=new ProcessIdentity {
            Pid=45670,StartedUtc=started.AddSeconds(1),
            ExecutablePath=serverExe,Arguments=map.Arguments
        };
        var loader=new ProcessIdentity {
            Pid=45671,StartedUtc=started,
            ExecutablePath=loaderExe,Arguments=map.ApiLoaderArguments
        };
        var steady=new ProcessCheck(true,"fixture observed",server,false,loader);
        Check(AsaReadyProbe.IsRegisteredServerBinary(map),
            "fixed local registered ASA/loader paths recognized without executing binaries");

        int processPolls=0,rconCalls=0;
        byte[] unchangedServer=File.ReadAllBytes(serverExe);
        var healthy=await AsaReadyProbe.ObserveAsync(map,()=>{
            processPolls++;
            return steady;
        },_=>{
            rconCalls++;
            return Task.FromResult("No Players Connected.");
        });
        Check(healthy.Stage==AsaRuntimeStage.RconAuthenticated &&
            healthy.LoaderObserved && processPolls==2 && rconCalls==1,
            "process and loader identity rechecked across authenticated RCON fixture");
        Check(!healthy.ApiInitialized&&!healthy.JoinAdvertised&&!healthy.ProductionReady,
            "ListPlayers is not mistaken for API initialized or join advertised");
        Check(unchangedServer.AsSpan().SequenceEqual(File.ReadAllBytes(serverExe)),
            "read-only readiness never writes server executable");

        processPolls=0;
        var switched=await AsaReadyProbe.ObserveAsync(map,()=>{
            processPolls++;
            return processPolls==1?steady:steady with {
                Identity=new ProcessIdentity {
                    Pid=server.Pid,StartedUtc=server.StartedUtc.AddSeconds(10),
                    ExecutablePath=serverExe,Arguments=map.Arguments
                }
            };
        },_=>Task.FromResult("No Players Connected"));
        Check(switched.Stage==AsaRuntimeStage.ProcessVerified &&
            !switched.ProductionReady,
            "reused PID with changed start time cannot pass RCON readiness");

        processPolls=0;
        var differentLoader=await AsaReadyProbe.ObserveAsync(map,()=>{
            processPolls++;
            return processPolls==1?steady:steady with {
                LoaderIdentity=new ProcessIdentity {
                    Pid=loader.Pid+10,StartedUtc=loader.StartedUtc,
                    ExecutablePath=loaderExe,Arguments=map.ApiLoaderArguments
                }
            };
        },_=>Task.FromResult("No Players Connected"));
        Check(differentLoader.Stage==AsaRuntimeStage.ProcessVerified,
            "loader identity replacement during probe is refused");

        rconCalls=0;
        var wrongExe=await AsaReadyProbe.ObserveAsync(map,
            ()=>steady with {Identity=new ProcessIdentity {
                Pid=server.Pid,StartedUtc=server.StartedUtc,
                ExecutablePath=loaderExe,Arguments=map.Arguments}},
            _=>{rconCalls++;return Task.FromResult("No Players Connected");});
        Check(wrongExe.Stage==AsaRuntimeStage.ExecutableVerified && rconCalls==0,
            "wrong process executable rejected before RCON authentication");

        var failed=await AsaReadyProbe.ObserveAsync(map,()=>steady,
            _=>throw new IOException("password=DO_NOT_LEAK private=C:\\sensitive"));
        Check(failed.Stage==AsaRuntimeStage.ProcessVerified &&
            !failed.Explanation.Contains("DO_NOT_LEAK") &&
            !failed.Explanation.Contains("sensitive"),
            "RCON error cannot leak credentials or local paths");

        var empty=await AsaReadyProbe.ObserveAsync(map,()=>steady,
            _=>Task.FromResult(""));
        Check(empty.Stage==AsaRuntimeStage.ProcessVerified,
            "empty RCON response cannot establish authenticated readiness");

        // API ON -> OFF must not retain the Loader PID requirement.
        map.StartWithAsaApi=false;
        var directProcess=steady with {LoaderIdentity=null};
        var direct=await AsaReadyProbe.ObserveAsync(map,()=>directProcess,
            _=>Task.FromResult("No Players Connected."));
        Check(direct.Stage==AsaRuntimeStage.RconAuthenticated &&
            !direct.LoaderObserved && !direct.ProductionReady,
            "direct mode verifies ASA/RCON without requiring cached Loader PID");
        File.Delete(loaderExe);
        Check(AsaReadyProbe.IsRegisteredServerBinary(map),
            "direct mode remains valid with missing inactive cached Loader");
        map.StartWithAsaApi=true;
        Check(!AsaReadyProbe.IsRegisteredServerBinary(map),
            "API opt-in refuses a missing required Loader executable");
        File.WriteAllText(loaderExe,"Isolated loader-path fixture; never executed");

        map.Maintenance=true;
        rconCalls=0;
        var maintenance=await AsaReadyProbe.ObserveAsync(map,()=>steady,
            _=>{rconCalls++;return Task.FromResult("No Players Connected");});
        Check(maintenance.Stage==AsaRuntimeStage.ExecutableVerified && rconCalls==0,
            "maintenance state prevents network probes");
        map.Maintenance=false;

        map.RconPasswordProtected="";
        var missingCredential=await AsaReadyProbe.ObserveAsync(map,()=>steady,
            _=>{rconCalls++;return Task.FromResult("No Players Connected");});
        Check(missingCredential.Stage==AsaRuntimeStage.ProcessVerified && rconCalls==0,
            "missing local RCON credentials never invoke transport");
        map.RconPasswordProtected="TEST_ONLY_ENCRYPTED_PLACEHOLDER";

        // Current-session join advertising is independent of ListPlayers.
        // These are synthetic local logs, not a spawned or contacted ASA.
        string gameLog=Path.Combine(installation,"ShooterGame","Saved","Logs",
            "ShooterGame.log");
        Directory.CreateDirectory(Path.GetDirectoryName(gameLog)!);
        map.LogFilePath=gameLog;
        static string GameStamp(DateTimeOffset utc)=>
            utc.UtcDateTime.ToString("yyyy.MM.dd-HH.mm.ss:fff",
                System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllLines(gameLog,new[]{
            "["+GameStamp(started.AddMinutes(2))+"]Log file open",
            "["+GameStamp(started.AddMinutes(3))+"]Server has successfully started!",
            "["+GameStamp(started.AddMinutes(4))+"]Server has completed startup and is now advertising for join."
        });
        var joinOnly=await AsaReadyProbe.ObserveAsync(map,()=>steady,
            _=>Task.FromResult("No Players Connected."));
        Check(joinOnly.Stage==AsaRuntimeStage.RconAuthenticated &&
              joinOnly.JoinAdvertised && !joinOnly.ApiInitialized &&
              !joinOnly.ProductionReady && joinOnly.ApiRequired,
            "current ASA join-advertising log alone cannot certify opted-in API");

        string apiLog=Path.Combine(installation,"ShooterGame","Win64","logs",
            "ArkApi.log");
        Directory.CreateDirectory(Path.GetDirectoryName(apiLog)!);
        string apiTimestamp=DateTime.Now.ToString("MM/dd/yy HH:mm",
            System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(apiLog,apiTimestamp+
            " [API] API was successfully loaded"+Environment.NewLine);
        var loadWithoutHooks=await AsaReadyProbe.ObserveAsync(map,()=>steady,
            _=>Task.FromResult("No Players Connected."));
        Check(!loadWithoutHooks.ApiInitialized &&
              !loadWithoutHooks.ProductionReady,
            "API load log without Initialized hooks never certifies runtime");
        File.AppendAllText(apiLog,apiTimestamp+
            " [API] Initialized hooks"+Environment.NewLine);
        var apiReady=await AsaReadyProbe.ObserveAsync(map,()=>steady,
            _=>Task.FromResult("No Players Connected."));
        Check(apiReady.ProductionReady && apiReady.JoinAdvertised &&
              apiReady.ApiInitialized && apiReady.LoaderObserved,
            "opted-in API runtime readiness requires matching loader-run log");
        File.Delete(apiLog);
        var missingApi=await AsaReadyProbe.ObserveAsync(map,()=>steady,
            _=>Task.FromResult("No Players Connected."));
        Check(!missingApi.ProductionReady && !missingApi.ApiInitialized,
            "missing API log never reuses a previous positive runtime proof");

        map.StartWithAsaApi=false;
        map.LogFilePath=""; // Fresh map registry has no explicitly selected log.
        var directReady=await AsaReadyProbe.ObserveAsync(map,
            ()=>steady with {LoaderIdentity=null},
            _=>Task.FromResult("No Players Connected."));
        Check(directReady.ProductionReady && directReady.JoinAdvertised &&
              !directReady.ApiRequired && !directReady.ApiInitialized,
            "direct ASA + RCON + current join-advertising log is runtime-ready without API");
        map.StartWithAsaApi=true;
        File.AppendAllText(gameLog,
            "["+GameStamp(DateTimeOffset.UtcNow)+"]Log file closed"+Environment.NewLine);
        var shutdown=await AsaReadyProbe.ObserveAsync(map,()=>steady,
            _=>Task.FromResult("No Players Connected."));
        Check(!shutdown.JoinAdvertised && !shutdown.ProductionReady,
            "closed ASA log session cannot claim an accepting server");

        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        bool cancelledBeforeInspect=false;
        try
        {
            await AsaReadyProbe.ObserveAsync(map,()=>throw new Exception("unexpected inspect"),
                _=>throw new Exception("unexpected RCON"),cancelled.Token);
        }
        catch(OperationCanceledException){cancelledBeforeInspect=true;}
        Check(cancelledBeforeInspect,"pre-cancelled probe never examines process or connects");

        var fakeMap=new LocalMap {
            Name="Fake fixture",InstallationPath=installation,
            ExecutablePath=loaderExe,RconPasswordProtected="TEST_ONLY",RconPort=27999
        };
        Check(!AsaReadyProbe.IsRegisteredServerBinary(fakeMap),
            "arbitrary loader/FakeAsa paths cannot impersonate registered ASA EXE");

        var otherMap=new LocalMap {
            Name="Other local map",InstallationPath=installation,
            GamePort=7777,RconPort=map.RconPort
        };
        Check(AsaReadyProbe.HasPortConflict(map,new[]{map,otherMap}),
            "duplicate local RCON port cannot claim one map's readiness");
        otherMap.RconPort=27998;otherMap.GamePort=7777;map.GamePort=7777;
        Check(AsaReadyProbe.HasPortConflict(map,new[]{map,otherMap}),
            "duplicate local game port is detected across owned maps");
        map.GamePort=0;otherMap.RconPort=map.RconPort;
        string originalArguments=map.Arguments;
        map.Arguments+=" ?RCONPort=27998";
        Check(AsaReadyProbe.DeclaredPortMismatch(map),
            "RCON endpoint disagreement with launch arguments fails preflight");
        map.Arguments=originalArguments+" ?RCONPort="+map.RconPort;
        Check(!AsaReadyProbe.DeclaredPortMismatch(map),
            "correctly matching explicit RCON port is accepted");
        map.Arguments=originalArguments;

        await using(var owner=new ClusterService("ready-fixture",
            Path.Combine(testRoot,"readiness-owner")))
        {
            owner.AddMap(map);
            owner.AddMap(otherMap);
            var collision=await AsaReadyProbe.ProbeAsync(owner,map.Id);
            Check(collision.Stage==AsaRuntimeStage.Unconfigured && !collision.ProductionReady,
                "owned profile port collision blocks readiness before contacting RCON");
            bool foreignRefused=false;
            try{await AsaReadyProbe.ProbeAsync(owner,Guid.NewGuid());}
            catch(UnauthorizedAccessException){foreignRefused=true;}
            Check(foreignRefused,"other Manager's unregistered map cannot be probed");
        }
    }
}
