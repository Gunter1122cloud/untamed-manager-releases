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
    string unsafeZip=Path.Combine(root,"unsafe-plugin.zip");
    using(var stream=File.Create(unsafeZip))
    using(var archive=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create))
        using(var writer=new StreamWriter(archive.CreateEntry("SamplePlugin/../evil.dll").Open()))writer.Write("evil");
    bool unsafeBlocked=false;
    try{SafePluginZip.Inspect(unsafeZip);}catch(InvalidDataException){unsafeBlocked=true;}
    Assert(unsafeBlocked,"Plugin ZIP traversal is rejected before disk modification");
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
    var familyRoot=Path.Combine(root,"family-profile");
    var familyService=new ClusterService("family-test",familyRoot);
    var familyMap=new LocalMap{Name="Family",InstallationPath=Path.GetDirectoryName(fake)!,ExecutablePath=fake,Arguments="--child-fixture",ApiLoaderPath=fake,ApiLoaderArguments="--loader-fixture"};
    familyService.AddMap(familyMap);
    Assert(familyService.Check(familyMap).Running,"Manager integrates WMI family validation");
    bool familyDuplicate=false;
    try{familyService.LaunchLocal(familyMap.Id);}catch(InvalidOperationException){familyDuplicate=true;}
    Assert(familyDuplicate,"Family launch gate rejects duplicate start");
    await familyService.DisposeAsync();
    familyService=new ClusterService("family-test",familyRoot);familyMap=familyService.Config.LocalMaps.Single();
    Assert(familyService.Check(familyMap).Running,"Restarted Manager reattaches verified family");
    familyLoader.Kill();await familyLoader.WaitForExitAsync();familyLoader.Dispose();familyLoader=null;
    Assert(familyService.Check(familyMap).Running,"Persisted child reattaches after loader exits");
    await familyService.DisposeAsync();
    Assert(!familyChild.HasExited,"Fixture child survives loader exit");
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
    var stalled=new TcpListener(IPAddress.Loopback,0);stalled.Start();
    try
    {
        using var deadline=new CancellationTokenSource(200);
        bool timedOut=false;
        try { await RconProtocol.Execute("127.0.0.1",((IPEndPoint)stalled.LocalEndpoint).Port,"fixture","SaveWorld",deadline.Token); }
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
