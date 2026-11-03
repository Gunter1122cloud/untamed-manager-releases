using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// Source RCON wire format. Runs against a locally configured ASA server only.
// Remote peer messages never reach this protocol.
public static class RconProtocol
{
    public static async Task<string> Execute(string host,int port,string password,string command,
        CancellationToken cancellationToken=default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Missing RCON password");
        if (command.Length==0||command.Length>1024) throw new ArgumentException("Invalid command");
        if (!await IsLocalAddress(host))throw new UnauthorizedAccessException("RCON host must be a local interface");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var client=new TcpClient();
        await client.ConnectAsync(host,port,timeout.Token);
        using var stream=client.GetStream();
        int challenge=Random.Shared.Next(1000,1000000);
        await WritePacket(stream,challenge,3,password,timeout.Token);
        var authenticated=false;
        for(int i=0;i<3;i++)
        {
            var packet=await ReadPacket(stream,timeout.Token);
            if(packet.Id==-1)throw new UnauthorizedAccessException("Wrong RCON password");
            if(packet.Id==challenge && packet.Type==2){authenticated=true;break;}
        }
        if(!authenticated)throw new IOException("RCON login response invalid");
        int commandId=Random.Shared.Next(1000,1000000);
        await WritePacket(stream,commandId,2,command,timeout.Token);
        var responses=new List<string>();
        // Read until short quiet timeout, with strict total bounds.
        for(int i=0;i<32;i++)
        {
            using var quiet=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            quiet.CancelAfter(TimeSpan.FromMilliseconds(i==0?1800:350));
            try{
                var packet=await ReadPacket(stream,quiet.Token);
                if(packet.Id==-1)throw new UnauthorizedAccessException("RCON response rejected");
                if(packet.Id==commandId)responses.Add(packet.Body);
                if(responses.Sum(s=>s.Length)>250000)break;
            }
            catch(OperationCanceledException) when(!timeout.IsCancellationRequested){break;}
        }
        return responses.Count==0?"RCON parancs elküldve, a szerver nem küldött válaszszöveget.":string.Join("\n",responses);
    }
    private sealed record Packet(int Id,int Type,string Body);
    private static async Task WritePacket(Stream stream,int id,int type,string body,CancellationToken ct)
    {
        byte[] payload=Encoding.UTF8.GetBytes(body);
        if(payload.Length>4000)throw new InvalidDataException("RCON request too large");
        byte[] data=new byte[14+payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0,4),10+payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4,4),id);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8,4),type);
        Buffer.BlockCopy(payload,0,data,12,payload.Length);
        await stream.WriteAsync(data,ct);
        await stream.FlushAsync(ct);
    }
    private static async Task<Packet> ReadPacket(Stream stream,CancellationToken ct)
    {
        byte[] sizeBytes=new byte[4];
        await stream.ReadExactlyAsync(sizeBytes,ct);
        int length=BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
        if(length<10||length>64000)throw new InvalidDataException("Invalid RCON frame");
        byte[] data=new byte[length];
        await stream.ReadExactlyAsync(data,ct);
        int id=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0,4));
        int type=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4,4));
        return new Packet(id,type,Encoding.UTF8.GetString(data,8,length-10).TrimEnd('\0'));
    }
    private static async Task<bool> IsLocalAddress(string host)
    {
        IPAddress[] resolved=await Dns.GetHostAddressesAsync(host);
        var local=new HashSet<IPAddress>(await Dns.GetHostAddressesAsync(Dns.GetHostName())){
            IPAddress.Loopback, IPAddress.IPv6Loopback
        };
        return resolved.Length>0 && resolved.All(local.Contains);
    }
}
