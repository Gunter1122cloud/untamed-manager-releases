using System;
using System.Collections.Generic;
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
            observations=new[]{sample with{CommandLine=command+" -usestore -usestore"}};
            var duplicatedStore=await Call(bridge,"servers.runningPreview",
                new{pid=71221,startedUtc=started.ToString("O"),name="Ragnarok",
                    gamePort=7817,queryPort=27037});
            Check(!duplicatedStore.GetProperty("ok").GetBoolean(),
                "duplicate -usestore cannot bypass a telemetry cardinality check");
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
                    .Single() is {MatchState:"profileCandidate",
                        ObservedReadOnlyImported:true},
                "fresh lifecycle remains denied while observation reattaches");
            var runningEvidence=ObservedAsaDashboardTelemetry.For(map,
                LocalRunningAsaDiscovery.Classify(observations,cluster.Config.LocalMaps));
            Check(runningEvidence.Code=="observedRunning"&&
                runningEvidence.ObservedPid==71221,
                "one exact WMI profile candidate is labelled observed, not adopted");
            var duplicateEvidence=ObservedAsaDashboardTelemetry.For(map,
                LocalRunningAsaDiscovery.Classify(new[]{
                    sample,sample with{Pid=71222,StartedUtc=started.AddSeconds(1)}
                },cluster.Config.LocalMaps));
            Check(duplicateEvidence.Code=="unverified" &&
                duplicateEvidence.ObservedPid==null,
                "two matching PIDs cannot become a confirmed observed runtime");
            Check(ObservedAsaDashboardTelemetry.For(map,
                Array.Empty<LocalRunningAsaDiscovery.Observation>()).Code=="unverified",
                "WMI absence is not proof that external ASA stopped");
            Check(!cluster.Check(map).ConfirmedStopped &&
                !cluster.Check(map).Running && cluster.Check(map).Identity==null,
                "unowned observed map cannot become confirmed stopped for disk writers");
            using(var drafts=new DesktopModDraftBridge(cluster))
            using(var deniedDraft=JsonDocument.Parse(await drafts.HandleAsync(
                JsonSerializer.Serialize(new{id=Guid.NewGuid(),
                    method="mods.draftStart",mapId=map.Id}))))
                Check(!deniedDraft.RootElement.GetProperty("ok").GetBoolean(),
                    "observed map cannot enter mod draft editor");
            using(var ini=new DesktopIniBridge(cluster))
            using(var deniedIni=JsonDocument.Parse(await ini.HandleAsync(
                JsonSerializer.Serialize(new{id=Guid.NewGuid(),
                    method="ini.open",mapId=map.Id,file="GameUserSettings.ini"}))))
                Check(!deniedIni.RootElement.GetProperty("ok").GetBoolean() &&
                    deniedIni.RootElement.GetProperty("error").GetString()==
                    "UM-LIVE-OBSERVED-MAP-READONLY",
                    "observed map is denied before on-disk INI access");
            bool coreLaunchDenied=false,legacyLaunchDenied=false;
            try{cluster.LaunchFreshAsa(map.Id,true);}
            catch(UnauthorizedAccessException e) when(e.Message=="UM-LIVE-OBSERVED-MAP-READONLY")
            {coreLaunchDenied=true;}
            try{cluster.LaunchLocal(map.Id);}
            catch(UnauthorizedAccessException e) when(e.Message=="UM-LIVE-OBSERVED-MAP-READONLY")
            {legacyLaunchDenied=true;}
            Check(coreLaunchDenied&&legacyLaunchDenied,
                "both Core launch entry points deny observed foreign ASA process");
            bool coreReplacementDenied=false;
            try{cluster.ReplaceMap(map,new LocalMap{
                Id=map.Id,Name=map.Name,InstallationPath=map.InstallationPath,
                ExecutablePath=map.ExecutablePath,ObservedReadOnlyImported=false});}
            catch(UnauthorizedAccessException){coreReplacementDenied=true;}
            Check(coreReplacementDenied && map.ObservedReadOnlyImported,
                "generic Core map replacement cannot promote observed ownership");
            bool copyDenied=false;
            try{LocalModCopyPlanner.Preview(cluster,map.Id,
                new[]{Guid.NewGuid()},LocalModCopyMode.Merge);}
            catch(UnauthorizedAccessException){copyDenied=true;}
            Check(copyDenied,"observed map cannot be a mod copy source or target");
            // Native takeover is a distinct, explicit, non-Store-only workflow.
            // Synthetic WMI must NEVER be accepted as physical process evidence.
            using(var takeover=new DesktopRunningAsaAdoptionBridge(cluster,Observe))
            {
                static async Task<JsonElement> AdoptCall(
                    DesktopRunningAsaAdoptionBridge bridge,string method,object payload)
                {
                    using var input=JsonDocument.Parse(JsonSerializer.Serialize(payload));
                    var props=input.RootElement.EnumerateObject()
                        .ToDictionary(p=>p.Name,p=>(object?)p.Value.Clone());
                    props["id"]=Guid.NewGuid().ToString("D");
                    props["method"]=method;
                    using var response=JsonDocument.Parse(
                        await bridge.HandleAsync(JsonSerializer.Serialize(props)));
                    return response.RootElement.Clone();
                }
                var adoptPreview=await AdoptCall(takeover,
                    "servers.runningAdoptPreview",new{mapId=map.Id});
                Check(adoptPreview.GetProperty("ok").GetBoolean() &&
                    cluster.Config.LocalMaps.Single().ObservedReadOnlyImported,
                    "non-Store takeover preview checks observed candidate without ownership changes");
                string adoptToken=adoptPreview.GetProperty("data")
                    .GetProperty("token").GetString()!;
                var noApproval=await AdoptCall(takeover,
                    "servers.runningAdoptApply",
                    new{token=adoptToken,confirm=true,externalControllerStopped=false});
                Check(!noApproval.GetProperty("ok").GetBoolean() &&
                    map.ObservedReadOnlyImported && map.Arguments=="",
                    "missing external Manager shutdown acknowledgement denies takeover");
                var deniedTokenReplay=await AdoptCall(takeover,
                    "servers.runningAdoptApply",
                    new{token=adoptToken,confirm=true,externalControllerStopped=true});
                Check(!deniedTokenReplay.GetProperty("ok").GetBoolean(),
                    "denied takeover approval token cannot be replayed");
                adoptPreview=await AdoptCall(takeover,
                    "servers.runningAdoptPreview",new{mapId=map.Id});
                adoptToken=adoptPreview.GetProperty("data")
                    .GetProperty("token").GetString()!;
                observations=new[]{sample with{CommandLine=command+" -passivemods=100004"}};
                var changedTakeover=await AdoptCall(takeover,
                    "servers.runningAdoptApply",
                    new{token=adoptToken,confirm=true,externalControllerStopped=true});
                Check(!changedTakeover.GetProperty("ok").GetBoolean() &&
                    map.ObservedReadOnlyImported,
                    "changed live launch identity denies takeover");
                observations=new[]{sample with{CommandLine=command+" -usestore"}};
                var originalStoreDenied=await AdoptCall(takeover,
                    "servers.runningAdoptPreview",new{mapId=map.Id});
                Check(!originalStoreDenied.GetProperty("ok").GetBoolean(),
                    "a Store flag never permits registered non-Store promotion");
                observations=new[]{sample,sample with{Pid=71222}};
                var duplicateDenied=await AdoptCall(takeover,
                    "servers.runningAdoptPreview",new{mapId=map.Id});
                Check(!duplicateDenied.GetProperty("ok").GetBoolean(),
                    "ambiguous same-map live processes never authorize takeover");
                observations=new[]{sample};
                adoptPreview=await AdoptCall(takeover,
                    "servers.runningAdoptPreview",new{mapId=map.Id});
                adoptToken=adoptPreview.GetProperty("data")
                    .GetProperty("token").GetString()!;
                var syntheticCannotPromote=await AdoptCall(takeover,
                    "servers.runningAdoptApply",
                    new{token=adoptToken,confirm=true,externalControllerStopped=true});
                Check(!syntheticCannotPromote.GetProperty("ok").GetBoolean() &&
                    map.ObservedReadOnlyImported && map.LastProcess==null &&
                    map.Arguments=="",
                    "synthetic observation cannot bypass Core physical Windows WMI proof");
            }
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
        // Full native batch import: one CAS, with zero process or Store writes.
        string nextCommand=command.Replace("Ragnarok_WP","TheCenter_WP",
            StringComparison.Ordinal).Replace("-Port=7817","-Port=7827",
            StringComparison.Ordinal).Replace("-QueryPort=27037","-QueryPort=27047",
            StringComparison.Ordinal);
        var secondSample=new LocalRunningAsaDiscovery.Sample(
            71223,200,started.AddSeconds(4),
            "ArkAscendedServer.exe",exe,nextCommand);
        observations=new[]{sample,secondSample};
        await using(var batchCluster=new ClusterService("p0-batch-running",
            Path.Combine(root,"p0-batch-profile"),
            _=>new ProcessCheck(false,"synthetic unowned ASA",null,true)))
        using(var batchBridge=new DesktopRunningAsaRegistrationBridge(batchCluster,Observe))
        {
            var batchPreview=await Call(batchBridge,"servers.runningBatchPreview",new{});
            Check(batchPreview.GetProperty("ok").GetBoolean() &&
                  batchPreview.GetProperty("data").GetProperty("count").GetInt32()==2 &&
                  batchCluster.Config.LocalMaps.Count==0,
                "two separate non-Store ASA processes yield one read-only atomic preview");
            string approved=batchPreview.GetProperty("data").GetProperty("token").GetString()!;
            observations=new[]{sample,secondSample with{
                StartedUtc=secondSample.StartedUtc!.Value.AddSeconds(1)}};
            var stale=await Call(batchBridge,"servers.runningBatchRegister",
                new{token=approved,confirm=true});
            Check(!stale.GetProperty("ok").GetBoolean() &&
                  batchCluster.Config.LocalMaps.Count==0,
                "one changed PID start timestamp prevents the entire batch ConfigStore commit");
            observations=new[]{sample,secondSample};
            batchPreview=await Call(batchBridge,"servers.runningBatchPreview",new{});
            approved=batchPreview.GetProperty("data").GetProperty("token").GetString()!;
            var bulk=await Call(batchBridge,"servers.runningBatchRegister",
                new{token=approved,confirm=true});
            Check(bulk.GetProperty("ok").GetBoolean()&&
                  bulk.GetProperty("data").GetProperty("imported").GetInt32()==2&&
                  batchCluster.Config.LocalMaps.Count==2&&
                  batchCluster.Config.LocalMaps.All(x=>x.ObservedReadOnlyImported&&
                      x.RconPort==0&&!x.AutoStart&&!x.HistoricalStoreProtected&&
                      x.LastProcess==null&&x.Arguments==""),
                "two WMI-verified maps persist in one CAS without granting lifecycle or Store writer");
            var replayBatch=await Call(batchBridge,"servers.runningBatchRegister",
                new{token=approved,confirm=true});
            Check(!replayBatch.GetProperty("ok").GetBoolean()&&
                  batchCluster.Config.LocalMaps.Count==2,
                "native running ASA bulk approval cannot be replayed");
        }
        observations=new[]{sample};
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
