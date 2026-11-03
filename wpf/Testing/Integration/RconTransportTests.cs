using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
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
        string body,bool fragmented=false,bool malformed=false)
    {
        byte[] payload=Encoding.UTF8.GetBytes(body),packet=new byte[payload.Length+14];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0,4),payload.Length+10);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4,4),id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8,4),type);
        payload.CopyTo(packet.AsSpan(12));
        if(malformed)packet[^1]=0x11;
        if(fragmented)
        {
            foreach(byte chunk in packet)
                await stream.WriteAsync(new[]{chunk});
        }
        else await stream.WriteAsync(packet);
    }

    private static async Task<string> Probe(bool malformed=false)
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
            Assert(query.Type==2&&query.Body=="ListPlayers","fixture only ListPlayers");
            await WritePacket(stream,query.Id,0,"1. Test",fragmented:true,
                malformed:malformed);
            if(!malformed)
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
            try{result=await RconProtocol.Execute("127.0.0.1",port,
                "synthetic-secret","ListPlayers");}
            finally{await server;}
            return result;
        }
        finally{listener.Stop();}
    }

    public static async Task Run()
    {
        string result=await Probe();
        Assert(result=="1. TestPlayer\n",
            "fragmented, multi-frame ListPlayers response waits for completion");
        bool refused=false;
        try{await Probe(malformed:true);}
        catch(InvalidDataException){refused=true;}
        Assert(refused,"RCON invalid trailing frame bytes fail closed");
    }
}
