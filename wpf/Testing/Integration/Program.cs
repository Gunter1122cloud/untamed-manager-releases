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
ClusterService? hp=null;
ClusterService? ryzen=null;
try
{
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
    if(duplicate!=null)try{if(!duplicate.HasExited)duplicate.Kill(true);duplicate.Dispose();}catch{}
    if(p!=null)try{if(!p.HasExited)p.Kill(true);p.Dispose();}catch{}
    try{Directory.Delete(root,true);}catch{}
}
