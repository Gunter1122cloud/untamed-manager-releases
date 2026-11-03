using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class OperationalFeaturesTests
{
    private static void Check(bool ok,string detail)
    {
        if(!ok)throw new Exception("INTEGRATION TEST FAILED: v1.5 "+detail);
        Console.WriteLine("PASS v1.5 "+detail);
    }

    public static async Task Run(string root,string fakeAsa,string? fakeSteam)
    {
        var now=DateTimeOffset.UtcNow;
        string install=Path.Combine(root,"v15-local-install");
        Directory.CreateDirectory(install);
        var map=new LocalMap{Name="Local v1.5 fixture",InstallationPath=install,
            ExecutablePath=fakeAsa,
            Arguments="--map=TheIsland_WP -mods=930170,928548,912815 -passivemods=930170 -log",
            DisabledModIds=new(){"935528"}};
        var baseline=ModStateController.Read(map);
        Check(baseline.Active.SequenceEqual(new[]{"928548","912815"}) &&
            baseline.Passive.Single()=="930170" &&
            baseline.Disabled.Single()=="935528","separate active/passive/disabled map config");
        var preview=ModStateController.Preview(map,new(map.Id,"935528",ModMode.Active,0));
        Check(preview.Updated.Active[0]=="935528" &&
            preview.Updated.Disabled.Count==0 &&
            preview.NewArguments.Contains("-mods=930170,935528,928548,912815") &&
            preview.NewArguments.Contains("-passivemods=930170") &&
            preview.NewArguments.Contains("--map=TheIsland_WP"),
            "mod state move preserves original non-mod arguments");
        await using(var cluster=new ClusterService("v15-test",
            Path.Combine(root,"v15-profile"),
            inspect:_=>new ProcessCheck(false,"confirmed fixture",null,true)))
        {
            cluster.AddMap(map);
            ModStateController.ApplyLocalBatch(cluster,new[]{
                new ModEdit(map.Id,"935528",ModMode.Active,0),
                new ModEdit(map.Id,"912815",ModMode.Passive,0)});
            var restored=ModStateController.Read(cluster.Config.LocalMaps.Single());
            Check(restored.Pending && restored.Passive.SequenceEqual(new[]{"912815","930170"}) &&
                restored.Active.SequenceEqual(new[]{"935528","928548"}) &&
                cluster.Config.LocalMaps.Single().Arguments.Contains(
                    "-mods=912815,930170,935528,928548 -passivemods=912815,930170"),
                "local atomic mod changes preserve passive subset and do not restart ASA");
            bool foreign=false;
            try{ModStateController.ApplyLocalBatch(cluster,new[]{
                new ModEdit(Guid.NewGuid(),"928548",ModMode.Disabled)});}
            catch(UnauthorizedAccessException){foreign=true;}
            Check(foreign,"foreign map cannot be assigned a mod");
        }
        var invalid=new LocalMap{Name="bad args",Arguments="-mods=928548 -mods=912815"};
        bool duplicated=false;try{ModStateController.Read(invalid);}
        catch(InvalidDataException){duplicated=true;}
        Check(duplicated,"duplicate -mods parameters blocked");
        var legacy=new LocalMap{Name="legacy passive subset",
            Arguments="-mods=930170,928548 -passivemods=930170"};
        var interpreted=ModStateController.Read(legacy);
        Check(interpreted.Active.SequenceEqual(new[]{"928548"}) &&
            interpreted.Passive.SequenceEqual(new[]{"930170"}),
            "existing -mods/-passivemods overlapping switches are imported without state loss");
        var missingPassive=new LocalMap{Name="passive missing from load list",
            Arguments="-mods=928548 -passivemods=930170"};
        bool missingRejected=false;try{ModStateController.Read(missingPassive);}
        catch(InvalidDataException){missingRejected=true;}
        Check(missingRejected,"passive project absent from -mods must fail closed");

        var stopped=new ProcessCheck(false,"verified stopped",null,true);
        string iniRoot=Path.Combine(install,"ShooterGame","Saved","Config","WindowsServer");
        Directory.CreateDirectory(iniRoot);
        string ini=Path.Combine(iniRoot,"Game.ini");
        var utf8Bom=new UTF8Encoding(true);
        File.WriteAllBytes(ini,utf8Bom.GetPreamble().Concat(
            new UTF8Encoding(false).GetBytes(
                "; test comment\r\n[ServerSettings]\r\nXPMultiplier=1\r\nXPMultiplier=3\r\nUnknownModLine=ok\r\n")).ToArray());
        var snap=SafeConfigEditor.Read(ini,install,ConfigKind.GameIni,stopped);
        var next=SafeConfigEditor.SetIniValue(snap,"ServerSettings","XPMultiplier","2",1);
        var p=SafeConfigEditor.Preview(snap,next);
        Check(p.Valid&&p.Changed&&next.Contains("XPMultiplier=1")&&
            next.Contains("XPMultiplier=2")&&next.Contains("; test comment"),
            "INI structured editor preserves duplicate keys and comments");
        string bak=SafeConfigEditor.Save(snap,next,install,stopped);
        var saved=File.ReadAllBytes(ini);
        Check(File.Exists(bak)&&saved.Take(3).SequenceEqual(new byte[]{239,187,191})&&
            Encoding.UTF8.GetString(saved).Contains("\r\nUnknownModLine=ok"),
            "INI preserves UTF8 BOM, CRLF and makes atomic preimage backup");
        bool conflicted=false;
        var older=SafeConfigEditor.Read(ini,install,ConfigKind.GameIni,stopped);
        File.AppendAllText(ini,"; outside-change\r\n");
        try{SafeConfigEditor.Save(older,older.Text+"; local-change",install,stopped);}
        catch(IOException){conflicted=true;}
        Check(conflicted,"INI refuses external concurrent modification");

        var pluginRoot=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "ArkApi","Plugins","TestPlugin");
        Directory.CreateDirectory(pluginRoot);
        string json=Path.Combine(pluginRoot,"config.json");
        File.WriteAllText(json,"{\"enabled\":true,\"unknown\":{\"pass\":123}}");
        File.WriteAllText(Path.Combine(pluginRoot,"license.key"),"test-license");
        File.WriteAllText(Path.Combine(pluginRoot,"TestPlugin.dll"),"synthetic DLL");
        var pluginJson=SafeConfigEditor.Read(json,install,ConfigKind.PluginJson,stopped);
        bool invalidJson=false;
        try{SafeConfigEditor.Preview(pluginJson,"not-json");}
        catch(JsonException){invalidJson=true;}
        Check(invalidJson,"malformed plugin JSON blocked before any write");
        var pluginPreview=SafeConfigEditor.Preview(pluginJson,
            "{\"enabled\":false,\"unknown\":{\"pass\":123}}");
        Check(pluginPreview.Changed,"unknown plugin JSON fields retained by explicit raw edit");
        SafeConfigEditor.Save(pluginJson,
            "{\"enabled\":false,\"unknown\":{\"pass\":123}}",install,stopped);
        var disabled=PluginStateManager.Toggle(map,stopped,"TestPlugin",false,
            Path.Combine(root,"v15-profile"));
        Check(!Directory.Exists(pluginRoot) &&
            File.Exists(Path.Combine(disabled.Destination,"config.json")) &&
            File.ReadAllText(Path.Combine(disabled.Destination,"license.key"))=="test-license",
            "disabled plugin retains all JSON, DLL and license data outside active tree");
        var enabled=PluginStateManager.Toggle(map,stopped,"TestPlugin",true,
            Path.Combine(root,"v15-profile"));
        Check(Directory.Exists(pluginRoot)&&File.Exists(enabled.BackupPackage is {} ?
            Path.Combine(enabled.BackupPackage,"manifest.json") : "") &&
            File.ReadAllText(json).Contains("\"enabled\":false"),
            "re-enabled plugin preserves previous settings and complete SHA backup");
        bool legacyBlocked=false;
        try{LocalContentOperations.InstallLocalPluginFolder(map,pluginRoot);}
        catch(NotSupportedException){legacyBlocked=true;}
        Check(legacyBlocked,"legacy nontransactional plugin copy is disabled");

        string binary=Path.Combine(install,"ShooterGame","Binaries","Win64");
        File.Copy(fakeAsa,Path.Combine(binary,"AsaApiLoader.exe"),true);
        File.Copy(fakeAsa,Path.Combine(binary,"ArkApi","AsaApi.dll"),true);
        File.WriteAllText(Path.Combine(binary,"config.json"),"{}");
        var api=AsaApiInspector.Inspect(install,stopped);
        Check(api.Complete && api.Binaries.All(x=>x.ValidPe64),
            "ASA API read-only inspection recognizes 64-bit PE loader and DLL");
        File.WriteAllText(Path.Combine(binary,"ArkApi","AsaApi.dll"),"bad DLL");
        Check(!AsaApiInspector.Inspect(install,stopped).Complete,
            "invalid ASA API DLL is rejected");

        // Independent Supervisor recurring runner: real read-only callback,
        // durable claims, no retry/catch-up, all live modifications blocked.
        string recurringRoot=Path.Combine(root,"v15-recurring-supervisor");
        var runner=new RecurringReadOnlyRunner(recurringRoot);
        var recurringOwned=new HashSet<Guid>{map.Id};
        var readOnlyJob=new RecurringSchedule{
            Map=map.Id,Kind=OperationalTaskKind.ModCheck,
            Frequency=ScheduleFrequency.IntervalMinutes,IntervalMinutes=5,
            LastDueUtc=now,Enabled=true};
        int inspections=0;
        var registered=runner.Tick(new[]{readOnlyJob},recurringOwned,now,
            _=>false,_=>inspections++);
        Check(registered.Length==0 && inspections==0,
            "new recurring job records future slot without immediate execution");
        var fired=runner.Tick(new[]{readOnlyJob},recurringOwned,now.AddMinutes(5).AddSeconds(1),
            _=>false,_=>inspections++);
        Check(inspections==1 && fired.Any(x=>x.Status=="succeeded"),
            "Supervisor recurring read-only ModCheck executes once at scheduled slot");
        var restartedRunner=new RecurringReadOnlyRunner(recurringRoot);
        Check(restartedRunner.Tick(new[]{readOnlyJob},recurringOwned,
            now.AddMinutes(5).AddSeconds(2),_=>false,_=>inspections++).Length==0 &&
            inspections==1,"restarting Supervisor never replays already-claimed slot");
        var unsafeJob=new RecurringSchedule{
            Map=map.Id,Kind=OperationalTaskKind.ServerRestart,
            Frequency=ScheduleFrequency.IntervalMinutes,IntervalMinutes=5,
            LastDueUtc=now,Enabled=true};
        restartedRunner.Tick(new[]{readOnlyJob,unsafeJob},recurringOwned,now.AddMinutes(6),
            _=>false,_=>inspections++);
        var rejected=restartedRunner.Tick(new[]{readOnlyJob,unsafeJob},recurringOwned,
            now.AddMinutes(11),_=>false,_=>inspections++);
        Check(rejected.Any(x=>x.Job==unsafeJob.Id &&
            x.Status=="live-operation-disabled") && inspections==2,
            "recurring restart is always blocked while read-only jobs may execute");
        var maintenanceJob=new RecurringSchedule{
            Map=map.Id,Kind=OperationalTaskKind.ApiCheck,Enabled=true,
            Frequency=ScheduleFrequency.IntervalMinutes,IntervalMinutes=5,
            LastDueUtc=now};
        restartedRunner.Tick(new[]{maintenanceJob},recurringOwned,now.AddMinutes(12),
            _=>true,_=>inspections++);
        var maintenanceResults=restartedRunner.Tick(new[]{maintenanceJob},recurringOwned,
            now.AddMinutes(17),_=>true,_=>inspections++);
        Check(maintenanceResults.Any(x=>x.Status=="maintenance-blocked"),
            "recurring read-only inspection respects maintenance");
        var missedJob=new RecurringSchedule{
            Map=map.Id,Kind=OperationalTaskKind.SteamCheck,Enabled=true,
            Frequency=ScheduleFrequency.IntervalMinutes,IntervalMinutes=5,
            LastDueUtc=now};
        restartedRunner.Tick(new[]{missedJob},recurringOwned,now.AddMinutes(18),
            _=>false,_=>inspections++);
        var missed=restartedRunner.Tick(new[]{missedJob},recurringOwned,
            now.AddMinutes(30),_=>false,_=>inspections++);
        Check(missed.Any(x=>x.Status=="missed-no-catchup") &&
            !missed.Any(x=>x.Status=="succeeded"),
            "missed recurring slot cannot run after long Supervisor outage");
        var foreignSchedule=new RecurringSchedule{
            Map=Guid.NewGuid(),Kind=OperationalTaskKind.ModCheck,Enabled=true,
            Frequency=ScheduleFrequency.IntervalMinutes,IntervalMinutes=5,
            LastDueUtc=now};
        restartedRunner.Tick(new[]{foreignSchedule},recurringOwned,now.AddMinutes(31),
            _=>false,_=>inspections++);
        var foreignResult=restartedRunner.Tick(new[]{foreignSchedule},recurringOwned,
            now.AddMinutes(36),_=>false,_=>inspections++);
        Check(foreignResult.Any(x=>x.Status=="foreign-map-blocked"),
            "recurring runner refuses remote map operations");
        Check(restartedRunner.Recent().Any(x=>x.Status=="failed-not-retried")==false,
            "read-only recurring action history remains deterministic");
        var daily=new RecurringSchedule{Map=map.Id,Kind=OperationalTaskKind.WildDinoWipe,
            Enabled=false,LocalClock=new TimeSpan(7,30,0)};
        var calendar=OperationalScheduler.Next(daily,
            new DateTimeOffset(2026,3,28,12,0,0,TimeSpan.Zero));
        Check(calendar.Status=="disabled" &&
            calendar.DueUtc==new DateTimeOffset(2026,3,29,5,30,0,TimeSpan.Zero),
            "07:30 Budapest daily schedule crosses DST spring correctly");
        daily.LocalClock=new TimeSpan(2,30,0);
        var spring=OperationalScheduler.Next(daily,
            new DateTimeOffset(2026,3,28,12,0,0,TimeSpan.Zero));
        Check(spring.DueUtc==new DateTimeOffset(2026,3,29,1,0,0,TimeSpan.Zero),
            "missing 02:30 during DST spring shift moves to next valid wall-clock time");
        var autumn=OperationalScheduler.Next(daily,
            new DateTimeOffset(2026,10,24,12,0,0,TimeSpan.Zero));
        Check(autumn.DueUtc==new DateTimeOffset(2026,10,25,1,30,0,TimeSpan.Zero),
            "ambiguous 02:30 during DST fall shift runs exactly once at later occurrence");
        Check(OperationalScheduler.UntamedTemplates(new[]{map.Id})
            .All(x=>!x.Enabled&&x.Kind==OperationalTaskKind.WildDinoWipe),
            "Untamed wild dino wipe templates default to disabled");

        var rcon=RconCommandPolicy.Preview(map.Id,RconAction.WildDinoWipe,"",
            true,false,true);
        Check(rcon.Command=="DestroyWildDinos" && rcon.Destructive&&!rcon.LiveAllowed,
            "Wild Dino Wipe RCON is destructive and never auto-approved");
        bool injection=false;
        try{RconCommandPolicy.Preview(map.Id,RconAction.Broadcast,
            "bad\nSaveWorld",true,false,true);}
        catch(InvalidDataException){injection=true;}
        Check(injection,"RCON newline injection blocked");
        bool foreignRcon=false;
        try{RconCommandPolicy.Preview(map.Id,RconAction.ListPlayers,
            "",false,false,true);}
        catch(UnauthorizedAccessException){foreignRcon=true;}
        Check(foreignRcon,"foreign map RCON blocked");
        var fakeQueue=new FakeRconQueue(Path.Combine(root,"fake-rcon"));
        var unique=Guid.NewGuid();int sends=0;
        var fakeReceipt=await fakeQueue.ExecuteOnceAsync(unique,rcon,
            (cmd,ct)=>{sends++;return Task.FromResult(cmd);});
        bool replay=false;
        try{await new FakeRconQueue(Path.Combine(root,"fake-rcon"))
            .ExecuteOnceAsync(unique,rcon,(cmd,ct)=>{sends++;return Task.FromResult(cmd);});}
        catch(InvalidOperationException){replay=true;}
        Check(replay && sends==1 && fakeReceipt.Status=="fake-rcon-succeeded",
            "durable FakeRcon transaction claim prevents cross-instance duplicate wipe");

        var history=new TelemetryHistory(Path.Combine(root,"telemetry-sim"));
        var sample=new MapObservation(map.Id,map.Name,true,"test","test",
            34.5,512L*1048576,35,now,3,false,
            "healthy","log","none","noerror");
        history.Observe(sample,now);
        history.Observe(sample with{ObservedUtc=now.AddMinutes(-10)},now);
        var stored=history.Read(map.Id,now);
        Check(stored.Count==1&&stored[0].CpuPercent==34.5&&stored[0].Players==3,
            "telemetry history preserves measured values and rejects stale samples");
        Check(stored[0].RconLatencyMs==null&&stored[0].NetworkBytesPerSecond==null,
            "unmeasured RCON latency and network throughput stay unknown");

        var backupSrc=Path.Combine(root,"retention-source");
        Directory.CreateDirectory(backupSrc);
        File.WriteAllText(Path.Combine(backupSrc,"save.txt"),"synthetic savedark");
        var job=Guid.NewGuid();var owner=map.Id;
        string managed=Path.Combine(root,"managed-retention");
        string old=Path.Combine(managed,"old"),recent=Path.Combine(managed,"new");
        TestBackupPackage.Create(backupSrc,old,owner,"synthetic-world-only");
        TestBackupPackage.Create(backupSrc,recent,owner,"synthetic-world-only");
        long bytes=TestBackupPackage.Verify(old,owner).Files.Sum(x=>x.Size);
        var points=new[]{
            new RetainedBackup(job,owner,recent,now,bytes,false,true,"synthetic-world-only"),
            new RetainedBackup(job,owner,old,now.AddDays(-30),bytes,false,true,"synthetic-world-only")};
        var retention=BackupRetention.Preview(points,
            new RetentionRule(1,TimeSpan.FromDays(7),0),now);
        Check(retention.Candidates==1 && retention.Items[0].Backup.Package==recent &&
            !retention.Items[0].Eligible,"retention keeps newest point of a job");
        var selected=retention.Items.Single(x=>x.Eligible);
        string quarantine=BackupRetention.Quarantine(selected,managed,
            Path.Combine(root,"retention-quarantine"),job,owner);
        Check(Directory.Exists(quarantine)&&!Directory.Exists(old)&&
            Directory.Exists(recent)&&File.Exists(Path.Combine(backupSrc,"save.txt")),
            "old verified backup moved to quarantine without deleting original or newest point");

        string kovaExport=Path.Combine(root,"kova-manager-copy.json");
        string storeCopy=Path.Combine(root,"kova-store-copy.json");
        var kovaSource=new{
            version=1,profiles=new[]{new{
                id="local-test",localManaged=true,installPath=install,
                map="The Island",mapId="TheIsland_WP",gamePort=7777,queryPort=27015,
                mods=new[]{new {id=928548,mode="active"},
                    new {id=930170,mode="passive"}}}}};
        File.WriteAllText(kovaExport,JsonSerializer.Serialize(kovaSource));
        File.WriteAllText(storeCopy,JsonSerializer.Serialize(new{
            schema=1,maps=new Dictionary<string,object>{
                ["local-test"]=new{phase="active",root=install,map="TheIsland_WP",
                    backupId="synthetic-backup",preparedAt="2026-10-20T00:00:00Z"}
            }}));
        string originalKova=File.ReadAllText(kovaExport);
        var importer=KovaProfileImporter.InspectCopy(kovaExport,storeCopy);
        var inert=KovaProfileImporter.BuildInertLocalDraft(importer,Guid.NewGuid().ToString("N"));
        Check(importer.Maps.Count==1 && importer.Maps[0].StorePhase=="active" &&
            !importer.StoreCompatible && inert.LocalMaps.Single().Maintenance &&
            !inert.LocalMaps.Single().AutoStart &&
            string.IsNullOrEmpty(inert.LocalMaps.Single().ExecutablePath),
            "Kova profile v1 + Store-v1 copy produces maintenance-only inert no-launch draft");
        Check(File.ReadAllText(kovaExport)==originalKova &&
            inert.LocalMaps.Single().DisabledModIds.Count==2,
            "Kova copies remain unmodified and no executable or Store conversion is imported");
        bool duplicateKova=false;
        File.WriteAllText(kovaExport,JsonSerializer.Serialize(new{
            version=1,profiles=new[]{kovaSource.profiles[0],kovaSource.profiles[0]}}));
        try{KovaProfileImporter.InspectCopy(kovaExport);}
        catch(InvalidDataException){duplicateKova=true;}
        Check(duplicateKova,"Kova duplicate server roots and profile IDs blocked");

        var files=new CurseForgeFilesResult(928548,
            new[]{
                new CurseForgeFile(101,928548,"old",
                    new DateTimeOffset(2025,1,1,0,0,0,TimeSpan.Zero),true,new[]{928621}),
                new CurseForgeFile(102,928548,"new",
                    new DateTimeOffset(2026,10,1,0,0,0,TimeSpan.Zero),true,new[]{928621})
            },0,2,false);
        var evidence=new LocalModEvidence("928548",true,101,"synthetic fixture",
            "This is a trusted TEST file ID only");
        Check(ModUpdatePlanner.Assess(evidence,files).State==ModUpdateState.NewerFileAvailable &&
            !ModUpdatePlanner.Assess(evidence,files).CanAutoInstall,
            "remote CurseForge file ID comparison identifies update without granting install rights");
        Check(ModUpdatePlanner.Assess(evidence with{VerifiedFileId=null},files).State==
            ModUpdateState.Unknown,"unknown local CurseForge file ID cannot imply safe update");
        Directory.CreateDirectory(Path.Combine(install,"ShooterGame","Mods","928548"));
        Check(ModUpdatePlanner.InspectCandidate(install,"928548").DirectoryPresent,
            "local mod candidate folder inventory is read-only, without version guessing");

        if(!string.IsNullOrWhiteSpace(fakeSteam))
        {
            string steamInstall=Path.Combine(root,"SteamCmd-Isolated");
            Directory.CreateDirectory(Path.Combine(steamInstall,"ShooterGame",
                "Saved","Config","WindowsServer"));
            string protectedIni=Path.Combine(steamInstall,"ShooterGame",
                "Saved","Config","WindowsServer","Game.ini");
            File.WriteAllText(protectedIni,"[ServerSettings]\nXP=2");
            var success=await SteamCmdTestRunner.RunAsync(fakeSteam,steamInstall,root,
                TimeSpan.FromSeconds(10));
            Check(success.State=="fake-update-verified"&&success.ExitCode==0&&success.IniPreserved,
                "actual spawned FakeSteamCMD simulates ASA app 2430930 and verifies preserved INI");
            var failure=await SteamCmdTestRunner.RunAsync(fakeSteam,steamInstall,root,
                TimeSpan.FromSeconds(10),injectFailure:true);
            Check(failure.State=="fake-update-failed"&&failure.ExitCode!=0,
                "FakeSteamCMD nonzero exit rejected");
            var timeout=await SteamCmdTestRunner.RunAsync(fakeSteam,steamInstall,root,
                TimeSpan.FromSeconds(1),injectHang:true);
            Check(timeout.State=="timeout",
                "FakeSteamCMD hung process is bounded and killed without affecting real ASA");
            var tampered=await SteamCmdTestRunner.RunAsync(fakeSteam,steamInstall,root,
                TimeSpan.FromSeconds(10),injectIniChange:true);
            Check(tampered.State=="ini-mismatch"&&!tampered.IniPreserved,
                "FakeSteamCMD INI corruption detected and not reported as success");
        }
        Check(!ReleaseCapabilityGate.Check(ManagerOperation.RealAsaRestart,true,
            confirmedStopped:true,realSaveWorldProof:true,storeVerified:true).Allowed,
            "v1.5 tests do not unlock production ASA restart or Kova Store mutation");
    }
}
