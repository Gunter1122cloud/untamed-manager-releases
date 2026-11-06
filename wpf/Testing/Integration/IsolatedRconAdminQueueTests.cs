using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using UntamedManager.Core;

public static class IsolatedRconAdminQueueTests
{
    static void Check(bool good,string reason)
    {if(!good)throw new Exception("Admin queue: "+reason);Console.WriteLine("PASS rcon-admin "+reason);}
    static async Task<bool> Refused(Func<Task> action)
    {try{await action();return false;}catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException){return true;}}
    public static async Task Run(string fakeExecutable)
    {
        string root=Path.Combine(Path.GetTempPath(),"Untamed-RconLab-"+Guid.NewGuid().ToString("N"));
        string install=Path.Combine(root,"installation");Directory.CreateDirectory(install);
        string executable=Path.Combine(install,"FakeAsa.exe");File.Copy(fakeExecutable,executable);
        const string password="synthetic-admin-fixture-password";
        File.WriteAllText(Path.Combine(root,"fixture-password"),password);
        var portProbe=new TcpListener(IPAddress.Loopback,0);portProbe.Start();int port=((IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
        string arguments="--rcon-admin-fixture \""+root+"\" "+port;
        using var child=Process.Start(new ProcessStartInfo(executable,arguments){UseShellExecute=false,CreateNoWindow=true})!;
        try
        {
            var until=DateTimeOffset.UtcNow.AddSeconds(20);
            while(!File.Exists(Path.Combine(root,"listener-ready"))&&!child.HasExited&&DateTimeOffset.UtcNow<until)await Task.Delay(30);
            Check(!child.HasExited&&File.Exists(Path.Combine(root,"listener-ready")),"independent harmless Windows child owns actual TCP RCON listener");
            var identity=new ProcessIdentity{Pid=child.Id,StartedUtc=child.StartTime.ToUniversalTime(),ExecutablePath=executable,Arguments=arguments};
            int observations=0;bool replaceAfterAuth=false;
            await using var cluster=new ClusterService("admin-test",Path.Combine(root,"profile"),_=>{
                observations++;
                return new(true,"CI verified process",replaceAfterAuth&&observations>=3?
                    new ProcessIdentity{Pid=child.Id+1,StartedUtc=identity.StartedUtc,ExecutablePath=executable,Arguments=arguments}:identity);
            });
            var map=new LocalMap{Id=Guid.NewGuid(),Name="RCON isolated fixture",InstallationPath=install,
                ExecutablePath=executable,Arguments=arguments,RconHost="127.0.0.1",RconPort=port,
                RconPasswordProtected=SecureStore.Protect(password)};
            cluster.Config.LocalMaps.Add(map);cluster.Save();
            Check(RconPortOwnership.IsOwnedListener(port,child.Id)&&!RconPortOwnership.IsOwnedListener(port,Environment.ProcessId),
                "kernel ownership accepts listener process and rejects another process at same port");
            int Commands()=>Directory.GetFiles(root,"command-*.json").Length;
            var permission=IsolatedRconAdminQueue.Approve(cluster,map.Id,RconAction.Broadcast,"Árvíztűrő teszt");
            var receipt=await IsolatedRconAdminQueue.ExecuteOnceAsync(cluster,permission);
            Check(receipt.Acknowledged&&!receipt.SaveCompleteProven&&Commands()==1,
                "real authentication and one Unicode admin command, with explicit reply barrier");
            Check(await Refused(()=>IsolatedRconAdminQueue.ExecuteOnceAsync(cluster,permission))&&Commands()==1,
                "replayed permit cannot transmit a second mutating command");
            var changed=IsolatedRconAdminQueue.Approve(cluster,map.Id,RconAction.ServerChat,"test");
            map.RconPort++;
            Check(await Refused(()=>IsolatedRconAdminQueue.ExecuteOnceAsync(cluster,changed))&&Commands()==1,
                "configuration change invalidates prior permission before socket connection");map.RconPort--;
            observations=0;replaceAfterAuth=true;
            var replaced=IsolatedRconAdminQueue.Approve(cluster,map.Id,RconAction.ServerChat,"test");
            Check(await Refused(()=>IsolatedRconAdminQueue.ExecuteOnceAsync(cluster,replaced))&&Commands()==1,
                "identity change after authentication prevents all mutating command bytes");replaceAfterAuth=false;
            map.RconPasswordProtected=SecureStore.Protect("incorrect-fixture-password");
            var wrong=IsolatedRconAdminQueue.Approve(cluster,map.Id,RconAction.ServerChat,"test");
            Check(await Refused(()=>IsolatedRconAdminQueue.ExecuteOnceAsync(cluster,wrong))&&Commands()==1,
                "failed RCON authentication cannot send admin command");map.RconPasswordProtected=SecureStore.Protect(password);
            var valid=IsolatedRconAdminQueue.Approve(cluster,map.Id,RconAction.SaveWorld,null);
            var expired=new RconAdminPermit(valid.Map,valid.OperatorSid,valid.Configuration,valid.Profile,
                valid.Draft,valid.Process,DateTimeOffset.UtcNow.AddSeconds(-1));
            Check(await Refused(()=>IsolatedRconAdminQueue.ExecuteOnceAsync(cluster,expired))&&Commands()==1,
                "expired operator confirmation is refused without sending");
            var saved=await IsolatedRconAdminQueue.ExecuteOnceAsync(cluster,valid);
            Check(saved.Acknowledged&&!saved.SaveCompleteProven&&Commands()==2,
                "SaveWorld network acknowledgement is never SaveComplete proof");
            File.WriteAllText(Path.Combine(root,"withhold-response"),"test");
            var timeout=IsolatedRconAdminQueue.Approve(cluster,map.Id,RconAction.ServerChat,"one attempt");
            using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(750)))
                Check(await Refused(()=>IsolatedRconAdminQueue.ExecuteOnceAsync(cluster,timeout,cancel.Token)),
                    "post-dispatch timeout is bounded without automatic retry");
            Check(Commands()==3&&cluster.Operations.Observe(install)!.Phase==UntamedOperationPhase.NeedsOperator&&
                await Refused(()=>IsolatedRconAdminQueue.ExecuteOnceAsync(cluster,timeout))&&Commands()==3,
                "ambiguous dispatch keeps durable claim and NeedsOperator state; repeated command is blocked");
            string[] claimFiles=Directory.GetFiles(Path.Combine(cluster.Store.Root,"rcon-admin-claims"));
            Check(claimFiles.All(f=>!File.ReadAllText(f).Contains(password)&&!File.ReadAllText(f).Contains("Árvíztűrő")),
                "durable receipts contain neither credentials nor raw command/response text");
        }
        finally{if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}}
    }
}
