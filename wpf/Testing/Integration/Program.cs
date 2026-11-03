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
ClusterService? hp=null;
ClusterService? ryzen=null;
try
{
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
    familyLoader.Kill();await familyLoader.WaitForExitAsync();familyLoader.Dispose();familyLoader=null;
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
    await ryzen.DisposeAsync();ryzen=null;
    var peerOffline=await hp.PollPeerAsync();
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
