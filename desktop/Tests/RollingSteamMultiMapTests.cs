using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

internal static class RollingSteamMultiMapTests
{
    private static void Check(bool ok,string label)
    {
        if(!ok)throw new InvalidOperationException("ROLLING MULTIMAP: "+label);
        Console.WriteLine("PASS rolling multimap: "+label);
    }
    internal static async Task Run(string root)
    {
        await Scenario(root,"success");
        await Scenario(root,"steam-failure");
        await Scenario(root,"wrong-build");
        await Scenario(root,"start-failure");
        await Scenario(root,"blocked-map");
        await Scenario(root,"interrupted-countdown");
        await Scenario(root,"interrupted-verified-files");
    }
    private static async Task Scenario(string root,string mode)
    {
        string profile=Path.Combine(root,"rolling-multimap-"+mode);
        var observed=new Dictionary<Guid,ProcessCheck>();
        await using var cluster=new ClusterService("rolling-"+mode,profile,
            map=>observed[map.Id]);
        string steamExe=Path.Combine(profile,"steamcmd.exe");
        Directory.CreateDirectory(profile);
        // Inert PE header; no process is ever launched. Also passes the
        // stricter SteamCMD preflight being developed in the other PR.
        byte[] pe=new byte[512];
        pe[0]=0x4d;pe[1]=0x5a;BitConverter.GetBytes(0x80).CopyTo(pe,0x3c);
        pe[0x80]=0x50;pe[0x81]=0x45;
        BitConverter.GetBytes((ushort)0x14c).CopyTo(pe,0x84);
        BitConverter.GetBytes((ushort)1).CopyTo(pe,0x86);
        BitConverter.GetBytes((ushort)0xe0).CopyTo(pe,0x94);
        File.WriteAllBytes(steamExe,pe);
        cluster.UpdateSettings(cfg=>cfg.SteamCmdExecutable=steamExe);
        var maps=new List<LocalMap>();var saves=new Dictionary<Guid,string>();var hashes=new Dictionary<Guid,string>();
        int pid=51000;
        ProcessCheck Running(LocalMap m)=>new(true,"isolated process epoch",
            new ProcessIdentity{Pid=++pid,StartedUtc=DateTimeOffset.UtcNow,
                ExecutablePath=m.ExecutablePath,Arguments=m.Arguments},false);
        ProcessCheck Stopped()=>new(false,"isolated stopped proof",null,true);
        string Manifest(LocalMap m)=>Path.Combine(m.InstallationPath,"steamapps","appmanifest_2430930.acf");
        void Build(LocalMap m,string build)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Manifest(m))!);
            File.WriteAllText(Manifest(m),"\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \""+build+"\"\n}\n");
        }
        for(int i=0;i<4;i++)
        {
            string install=Path.Combine(profile,"map-"+i);
            var m=new LocalMap{Name=new[]{"Astraeos","Ragnarok","The Island","Valguero"}[i],
                InstallationPath=install,ExecutablePath=Path.Combine(install,"ShooterGame","Binaries","Win64","ArkAscendedServer.exe"),
                GamePort=7000+i*10,QueryPort=27010+i,RconPort=37100+i,
                RconHost="127.0.0.1",RconPasswordProtected=SecureStore.Protect("isolated-only-password")};
            Directory.CreateDirectory(Path.GetDirectoryName(m.ExecutablePath)!);
            File.WriteAllBytes(m.ExecutablePath,new byte[]{0x4d,0x5a,0,0});
            Build(m,"123456");
            string save=Path.Combine(install,"ShooterGame","Saved","SavedArks","fixture.ark");
            Directory.CreateDirectory(Path.GetDirectoryName(save)!);
            File.WriteAllText(save,"world fixture "+m.Name);
            saves[m.Id]=save;hashes[m.Id]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(save)));
            observed[m.Id]=i==1?Stopped():Running(m);
            cluster.AddMap(m);maps.Add(m);
        }
        var a=maps[0];var b=maps[1];var c=maps[2];var disabled=maps[3];
        bool Owns(int port,int process)=>maps.Any(m=>m.RconPort==port&&observed[m.Id].Identity?.Pid==process);
        var trace=new List<string>();var warns=new List<string>();
        string journal=Path.Combine(cluster.Store.Root,"rolling-steam-v1.json");
        void Claim(LocalMap m,string stage)
        {
            // Read through the same native named mutex as the writer.
            // FILE_SHARE_DELETE alone does not guarantee Windows replace
            // semantics for an open destination handle.
            string identity=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(cluster.Store.Root)).ToUpperInvariant())));
            using var mutex=new Mutex(false,@"Global\Untamed-RollingSteam-"+identity);
            bool held=false;
            string snapshot;
            try
            {
                try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}
                catch(AbandonedMutexException){held=true;}
                Check(held,"native rolling journal read lock acquired");
                snapshot=File.ReadAllText(journal);
            }
            finally{if(held)mutex.ReleaseMutex();}
            using var doc=JsonDocument.Parse(snapshot);
            var run=doc.RootElement.GetProperty("current");
            Check(run.GetProperty("mapId").GetGuid()==m.Id&&run.GetProperty("state").GetString()=="running"&&
                run.GetProperty("stage").GetString()==stage,"durable "+stage+" claim before "+m.Name);
        }
        var chat=new DesktopRconAdminBridge(cluster,Owns,(_,port,_,command,_)=>{
            var m=maps.Single(x=>x.RconPort==port);Claim(m,"countdown");
            Check(observed[m.Id].Running&&command.StartsWith("ServerChat ",StringComparison.Ordinal),"chat only to the current running fixture");
            warns.Add(m.Name+":"+command);return Task.FromResult("synthetic RCON acknowledgment");
        });
        Task<string> Never(string _)=>throw new InvalidOperationException("Unexpected real lifecycle bridge");
        using var lifecycle=new DesktopLifecycleBatchBridge(cluster,Never,Never,Never,Never,Never,Never,Never,
            isolatedObservation:m=>observed[m.Id],
            isolatedExecution:(id,action,historical,ct)=>{
                ct.ThrowIfCancellationRequested();
                var m=maps.Single(x=>x.Id==id);Check(!historical,"fresh isolated map only");
                Claim(m,action=="stop"?"stopping":"starting");
                trace.Add(m.Name+":"+action);
                if(action=="stop")
                {
                    Check(observed[id].Running&&warns.Count(w=>w.StartsWith(m.Name+":",StringComparison.Ordinal))==5,
                        "five warnings before stop "+m.Name);
                    observed[id]=Stopped();
                    return Task.FromResult<(int,Guid?)>((0,Guid.NewGuid()));
                }
                Check(action=="start"&&observed[id].ConfirmedStopped&&
                    AsaSteamBuildCatalog.InstalledBuild(m.InstallationPath)=="123457","start only after target build verified");
                if(mode=="start-failure"&&id==a.Id)throw new InvalidOperationException("UM-TEST-START-FAILED");
                observed[id]=Running(m);
                return Task.FromResult<(int,Guid?)>((observed[id].Identity!.Pid,Guid.NewGuid()));
            });
        int checks=0;
        using var steam=new DesktopStoppedSteamUpdateBridge(cluster,
            m=>new SteamUpdatePreview(Path.GetFullPath(m.InstallationPath),new[]{m.Name},"ISOLATED",
                AsaSteamBuildCatalog.InstalledBuild(m.InstallationPath)!,1,100,true,"isolated plan"),
            (m,progress)=>{
                Claim(m,"updating");
                // Run the real Core stopped-installation guard too.
                SteamStoppedMapUpdater.DemandSafeLocalInstallation(cluster,m);
                trace.Add(m.Name+":steam");
                progress.Report(SteamMaintenanceStage.Backup);
                var backup=Path.Combine(profile,"backup-"+m.Id.ToString("N"));
                Directory.CreateDirectory(backup);
                File.Copy(saves[m.Id],Path.Combine(backup,"fixture.ark"));
                Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(backup,"fixture.ark"))))==hashes[m.Id],
                    "synthetic backup byte identity "+m.Name);
                if(mode=="steam-failure"&&m.Id==b.Id)throw new InvalidOperationException("UM-TEST-STEAM-FAILED");
                string next=mode=="wrong-build"&&m.Id==b.Id?"123458":"123457";
                Build(m,next);
                return Task.FromResult(new SteamStoppedMapUpdater.Receipt(m.Id,m.InstallationPath,"123456",
                    next,backup,DateTimeOffset.UtcNow,true));
            },
            (m,ct)=>{ct.ThrowIfCancellationRequested();checks++;
                return Task.FromResult(AsaSteamBuildCatalog.Compare(m.InstallationPath,"public",
                    AsaSteamBuildCatalog.InstalledBuild(m.InstallationPath),"123457",DateTimeOffset.UtcNow));});
        using var gate=new SemaphoreSlim(1,1);
        int entered=0,left=0,minuteWaits=0;
        Task Delay(TimeSpan d,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(d==TimeSpan.FromMinutes(1)){minuteWaits++;return Task.CompletedTask;}
            return Task.Delay(5,ct); // stress concurrent status polling against real journal transitions
        }
        using var supervisor=new DesktopRollingSteamSupervisor(cluster,lifecycle,steam,gate,
            ()=>{entered++;return true;},()=>left++,()=>false,chat,Owns,Delay);
        supervisor.Start();
        async Task<JsonElement> Call(string method,object fields)
        {
            var data=new Dictionary<string,object?>{["id"]=Guid.NewGuid(),["method"]=method};
            foreach(var f in JsonSerializer.SerializeToElement(fields).EnumerateObject())data[f.Name]=f.Value.Clone();
            using var doc=JsonDocument.Parse(await supervisor.HandleAsync(JsonSerializer.Serialize(data)));
            return doc.RootElement.Clone();
        }
        AsaManualRestartJournal? blockedJournal=null;
        AsaRestartRecord? blockedRecord=null;
        if(mode=="blocked-map")
        {
            blockedJournal=new AsaManualRestartJournal(a.InstallationPath);
            var claim=blockedJournal.Begin(a.Id,observed[a.Id].Identity!.Pid);
            blockedRecord=blockedJournal.Change(claim.Id,AsaRestartPhase.NeedsOperator);
        }
        foreach(var m in maps.Take(3))
        {
            var accepted=await Call("steam.rollingSet",new{mapId=m.Id,enabled=true,confirm=true,apiRiskAccepted=false});
            Check(accepted.GetProperty("ok").GetBoolean(),"explicit opt-in "+m.Name+": "+accepted.GetRawText());
            if(mode=="blocked-map"&&m.Id==a.Id)
                Check(accepted.GetProperty("data").GetProperty("journalBlocker").ValueKind==JsonValueKind.String,
                    "blocked map opt-in stores intent and reports blocker without clearing journal");
        }
        if(mode=="interrupted-countdown")
        {
            // Emulate an abandoned Manager before any Stop/Steam mutation.
            // The worker owns no lock now; the next poll must archive and proceed.
            var persisted=JsonNode.Parse(File.ReadAllText(journal))!.AsObject();
            persisted["current"]=JsonSerializer.SerializeToNode(new DesktopRollingSteamSupervisor.Run(
                Guid.NewGuid(),a.Id,"running","countdown","123456","123457",true,DateTimeOffset.UtcNow.AddMinutes(-10)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.WriteAllText(journal,persisted.ToJsonString());
        }
        if(mode=="interrupted-verified-files")
        {
            // Real native update has finished; rolling worker exited before
            // Start dispatch. Persist exact settings fingerprint and build.
            Build(a,"123457");observed[a.Id]=Stopped();
            var persisted=JsonNode.Parse(File.ReadAllText(journal))!.AsObject();
            string fingerprint=persisted["settings"]!.AsArray()
                .Single(x=>x!["mapId"]!.GetValue<Guid>()==a.Id)!["fingerprint"]!.GetValue<string>();
            persisted["current"]=JsonSerializer.SerializeToNode(new DesktopRollingSteamSupervisor.Run(
                Guid.NewGuid(),a.Id,"running","files-verified","123456","123457",true,
                DateTimeOffset.UtcNow.AddMinutes(-10),ProfileFingerprint:fingerprint),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.WriteAllText(journal,persisted.ToJsonString());
        }
        bool failed=false;
        using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            try{await supervisor.TickOnceAsync(deadline.Token);}
            catch(Exception ex) when(ex is not OperationCanceledException)
            {
                failed=true;Console.WriteLine("Rolling fixture outcome: "+ex.Message);
                // The fixture contains synthetic data only; expose the exact
                // persisted target evidence when an unexpected stage fails.
                string batchReceipt=await lifecycle.HandleAsync(JsonSerializer.Serialize(new{
                    id=Guid.NewGuid(),method="operations.lifecycleBatchCurrent"}));
                Console.WriteLine("Rolling fixture lifecycle receipt: "+batchReceipt);
            }
        }
        var status=(await Call("steam.rollingStatus",new{})).GetProperty("data");
        var current=status.GetProperty("current");
        Check(entered==1&&left==1&&gate.CurrentCount==1&&checks==1,"one host lane and one published build query");
        Check(AsaSteamBuildCatalog.InstalledBuild(disabled.InstallationPath)=="123456"&&observed[disabled.Id].Running&&
            !trace.Any(x=>x.StartsWith(disabled.Name+":",StringComparison.Ordinal)),"non-opted map untouched");
        if(mode=="blocked-map")
        {
            var setting=status.GetProperty("settings").EnumerateArray().Single(x=>x.GetProperty("mapId").GetGuid()==a.Id);
            Check(!failed&&setting.GetProperty("enabled").GetBoolean()&&
                setting.GetProperty("journalBlocker").ValueKind==JsonValueKind.String&&
                blockedJournal!.Observe()==blockedRecord&&observed[a.Id].Running&&
                AsaSteamBuildCatalog.InstalledBuild(a.InstallationPath)=="123456"&&
                trace.SequenceEqual(new[]{"Ragnarok:steam","The Island:stop","The Island:steam","The Island:start"})&&
                minuteWaits==5&&current.GetProperty("state").GetString()=="completed",
                "blocked first map remains untouched while other local maps update");
            Check(AsaSteamBuildCatalog.InstalledBuild(b.InstallationPath)=="123457"&&
                AsaSteamBuildCatalog.InstalledBuild(c.InstallationPath)=="123457",
                "eligible siblings complete despite old per-map journal");
        }
        else if(mode=="interrupted-verified-files")
        {
            Check(!failed&&trace.SequenceEqual(new[]{"Astraeos:start","Ragnarok:steam",
                "The Island:stop","The Island:steam","The Island:start"})&&minuteWaits==5&&
                observed[a.Id].Running&&current.GetProperty("state").GetString()=="completed",
                "verified-files recovery starts only the previously running map without a second Steam update");
        }
        else if(mode=="success"||mode=="interrupted-countdown")
        {
            if(mode=="interrupted-countdown")
                Check(Directory.GetFiles(profile,"rolling-steam-abandoned-*.json").Length==1,
                    "interrupted countdown is archived automatically before normal fresh cycle");
            Check(!failed&&current.GetProperty("state").GetString()=="completed"&&
                trace.SequenceEqual(new[]{"Astraeos:stop","Astraeos:steam","Astraeos:start","Ragnarok:steam",
                    "The Island:stop","The Island:steam","The Island:start"}),"strict serial stop/update/start; stopped map never starts");
            Check(observed[a.Id].Running&&observed[b.Id].ConfirmedStopped&&observed[c.Id].Running&&minuteWaits==10,
                "original running states and exact per-minute warning count");
            foreach(var m in maps.Take(3))Check(AsaSteamBuildCatalog.InstalledBuild(m.InstallationPath)=="123457","verified target build "+m.Name);
            int before=trace.Count;
            await supervisor.TickOnceAsync();
            Check(trace.Count==before,"same published build never replays a completed cycle");
        }
        else
        {
            string[] expected=mode=="start-failure"?new[]{"Astraeos:stop","Astraeos:steam","Astraeos:start"}:
                new[]{"Astraeos:stop","Astraeos:steam","Astraeos:start","Ragnarok:steam"};
            Check(failed&&current.GetProperty("state").GetString()=="needs-review"&&trace.SequenceEqual(expected),
                "failure freezes remaining queue: "+mode+" "+string.Join(",",trace));
            Check(AsaSteamBuildCatalog.InstalledBuild(c.InstallationPath)=="123456"&&observed[c.Id].Running,
                "unvisited map remains online on original build");
            int before=trace.Count;await supervisor.TickOnceAsync();
            Check(trace.Count==before&&entered==1,"uncertain update cannot auto-replay");
            Guid failedMap=current.GetProperty("mapId").GetGuid();
            var reviewed=await Call("steam.rollingReview",new{runId=current.GetProperty("id").GetGuid(),confirm=true});
            Check(reviewed.GetProperty("ok").GetBoolean(),"review archives failed rolling run");
            var settings=(await Call("steam.rollingStatus",new{})).GetProperty("data").GetProperty("settings");
            foreach(var m in maps.Take(3))
                Check(settings.EnumerateArray().Single(x=>x.GetProperty("mapId").GetGuid()==m.Id)
                    .GetProperty("enabled").GetBoolean()==(m.Id!=failedMap),
                    "only the failed map loses rolling opt-in");
        }
        foreach(var m in maps)Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(saves[m.Id])))==hashes[m.Id],
            "world fixture unchanged "+m.Name);
        Check(File.ReadAllBytes(steamExe).SequenceEqual(pe),"inert SteamCMD bytes unchanged");
    }
}
