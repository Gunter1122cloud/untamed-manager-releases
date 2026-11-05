using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class ClusterSteamLeaseTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new InvalidOperationException("CLUSTER STEAM LEASE: "+reason);
        Console.WriteLine("PASS cluster Steam lease: "+reason);
    }
    private static int FreePort()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private const string SharedKey="CI-SYNTHETIC-STEAM-FENCE-PAIRING-ONLY-12345678";
    public static async Task Run(string root)
    {
        var hostA="11111111111111111111111111111111";
        var hostB="22222222222222222222222222222222";
        var dirA=Path.Combine(root,"fence-host-A");
        var dirB=Path.Combine(root,"fence-host-B");
        var local=new ClusterSteamLeaseStore(hostA,dirA);
        var peer=new ClusterSteamLeaseStore(hostB,dirB);
        int port=FreePort();
        var listener=new PeerStatusListener(port,SharedKey,()=>
            new HostStatus{HostId=hostB,HostName="CI-SIMULATED-HOST-B"},peer);
        listener.Start();
        try
        {
            // Existing paired read-only GET_STATUS behavior is not altered.
            var status=await PeerStatusClient.Query("127.0.0.1",port,SharedKey);
            Check(status.HostId==hostB && status.HostName=="CI-SIMULATED-HOST-B",
                "ordinary authenticated GET_STATUS still works");

            bool wrongSecretRejected=false;
            try{await PeerSteamLeaseClient.RequestAsync("127.0.0.1",port,
                "CI-DIFFERENT-NONMATCHING-SECRET-12345678",
                new("PREPARE",Guid.NewGuid(),hostA,hostB));}
            catch(Exception e) when(e is IOException or InvalidDataException
                or System.Security.Cryptography.CryptographicException or OperationCanceledException)
            {wrongSecretRejected=true;}
            Check(wrongSecretRejected && peer.Observe()==null,
                "wrong HMAC key cannot place remote maintenance reservation");

            int count=0;
            var manager=new ClusterSteamLeaseCoordinator(local,
                "127.0.0.1",port,SharedKey,hostB);
            await manager.ExecuteAsync(async(checkpoint,ct)=>{
                count++;
                await checkpoint(ct);
                await Task.Delay(20,ct);
                await checkpoint(ct);
            });
            Check(count==1 && local.Observe()?.Phase==ClusterSteamLeasePhase.Completed &&
                peer.Observe()?.Phase==ClusterSteamLeasePhase.Completed &&
                local.Observe()?.OperationId==peer.Observe()?.OperationId,
                "both peers commit same operation and release only after verified completion");

            var old=local.Observe()!;
            bool oldCannotReplay=local.Apply(new("COMMIT",old.OperationId,hostA,hostB)).Accepted;
            Check(!oldCannotReplay,"completed lease cannot be silently recommitted");
            var newOperation=Guid.NewGuid();
            var prepared=local.Apply(new("PREPARE",newOperation,hostA,hostB));
            var remotePrepared=await PeerSteamLeaseClient.RequestAsync(
                "127.0.0.1",port,SharedKey,
                new("PREPARE",newOperation,hostA,hostB));
            Check(prepared.Accepted && remotePrepared.Accepted &&
                local.Observe()?.Phase==ClusterSteamLeasePhase.Prepared &&
                peer.Observe()?.Phase==ClusterSteamLeasePhase.Prepared,
                "durable prepared reservation survives a simulated crash");
            int resumed=0;
            bool refused=false;
            try{await new ClusterSteamLeaseCoordinator(local,
                "127.0.0.1",port,SharedKey,hostB)
                .ExecuteAsync((_,_)=>{resumed++;return Task.CompletedTask;});}
            catch(IOException){refused=true;}
            Check(refused && resumed==0,
                "a restart cannot blindly replay an incompletely prepared cluster update");
            // Explicitly cancel a never-committed reservation; this does not
            // simulate production recovery from a partially updated map.
            Check(local.Apply(new("CANCEL_PREPARE",newOperation,hostA,hostB)).Accepted,
                "operator can cancel a purely prepared local lease");
            Check((await PeerSteamLeaseClient.RequestAsync("127.0.0.1",port,
                SharedKey,new("CANCEL_PREPARE",newOperation,hostA,hostB))).Accepted,
                "paired purely prepared lease can also be cancelled");

            var partitioned=new ClusterSteamLeaseCoordinator(local,
                "127.0.0.1",port,SharedKey,hostB);
            bool failed=false;
            try
            {
                await partitioned.ExecuteAsync(async(checkpoint,ct)=>{
                    await listener.DisposeAsync();
                    await checkpoint(ct); // fail closed after losing peer
                });
            }
            catch(Exception e) when(e is IOException or InvalidDataException or
                SocketException or OperationCanceledException){failed=true;}
            Check(failed && local.Observe()?.Phase==ClusterSteamLeasePhase.NeedsOperator,
                "lost peer connection after commit blocks completion and marks needs-operator");

            bool noNewWork=false;
            try{await partitioned.ExecuteAsync((_,_)=>Task.CompletedTask);}
            catch(IOException){noNewWork=true;}
            Check(noNewWork,"a partitioned lease prevents new cluster maintenance");
            var reread=new ClusterSteamLeaseStore(hostA,dirA).Observe();
            Check(reread?.Phase==ClusterSteamLeasePhase.NeedsOperator,
                "fresh Manager reads the same durable operator-review fence");
        }
        finally{await listener.DisposeAsync();}
    }
}
