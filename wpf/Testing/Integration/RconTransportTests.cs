using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using UntamedManager.Core;

public static class RconTransportTests
{
    private static void Assert(bool ok,string message)
    {
        if(!ok)throw new Exception("RCON transport fixture failed: "+message);
        Console.WriteLine("PASS RCON "+message);
    }

    private static async Task<(int Id,int Type,string Body)> ReadPacket(NetworkStream stream)
    {
        var len=new byte[4];
        await stream.ReadExactlyAsync(len);
        int length=BinaryPrimitives.ReadInt32LittleEndian(len);
        if(length is < 10 or > 20_000)throw new InvalidDataException("Test frame size");
        var body=new byte[length];
        await stream.ReadExactlyAsync(body);
        return (BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0,4)),
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4,4)),
            Encoding.UTF8.GetString(body,8,length-10));
    }

    private static async Task WritePacket(NetworkStream stream,int id,int type,
        string body,bool fragmented=false,bool malformed=false,bool invalidUtf8=false,bool embeddedNull=false)
    {
        byte[] payload=Encoding.UTF8.GetBytes(body),packet=new byte[payload.Length+14];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0,4),payload.Length+10);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4,4),id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8,4),type);
        payload.CopyTo(packet.AsSpan(12));
        if(invalidUtf8)packet[12]=0xFF;
        if(embeddedNull)packet[12]=0;
        if(malformed)packet[^1]=0x11;
        if(fragmented)
        {
            foreach(byte chunk in packet)
                await stream.WriteAsync(new[]{chunk});
        }
        else await stream.WriteAsync(packet);
    }

    private static async Task<string> Probe(bool malformed=false,bool invalidUtf8=false,bool embeddedNull=false,string host="127.0.0.1")
    {
        var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(async ()=>{
            using var accepted=await listener.AcceptTcpClientAsync();
            using var stream=accepted.GetStream();
            var auth=await ReadPacket(stream);
            Assert(auth.Type==3,"fixture AUTH request");
            await WritePacket(stream,auth.Id,2,"",fragmented:true);
            var query=await ReadPacket(stream);
            Assert(auth.Id!=query.Id,"authentication and query request IDs are distinct");
            Assert(query.Type==2&&query.Body=="ListPlayers","fixture only ListPlayers");
            await WritePacket(stream,query.Id,0,"1. Test",fragmented:true,
                malformed:malformed,invalidUtf8:invalidUtf8,embeddedNull:embeddedNull);
            if(!malformed&&!invalidUtf8&&!embeddedNull)
            {
                var barrier=await ReadPacket(stream);
                Assert(barrier.Type==2&&barrier.Body=="ListPlayers",
                    "ordered read-only completion barrier");
                await WritePacket(stream,query.Id,0,"Player\n",fragmented:true);
                await WritePacket(stream,barrier.Id,0,"1. TestPlayer\n");
            }
        });
        try
        {
            string result="";
            try{result=await RconProtocol.Execute(host,port,
                "synthetic-secret","ListPlayers");}
            finally{await server;}
            return result;
        }
        finally{listener.Stop();}
    }

    private static async Task ProbeSaveWorldDispatch()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(async()=>{
            using var accepted=await listener.AcceptTcpClientAsync();
            using var stream=accepted.GetStream();
            var auth=await ReadPacket(stream);
            Assert(auth.Type==3,"save dispatch authenticates before command");
            await WritePacket(stream,auth.Id,2,"");
            var sent=await ReadPacket(stream);
            Assert(sent.Type==2 && sent.Body=="SaveWorld",
                "explicit SaveWorld dispatch sends only approved single command");
        });
        try
        {
            var result=await RconProtocol.DispatchSaveWorldAsync(
                "127.0.0.1",port,"isolated-rcon-credential");
            await server;
            Assert(result.DispatchedUtc> DateTimeOffset.UtcNow.AddMinutes(-1) &&
                   !result.SaveCompleteProven,
                "authenticated SaveWorld RCON write cannot masquerade as SaveComplete");
            bool refused=false;
            try
            {
                await RconProtocol.Execute("127.0.0.1",port,"fixture","DoExit");
            }
            catch(InvalidOperationException){refused=true;}
            Assert(refused,"read-only RCON API still refuses DoExit and other mutations");
        }
        finally{listener.Stop();}
    }

    private static async Task ProbeReadOnlyLog(string command)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(async()=>{
            using var accepted=await listener.AcceptTcpClientAsync();
            using var stream=accepted.GetStream();
            var auth=await ReadPacket(stream);
            Assert(auth.Type==3,"read-only log probe authenticates");
            await WritePacket(stream,auth.Id,2,"");
            var query=await ReadPacket(stream);
            Assert(query.Type==2&&query.Body==command,
                "only an exact read-only log command is sent");
            await WritePacket(stream,query.Id,0,"Line one – ",fragmented:true);
            var barrier=await ReadPacket(stream);
            Assert(barrier.Type==2&&barrier.Body=="ListPlayers",
                "read-only log response uses a second read-only completion barrier");
            await WritePacket(stream,query.Id,0,"Line two",fragmented:true);
            await WritePacket(stream,barrier.Id,0,"1. Fixture player");
        });
        try
        {
            var answer=await RconProtocol.Execute(
                "127.0.0.1",port,"fixture-only-secret",command);
            await server;
            Assert(answer=="Line one – Line two",
                "RCON "+command+" waits for full fragmented log response");
        }
        finally{listener.Stop();}
    }

    private static async Task ProbeWipeAcknowledgement(string response)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(async()=>{
            using var accepted=await listener.AcceptTcpClientAsync();
            using var stream=accepted.GetStream();
            var auth=await ReadPacket(stream);
            await WritePacket(stream,auth.Id,2,"");
            var sent=await ReadPacket(stream);
            Assert(sent.Type==2&&sent.Body=="DestroyWildDinos","exact unprefixed wipe command");
            await WritePacket(stream,sent.Id,0,response);
        });
        try
        {
            int authorized=0;
            var result=await RconProtocol.DispatchApprovedAdminAsync("127.0.0.1",port,"fixture",
                "DestroyWildDinos",()=>authorized++,CancellationToken.None);
            await server;
            Assert(authorized==1&&result=="ADMIN_RCON_ACKNOWLEDGED_NO_GAMEPLAY_PROOF",
                "empty/no-content wipe response acknowledged without claiming gameplay effect");
        }
        finally{listener.Stop();}
    }
    private static async Task ProbePersistentReadSession()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(async()=>{
            using(var first=await listener.AcceptTcpClientAsync())
            using(var stream=first.GetStream())
            {
                var auth=await ReadPacket(stream);Assert(auth.Type==3,"persistent AUTH once");
                await WritePacket(stream,auth.Id,2,"");
                var read=await ReadPacket(stream);Assert(read.Body=="GetChat","first command");
                await WritePacket(stream,read.Id,0,"hello");
                var barrier=await ReadPacket(stream);Assert(barrier.Body=="ListPlayers","persistent barrier");
                await WritePacket(stream,barrier.Id,0,"1. player");
                await WritePacket(stream,barrier.Id,0,"2. trailing player");
                var second=await ReadPacket(stream);
                Assert(second.Type==2&&second.Body=="ListPlayers"&&second.Id!=read.Id,"same socket, new request ID");
                await WritePacket(stream,second.Id,0,"No Players Connected");
            }
            using var next=await listener.AcceptTcpClientAsync();
            using var stream2=next.GetStream();
            var auth2=await ReadPacket(stream2);Assert(auth2.Type==3,"new process epoch reauthenticates");
            await WritePacket(stream2,auth2.Id,2,"");
            var last=await ReadPacket(stream2);
            await WritePacket(stream2,last.Id,0,"No Players Connected");
        });
        try
        {
            var map=Guid.NewGuid();var epoch=DateTimeOffset.UtcNow;int verified=0;
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<string> Call(DateTimeOffset e,string command)=>RconProtocol.ExecuteOwnedReadAsync(
                map,12345,e,port,"fixture",command,()=>verified++,deadline.Token);
            Assert(await Call(epoch,"GetChat")=="hello","persistent complete chat");
            Assert(await Call(epoch,"ListPlayers")=="No Players Connected","trailing barrier isolated");
            Assert(await Call(epoch.AddSeconds(1),"ListPlayers")=="No Players Connected","epoch invalidates connection");
            await server;Assert(verified>=9,"each command verifies owner before/after dispatch");
        }
        finally{listener.Stop();}
    }
    public static async Task Run()
    {
        await ProbePersistentReadSession();
        await ProbeWipeAcknowledgement("");
        await ProbeWipeAcknowledgement("Server received, But no response!!");
        await ProbeSaveWorldDispatch();
        await ProbeReadOnlyLog("GetGameLog");
        await ProbeReadOnlyLog("GetChat");
        string result=await Probe();
        Assert(result=="1. TestPlayer\n",
            "fragmented, multi-frame ListPlayers response waits for completion");
        bool refused=false;
        try{await Probe(malformed:true);}
        catch(InvalidDataException){refused=true;}
        Assert(refused,"RCON invalid trailing frame bytes fail closed");
        refused=false;try{await Probe(invalidUtf8:true);}catch(InvalidDataException){refused=true;}
        Assert(refused,"malformed UTF-8 is not replaced with apparently valid player text");
        refused=false;try{await Probe(embeddedNull:true);}catch(InvalidDataException){refused=true;}
        Assert(refused,"embedded NUL cannot truncate a player response");
        Assert(await Probe(host:"localhost")=="1. TestPlayer\n","resolved local hostname is pinned to a verified local IP");
        Assert(await Probe(host:"::ffff:127.0.0.1")=="1. TestPlayer\n","IPv4-mapped loopback is normalized before connection");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        bool cancelledBeforeDns=false;
        try{await RconProtocol.Execute("not-contacted.invalid",1,"synthetic","ListPlayers",cancelled.Token);}
        catch(OperationCanceledException){cancelledBeforeDns=true;}
        Assert(cancelledBeforeDns,"pre-cancelled request stops before hostname resolution");
        refused=false;try{await RconProtocol.Execute("192.0.2.1",1,"synthetic","ListPlayers");}catch(UnauthorizedAccessException){refused=true;}
        Assert(refused,"nonlocal literal is refused without a connection");
        refused=false;try{await RconProtocol.Execute("127.0.0.1",1,"bad\0credential","ListPlayers");}catch(InvalidDataException){refused=true;}
        Assert(refused,"NUL credential is refused before network access");
    }
}

