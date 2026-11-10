using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

internal static class RunningAsaRegistrationTests
{
    public static async Task Run(string root)
    {
        string install=Path.Combine(root,"p0-real-live-import-synthetic");
        string bin=Path.Combine(install,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(bin);
        string exe=Path.Combine(bin,"ArkAscendedServer.exe");
        File.WriteAllBytes(exe,new byte[]{77,90,4,7});
        string world=Path.Combine(install,"ShooterGame","Saved","SavedArks","Ragnarok_WP.ark");
        Directory.CreateDirectory(Path.GetDirectoryName(world)!);
        byte[] original={7,4,2,1,0};
        File.WriteAllBytes(world,original);
        var started=DateTimeOffset.UtcNow.AddMinutes(-5);
        string command="\""+exe+"\" Ragnarok_WP?listen -Port=7817 -QueryPort=27037"+
            " -mods=100001,100002";
        var sample=new LocalRunningAsaDiscovery.Sample(71221,200,started,
            "ArkAscendedServer.exe",exe,command);
        LocalRunningAsaDiscovery.Sample[] observations={sample};
        IReadOnlyList<LocalRunningAsaDiscovery.Sample> Observe()=>observations;

        static void Check(bool yes,string caseName)
        {
            if(!yes)throw new InvalidOperationException("LIVE REGISTRATION TEST FAILED: "+caseName);
            Console.WriteLine("PASS running ASA read-only registration: "+caseName);
        }
        static async Task<JsonElement> Call(
            DesktopRunningAsaRegistrationBridge bridge,string method,object payload)
        {
            string body=JsonSerializer.Serialize(payload);
            using var doc=JsonDocument.Parse(body);
            var fields=doc.RootElement.EnumerateObject()
                .ToDictionary(p=>p.Name,p=>(object?)p.Value.Clone());
            fields["id"]=Guid.NewGuid().ToString("D");
            fields["method"]=method;
            string json=JsonSerializer.Serialize(fields);
            string reply=await bridge.HandleAsync(json);
            using var result=JsonDocument.Parse(reply);
            return result.RootElement.Clone();
        }

        string profile=Path.Combine(root,"p0-running-profile");
        await using(var cluster=new ClusterService("p0-running",profile,
            _=>new ProcessCheck(false,"synthetic unowned process",null,true)))
        using(var bridge=new DesktopRunningAsaRegistrationBridge(cluster,Observe))
        {
            var invalid=await Call(bridge,"servers.runningPreview",
                new{pid=71221,startedUtc=started.ToString("O"),name="Ragnarok",
                    gamePort=7900,queryPort=27037});
            Check(!invalid.GetProperty("ok").GetBoolean(),
                "manual port cannot contradict actual process command");
            var preview=await Call(bridge,"servers.runningPreview",
                new{pid=71221,startedUtc=started.ToString("O"),name="Ragnarok",
                    gamePort=7817,queryPort=27037});
            Check(preview.GetProperty("ok").GetBoolean()&&
                !preview.GetProperty("data").GetProperty("lifecycleAuthorized").GetBoolean()&&
                cluster.Config.LocalMaps.Count==0,
                "exact PID/started/exe preview never writes config or grants lifecycle");
            string token=preview.GetProperty("data").GetProperty("token").GetString()!;
            observations=new[]{sample with{StartedUtc=started.AddSeconds(9)}};
            var pidReused=await Call(bridge,"servers.runningRegister",
                new{token,confirm=true});
            Check(!pidReused.GetProperty("ok").GetBoolean()&&
                cluster.Config.LocalMaps.Count==0,
                "changed process start time revokes single-use approval");
            observations=new[]{sample with{CommandLine=command+" -usestore"}};
            var blockedStore=await Call(bridge,"servers.runningPreview",
                new{pid=71221,startedUtc=started.ToString("O"),name="Ragnarok",
                    gamePort=7817,queryPort=27037});
            Check(!blockedStore.GetProperty("ok").GetBoolean()&&
                blockedStore.GetProperty("error").GetString()
                    =="UM-LIVE-STORE-REQUIRES-HISTORICAL-IMPORT",
                "original Store process cannot be imported without historical identity");
            observations=new[]{sample with{CommandLine=command+" -converttostore"}};
            var conversion=await Call(bridge,"servers.runningPreview",
                new{pid=71221,startedUtc=started.ToString("O"),name="Ragnarok",
                    gamePort=7817,queryPort=27037});
            Check(!conversion.GetProperty("ok").GetBoolean(),
                "conversion process cannot cross the non-Store import boundary");
            observations=new[]{sample};
            preview=await Call(bridge,"servers.runningPreview",
                new{pid=71221,startedUtc=started.ToString("O"),name="Ragnarok",
                    gamePort=7817,queryPort=27037});
            token=preview.GetProperty("data").GetProperty("token").GetString()!;
            observations=new[]{sample with{CommandLine=command+" -passivemods=100003"}};
            var changed=await Call(bridge,"servers.runningRegister",
                new{token,confirm=true});
            Check(!changed.GetProperty("ok").GetBoolean()&&
                cluster.Config.LocalMaps.Count==0,
                "changed command line requires a new preflight");
            observations=new[]{sample};
            preview=await Call(bridge,"servers.runningPreview",
                new{pid=71221,startedUtc=started.ToString("O"),name="Ragnarok",
                    gamePort=7817,queryPort=27037});
            token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var done=await Call(bridge,"servers.runningRegister",
                new{token,confirm=true});
            var map=cluster.Config.LocalMaps.Single();
            Check(done.GetProperty("ok").GetBoolean() &&
                map.ObservedReadOnlyImported&&map.ObservedMapKey=="Ragnarok_WP"&&
                map.GamePort==7817&&map.QueryPort==27037&&
                map.Arguments==""&&map.RconPort==0&&map.LastProcess==null&&
                !map.AutoStart&&!map.StartWithAsaApi&&!map.HistoricalStoreProtected,
                "existing-world process registered in local config only, permanently read-only");
            Check(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map) &&
                LocalRunningAsaDiscovery.Classify(observations,cluster.Config.LocalMaps)
                    .Single().MatchState=="profileCandidate",
                "fresh lifecycle remains denied while observation reattaches");
            Check(File.ReadAllBytes(world).SequenceEqual(original) &&
                File.ReadAllBytes(exe).SequenceEqual(new byte[]{77,90,4,7}),
                "existing world and executable remain untouched");
            var replay=await Call(bridge,"servers.runningRegister",
                new{token,confirm=true});
            Check(!replay.GetProperty("ok").GetBoolean(),
                "registration token is single-use");
            var duplicate=await Call(bridge,"servers.runningPreview",
                new{pid=71221,startedUtc=started.ToString("O"),name="OtherName",
                    gamePort=7817,queryPort=27037});
            Check(!duplicate.GetProperty("ok").GetBoolean(),
                "same running map cannot be registered under a different label");
        }
        await using(var reloaded=new ClusterService("p0-running",profile,
            _=>new ProcessCheck(false,"synthetic unowned process",null,true)))
        {
            var restored=reloaded.Config.LocalMaps.Single();
            Check(restored.ObservedReadOnlyImported &&
                LocalRunningAsaDiscovery.Classify(observations,reloaded.Config.LocalMaps)
                    .Single().ProfileMapId==restored.Id.ToString("D"),
                "restart rehydrates stable map identity without persisting PID or command line");
        }
    }
}
