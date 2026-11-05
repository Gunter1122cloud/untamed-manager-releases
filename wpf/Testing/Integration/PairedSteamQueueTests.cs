using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class PairedSteamQueueTests
{
    private static void Check(bool valid,string reason)
    {
        if(!valid)throw new InvalidOperationException("PAIRED STEAM QUEUE: "+reason);
        Console.WriteLine("PASS paired Steam queue: "+reason);
    }
    private static int Port()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        int result=((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return result;
    }
    private const string A="33333333333333333333333333333333";
    private const string B="44444444444444444444444444444444";
    private const string Key="SYNTHETIC-PAIRED-QUEUE-HMAC-KEY-1234567890";
    private static LocalMap Map(string root,string name)=>new(){
        Name=name,InstallationPath=Path.Combine(root,"install-"+name),
        ExecutablePath=Path.Combine(root,"install-"+name,
            "ShooterGame","Binaries","Win64","ArkAscendedServer.exe")
    };
    private static ProcessCheck Inspect(LocalMap _) =>
        new(false,"Verified stopped fixture",null,true);

    public static async Task Run(string sandbox)
    {
        var root=Path.Combine(sandbox,"paired-queue-"+Guid.NewGuid().ToString("N"));
        var local=new ClusterSteamLeaseStore(A,Path.Combine(root,"first"));
        var remote=new ClusterSteamLeaseStore(B,Path.Combine(root,"second"));
        int port=Port();
        await using(var listener=new PeerStatusListener(port,Key,
            ()=>new HostStatus{HostId=B,HostName="simulated second PC"},remote))
        {
            listener.Start();
            var coordinator=new ClusterSteamLeaseCoordinator(local,
                "127.0.0.1",port,Key,B);
            var maps=new[]{Map(root,"Island"),Map(root,"Aberration")};
            var queue=new SteamSerialMaintenanceQueue(Path.Combine(root,"queue"),A);
            int running=0,peak=0,processed=0;
            var completed=await queue.ExecutePairedAsync(coordinator,maps,maps,
                Inspect,async (_,ct)=>{
                    int now=Interlocked.Increment(ref running);
                    peak=Math.Max(peak,now);
                    await Task.Delay(15,ct);
                    Interlocked.Decrement(ref running);
                    Interlocked.Increment(ref processed);
                });
            Check(processed==2&&peak==1&&
                completed.Phase==SteamQueuePhase.Completed&&
                local.Observe()?.Phase==ClusterSteamLeasePhase.Completed&&
                remote.Observe()?.Phase==ClusterSteamLeasePhase.Completed,
                "serial map operations hold the same committed peer lease throughout");

            bool wrongLeader=false;
            try{_=new ClusterSteamLeaseCoordinator(remote,"127.0.0.1",port,Key,A);}
            catch(InvalidDataException){wrongLeader=true;}
            Check(wrongLeader,
                "higher-ID peer cannot independently start a conflicting cluster queue");
        }

        // Physical LAN partition between maps: the second map must not begin.
        var brokenRoot=Path.Combine(sandbox,"paired-queue-partition-"+Guid.NewGuid().ToString("N"));
        var lostLocal=new ClusterSteamLeaseStore(A,Path.Combine(brokenRoot,"first"));
        var lostRemote=new ClusterSteamLeaseStore(B,Path.Combine(brokenRoot,"second"));
        int brokenPort=Port();
        var lostListener=new PeerStatusListener(brokenPort,Key,
            ()=>new HostStatus{HostId=B},lostRemote);
        lostListener.Start();
        bool listenerDisposed=false;
        try
        {
            var coordinator=new ClusterSteamLeaseCoordinator(lostLocal,
                "127.0.0.1",brokenPort,Key,B);
            var maps=new[]{Map(brokenRoot,"Island"),Map(brokenRoot,"Astraeos")};
            var queue=new SteamSerialMaintenanceQueue(
                Path.Combine(brokenRoot,"queue"),A);
            int touched=0;
            bool lost=false;
            try
            {
                await queue.ExecutePairedAsync(coordinator,maps,maps,Inspect,
                    async (_,ct)=>{
                        touched++;
                        await lostListener.DisposeAsync();
                        listenerDisposed=true;
                    });
            }
            catch(Exception e) when(e is IOException or InvalidDataException or
                OperationCanceledException or SocketException){lost=true;}
            Check(lost&&touched==1&&
                queue.Observe()?.Phase==SteamQueuePhase.NeedsOperator &&
                lostLocal.Observe()?.Phase==ClusterSteamLeasePhase.NeedsOperator,
                "peer loss after first map halts queued work before second map");
            bool deniedRestart=false;
            try{await queue.ExecutePairedAsync(coordinator,maps,maps,Inspect,
                (_,_)=>Task.CompletedTask);}
            catch(IOException){deniedRestart=true;}
            Check(deniedRestart,
                "fresh queue cannot auto-clear a partitioned two-host lease");
        }
        finally{if(!listenerDisposed)await lostListener.DisposeAsync();}
    }
}
