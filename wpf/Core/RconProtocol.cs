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
        if (command!="ListPlayers")
            throw new InvalidOperationException(
                "Live RCON is restricted to the read-only ListPlayers probe");
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
        int barrierId=commandId+1;
        await WritePacket(stream,commandId,2,command,timeout.Token);
        var responses=new List<string>();
        bool barrierSent=false;
        int totalBytes=0;
        // ASA's populated ListPlayers may span multiple packets. A quiet
        // period is NEVER proof of a complete list. The ordered second
        // read-only request acts as an explicit completion barrier.
        for(int i=0;i<128;i++)
        {
            var packet=await ReadPacket(stream,timeout.Token);
            if(packet.Id==-1)throw new UnauthorizedAccessException("RCON response rejected");
            if(packet.Type!=0)
                throw new InvalidDataException("Unexpected RCON response packet");
            if(packet.Id==commandId)
            {
                totalBytes=checked(totalBytes+System.Text.Encoding.UTF8.GetByteCount(packet.Body));
                if(totalBytes>250_000)throw new InvalidDataException("Oversized RCON player list");
                responses.Add(packet.Body);
                if(responses.Count==1 &&
                   System.Text.RegularExpressions.Regex.IsMatch(packet.Body.Trim(),
                       @"\ANo Players Connected\.?\z",
                       System.Text.RegularExpressions.RegexOptions.IgnoreCase|
                       System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    return packet.Body;
                if(!barrierSent)
                {
                    barrierSent=true;
                    await WritePacket(stream,barrierId,2,"ListPlayers",timeout.Token);
                }
            }
            else if(packet.Id==barrierId && barrierSent)
            {
                if(responses.Count==0 || string.IsNullOrWhiteSpace(
                    string.Concat(responses)))
                    throw new InvalidDataException("Incomplete RCON player list");
                return string.Concat(responses);
            }
            else throw new InvalidDataException("Unmatched RCON reply ID");
        }
        throw new InvalidDataException("RCON player list completion not proven");
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
        if(data[length-2]!=0 || data[length-1]!=0)
            throw new InvalidDataException("RCON frame terminator missing");
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
