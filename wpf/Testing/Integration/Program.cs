using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using UntamedManager.Core;

static void Assert(bool condition,string reason)
{
    if(!condition)throw new Exception("INTEGRATION TEST FAILED: "+reason);
    Console.WriteLine("PASS "+reason);
}
static int FreePort()
{
    var listener=new TcpListener(IPAddress.Loopback,0);
    listener.Start();
    int port=((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();return port;
}
if(args.Length==4&&args[0]=="--coordinator-hold")
{
    using var lease=new UntamedOperationCoordinator(args[1]).Acquire(args[2],UntamedOperationKind.Update);
    lease.MarkMutationStarted();File.WriteAllText(args[3],"ready");
    await Task.Delay(Timeout.Infinite);return;
}
if(args.Length==0||!File.Exists(args[0]))
    throw new ArgumentException("First argument must be the built FakeAsa.exe");
string fake=Path.GetFullPath(args[0]);
string root=Path.Combine(Path.GetTempPath(),"Untamed-Integration-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string aRoot=Path.Combine(root,"HP");
string bRoot=Path.Combine(root,"Ryzen");
Process? p=null;
Process? duplicate=null;
Process? familyLoader=null;
Process? familyChild=null;
Process? persistentWorker=null;
ClusterService? hp=null;
ClusterService? ryzen=null;
try
{
    string steamFixture=Path.Combine(root,"steamcmd.exe");
    File.WriteAllText(steamFixture,"offline synthetic steamcmd file");
    await OperationalFeaturesTests.Run(root,fake,args.Length>3?args[3]:null);
    await SteamCmdIsolatedUpdaterTests.Run(root,args.Length>3?args[3]:null);
    await ModDependencyPlannerTests.Run();
    await CurseForgeDependencyResolverTests.Run();
    await AsaApiReleaseStagingTests.Run();
    await AsaApiIsolatedInstallerTests.Run();
    await ModArgumentSafetyTests.Run(root);
    await GuardedPluginUpdateTests.Run(root);
    await IsolatedRconAdminQueueTests.Run(fake);
    await ProfessionalOperationsTests.Run(root,fake);
    await OperationSafetyTests.Run(root);
    DurableWatchdogTrackerTests.Run(root);
    SafeConfigRecheckTests.Run(root);
    RecurringReadOnlyRecoveryTests.Run(root);
    await AsaReadyProbeTests.Run(root);
    StorePathIdentityTests.Run(root);
    await VerifiedRestartTests.Run();
    ClusterTransferConsistencyTests.Run();
    ClusterBackupInventoryTests.Run();
    ClusterBatchArchiveTests.Run(root);
    ClusterRestoreCoordinatorTests.Run(root);
    ClusterVaultTests.Run(root);
    ManagerMigrationArchiveTests.Run(root);
    SafeSupportBundleTests.Run(root);
    await DiscordStatusTests.Run();
    NetworkLatencyTests.Run();
    AsaHistoricalEvidenceTests.Run();
    await RconTransportTests.Run();
    var steam=SteamCmdPreflight.Inspect(steamFixture);
    Assert(steam.LengthBytes==new FileInfo(steamFixture).Length &&
        steam.Sha256==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(steamFixture))),"SteamCMD offline preflight hashes a local fixed file");
    Assert(File.ReadAllText(steamFixture)=="offline synthetic steamcmd file",
        "SteamCMD preflight is read-only");
    bool wrongExecutableBlocked=false;
    try{SteamCmdPreflight.Inspect(Path.Combine(root,"other.exe"));}
    catch(InvalidDataException){wrongExecutableBlocked=true;}
    Assert(wrongExecutableBlocked,"SteamCMD preflight rejects other executable names");
    bool missingExecutableBlocked=false;
    try{SteamCmdPreflight.Inspect(Path.Combine(root,"missing","steamcmd.exe"));}
    catch(FileNotFoundException){missingExecutableBlocked=true;}
    Assert(missingExecutableBlocked,"SteamCMD preflight rejects missing executables");
    // v1.4.0 combined release-candidate acceptance: safe real code paths
    // and explicit refusal of unverified production ASA/SQL/Store operations.
    foreach(var denied in new[]{ManagerOperation.RealAsaStart,
        ManagerOperation.RealAsaStop,ManagerOperation.RealAsaRestart,
        ManagerOperation.RealAsaKill,ManagerOperation.LiveSteamUpdate,
        ManagerOperation.LiveSqlRestore,ManagerOperation.LiveVaultStoreWrite})
    {
        var decision=ReleaseCapabilityGate.Check(denied,true,
            confirmedStopped:true,realSaveWorldProof:true,storeVerified:true);
        Assert(!decision.Allowed,"Production RC capability remains blocked without real Kova/ASA proof: "+denied);
    }
    Assert(!ReleaseCapabilityGate.Check(ManagerOperation.LocalProcessObservation,false).Allowed,
        "Foreign map cannot claim local process observation permission");
    Assert(ReleaseCapabilityGate.Check(ManagerOperation.FakeAsaStart,true,
        confirmedStopped:true).Allowed &&
        !ReleaseCapabilityGate.Check(ManagerOperation.FakeAsaStart,true,
        confirmedStopped:false).Allowed,
        "Only confirmed stopped, owned FakeAsa test start may be prepared");
    var gateRoot=Path.Combine(root,"capability-test-profile");
    await using(var gateService=new ClusterService("capability-test",gateRoot,
        inspect:_=>new ProcessCheck(false,"synthetic stopped",null,true)))
    {
        var gateMap=new LocalMap{Name="FakeAsa safe launch",ExecutablePath=fake,
            InstallationPath=Path.GetDirectoryName(fake)!,
            Arguments="--safe-fixture-only"};
        gateService.AddMap(gateMap);
        ReleaseCapabilityGate.DemandFakeAsaStart(gateService,gateMap);
        var invalidMap=new LocalMap{Name="Unsafe executable test",ExecutablePath=steamFixture,
            InstallationPath=root,Arguments="-log"};
        gateService.AddMap(invalidMap);
        bool wrongBinaryDenied=false;
        try{ReleaseCapabilityGate.DemandFakeAsaStart(gateService,invalidMap);}
        catch(InvalidOperationException){wrongBinaryDenied=true;}
        Assert(wrongBinaryDenied,"Test Start gate refuses an arbitrary Windows executable");
        bool serviceGatewayDenied=false;
        try{gateService.LaunchLocal(invalidMap.Id);}
        catch(InvalidOperationException){serviceGatewayDenied=true;}
        Assert(serviceGatewayDenied,
            "ClusterService itself refuses real/arbitrary executable even if fake inspector says stopped");
    }

    var profileConfig=new ManagerConfig{LocalHostId=Guid.NewGuid().ToString("N"),
        CurseForgeApiKeyProtected="TEST_SECRET_SHOULD_NOT_EXPORT",
        PairingKeyProtected="DO_NOT_EXPORT_PAIRING"};
    var prefMap=new LocalMap{Name="Offline preference map",
        RconPasswordProtected="TEST_RCON_SECRET",Arguments="-secret=DO_NOT_EXPORT",
        WatchdogIntervalSeconds=15,MonitorRcon=false};
    profileConfig.LocalMaps.Add(prefMap);
    string preferences=LocalProfileTransfer.Export(profileConfig);
    Assert(!preferences.Contains("TEST_SECRET_SHOULD_NOT_EXPORT") &&
        !preferences.Contains("DO_NOT_EXPORT_PAIRING") &&
        !preferences.Contains("TEST_RCON_SECRET") &&
        !preferences.Contains("DO_NOT_EXPORT") &&
        LocalProfileTransfer.Preview(profileConfig,preferences).Allowed,
        "Host-bound exported profile excludes secrets, executable arguments and RCON credentials");
    prefMap.WatchdogIntervalSeconds=60;prefMap.MonitorRcon=true;
    LocalProfileTransfer.Apply(profileConfig,preferences);
    Assert(prefMap.WatchdogIntervalSeconds==15 && !prefMap.MonitorRcon,
        "Allowed profile import restores only non-secret local monitoring preferences");
    var otherProfile=new ManagerConfig{LocalHostId=Guid.NewGuid().ToString("N")};
    otherProfile.LocalMaps.Add(prefMap);
    Assert(!LocalProfileTransfer.Preview(otherProfile,preferences).Allowed,
        "Profile transfer rejects foreign Manager host ownership");
    var evilPref=System.Text.Json.JsonSerializer.Deserialize<SafeProfileBundle>(preferences)!;
    var injected=evilPref with{Maps=new[]{evilPref.Maps[0],
        evilPref.Maps[0]}};
    Assert(!LocalProfileTransfer.Preview(profileConfig,
        System.Text.Json.JsonSerializer.Serialize(injected)).Allowed,
        "Duplicate map IDs in imported preferences fail closed");

    string offlineSourceRoot=Path.Combine(root,"exported-sql-fixtures");
    Directory.CreateDirectory(offlineSourceRoot);
    string sqlFile=Path.Combine(offlineSourceRoot,"arkshop.sql");
    File.WriteAllText(sqlFile,"-- synthetic offline SQL export\nCREATE TABLE fake_fixture (id INT);\n");
    string sqlProfile=Path.Combine(root,"offline-sql-manager-profile");
    var sqlOwner=Guid.NewGuid();
    var sqlArchive=OfflineSqlArchive.ImportFixture(sqlProfile,sqlFile,"arkshop",sqlOwner);
    Assert(sqlArchive.Verified && sqlArchive.Bytes==new FileInfo(sqlFile).Length &&
        OfflineSqlArchive.Verify(sqlArchive.Package,"arkshop",sqlOwner).Sha256==sqlArchive.Sha256,
        "Offline ArkShop .sql import hashes and verifies one isolated backup archive");
    string restoredSql=OfflineSqlArchive.SimulateRestore(sqlArchive.Package,"arkshop",sqlOwner);
    Assert(File.ReadAllText(restoredSql)==File.ReadAllText(sqlFile),
        "Offline SQL restore rehearsal copies only into a new TEMP sandbox");
    bool crossDbDenied=false;
    try{OfflineSqlArchive.Verify(sqlArchive.Package,"arkpermissions",sqlOwner);}
    catch(InvalidDataException){crossDbDenied=true;}
    Assert(crossDbDenied,"SQL archive cannot be reinterpreted as another database");
    File.WriteAllText(Path.Combine(sqlArchive.Package,"data","arkshop.sql"),"TAMPER");
    bool corruptSqlBlocked=false;
    try{OfflineSqlArchive.SimulateRestore(sqlArchive.Package,"arkshop",sqlOwner);}
    catch(InvalidDataException){corruptSqlBlocked=true;}
    Assert(corruptSqlBlocked,"Tampered SQL archive is rejected before any offline restore");
    Assert(File.ReadAllText(sqlFile).Contains("fake_fixture"),
        "Original user-supplied offline SQL fixture remains untouched");

    string kovaFixture=Path.Combine(Path.GetTempPath(),
        "Untamed-Vault-Fixture-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(kovaFixture);
    File.WriteAllText(Path.Combine(kovaFixture,"synthetic.fixture"),
        "NOT A REAL KOVA STORE OR CHARACTER");
    var kovaOwner=Guid.NewGuid();
    var audited=VaultFixtureAudit.Inspect(kovaFixture,kovaOwner);
    Assert(!audited.FormatCompatible && audited.Files==1,
        "Untamed Cluster Vault synthetic fixture can be SHA-audited without claiming unknown Store schema support");
    string kovaArchivePath=Path.Combine(root,"synthetic-kova-backup");
    VaultFixtureAudit.Snapshot(kovaFixture,kovaOwner,kovaArchivePath);
    Assert(TestBackupPackage.Verify(kovaArchivePath,kovaOwner).Files.Count==1 &&
        File.ReadAllText(Path.Combine(kovaFixture,"synthetic.fixture"))==
        "NOT A REAL KOVA STORE OR CHARACTER",
        "Untamed Cluster Vault byte-preserving synthetic backup leaves source file unchanged");
    bool liveStoreDenied=false;
    try{VaultFixtureAudit.Inspect(Path.Combine(root,"actual-KovaCluster"),kovaOwner);}
    catch(UnauthorizedAccessException){liveStoreDenied=true;}
    Assert(liveStoreDenied,"Untamed Cluster Vault fixture audit refuses arbitrary non-TEMP live Store paths");

    string steamInstall=Path.Combine(root,"shared-asa-installation");
    string steamBin=Path.Combine(steamInstall,"ShooterGame","Binaries","Win64");
    Directory.CreateDirectory(steamBin);
    Directory.CreateDirectory(Path.Combine(steamInstall,"steamapps"));
    File.WriteAllText(Path.Combine(steamBin,"ArkAscendedServer.exe"),"fake ASA test executable");
    File.WriteAllText(Path.Combine(steamInstall,"steamapps","appmanifest_2430930.acf"),
        "\"buildid\" \"12345\"");
    var steamA=new LocalMap{Name="Offline shared install A",InstallationPath=steamInstall};
    var steamB=new LocalMap{Name="Offline shared install B",InstallationPath=steamInstall};
    var steamPlan=SteamUpdateDryRun.Plan(steamFixture,steamInstall,
        new[]{steamA,steamB},
        _=>new ProcessCheck(false,"synthetic stopped",null,true),1024*1024);
    Assert(steamPlan.DryRunAllowed && steamPlan.Maps.Length==2 &&
        steamPlan.InstalledBuild=="12345",
        "Steam dry-run groups shared ASA installation and recognizes local build manifest");
    string steamPreviewRoot=Path.Combine(root,"simulated-steam-update");
    var steamReceipt=SteamUpdateDryRun.Simulate(steamPlan,steamPreviewRoot);
    Assert(steamReceipt.State=="dry-run-complete" &&
        Directory.GetFiles(Path.Combine(steamPreviewRoot,"steamcmd-test-logs")).Length==1 &&
        File.ReadAllText(Path.Combine(steamBin,"ArkAscendedServer.exe"))=="fake ASA test executable",
        "SteamCMD simulator writes only Manager receipts and never executes or modifies ASA");
    var runningPlan=SteamUpdateDryRun.Plan(steamFixture,steamInstall,
        new[]{steamA,steamB},
        x=>x.Id==steamA.Id?new ProcessCheck(true,"synthetic running",
            new ProcessIdentity{Pid=555,StartedUtc=DateTimeOffset.UtcNow,ExecutablePath=fake}):
            new ProcessCheck(false,"synthetic stopped",null,true),1024*1024);
    Assert(!runningPlan.DryRunAllowed,"Steam dry-run refuses one running map in a shared installation");
    bool foreignInstallDenied=false;
    try{SteamUpdateDryRun.Plan(steamFixture,Path.Combine(root,"foreign-asa"),
        new[]{steamA,steamB},
        _=>new ProcessCheck(false,"stopped",null,true),1024*1024);}
    catch(UnauthorizedAccessException){foreignInstallDenied=true;}
    Assert(foreignInstallDenied,"Steam planner refuses installation without a locally registered map");
    Assert(File.ReadAllText(steamFixture)=="offline synthetic steamcmd file",
        "SteamCMD source executable is never run or altered by dry-run");

    // Mod order must be explicit, exact and local-only; it is never an update/download.
    const string modArguments="-log -mods=912815,928548,942024 -port=7777";
    Assert(LocalContentOperations.ReadMods(modArguments).SequenceEqual(new[]{"912815","928548","942024"}),
        "Mod inspection preserves configured load order");
    var reordered=LocalContentOperations.ReorderMods(modArguments,new[]{"942024","912815","928548"});
    Assert(reordered=="-log -mods=942024,912815,928548 -port=7777",
        "Mod reorder changes only mod order, preserving other launch flags");
    var extended=LocalContentOperations.ModifyMods(modArguments,new[]{"950914"},true);
    Assert(LocalContentOperations.ReadMods(extended).SequenceEqual(new[]{"912815","928548","942024","950914"}),
        "Mod addition preserves prior order and appends new mod");
    foreach(var invalidOrder in new[]{
        new[]{"942024","912815"},
        new[]{"912815","928548","928548"},
        new[]{"912815","928548","999999"}
    })
    {
        bool rejected=false;
        try{LocalContentOperations.ReorderMods(modArguments,invalidOrder);}
        catch(InvalidDataException){rejected=true;}
        Assert(rejected,"Reorder refuses missing, duplicated or substituted mod IDs");
    }
    foreach(var malformed in new[]{
        "-mods=912815,912815","-mods=912815,,928548","-mods=","-mods=912815 -mods=928548"
    })
    {
        bool rejected=false;
        try{LocalContentOperations.ReadMods(malformed);}
        catch(InvalidDataException){rejected=true;}
        Assert(rejected,"Ambiguous or malformed existing mod list is rejected");
    }
    bool absentOrderBlocked=false;
    try{LocalContentOperations.ReorderMods("-log",new[]{"912815"});}
    catch(InvalidOperationException){absentOrderBlocked=true;}
    Assert(absentOrderBlocked,"Reorder refuses to invent a missing mod list");
    var modA=new LocalMap{Name="Astraeos inventory",Arguments="-log -mods=912815,928548,942024"};
    var modB=new LocalMap{Name="Island inventory",Arguments="-mods=928548,912815,950914"};
    var modC=new LocalMap{Name="Genesis inventory",Arguments="-mods=912815,928548"};
    var modList=LocalModAssignments.Inspect(new[]{modA,modB,modC});
    Assert(modList.Complete && modList.Maps.Count==3 &&
        modList.Maps[0].OrderedIds.SequenceEqual(new[]{"912815","928548","942024"}) &&
        modList.CommonIds.SequenceEqual(new[]{"912815","928548"}) &&
        modList.SingleMapIds.SequenceEqual(new[]{"942024","950914"}) &&
        modList.UniqueIdCount==4,
        "Local-only mod inventory reports actual order, shared and exclusive IDs");
    var broken=new LocalMap{Name="Invalid mod arguments",Arguments="-mods=912815,912815"};
    var uncertain=LocalModAssignments.Inspect(new[]{modA,broken});
    Assert(!uncertain.Complete && uncertain.CommonIds.Count==0 &&
        uncertain.SingleMapIds.Count==0 && uncertain.Maps.Last().Error!=null,
        "Corrupt map mod list never produces an unproven global common-mod claim");
    bool invalidMapBlocked=false;
    try{LocalModAssignments.Inspect(new[]{modA,modA});}
    catch(InvalidDataException){invalidMapBlocked=true;}
    Assert(invalidMapBlocked,"Local mod inventory rejects duplicate map ownership identities");
    // The CurseForge catalog is tested with an injected HTTP handler: no real API key,
    // no network, no mod downloads and no server modifications.
    using(var client=new HttpClient(new SimulatedCurseForgeHttp()))
    {
        var catalog=new CurseForgeCatalog(client);
        var mods=await catalog.SearchAsaAsync("synthetic-approved-key","Shiny");
        Assert(mods.GameId==83374 && mods.Mods.Count==1 &&
            mods.Mods[0].Id==928548 && mods.Mods[0].Name=="Shiny! Dinos",
            "Approved-key CurseForge game discovery and read-only search parse fixture");
        Assert(mods.Mods[0].LatestFile=="Synthetic release" &&
            mods.Mods[0].Website=="https://www.curseforge.com/ark-survival-ascended/mods/shiny-dinos",
            "CurseForge search displays grounded metadata and safe provider URL");
        bool missingKey=false;
        try{await catalog.SearchAsaAsync("","Shiny");}
        catch(ArgumentException){missingKey=true;}
        Assert(missingKey,"CurseForge API refuses missing credentials");
        bool unauthorized=false;
        try{await catalog.SearchAsaAsync("invalid-key","Shiny");}
        catch(UnauthorizedAccessException){unauthorized=true;}
        Assert(unauthorized,"CurseForge denied API key is reported and no fallback scraping occurs");
    }
    // v1.3.4: Local file-copy backup does NOT claim live ASA, cluster or database consistency.
    string offlineMap=Path.Combine(root,"offline-map");
    string worldDir=Path.Combine(offlineMap,"ShooterGame","Saved","SavedArks");
    string iniDir=Path.Combine(offlineMap,"ShooterGame","Saved","Config","WindowsServer");
    string pluginsDir=Path.Combine(offlineMap,"ShooterGame","Binaries","Win64","ArkApi","Plugins","SamplePlugin");
    Directory.CreateDirectory(worldDir);Directory.CreateDirectory(iniDir);Directory.CreateDirectory(pluginsDir);
    File.WriteAllText(Path.Combine(worldDir,"world.ark"),"synthetic-world");
    File.WriteAllText(Path.Combine(iniDir,"Game.ini"),"synthetic-ini");
    File.WriteAllText(Path.Combine(pluginsDir,"settings.json"),"{\"secret\":\"preserve\"}");
    File.WriteAllText(Path.Combine(pluginsDir,"license.key"),"keep-license");
    File.WriteAllText(Path.Combine(pluginsDir,"data.db"),"persistent-rows");
    File.WriteAllText(Path.Combine(pluginsDir,"SamplePlugin.dll"),"old-binary");
    var offline=new LocalMap{Name="BackupFixture",InstallationPath=offlineMap};
    var stoppedMap=new ProcessCheck(false,"confirmed stopped",null,true);
    string goodPackage=Path.Combine(root,"scoped-backup");
    var scoped=LocalBackupWorkflow.Create(offline,stoppedMap,goodPackage,
        LocalBackupScope.WorldAndProfiles|LocalBackupScope.Ini|LocalBackupScope.Plugins);
    Assert(scoped.Manifest.Files.Count==6 &&
        TestBackupPackage.Verify(goodPackage,offline.Id).Files.Count==6,
        "Offline scoped world, INI and plugin backup verifies exact source files");
    Assert(scoped.Consistency.Contains("NOT established"),"Offline file-copy does not claim ASA consistency");
    string historyDirectory=Path.Combine(root,"backup-history-fixture");
    string historySource=Path.Combine(root,"backup-history-source");
    Directory.CreateDirectory(historySource);
    File.WriteAllText(Path.Combine(historySource,"world.dat"),"intact synthetic world");
    TestBackupPackage.Create(historySource,Path.Combine(historyDirectory,"20261103-demo"),
        offline.Id,"Synthetic backup history only");
    var history=LocalBackupCatalog.Inspect(historyDirectory,offline.Id);
    Assert(history.Count==1 && history[0].Valid && history[0].FileCount==1,
        "Per-map backup history verifies an intact package");
    File.WriteAllText(Path.Combine(historyDirectory,"20261103-demo","data","world.dat"),"damaged");
    history=LocalBackupCatalog.Inspect(historyDirectory,offline.Id);
    Assert(history.Count==1 && !history[0].Valid,
        "Per-map backup history flags corrupted packages without restoring them");
    Directory.CreateDirectory(Path.Combine(historyDirectory,"20261103-interrupted.incomplete"));
    history=LocalBackupCatalog.Inspect(historyDirectory,offline.Id);
    Assert(history.Count==2 && history.Any(x=>!x.Valid &&
        x.Status.Contains("Incomplete backup")),
        "Incomplete backup staging is visible and never marked restorable");
    string overviewRoot=Path.Combine(root,"multi-map-overview");
    var otherOwned=new LocalMap{Name="OtherLocalFixture"};
    string ownerRoot=Path.Combine(overviewRoot,"local-backups",offline.Id.ToString("N"));
    string ownerGood=Path.Combine(ownerRoot,"complete");
    TestBackupPackage.Create(historySource,ownerGood,offline.Id,"Synthetic multi-map history");
    var overview=LocalBackupOverview.Inspect(overviewRoot,new[]{offline,otherOwned});
    Assert(overview.Count==2 && overview[0].ValidPackages==1 &&
        overview[0].VerifiedBytes==new FileInfo(Path.Combine(historySource,"world.dat")).Length &&
        overview[1].ValidPackages==0,
        "Read-only multi-map backup overview reports real verified sizes and empty owned maps");
    File.WriteAllText(Path.Combine(ownerGood,"data","world.dat"),"corrupt");
    Directory.CreateDirectory(Path.Combine(ownerRoot,"interrupted.incomplete"));
    overview=LocalBackupOverview.Inspect(overviewRoot,new[]{offline,otherOwned});
    Assert(overview[0].ValidPackages==0 && overview[0].InvalidPackages==2 &&
        overview[0].VerifiedBytes==0,
        "Multi-map overview fails closed on corrupt and interrupted backup packages");
    bool duplicateOverviewBlocked=false;
    try{LocalBackupOverview.Inspect(overviewRoot,new[]{offline,offline});}
    catch(InvalidDataException){duplicateOverviewBlocked=true;}
    Assert(duplicateOverviewBlocked,"Multi-map backup overview rejects duplicate local map ownership");
    bool runningBackupBlocked=false;
    try{LocalBackupWorkflow.Create(offline,new ProcessCheck(true,"running",null),
        Path.Combine(root,"running-copy"),LocalBackupScope.Ini);}catch(InvalidOperationException){runningBackupBlocked=true;}
    Assert(runningBackupBlocked,"Backup requires confirmed stopped local map");
    bool nestedBackupBlocked=false;
    try{LocalBackupWorkflow.Create(offline,stoppedMap,Path.Combine(offlineMap,"nested-backup"),
        LocalBackupScope.Ini);}catch(InvalidDataException){nestedBackupBlocked=true;}
    Assert(nestedBackupBlocked,"Backup refuses source-destination path overlap");
    File.WriteAllText(Path.Combine(goodPackage,"data","ShooterGame","Saved","SavedArks","world.ark"),"tampered");
    bool scopedCorruption=false;
    try{TestBackupPackage.Verify(goodPackage,offline.Id);}catch(InvalidDataException){scopedCorruption=true;}
    Assert(scopedCorruption,"Scoped backup detects tampered world file");
    string packageZip=Path.Combine(root,"plugin-update.zip");
    using(var stream=File.Create(packageZip))
    using(var archive=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create))
    {
        archive.CreateEntry("SamplePlugin/"); // Normal ZIP tools include the plugin root directory.
        using(var writer=new StreamWriter(archive.CreateEntry("SamplePlugin/SamplePlugin.dll").Open()))writer.Write("new-binary");
        using(var writer=new StreamWriter(archive.CreateEntry("SamplePlugin/settings.json").Open()))writer.Write("{\"secret\":\"overwrite-attempt\"}");
    }
    var zipInfo=SafePluginZip.Inspect(packageZip);
    Assert(zipInfo.PluginName=="SamplePlugin"&&zipInfo.BinaryCount==1&&zipInfo.SkippedNonBinaryCount==1,
        "Safe plugin ZIP identifies root DLL and omitted configuration");
    bool runningInstallBlocked=false;
    try{SafePluginZip.Install(offline,new ProcessCheck(false,"unknown",null),
        packageZip,Path.Combine(root,"plugin-backups"));}catch(InvalidOperationException){runningInstallBlocked=true;}
    Assert(runningInstallBlocked,"Plugin ZIP refuses uncertain server state");
    var pluginPreview=SafePluginZip.Preview(offline,stoppedMap,packageZip,
        Path.Combine(root,"plugin-backups"));
    Assert(pluginPreview.Ready && pluginPreview.PreviousPluginExists &&
        pluginPreview.PluginName=="SamplePlugin" && pluginPreview.BinaryCount==1,
        "Plugin ZIP preview identifies existing plugin before changes");
    Assert(File.ReadAllText(Path.Combine(pluginsDir,"SamplePlugin.dll"))=="old-binary",
        "ZIP preview does not mutate existing plugin DLL");
    var blockedPreview=SafePluginZip.Preview(offline,new ProcessCheck(false,"unknown",null),
        packageZip,Path.Combine(root,"plugin-backups"));
    Assert(!blockedPreview.Ready,"Plugin ZIP preview blocks uncertain local map state");
    string kovaZip=Path.Combine(root,"protected-kova-plugin.zip");
    using(var stream=File.Create(kovaZip))
    using(var archive=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create))
        using(var writer=new StreamWriter(archive.CreateEntry("KovaCluster/KovaCluster.dll").Open()))
            writer.Write("synthetic forbidden DLL");
    bool protectedZipBlocked=false;
    try{SafePluginZip.Inspect(kovaZip);}catch(InvalidDataException){protectedZipBlocked=true;}
    Assert(protectedZipBlocked,"Untamed Cluster Vault plugin ZIP rejected before any installation or backup writes");
    var installed=SafePluginZip.Install(offline,stoppedMap,packageZip,Path.Combine(root,"plugin-backups"));
    Assert(File.ReadAllText(Path.Combine(pluginsDir,"SamplePlugin.dll"))=="new-binary",
        "Plugin ZIP replaces binary on confirmed stopped map");
    Assert(File.ReadAllText(Path.Combine(pluginsDir,"settings.json"))=="{\"secret\":\"preserve\"}" &&
        File.ReadAllText(Path.Combine(pluginsDir,"data.db"))=="persistent-rows" &&
        File.ReadAllText(Path.Combine(pluginsDir,"license.key"))=="keep-license",
        "Plugin ZIP preserves config, persistent data and license");
    Assert(installed.PreviousBackup!=null&&
        File.ReadAllText(Path.Combine(installed.PreviousBackup,"SamplePlugin.dll"))=="old-binary" &&
        !installed.PreviousBackup.StartsWith(offlineMap,StringComparison.OrdinalIgnoreCase),
        "Previous plugin fully backed up outside active installation");
    Assert(installed.PreviousBackup!=null &&
        File.Exists(installed.PreviousBackup+".untamed-manifest.json"),
        "Plugin update persists full SHA-256 manifest for guarded rollback");
    bool unknownRollbackBlocked=false;
    try{SafePluginZip.PreviewRollback(offline,new ProcessCheck(false,"unknown",null),
        "SamplePlugin",installed.PreviousBackup!,Path.Combine(root,"plugin-backups"));}
    catch(InvalidOperationException){unknownRollbackBlocked=true;}
    // Preview returns not-ready rather than throwing for uncertain process.
    var rollbackNotReady=SafePluginZip.PreviewRollback(offline,
        new ProcessCheck(false,"unknown",null),"SamplePlugin",
        installed.PreviousBackup!,Path.Combine(root,"plugin-backups"));
    Assert(!rollbackNotReady.Allowed,
        "Plugin rollback cannot be applied to uncertain or running map");
    var previewRollback=SafePluginZip.PreviewRollback(offline,stoppedMap,
        "SamplePlugin",installed.PreviousBackup!,Path.Combine(root,"plugin-backups"));
    Assert(previewRollback.Allowed && previewRollback.PreviousBinaryCount==1,
        "Guarded rollback preview validates prior full backup and map ownership");
    bool wrongOwnerRollbackBlocked=false;
    try{SafePluginZip.PreviewRollback(new LocalMap{
        Name="Foreign",InstallationPath=offline.InstallationPath},stoppedMap,
        "SamplePlugin",installed.PreviousBackup!,Path.Combine(root,"plugin-backups"));}
    catch(UnauthorizedAccessException){wrongOwnerRollbackBlocked=true;}
    Assert(wrongOwnerRollbackBlocked,
        "Plugin backup cannot be imported into a different local map identity");
    bool kovaRollbackBlocked=false;
    try{SafePluginZip.PreviewRollback(offline,stoppedMap,
        "KovaCluster",installed.PreviousBackup!,Path.Combine(root,"plugin-backups"));}
    catch(InvalidDataException){kovaRollbackBlocked=true;}
    Assert(kovaRollbackBlocked,"KovaCluster plugin rollback is forbidden");
    var rolledBack=SafePluginZip.Rollback(offline,stoppedMap,
        "SamplePlugin",installed.PreviousBackup!,Path.Combine(root,"plugin-backups"));
    Assert(rolledBack.RestoredBinaryCount==1 &&
        File.ReadAllText(Path.Combine(pluginsDir,"SamplePlugin.dll"))=="old-binary",
        "Guarded plugin rollback restores earlier DLL from verified backup");
    Assert(File.ReadAllText(Path.Combine(pluginsDir,"settings.json"))=="{\"secret\":\"preserve\"}" &&
        File.ReadAllText(Path.Combine(pluginsDir,"data.db"))=="persistent-rows" &&
        File.ReadAllText(Path.Combine(pluginsDir,"license.key"))=="keep-license",
        "Plugin rollback retains current configuration, database and license files");
    Assert(File.ReadAllText(Path.Combine(rolledBack.PreRollbackBackup,
        "SamplePlugin.dll"))=="new-binary" &&
        File.Exists(rolledBack.PreRollbackBackup+".untamed-manifest.json"),
        "Plugin rollback makes complete preimage protection before binary swap");
    File.WriteAllText(Path.Combine(installed.PreviousBackup!,"SamplePlugin.dll"),
        "tampered backup");
    bool tamperedRollbackBlocked=false;
    try{SafePluginZip.Rollback(offline,stoppedMap,"SamplePlugin",
        installed.PreviousBackup!,Path.Combine(root,"plugin-backups"));}
    catch(InvalidDataException){tamperedRollbackBlocked=true;}
    Assert(tamperedRollbackBlocked &&
        File.ReadAllText(Path.Combine(pluginsDir,"SamplePlugin.dll"))=="old-binary",
        "Corrupt rollback backup rejected before changing active plugin DLL");
    string syntheticKova=Path.Combine(offlineMap,"ShooterGame","Binaries","Win64",
        "ArkApi","Plugins","KovaCluster");
    Directory.CreateDirectory(syntheticKova);
    File.WriteAllText(Path.Combine(syntheticKova,"store.db"),"do not inspect this");
    var localInventory=LocalPluginInventory.Scan(offline);
    Assert(localInventory.Any(p=>p.PluginName=="SamplePlugin" && !p.Protected &&
        p.Binaries.Single().Sha256==Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(pluginsDir,"SamplePlugin.dll"))))),
        "Local plugin inventory reports verified DLL hashes without config reads");
    Assert(localInventory.Any(p=>p.PluginName=="KovaCluster" &&
        p.Protected && p.Binaries.Count==0) &&
        File.ReadAllText(Path.Combine(syntheticKova,"store.db"))=="do not inspect this",
        "Untamed Cluster Vault plugin is protected and store files remain unchanged");

    string sharedAsa=Path.Combine(root,"shared-asa");
    string asaExe=Path.Combine(sharedAsa,"ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(asaExe)!);
    File.WriteAllText(asaExe,"synthetic binary fixture");
    Directory.CreateDirectory(Path.Combine(sharedAsa,"steamapps"));
    File.WriteAllText(Path.Combine(sharedAsa,"steamapps","appmanifest_2430930.acf"),
        "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123456789\"\n}");
    var sharedA=new LocalMap{Name="AstraeosSynthetic",InstallationPath=sharedAsa};
    var sharedB=new LocalMap{Name="GenesisSynthetic",InstallationPath=sharedAsa};
    var groupedAsa=AsaInstallationInventory.Inspect(
        new[]{sharedA,sharedB},_=>new ProcessCheck(false,"stopped",null,true));
    Assert(groupedAsa.Count==1 && groupedAsa[0].LocalMaps.Count==2 &&
        groupedAsa[0].InstalledSteamBuild=="123456789" && groupedAsa[0].UpdateBlocked,
        "Shared ASA installation groups both local maps and never enables update");
    var runningAsa=AsaInstallationInventory.Inspect(
        new[]{sharedA,sharedB},_=>new ProcessCheck(true,"running",null));
    Assert(runningAsa[0].UpdateBlocked &&
        runningAsa[0].Reason.Contains("running or has uncertain"),
        "ASA update remains blocked when any map on shared installation is running");
    string unsafeZip=Path.Combine(root,"unsafe-plugin.zip");
    using(var stream=File.Create(unsafeZip))
    using(var archive=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create))
        using(var writer=new StreamWriter(archive.CreateEntry("SamplePlugin/../evil.dll").Open()))writer.Write("evil");
    bool unsafeBlocked=false;
    try{SafePluginZip.Inspect(unsafeZip);}catch(InvalidDataException){unsafeBlocked=true;}
    Assert(unsafeBlocked,"Plugin ZIP traversal is rejected before disk modification");
    foreach(var unsafeName in new[]{"SamplePlugin/CON.dll","SamplePlugin/extra.dll."})
    {
        string invalidZip=Path.Combine(root,"winpath-"+Guid.NewGuid().ToString("N")+".zip");
        using(var stream=File.Create(invalidZip))
        using(var archive=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create))
        {
            using(var writer=new StreamWriter(archive.CreateEntry("SamplePlugin/SamplePlugin.dll").Open()))
                writer.Write("safe-root");
            using(var writer=new StreamWriter(archive.CreateEntry(unsafeName).Open()))
                writer.Write("unsafe-path");
        }
        bool rejected=false;
        try{SafePluginZip.Inspect(invalidZip);}catch(InvalidDataException){rejected=true;}
        Assert(rejected,"Windows-reserved ZIP plugin paths are rejected before extraction: "+unsafeName);
    }
    familyLoader=Process.Start(new ProcessStartInfo(fake,"--loader-only-fixture"){UseShellExecute=false,CreateNoWindow=true})!;
    await Task.Delay(200);
    var loaderOnlyMap=new LocalMap{Name="Starting",ExecutablePath=fake,Arguments="--child-fixture",ApiLoaderPath=fake,ApiLoaderArguments="--loader-only-fixture"};
    var loaderOnlyState=new WindowsProcessInspector().InspectFamily(loaderOnlyMap);
    Assert(!loaderOnlyState.ConfirmedStopped,"Actual loader without child blocks duplicate start");
    familyLoader.Kill();await familyLoader.WaitForExitAsync();familyLoader.Dispose();familyLoader=null;
    familyLoader=Process.Start(new ProcessStartInfo(fake,"--loader-fixture"){UseShellExecute=false,CreateNoWindow=true})!;
    for(int attempt=0;attempt<30 && familyChild==null;attempt++)
    {
        await Task.Delay(100);
        using var query=new System.Management.ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,ExecutablePath,CommandLine FROM Win32_Process");
        using var rows=query.Get();
        foreach(System.Management.ManagementObject row in rows)
        {
            if(Convert.ToInt32(row["ParentProcessId"])!=familyLoader.Id)continue;
            if(!WindowsProcessInspector.CommandMatches(Convert.ToString(row["CommandLine"])??"",fake,"--child-fixture"))continue;
            if(!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToString(row["ExecutablePath"]),fake))continue;
            familyChild=Process.GetProcessById(Convert.ToInt32(row["ProcessId"]));
        }
    }
    Assert(familyChild!=null,"Windows WMI finds actual fixture child with exact EXE and arguments");
    var actualParent=new ProcessIdentity{Pid=familyLoader.Id,StartedUtc=new DateTimeOffset(familyLoader.StartTime.ToUniversalTime()),ExecutablePath=fake,Arguments="--loader-fixture"};
    var actualChild=new ProcessIdentity{Pid=familyChild!.Id,StartedUtc=new DateTimeOffset(familyChild.StartTime.ToUniversalTime()),ExecutablePath=fake,Arguments="--child-fixture"};
    Assert(ProcessFamily.IsVerifiedPair(new[]{new ObservedProcess(actualParent.Pid,0,actualParent.StartedUtc,fake,actualParent.Arguments),new ObservedProcess(actualChild.Pid,actualParent.Pid,actualChild.StartedUtc,fake,actualChild.Arguments)},actualParent,actualChild),"Actual Windows fixture identities validate parent-child pair");
    // Simulate Windows WMI reporting a changed orphan parent PID after loader exit.
    var survivor=new ObservedProcess(actualChild.Pid,0,actualChild.StartedUtc,fake,actualChild.Arguments);
    Assert(ProcessFamily.IsPersistedSurvivor(survivor,actualChild,actualParent,
        fake,"--child-fixture",fake,"--loader-fixture"),
        "Verified orphan child reattaches despite Windows reparenting");
    Assert(!ProcessFamily.IsPersistedSurvivor(survivor,new ProcessIdentity{
        Pid=actualChild.Pid,StartedUtc=actualChild.StartedUtc.AddSeconds(-5),
        ExecutablePath=fake,Arguments="--child-fixture"},actualParent,
        fake,"--child-fixture",fake,"--loader-fixture"),
        "Orphan PID reuse and mismatched creation time are rejected");
    Assert(!ProcessFamily.IsPersistedSurvivor(survivor,actualChild,null,
        fake,"--child-fixture",fake,"--loader-fixture"),
        "Orphan cannot reattach without previously verified loader identity");
    Assert(!ProcessFamily.IsPersistedSurvivor(survivor,actualChild,actualParent,
        fake,"--other-map",fake,"--loader-fixture"),
        "Orphan cannot reattach with altered map launch arguments");
    var familyRoot=Path.Combine(root,"family-profile");
    var familyService=new ClusterService("family-test",familyRoot);
    var familyMap=new LocalMap{Name="Family",InstallationPath=Path.GetDirectoryName(fake)!,ExecutablePath=fake,Arguments="--child-fixture",ApiLoaderPath=fake,ApiLoaderArguments="--loader-fixture"};
    familyService.AddMap(familyMap);
    var familyObservation=familyService.Check(familyMap);
    Assert(familyObservation.Running,"Manager integrates WMI family validation");
    Assert(familyService.RecordObservedIdentity(familyMap.Id,familyObservation),
        "Operator-confirmed family identity persists without poll-time writes");
    bool familyDuplicate=false;
    try{familyService.LaunchLocal(familyMap.Id);}catch(InvalidOperationException){familyDuplicate=true;}
    Assert(familyDuplicate,"Family launch gate rejects duplicate start");
    await familyService.DisposeAsync();
    familyService=new ClusterService("family-test",familyRoot);familyMap=familyService.Config.LocalMaps.Single();
    Assert(familyService.Check(familyMap).Running,"Restarted Manager reattaches verified family");
    familyLoader.Kill();await familyLoader.WaitForExitAsync();familyLoader.Dispose();familyLoader=null;
    // WMI may still expose the just-terminated parent for a short interval.
    // Keep the production inspector fail-closed; wait for a fresh observation
    // rather than claiming a stale or ambiguous family is safe.
    var orphanObservation=familyService.Check(familyMap);
    for(int retry=0;retry<30 && !orphanObservation.Running && !familyChild.HasExited;retry++)
    {
        await Task.Delay(100);
        orphanObservation=familyService.Check(familyMap);
    }
    Assert(!familyChild.HasExited,"Fixture child survives loader exit");
    Assert(orphanObservation.Running,
        "Persisted child reattaches after loader exits: "+orphanObservation.Status);
    await familyService.DisposeAsync();
    familyChild.Kill();await familyChild.WaitForExitAsync();familyChild.Dispose();familyChild=null;
    var restore=new SyntheticRestore();
    File.WriteAllText(Path.Combine(restore.TargetDirectory,"world"),"old world");
    string restoreSource=Path.Combine(root,"restore-source");Directory.CreateDirectory(restoreSource);
    File.WriteAllText(Path.Combine(restoreSource,"world"),"new world");
    string restorePackage=Path.Combine(root,"restore-package");
    TestBackupPackage.Create(restoreSource,restorePackage,restore.Owner,"Synthetic world");
    restore.Restore(restorePackage);
    Assert(File.ReadAllText(Path.Combine(restore.TargetDirectory,"world"))=="new world","Synthetic restore stages and verifies complete payload");
    File.WriteAllText(Path.Combine(restore.TargetDirectory,"world"),"protected world");
    bool interruptedRestore=false;
    try { restore.Restore(restorePackage,true); } catch(IOException) { interruptedRestore=true; }
    Assert(interruptedRestore && restore.RecoveryRequired,"Interrupted restore retains durable recovery journal");
    restore.Recover();
    Assert(!restore.RecoveryRequired && File.ReadAllText(Path.Combine(restore.TargetDirectory,"world"))=="protected world","Rollback restores and hashes the original directory");
    File.WriteAllText(Path.Combine(restorePackage,"data","world"),"bad payload");
    bool restoreCorrupt=false;
    try { restore.Restore(restorePackage); } catch(InvalidDataException) { restoreCorrupt=true; }
    Assert(restoreCorrupt && File.ReadAllText(Path.Combine(restore.TargetDirectory,"world"))=="protected world","Corrupt restore rejected before target changes");
    var loaderIdentity=new ProcessIdentity{Pid=10,StartedUtc=DateTimeOffset.UtcNow,ExecutablePath="loader.exe",Arguments="--test"};
    var childIdentity=new ProcessIdentity{Pid=11,StartedUtc=loaderIdentity.StartedUtc.AddSeconds(1),ExecutablePath="server.exe",Arguments="--map=test"};
    var observedLoader=new ObservedProcess(10,1,loaderIdentity.StartedUtc,"loader.exe","--test");
    var observedChild=new ObservedProcess(11,10,childIdentity.StartedUtc,"server.exe","--map=test");
    Assert(ProcessFamily.IsVerifiedPair(new[]{observedLoader,observedChild},loaderIdentity,childIdentity),"Exact loader-child observation validates");
    Assert(!ProcessFamily.IsVerifiedPair(new[]{observedLoader,observedChild with {ParentPid=99}},loaderIdentity,childIdentity),"Wrong parent rejects loader-child relationship");
    Assert(!ProcessFamily.IsVerifiedPair(new[]{observedLoader,observedChild with {StartedUtc=loaderIdentity.StartedUtc.AddSeconds(-1)}},loaderIdentity,childIdentity),"Reused child identity rejected");
    Assert(!ProcessFamily.IsVerifiedPair(new[]{observedLoader},loaderIdentity,childIdentity),"Missing child rejected");
    string backupDir=Path.Combine(root,"backup-fixture");
    Directory.CreateDirectory(backupDir);
    string backupFile=Path.Combine(backupDir,"world.test");
    File.WriteAllText(backupFile,"original world fixture");
    Guid backupOwner=Guid.NewGuid();
    var manifest=BackupIntegrity.Create(backupDir,backupOwner,"Synthetic world only; no Store or SQL");
    manifest=System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(BackupIntegrity.Serialize(manifest))!;
    BackupIntegrity.Verify(backupDir,manifest,backupOwner);
    Assert(manifest.Files.Count==1,"Manifest roundtrip preserves scope, ownership and file hashes");
    File.WriteAllText(backupFile,"corrupt world fixture!");
    bool corrupt=false;
    try { BackupIntegrity.Verify(backupDir,manifest,backupOwner); } catch(InvalidDataException) { corrupt=true; }
    Assert(corrupt,"Corrupt backup rejected");
    File.Delete(backupFile);
    bool missing=false;
    try { BackupIntegrity.Verify(backupDir,manifest,backupOwner); } catch(InvalidDataException) { missing=true; }
    Assert(missing,"Missing backup file rejected");
    string packageSource=Path.Combine(root,"package-source");
    Directory.CreateDirectory(packageSource);
    File.WriteAllText(Path.Combine(packageSource,"world.fixture"),"synthetic data");
    string package=Path.Combine(root,"package");
    TestBackupPackage.Create(packageSource,package,backupOwner,"Synthetic world only");
    Assert(TestBackupPackage.Verify(package,backupOwner).Files.Count==1,"Actual staged test backup commits and verifies");
    File.WriteAllText(Path.Combine(package,"data","world.fixture"),"damaged");
    bool packageCorrupt=false;
    try { TestBackupPackage.Verify(package,backupOwner); } catch(InvalidDataException) { packageCorrupt=true; }
    Assert(packageCorrupt,"Actual backup payload corruption rejected");
    File.Delete(Path.Combine(package,"complete"));
    bool incomplete=false;
    try { TestBackupPackage.Verify(package,backupOwner); } catch(InvalidDataException) { incomplete=true; }
    Assert(incomplete,"Uncommitted backup rejected");
    Assert(!LocalStoreProbe.Inspect(Path.Combine(root,"missing-store"),"KovaCluster","-usestore").Safe,"Unavailable Store blocks operations");
    Assert(!LocalStoreProbe.Inspect(packageSource,"KovaCluster","-usestore").Safe,"Unknown Store metadata fails closed");
    Assert(!LocalStoreProbe.Inspect(packageSource,"KovaCluster","-usestore -converttostore").Safe,"Store conversion is rejected");
    foreach(var mode in new[]{"success","no-save-proof","rcon-failure","timeout","exit-timeout","unknown-exit"})
    {
        var session=new LifecycleFixture(mode);
        bool succeeded=false;
        try { await SafeLifecycle.Stop(session,true,TimeSpan.FromMilliseconds(300)); succeeded=true; }
        catch(Exception ex) when(ex is InvalidOperationException or IOException or OperationCanceledException) {}
        Assert(succeeded==(mode=="success"),"Lifecycle result: "+mode);
        if(mode=="success")Assert(session.Commands.SequenceEqual(new[]{"ServerChat Server maintenance: saving world before shutdown","SaveWorld","DoExit"}),"Stop command order preserves save-before-exit");
        Assert(session.Starts==(mode=="success"?1:0),"Restart only after verified exit: "+mode);
        if(mode=="no-save-proof" || mode=="rcon-failure")
            Assert(!session.Commands.Contains("DoExit"),"No DoExit without save evidence: "+mode);
    }
    bool refusedLiveWrite=false;
    try { await RconProtocol.Execute("127.0.0.1",12345,"fixture","SaveWorld"); }
    catch(InvalidOperationException){refusedLiveWrite=true;}
    Assert(refusedLiveWrite,"Production RCON SaveWorld remains blocked before verified SaveComplete capability");
    var stalled=new TcpListener(IPAddress.Loopback,0);stalled.Start();
    try
    {
        using var deadline=new CancellationTokenSource(200);
        bool timedOut=false;
        try { await RconProtocol.Execute("127.0.0.1",((IPEndPoint)stalled.LocalEndpoint).Port,"fixture","ListPlayers",deadline.Token); }
        catch(OperationCanceledException) { timedOut=true; }
        Assert(timedOut,"Real RCON socket cancellation on silent endpoint");
    }
    finally { stalled.Stop(); }
    string protectedProfile=Path.Combine(root,"authenticated-profile");Directory.CreateDirectory(protectedProfile);
    var ipc=new SupervisorChannel(protectedProfile);
    var signedSnapshot=new SupervisorSnapshot("test-host",Environment.ProcessId,DateTimeOffset.UtcNow,fake,DateTimeOffset.UtcNow,Array.Empty<MapObservation>());
    ipc.Write(signedSnapshot);
    Assert(ipc.Read("test-host").HostId=="test-host","Protected DPAPI/HMAC local IPC roundtrip");
    var secondClient=new SupervisorChannel(protectedProfile);
    Assert(secondClient.Read("test-host").HostId=="test-host","Local IPC reconnects with durable user-protected key");
    bool wrongHost=false;try{ipc.Read("other-host");}catch(IOException){wrongHost=true;}
    Assert(wrongHost,"Authenticated IPC rejects foreign host identity");
    ipc.Write(signedSnapshot with{ObservedUtc=DateTimeOffset.UtcNow.AddMinutes(-2)});
    bool staleIpc=false;try{ipc.Read("test-host");}catch(IOException){staleIpc=true;}
    Assert(staleIpc,"Authenticated IPC rejects stale heartbeat");
    ipc.Write(signedSnapshot);
    var signedFrame=System.Text.Json.JsonSerializer.Deserialize<SignedObservation>(File.ReadAllText(ipc.SnapshotPath))!;
    string badSignature=(signedFrame.Signature[0]=='0'?"1":"0")+signedFrame.Signature[1..];
    File.WriteAllText(ipc.SnapshotPath,System.Text.Json.JsonSerializer.Serialize(signedFrame with{Signature=badSignature}));
    bool tamperedIpc=false;try{ipc.Read("test-host");}catch(System.Security.Cryptography.CryptographicException){tamperedIpc=true;}
    Assert(tamperedIpc,"Authenticated IPC rejects modified observation");
    string eventRoot=Path.Combine(root,"events");var eventLog=new EventJournal(eventRoot);
    eventLog.Append("hostA",backupOwner,"Warning","rcon-unavailable");
    eventLog.Append("hostB",Guid.Empty,"Information","process-start");
    var reopenedLog=new EventJournal(eventRoot);
    Assert(reopenedLog.Read("hostA",backupOwner,"Warning",DateTimeOffset.UtcNow.AddMinutes(-1)).Length==1,"Events persist and filter by host, map, severity and time");
    Assert(reopenedLog.Read().Select(x=>x.Id).Distinct().Count()==2,"Persisted events carry unique identifiers");
    bool secretEvent=false;try{eventLog.Append("hostA",null,"Error","password=secret");}catch(ArgumentException){secretEvent=true;}
    Assert(secretEvent,"Event codes reject arbitrary secret-bearing text");
    string scheduleRoot=Path.Combine(root,"persistent-schedule");var durable=new PersistentScheduler(scheduleRoot);
    var job=new ScheduledTask{Map=backupOwner,Kind=ScheduledKind.Notification,DueUtc=DateTimeOffset.UtcNow.AddSeconds(-1)};
    int executions=0;var owned=new HashSet<Guid>{backupOwner};
    await durable.RunDue(new[]{job},owned,DateTimeOffset.UtcNow,_=>{executions++;return Task.CompletedTask;});
    await new PersistentScheduler(scheduleRoot).RunDue(new[]{job},owned,DateTimeOffset.UtcNow,_=>{executions++;return Task.CompletedTask;});
    Assert(executions==1,"Persistent scheduler never repeats same due time after reconstruction");
    var failedJob=new ScheduledTask{Map=backupOwner,Kind=ScheduledKind.Observation,DueUtc=DateTimeOffset.UtcNow.AddSeconds(-1)};
    durable=new PersistentScheduler(scheduleRoot);
    await durable.RunDue(new[]{failedJob},owned,DateTimeOffset.UtcNow,_=>throw new IOException("synthetic failure"));
    durable=new PersistentScheduler(scheduleRoot);
    await durable.RunDue(new[]{failedJob},owned,DateTimeOffset.UtcNow,_=>{executions++;return Task.CompletedTask;});
    Assert(durable.Receipts.Single(x=>x.Task==failedJob.Id).Status=="failed-no-auto-retry"&&executions==1,"Failed schedule remains bounded and is not repeated on restart");
    var foreignJob=new ScheduledTask{Map=Guid.Empty,Kind=ScheduledKind.Notification,DueUtc=DateTimeOffset.UtcNow.AddSeconds(-1)};
    var autoJob=new ScheduledTask{Map=backupOwner,Kind=ScheduledKind.AutoStart,DueUtc=DateTimeOffset.UtcNow.AddSeconds(-1)};
    await durable.RunDue(new[]{foreignJob,autoJob},owned,DateTimeOffset.UtcNow,_=>throw new Exception("must not execute"));
    Assert(durable.Receipts.Single(x=>x.Task==foreignJob.Id).Status=="foreign-map-blocked","Scheduler enforces local map ownership");
    Assert(durable.Receipts.Single(x=>x.Task==autoJob.Id).Status=="autostart-disabled","Automatic server start remains disabled");
    var interruptedJob=new ScheduledTask{Map=backupOwner,Kind=ScheduledKind.Notification,DueUtc=DateTimeOffset.UtcNow.AddSeconds(-1)};
    string interruptedRoot=Path.Combine(root,"interrupted-scheduler");Directory.CreateDirectory(interruptedRoot);
    File.WriteAllText(Path.Combine(interruptedRoot,"scheduler-state.json"),System.Text.Json.JsonSerializer.Serialize(new[]{new TaskReceipt(interruptedJob.Id,interruptedJob.DueUtc,"claimed",0)}));
    await new PersistentScheduler(interruptedRoot).RunDue(new[]{interruptedJob},owned,DateTimeOffset.UtcNow,_=>throw new Exception("must not repeat uncertain action"));
    Assert(new PersistentScheduler(interruptedRoot).Receipts.Single().Status=="claimed","Interrupted claimed schedule is never blindly retried");
    // v1.3.7: all recovery and SaveWorld proofs are FakeAsa-only, without a
    // production RCON, filesystem restore, Process.Kill or server launch path.
    var syntheticTime=DateTimeOffset.UtcNow;
    var recoveryMap=Guid.NewGuid();
    var syntheticProcess=new ProcessIdentity{
        Pid=56780,StartedUtc=syntheticTime.AddMinutes(-5),
        ExecutablePath="FakeAsa.exe",Arguments="--synthetic-only"
    };
    const string recoveryToken="synthetic-save-correlation";
    var savedSignals=new[]{
        new SyntheticSaveSignal(recoveryMap,recoveryToken,SyntheticSaveSignalKind.Started,
            syntheticProcess,syntheticTime.AddMilliseconds(100)),
        new SyntheticSaveSignal(recoveryMap,recoveryToken,SyntheticSaveSignalKind.Completed,
            syntheticProcess,syntheticTime.AddMilliseconds(400))
    };
    var proof=SaveWorldValidator.VerifySynthetic(recoveryMap,syntheticProcess,
        recoveryToken,syntheticTime,syntheticTime.AddSeconds(1),savedSignals);
    Assert(proof.Proven,"Correlated FakeAsa SaveWorld start/end confirms only synthetic proof");
    Assert(!SaveWorldValidator.VerifyLiveUnavailable("OK",syntheticTime).Proven,
        "RCON reply and file modification cannot prove a live ASA SaveWorld");
    Assert(!SaveWorldValidator.VerifySynthetic(recoveryMap,syntheticProcess,
        recoveryToken,syntheticTime,syntheticTime.AddSeconds(1),savedSignals.Take(1)).Proven,
        "SaveWorld started without completion does not authorize shutdown");
    Assert(!SaveWorldValidator.VerifySynthetic(recoveryMap,syntheticProcess,
        recoveryToken,syntheticTime,syntheticTime.AddSeconds(1),savedSignals.Concat(savedSignals)).Proven,
        "Duplicate or replayed SaveWorld event pair fails closed");
    Assert(!SaveWorldValidator.VerifySynthetic(Guid.NewGuid(),syntheticProcess,
        recoveryToken,syntheticTime,syntheticTime.AddSeconds(1),savedSignals).Proven,
        "FakeAsa SaveWorld events cannot be replayed for a different local map");
    var modifiedProcess=new ProcessIdentity{
        Pid=syntheticProcess.Pid,StartedUtc=syntheticProcess.StartedUtc.AddSeconds(-1),
        ExecutablePath=syntheticProcess.ExecutablePath,Arguments=syntheticProcess.Arguments
    };
    Assert(!SaveWorldValidator.VerifySynthetic(recoveryMap,syntheticProcess,
        recoveryToken,syntheticTime,syntheticTime.AddSeconds(1),new[]{
            savedSignals[0],
            savedSignals[1] with {Server=modifiedProcess}
        }).Proven,"SaveWorld completion from reused PID or changed process is rejected");
    Assert(!SaveWorldValidator.VerifySynthetic(recoveryMap,syntheticProcess,
        recoveryToken,syntheticTime,syntheticTime.AddMinutes(4),savedSignals).Proven,
        "Old SaveWorld observations expire and cannot be reused");

    var diagnosis=new WatchdogDiagnostics();
    var runningSynthetic=new ProcessCheck(true,"running",syntheticProcess);
    for(int i=1;i<=3;i++)
    {
        var rconDiagnosis=diagnosis.Observe(new WatchdogEvidence(recoveryMap,
            runningSynthetic,false,false,false,RconSignal.Unreachable,true,true,
            false,syntheticTime.AddSeconds(i)));
        Assert(rconDiagnosis.ConfirmedRunning && !rconDiagnosis.ConfirmedStopped &&
            rconDiagnosis.Issues.Contains(WatchdogIssue.RconUnreachable) &&
            rconDiagnosis.FailureCounts[WatchdogIssue.RconUnreachable]==i &&
            rconDiagnosis.NeedsAttention==(i==3),
            "RCON failures accumulate without treating the ASA process as dead: "+i);
    }
    var recoveredRcon=diagnosis.Observe(new WatchdogEvidence(recoveryMap,
        runningSynthetic,false,false,false,RconSignal.Responding,true,true,
        false,syntheticTime.AddSeconds(4)));
    Assert(!recoveredRcon.NeedsAttention && recoveredRcon.Issues.Count==0,
        "Healthy independent RCON observation resets only relevant Watchdog counters");
    var badAuth=diagnosis.Observe(new WatchdogEvidence(recoveryMap,
        runningSynthetic,false,false,false,RconSignal.AuthenticationFailed,true,true,
        false,syntheticTime.AddSeconds(5)));
    Assert(badAuth.FailureCounts.ContainsKey(WatchdogIssue.RconAuthentication) &&
        !badAuth.FailureCounts.ContainsKey(WatchdogIssue.RconUnreachable),
        "Authentication failures have separate Watchdog counters");
    var loaderMissing=diagnosis.Observe(new WatchdogEvidence(recoveryMap,
        runningSynthetic,false,true,false,RconSignal.Responding,true,true,
        false,syntheticTime.AddSeconds(6)));
    Assert(loaderMissing.Issues.Contains(WatchdogIssue.LoaderUncertain) &&
        loaderMissing.ConfirmedRunning,"Missing loader cannot justify a duplicate process");
    var unknownWatchdog=diagnosis.Observe(new WatchdogEvidence(recoveryMap,
        new ProcessCheck(false,"unknown",null),false,false,false,
        RconSignal.NotConfigured,false,false,false,syntheticTime.AddSeconds(7)));
    Assert(unknownWatchdog.Issues.Contains(WatchdogIssue.ProcessUncertain) &&
        unknownWatchdog.Issues.Contains(WatchdogIssue.SupervisorStale) &&
        unknownWatchdog.Issues.Contains(WatchdogIssue.NetworkUncertain),
        "Stale heartbeat, unknown process and network outage are independent diagnoses");
    var stoppedWatchdog=diagnosis.Observe(new WatchdogEvidence(recoveryMap,
        new ProcessCheck(false,"verified stopped",null,true),true,false,false,
        RconSignal.NotConfigured,true,true,false,syntheticTime.AddSeconds(8)));
    Assert(stoppedWatchdog.Issues.Contains(WatchdogIssue.UnexpectedStop) &&
        stoppedWatchdog.ConfirmedStopped,"Unexpected verified stop is distinct from unknown state");
    var maintenanceDiagnosis=diagnosis.Observe(new WatchdogEvidence(recoveryMap,
        new ProcessCheck(false,"unknown",null),true,true,false,
        RconSignal.Unreachable,false,false,true,syntheticTime.AddSeconds(9)));
    Assert(maintenanceDiagnosis.Maintenance && !maintenanceDiagnosis.NeedsAttention &&
        maintenanceDiagnosis.Issues.Count==0,"Maintenance suppresses all automatic Watchdog action");

    string syntheticRecoveryProfile=Path.Combine(root,"synthetic-recovery-profile");
    var recoveryJournal=new RecoverySimulationJournal(syntheticRecoveryProfile);
    var syntheticScenario=new RecoveryScenario(recoveryMap,true,false,runningSynthetic,
        recoveryToken,syntheticTime,syntheticTime.AddSeconds(1),savedSignals,true,true);
    var done=RecoverySimulator.Run(recoveryJournal,syntheticScenario);
    Assert(done.Phase==RecoveryPhase.Completed && !done.ExecutedRealActions &&
        recoveryJournal.Read().Where(x=>x.Attempt==done.Attempt).Select(x=>x.Phase).SequenceEqual(
            new[]{RecoveryPhase.Prepared,RecoveryPhase.SaveRequested,
                RecoveryPhase.SaveProven,RecoveryPhase.ExitConfirmed,
                RecoveryPhase.StartConfirmed,RecoveryPhase.Completed}),
        "FakeAsa lifecycle records each synthetic SaveWorld/Stop/Start stage durably");
    bool blockedRepeat=false;
    try{RecoverySimulator.Run(new RecoverySimulationJournal(syntheticRecoveryProfile),syntheticScenario);}
    catch(InvalidOperationException){blockedRepeat=true;}
    Assert(blockedRepeat,"Completed synthetic attempt cannot be retried during cooldown");

    var missingSaveMap=Guid.NewGuid();
    var noProof=RecoverySimulator.Run(recoveryJournal,syntheticScenario with {
        Map=missingSaveMap,SaveSignals=Array.Empty<SyntheticSaveSignal>()});
    Assert(noProof.Phase==RecoveryPhase.Blocked &&
        recoveryJournal.Read().Where(x=>x.Attempt==noProof.Attempt)
            .All(x=>x.Phase!=RecoveryPhase.ExitConfirmed &&
                x.Phase!=RecoveryPhase.StartConfirmed),
        "No SaveWorld proof prevents simulated DoExit and Start");
    var failedExitMap=Guid.NewGuid();
    var noExit=RecoverySimulator.Run(recoveryJournal,syntheticScenario with{
        Map=failedExitMap,FakeExitConfirmed=false,
        SaveSignals=savedSignals.Select(x=>x with{Map=failedExitMap}).ToArray()});
    Assert(noExit.Phase==RecoveryPhase.Blocked &&
        !recoveryJournal.Read().Any(x=>x.Attempt==noExit.Attempt &&
            x.Phase==RecoveryPhase.StartConfirmed),
        "Unknown exit prevents synthetic restart");

    var crashedMap=Guid.NewGuid();
    var crashed=recoveryJournal.Begin(crashedMap,syntheticTime);
    bool crashCannotResume=false;
    try{RecoverySimulator.Run(new RecoverySimulationJournal(syntheticRecoveryProfile),
        syntheticScenario with {Map=crashedMap,Now=syntheticTime.AddMinutes(7)});}
    catch(InvalidOperationException){crashCannotResume=true;}
    Assert(crashCannotResume &&
        recoveryJournal.Read().Last(x=>x.Map==crashedMap).Phase==RecoveryPhase.Prepared,
        "Interrupted recovery state cannot auto-resume after Manager restart");
    var rateMap=Guid.NewGuid();
    for(int i=0;i<3;i++)
    {
        var sampled=syntheticTime.AddMinutes(6*i);
        var started=recoveryJournal.Begin(rateMap,sampled);
        recoveryJournal.Advance(started.Attempt,RecoveryPhase.Blocked,sampled);
    }
    bool rateLimited=false;
    try{recoveryJournal.Begin(rateMap,syntheticTime.AddMinutes(18));}
    catch(InvalidOperationException){rateLimited=true;}
    Assert(rateLimited,"Fourth synthetic recovery attempt within one hour is rate limited");
    bool foreignRecoveryBlocked=false;
    try{RecoverySimulator.Run(recoveryJournal,syntheticScenario with{
        Map=Guid.NewGuid(),Owned=false});}
    catch(UnauthorizedAccessException){foreignRecoveryBlocked=true;}
    Assert(foreignRecoveryBlocked,"Remote map recovery is forbidden even in simulation");
    Assert(RecoverySimulator.Run(recoveryJournal,syntheticScenario with{
        Map=Guid.NewGuid(),Maintenance=true}).Phase==RecoveryPhase.Blocked,
        "Maintenance prevents synthetic recovery before recording an attempt");
    Assert(RecoverySimulator.Run(recoveryJournal,syntheticScenario with{
        Map=Guid.NewGuid(),Initial=new ProcessCheck(false,"uncertain",null)}).Phase==
            RecoveryPhase.Blocked,"Unknown process state never enables a second FakeAsa");
    Assert(!new RecoverySimulationJournal(syntheticRecoveryProfile).Read().Any(x=>x.Code.Contains("kill")),
        "Recovery journal contains no real Kill or forced-start commands");

    // IP observations are offline fixtures: no router access and no real DDNS writes.
    var wanTracker=new WanChangeTracker();
    var wan0=wanTracker.Observe(new WanProbe(syntheticTime,
        "8.8.8.8","8.8.8.8",new[]{"8.8.8.8"}));
    var wan1=wanTracker.Observe(new WanProbe(syntheticTime.AddSeconds(31),
        "8.8.8.8","8.8.8.8",new[]{"8.8.8.8"}));
    Assert(wan0.State=="Learning" && wan1.State=="Stable" &&
        wan1.Baseline=="8.8.8.8" && !wan1.ConfirmedChange,
        "Two independent time-separated measurements establish WAN baseline");
    var disagreement=wanTracker.Observe(new WanProbe(syntheticTime.AddSeconds(62),
        "8.8.8.8","1.1.1.1",new[]{"8.8.8.8"}));
    Assert(disagreement.State=="Uncertain" && !disagreement.ConfirmedChange,
        "Disagreeing WAN providers never trigger recovery");
    var pendingChange=wanTracker.Observe(new WanProbe(syntheticTime.AddSeconds(93),
        "1.1.1.1","1.1.1.1",new[]{"8.8.8.8"}));
    var confirmedChange=wanTracker.Observe(new WanProbe(syntheticTime.AddSeconds(124),
        "1.1.1.1","1.1.1.1",new[]{"8.8.8.8"}));
    Assert(!pendingChange.ConfirmedChange && confirmedChange.ConfirmedChange &&
        !confirmedChange.DdnsMatches && confirmedChange.Baseline=="1.1.1.1",
        "Confirmed WAN change detects stale DDNS but never restarts servers");
    var privateWan=wanTracker.Observe(new WanProbe(syntheticTime.AddSeconds(155),
        "192.168.1.200","192.168.1.200",new[]{"192.168.1.200"}));
    Assert(privateWan.State=="Uncertain" && !privateWan.ConfirmedChange,
        "Private/LAN IP addresses are not mistaken for valid external WAN measurements");
    // The synthetic Store and SQL fixture contents never change in recovery tests.
    string safeStore=Path.Combine(syntheticRecoveryProfile,"Untamed Cluster VaultStore.fixture");
    string safeSql=Path.Combine(syntheticRecoveryProfile,"database.fixture");
    File.WriteAllText(safeStore,"original Untamed Cluster Vault synthetic fixture");
    File.WriteAllText(safeSql,"original SQL synthetic fixture");
    var untouchedMap=Guid.NewGuid();
    RecoverySimulator.Run(recoveryJournal,syntheticScenario with {
        Map=untouchedMap,SaveSignals=savedSignals.Select(x=>x with{Map=untouchedMap}).ToArray()});
    Assert(File.ReadAllText(safeStore)=="original Untamed Cluster Vault synthetic fixture" &&
           File.ReadAllText(safeSql)=="original SQL synthetic fixture",
        "Fake recovery never modifies synthetic Untamed Cluster Vault or SQL data");

    var policy=new SupervisorPolicy();Guid monitored=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
    var unknown=new ProcessCheck(false,"unknown",null);
    policy.Observe(monitored,unknown,false,now);policy.Observe(monitored,unknown,false,now);
    Assert(policy.Observe(monitored,unknown,false,now).AttentionRequired,"Supervisor flags repeated uncertain observations");
    Assert(policy.Observe(monitored,unknown,false,now).ConsecutiveFailures==3,"Supervisor failure counter is bounded");
    Assert(!policy.Observe(monitored,unknown,true,now).AttentionRequired,"Maintenance suppresses intervention recommendations");
    Assert(policy.DueNotification(monitored,now,now,false) && !policy.DueNotification(monitored,now,now,false),"Scheduler notification emitted once per due event");
    Assert(!policy.DueNotification(Guid.NewGuid(),now,now,true),"Maintenance suppresses scheduled notifications");
    Assert(!StatusFreshness.IsFresh(now.AddMinutes(-1),now) && !StatusFreshness.IsFresh(now.AddMinutes(1),now) && StatusFreshness.IsFresh(now,now),"Stale and future status timestamps rejected");
    string repo="Gunter1122cloud/untamed-manager-releases";
    object Release(string tag,bool preview,bool draft=false,string? digest=null)=>new {
        tag_name=tag,prerelease=preview,draft,body="fixture",assets=new[]{new {
            name="UntamedManager-2.0.0-SETUP.exe",browser_download_url=$"https://github.com/{repo}/releases/download/{tag}/UntamedManager-2.0.0-SETUP.exe",
            digest=digest??"sha256:"+new string('a',64)}}};
    string releaseJson=System.Text.Json.JsonSerializer.Serialize(new[]{Release("v2.0.0-wpf-preview.2",true),Release("v2.0.0-wpf-preview.10",true)});
    Assert(ReleaseCatalog.Select(releaseJson,repo,UpdateChannel.Stable)==null,"Stable channel excludes previews");
    var selectedPreview=ReleaseCatalog.Select(releaseJson,repo,UpdateChannel.Preview)!;
    Assert(selectedPreview.Tag.EndsWith(".10"),"Preview version 10 sorts after preview 2");
    Assert(ReleaseCatalog.IsNewer(selectedPreview,new Version(1,0,0),"v2.0.0-wpf-preview.2"),"Preview-to-preview upgrade allowed");
    Assert(!ReleaseCatalog.IsNewer(selectedPreview,new Version(2,0,0),"v2.0.0"),"Stable never downgrades to same-version preview");
    string stableJson=System.Text.Json.JsonSerializer.Serialize(new[]{Release("v2.0.0",false),Release("v2.0.0-wpf-preview.10",true)});
    Assert(ReleaseCatalog.Select(stableJson,repo,UpdateChannel.Preview)!.Tag=="v2.0.0","Stable final supersedes same-version preview");
    string rejectedJson=System.Text.Json.JsonSerializer.Serialize(new[]{Release("v2.0.0",false,true),Release("v2.0.0-wpf-preview.10",true,false,"invalid")});
    Assert(ReleaseCatalog.Select(rejectedJson,repo,UpdateChannel.Preview)==null,"Draft and unhashed release packages rejected");
    byte[] cipherKey=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    byte[] secret=System.Text.Encoding.UTF8.GetBytes("Private map status");
    byte[] encrypted=StatusCipher.Encrypt(cipherKey,secret);
    Assert(StatusCipher.Decrypt(cipherKey,encrypted).SequenceEqual(secret),"Encrypted status round trip");
    Assert(!encrypted.SequenceEqual(StatusCipher.Encrypt(cipherKey,secret)),"Fresh nonce for each encrypted status");
    encrypted[^1]^=1;
    bool tampered=false;
    try { StatusCipher.Decrypt(cipherKey,encrypted); } catch(System.Security.Cryptography.CryptographicException) { tampered=true; }
    Assert(tampered,"Tampered status rejected");
    bool wrongKey=false;
    try { StatusCipher.Decrypt(new byte[32],StatusCipher.Encrypt(cipherKey,secret)); }
    catch(System.Security.Cryptography.CryptographicException) { wrongKey=true; }
    Assert(wrongKey,"Wrong pairing key rejected");
    string exe=Path.GetFullPath(fake);
    Assert(WindowsProcessInspector.CommandMatches($"\"{exe}\" --map=Astraeos",exe,"--map=Astraeos"),
        "Exact quoted executable and argument suffix accepted");
    Assert(!WindowsProcessInspector.CommandMatches(exe+".other --map=Astraeos",exe,".other --map=Astraeos"),
        "Executable prefix collision rejected");
    foreach(var state in new[]{"WMI unavailable","Ambiguous processes","PID reused","Arguments unavailable"})
    {
        await using var blocked=new ClusterService("blocked-test",Path.Combine(root,"blocked-"+Guid.NewGuid()),
            _=>new ProcessCheck(false,state,null));
        var blockedMap=new LocalMap{Name="Blocked",InstallationPath=Path.GetDirectoryName(fake)!,ExecutablePath=fake};
        blocked.AddMap(blockedMap);
        bool refused=false;
        try { blocked.LaunchLocal(blockedMap.Id); } catch(InvalidOperationException) { refused=true; }
        Assert(refused,"Launch denied for uncertain state: "+state);
    }
    hp=new ClusterService("hp-test",aRoot);
    ryzen=new ClusterService("ryzen-test",bRoot);
    Assert(hp.Config.LocalMaps.Count==0 && ryzen.Config.LocalMaps.Count==0,
        "No automatic map import on first start");
    int hpPort=FreePort(),ryzenPort=FreePort();
    string key=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    hp.UpdateSettings(c=>{
        c.LocalHostName="UK-HOME-01-TEST";c.ListenPort=hpPort;
        c.PeerAddress="127.0.0.1";c.PeerPort=ryzenPort;
        c.PairingKeyProtected=SecureStore.Protect(key);
    });
    ryzen.UpdateSettings(c=>{
        c.LocalHostName="UK-HOME-02-TEST";c.ListenPort=ryzenPort;
        c.PeerAddress="127.0.0.1";c.PeerPort=hpPort;
        c.PairingKeyProtected=SecureStore.Protect(key);
    });
    var mapA=new LocalMap{
        Name="Astraeos",InstallationPath=Path.GetDirectoryName(fake)!,
        ExecutablePath=fake,Arguments="--map=Astraeos --port=7777",
        GamePort=7777,QueryPort=27015,RconPort=27020
    };
    var mapB=new LocalMap {
        Name="Ragnarok",InstallationPath=Path.GetDirectoryName(fake)!,
        ExecutablePath=fake,Arguments="--map=Ragnarok --port=7827",
        GamePort=7827,QueryPort=27025,RconPort=27030
    };
    hp.AddMap(mapA);ryzen.AddMap(mapB);
    mapA.ApiLoaderPath="unverified-loader.exe";
    Assert(!hp.Check(mapA).ConfirmedStopped,"Unverified API loader blocks launch");
    mapA.ApiLoaderPath="";
    string beforeStoreArgs=mapA.Arguments;
    mapA.Arguments+=" -usestore";
    bool storeLaunchBlocked=false;
    try { hp.LaunchLocal(mapA.Id); } catch(InvalidOperationException) { storeLaunchBlocked=true; }
    Assert(storeLaunchBlocked,"Store failure reaches actual local launch gate");
    mapA.Arguments=beforeStoreArgs;
    Assert(hp.Config.LocalMaps.Count==1 && ryzen.Config.LocalMaps.Count==1,
        "Each Manager has only its explicitly registered map");
    bool remoteWriteBlocked=false;
    try{hp.LaunchLocal(mapB.Id);}
    catch(UnauthorizedAccessException){remoteWriteBlocked=true;}
    Assert(remoteWriteBlocked,"Remote map cannot be launched via local Manager");
    await hp.BeginLanAsync();await ryzen.BeginLanAsync();
    var hpRemote=await hp.PollPeerAsync();var ryzenRemote=await ryzen.PollPeerAsync();
    Assert(hpRemote?.HostName=="UK-HOME-02-TEST" && hpRemote.Maps.Single().Name=="Ragnarok" &&
           ryzenRemote?.HostName=="UK-HOME-01-TEST",
           "Both Managers see authenticated read-only peer map lists");
    mapA.Maintenance=true;
    bool maintenanceBlocked=false;
    try{hp.LaunchLocal(mapA.Id);}catch(InvalidOperationException){maintenanceBlocked=true;}
    Assert(maintenanceBlocked,"Maintenance mode blocks actual launch gateway");
    mapA.Maintenance=false;
    Assert(hp.Check(mapA).ConfirmedStopped,"Clean process scan proves map is stopped");
    var own=hp.LaunchLocal(mapA.Id);
    p=Process.GetProcessById(own.Pid);
    ProcessCheck check=new(false,"",null);
    for(int i=0;i<20;i++){
        await Task.Delay(250);
        check=hp.Check(mapA);
        if(check.Running)break;
    }
    Assert(check.Running && check.Identity?.Pid==own.Pid,
        "Windows WMI matches running fake server executable, arguments and start time");
    if(args.Length<2 || !File.Exists(args[1]))throw new ArgumentException("Independent supervisor EXE required");
    var supervisorStart=new ProcessStartInfo(Path.GetFullPath(args[1])){UseShellExecute=false,CreateNoWindow=true};
    foreach(string value in new[]{"--once","--root",aRoot,"--profile","hp-test"})supervisorStart.ArgumentList.Add(value);
    using(var worker=Process.Start(supervisorStart)!)
    {
        using var workerDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try {await worker.WaitForExitAsync(workerDeadline.Token);}
        catch(OperationCanceledException){worker.Kill();await worker.WaitForExitAsync();throw;}
        Assert(worker.ExitCode==0,"Independent supervisor exits cleanly after real map observation");
    }
    using(var heartbeat=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(aRoot,"supervisor-heartbeat.json"))))
    {
        Assert(heartbeat.RootElement.GetProperty("readOnly").GetBoolean() && heartbeat.RootElement.GetProperty("states")[0].GetProperty("Map").GetGuid()==mapA.Id,
            "UI-independent worker observes registered local map and writes read-only heartbeat");
    }
    var authenticatedWorker=new SupervisorChannel(aRoot).Read(hp.Config.LocalHostId);
    Assert(authenticatedWorker.Maps.Single().Map==mapA.Id&&authenticatedWorker.Maps.Single().MemoryBytes>0&&authenticatedWorker.Maps.Single().Players==null,
        "Real supervisor publishes measured RAM and unknown player count through authenticated IPC");
    var persistedWatchdog=new DurableWatchdogTracker(
        Path.Combine(aRoot,"supervision")).ReadLocal(mapA.Id);
    Assert(persistedWatchdog!=null && persistedWatchdog.PreviouslyRunning &&
        !persistedWatchdog.Attention && !persistedWatchdog.AutoRecoveryAuthorized,
        "actual independent Windows Supervisor persists read-only map Watchdog identity without intervening");
    Assert(!p.HasExited,"Independent supervisor observation leaves FakeAsa alive");
    if(args.Length<3||!File.Exists(args[2]))throw new ArgumentException("Actual Manager EXE required for GUI lifetime test");
    async Task RunManagerLifetime()
    {
        var start=new ProcessStartInfo(Path.GetFullPath(args[2])){UseShellExecute=false,CreateNoWindow=true};
        foreach(string arg in new[]{"--supervisor-ci-test","--profile-root",aRoot,"--profile","hp-test"})start.ArgumentList.Add(arg);
        using var manager=Process.Start(start)!;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try{await manager.WaitForExitAsync(deadline.Token);}catch{manager.Kill();await manager.WaitForExitAsync();throw;}
        Assert(manager.ExitCode==0,"Actual WPF Manager connects to supervisor and exits cleanly");
    }
    await RunManagerLifetime();
    var afterUi=new SupervisorChannel(aRoot).Read(hp.Config.LocalHostId);
    persistentWorker=Process.GetProcessById(afterUi.Pid);
    Assert(!persistentWorker.HasExited&&!p.HasExited,"Supervisor and FakeAsa survive actual Manager UI shutdown");
    int persistedPid=persistentWorker.Id;
    await RunManagerLifetime();
    Assert(new SupervisorChannel(aRoot).Read(hp.Config.LocalHostId).Pid==persistedPid,"Restarted WPF Manager rejoins same supervisor process");
    var duplicateStart=new ProcessStartInfo(Path.GetFullPath(args[1])){UseShellExecute=false,CreateNoWindow=true};
    foreach(string arg in new[]{"--once","--root",aRoot,"--profile","hp-test"})duplicateStart.ArgumentList.Add(arg);
    using(var duplicateWorker=Process.Start(duplicateStart)!)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try{await duplicateWorker.WaitForExitAsync(deadline.Token);}catch{duplicateWorker.Kill();await duplicateWorker.WaitForExitAsync();throw;}
        Assert(duplicateWorker.ExitCode!=0,"Second supervisor cannot acquire same profile lease");
    }
    persistentWorker.Kill();await persistentWorker.WaitForExitAsync();persistentWorker.Dispose();persistentWorker=null;
    string appTree=Path.Combine(root,"update-application");Directory.CreateDirectory(appTree);
    Directory.CreateDirectory(Path.Combine(appTree,"dependencies"));
    File.WriteAllText(Path.Combine(appTree,"manager.bin"),"old exe");
    File.WriteAllText(Path.Combine(appTree,"dependencies","library.dll"),"old dll");
    File.WriteAllText(Path.Combine(appTree,"config.json"),"original local settings");
    string externalProfile=Path.Combine(root,"external-profile.json");File.WriteAllText(externalProfile,"map list");
    string fullSnapshot=Path.Combine(root,"update-snapshot");
    var updateTransaction=new DirectoryUpdateTransaction(fullSnapshot);updateTransaction.Prepare(appTree);
    File.WriteAllText(Path.Combine(appTree,"manager.bin"),"broken install");
    File.Delete(Path.Combine(appTree,"dependencies","library.dll"));
    File.WriteAllText(Path.Combine(appTree,"new-unwanted.dll"),"new dependency");
    // Reconstruct the coordinator as after updater process loss; recover from durable state.
    new DirectoryUpdateTransaction(fullSnapshot).Rollback();
    Assert(File.ReadAllText(Path.Combine(appTree,"manager.bin"))=="old exe" &&
        File.ReadAllText(Path.Combine(appTree,"dependencies","library.dll"))=="old dll" &&
        !File.Exists(Path.Combine(appTree,"new-unwanted.dll")),"Full updater rollback restores EXE, deleted dependency and removes new files");
    Assert(File.ReadAllText(Path.Combine(appTree,"config.json"))=="original local settings" &&
        File.ReadAllText(externalProfile)=="map list","Updater rollback preserves configuration and external map list");
    Assert(!p.HasExited,"Real FakeAsa survives full directory rollback");
    new DirectoryUpdateTransaction(fullSnapshot).Rollback();
    Assert(!p.HasExited,"Repeated rollback is idempotent and leaves FakeAsa alive");
    string corruptSnapshot=Path.Combine(root,"corrupt-update-snapshot");
    var corruptUpdate=new DirectoryUpdateTransaction(corruptSnapshot);corruptUpdate.Prepare(appTree);
    File.WriteAllText(Path.Combine(corruptSnapshot,"snapshot","manager.bin"),"corrupted snapshot");
    bool corruptRollbackBlocked=false;
    try { corruptUpdate.Rollback(); } catch(IOException) { corruptRollbackBlocked=true; }
    Assert(corruptRollbackBlocked && File.ReadAllText(Path.Combine(appTree,"manager.bin"))=="old exe","Corrupt rollback point rejected before application mutation");
    string successSnapshot=Path.Combine(root,"success-update-snapshot");
    var successUpdate=new DirectoryUpdateTransaction(successSnapshot);successUpdate.Prepare(appTree);
    File.WriteAllText(Path.Combine(appTree,"manager.bin"),"healthy new application");successUpdate.Commit();
    successUpdate.Rollback();
    Assert(File.ReadAllText(Path.Combine(appTree,"manager.bin"))=="healthy new application" && !p.HasExited,"Committed update retained while FakeAsa stays alive");
    string recoveryRoot=Path.Combine(root,"startup-updates");Directory.CreateDirectory(recoveryRoot);
    string recoverSnapshot=Path.Combine(recoveryRoot,"rollback-fixture");
    var startupTransaction=new DirectoryUpdateTransaction(recoverSnapshot);startupTransaction.Prepare(appTree);
    Assert(UpdateRecovery.FindPending(appTree,recoveryRoot)==recoverSnapshot,"Startup recognizes pending full-directory update");
    Assert(UpdateRecovery.FindPending(Path.Combine(root,"other-app"),recoveryRoot)==null,"Startup recovery never targets another application");
    startupTransaction.Rollback();
    Assert(UpdateRecovery.FindPending(appTree,recoveryRoot)==null,"Verified rollback clears automatic recovery requirement");
    string originalArgs=mapA.Arguments;
    mapA.Arguments+=" --changed";
    Assert(!hp.Check(mapA).ConfirmedStopped,"Changed arguments on same installation are uncertain, not stopped");
    bool changedBlocked=false;
    try { hp.LaunchLocal(mapA.Id); } catch(InvalidOperationException) { changedBlocked=true; }
    Assert(changedBlocked,"Changed arguments cannot spawn a second server");
    mapA.Arguments=originalArgs;
    duplicate=Process.Start(new ProcessStartInfo(fake,originalArgs){UseShellExecute=false,CreateNoWindow=true})!;
    await Task.Delay(500);
    var ambiguous=hp.Check(mapA);
    Assert(!ambiguous.Running && !ambiguous.ConfirmedStopped,"Two matching processes block dangerous operations");
    bool duplicateBlocked=false;
    try { hp.LaunchLocal(mapA.Id); } catch(InvalidOperationException) { duplicateBlocked=true; }
    Assert(duplicateBlocked,"Ambiguous processes cannot spawn a third server");
    duplicate.Kill(true);await duplicate.WaitForExitAsync();duplicate.Dispose();duplicate=null;
    await hp.DisposeAsync();hp=null;
    hp=new ClusterService("hp-test",aRoot);
    Assert(hp.Config.LocalMaps.Single().Id==mapA.Id,"Map configuration persists across Manager restart");
    check=hp.Check(hp.Config.LocalMaps.Single());
    Assert(check.Running && check.Identity?.Pid==own.Pid,
        "Restarted Manager reattaches to already running process without spawning replacement");
    Assert(!p.HasExited,"Server stays alive when Manager instance closes");
    // Simulate a binary update while a server process continues to run.
    string binary=Path.Combine(root,"UntamedManager-old.bin");
    File.WriteAllText(binary,"VERSION=1");
    File.WriteAllText(binary+".next","VERSION=2");
    File.Move(binary+".next",binary,true);
    Assert(!p.HasExited && File.ReadAllText(binary)=="VERSION=2",
        "Mock UI update changes Manager binary only, never terminates server");
    Assert(await hp.PollPeerAsync()!=null,"Restarted Manager obtains fresh peer observation before outage");
    await ryzen.DisposeAsync();ryzen=null;
    var peerOffline=await hp.PollPeerAsync();
    Assert(!hp.RemoteIsFresh && hp.RemoteSnapshot!=null,"Peer outage retains explicitly stale last observation");
    Assert(peerOffline==null && hp.Check(hp.Config.LocalMaps.Single()).Running,
        "Peer LAN outage does not affect local server control or process state");
    Assert(hp.Config.LocalMaps.Count==1 && hp.Config.LocalMaps[0].Arguments=="--map=Astraeos --port=7777",
        "Map configuration and launch arguments survive simulated update");
    Console.WriteLine("INTEGRATION PASSED. This validates FakeAsa and safety regressions, not live ASA or a real updater transaction.");
}
finally
{
    if(hp!=null)await hp.DisposeAsync();
    if(ryzen!=null)await ryzen.DisposeAsync();
    if(persistentWorker!=null)try{if(!persistentWorker.HasExited)persistentWorker.Kill();persistentWorker.Dispose();}catch{}
    if(familyChild!=null)try{if(!familyChild.HasExited)familyChild.Kill();familyChild.Dispose();}catch{}
    if(familyLoader!=null)try{if(!familyLoader.HasExited)familyLoader.Kill();familyLoader.Dispose();}catch{}
    if(duplicate!=null)try{if(!duplicate.HasExited)duplicate.Kill(true);duplicate.Dispose();}catch{}
    if(p!=null)try{if(!p.HasExited)p.Kill(true);p.Dispose();}catch{}
    try{Directory.Delete(root,true);}catch{}
}

sealed class LifecycleFixture(string mode) : ILocalServerSession
{
    public List<string> Commands { get; }=new();
    public int Starts { get; private set; }
    private bool exited;
    private readonly ProcessIdentity identity=new(){Pid=123,StartedUtc=DateTimeOffset.UtcNow,ExecutablePath="fixture.exe",Arguments="--map=test"};
    public ProcessCheck Check() => exited ? mode=="unknown-exit" ? new(false,"unknown",null) :
        new(false,"stopped",null,true) : new(true,"running",identity);
    public async Task Command(string command,CancellationToken ct)
    {
        Commands.Add(command);
        if(mode=="rcon-failure")throw new IOException("fixture RCON failed");
        if(mode=="timeout")await Task.Delay(Timeout.Infinite,ct);
        if(command=="DoExit" && mode!="exit-timeout")exited=true;
    }
    public Task<bool> SaveCompleted(CancellationToken ct)=>Task.FromResult(mode!="no-save-proof");
    public void Start() { if(!Check().ConfirmedStopped)throw new Exception("Unsafe restart");Starts++; }
}

sealed class SimulatedCurseForgeHttp : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? key=request.Headers.TryGetValues("x-api-key",out var values)
            ? values.SingleOrDefault() : null;
        if(key!="synthetic-approved-key")
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        string url=request.RequestUri?.AbsolutePath??"";
        string json=url switch
        {
            "/v1/games" => """{"data":[{"id":83374,"slug":"ark-survival-ascended","name":"ARK Survival Ascended"}],"pagination":{"totalCount":1}}""",
            "/v1/mods/search" => """{"data":[{"id":928548,"gameId":83374,"name":"Shiny! Dinos","summary":"Synthetic test only","dateModified":"2026-01-01T12:00:00Z","links":{"websiteUrl":"https://www.curseforge.com/ark-survival-ascended/mods/shiny-dinos"},"latestFiles":[{"displayName":"Synthetic release"}]}],"pagination":{"totalCount":1}}""",
            _ => """{"data":[]}"""
        };
        var response=new HttpResponseMessage(System.Net.HttpStatusCode.OK){
            Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")
        };
        return Task.FromResult(response);
    }
}

