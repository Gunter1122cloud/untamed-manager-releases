using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// Source RCON wire format. Runs against a locally configured ASA server only.
// Remote peer messages never reach this protocol.
internal sealed record RconSaveWorldDispatch(DateTimeOffset DispatchedUtc,bool SaveCompleteProven);

public static class RconProtocol
{
    private static readonly Encoding StrictUtf8=new UTF8Encoding(false,true);
    public static Task<string> Execute(string host,int port,string password,string command,
        CancellationToken cancellationToken=default) =>
        ExecuteCore(host,port,password,command,false,cancellationToken);

    // Only the isolated, permission/identity-bound one-shot queue calls this.
    // The second authorization happens AFTER RCON authentication and BEFORE
    // the first mutating command byte. Never expose arbitrary command sending.
    internal static async Task DispatchAdminAsync(int port,string password,RconDraft draft,
        Action authorizeAfterAuthentication,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(authorizeAfterAuthentication);
        var compiled=RconCommandPolicy.Preview(draft.Map,draft.Action,
            draft.Action is RconAction.Broadcast or RconAction.ServerChat?
                draft.Command[(draft.Command.IndexOf(' ')+1)..]:null,true,false,true);
        if(compiled.Command!=draft.Command||draft.Action is RconAction.ListPlayers or RconAction.Custom)
            throw new UnauthorizedAccessException("Admin command differs from approved action");
        await ExecuteCore("127.0.0.1",port,password,draft.Command,false,ct,
            true,authorizeAfterAuthentication);
    }

    // Does not authorize server shutdown. This performs an actual RCON write
    // after authentication, and returns only dispatch status, NEVER SaveComplete.
    // No production lifecycle path calls it until a trusted completion witness
    // is wired to the operation journal and validated against real ASA.
    internal static async Task<RconSaveWorldDispatch> DispatchSaveWorldAsync(
        string host,int port,string password,CancellationToken cancellationToken=default)
    {
        await ExecuteCore(host,port,password,"SaveWorld",true,cancellationToken);
        return new(DateTimeOffset.UtcNow,false);
    }

    // The UI's separately approved Broadcast/ServerChat/custom administrator
    // command uses this ONLY after a native process-bound approval. Recheck
    // the exact local RCON listener AFTER successful RCON authentication and
    // BEFORE sending the command. The result proves one TCP dispatch, not
    // execution or a complete RCON response. No implicit retries.
    internal static Task<string> DispatchApprovedAdminAsync(
        string host,int port,string password,string command,
        Action authorizeAfterAuthentication,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorizeAfterAuthentication);
        if(host!="127.0.0.1"||string.IsNullOrWhiteSpace(command)||
           command.Length>240||command.Any(char.IsControl)||command.Contains(';'))
            throw new InvalidDataException("UM-ADMIN-RCON-UNSAFE-DISPATCH");
        string token=command.Split(' ',2,StringSplitOptions.RemoveEmptyEntries)[0];
        if(token.Equals("SaveWorld",StringComparison.OrdinalIgnoreCase)||
           token.Equals("DoExit",StringComparison.OrdinalIgnoreCase)||
           token.Equals("Quit",StringComparison.OrdinalIgnoreCase)||
           token.Equals("Restart",StringComparison.OrdinalIgnoreCase)||
           token.Equals("Exit",StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("UM-ADMIN-RCON-LIFECYCLE-SEPARATE");
        return ExecuteCore("127.0.0.1",port,password,command,false,ct,
            adminDispatch:true,
            authorizeAfterAuthentication:authorizeAfterAuthentication,
            dispatchOnly:true);
    }

    // SaveWorld for the lifecycle path: bind the request to an OS-verified
    // local listener *after* RCON authentication, before dispatching bytes.
    internal static async Task DispatchOwnedSaveWorldAsync(int port,string password,
        Action verifyOwnedListener,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verifyOwnedListener);
        await ExecuteCore("127.0.0.1",port,password,"SaveWorld",true,ct,
            adminDispatch:true,authorizeAfterAuthentication:verifyOwnedListener);
    }

    // Explicit graceful dedicated-server exit. DoExit itself triggers a save
    // in ASA; no arbitrary browser RCON string may invoke this path.
    // The caller rechecks exact owned Windows PID and RCON listener after
    // authentication, before any DoExit bytes are transmitted.
    internal static async Task DispatchDoExitAsync(int port,string password,
        Action verifyOwnedListener,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verifyOwnedListener);
        await ExecuteCore("127.0.0.1",port,password,"DoExit",false,ct,
            adminDispatch:true,authorizeAfterAuthentication:verifyOwnedListener);
    }

    private static async Task<string> ExecuteCore(string host,int port,string password,
        string command,bool allowSaveWorld,CancellationToken cancellationToken,
        bool adminDispatch=false,Action? authorizeAfterAuthentication=null,
        bool dispatchOnly=false)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Missing RCON password");
        if(password.Contains('\0')||Encoding.UTF8.GetByteCount(password)>4000)
            throw new InvalidDataException("Invalid RCON credential framing");
        if(string.IsNullOrWhiteSpace(host)||host.Length>253)
            throw new InvalidDataException("Invalid RCON host");
        if(!adminDispatch&&(allowSaveWorld ? command!="SaveWorld" :
           command is not ("ListPlayers" or "GetGameLog" or "GetChat"))
          )
            throw new InvalidOperationException(
                "RCON operation not authorized by its explicit call path");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        timeout.Token.ThrowIfCancellationRequested();
        var address=await ResolveLocalAddress(host,timeout.Token);
        using var client=new TcpClient(address.AddressFamily);
        // Connect to the address already checked, never resolve the name a
        // second time after authorization. The deadline includes DNS as well.
        await client.ConnectAsync(address,port,timeout.Token);
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
        timeout.Token.ThrowIfCancellationRequested();
        if(adminDispatch)(authorizeAfterAuthentication??throw new UnauthorizedAccessException(
            "Missing per-dispatch authorization"))();
        int commandId=challenge+4;
        int barrierId=commandId+1;
        await WritePacket(stream,commandId,2,command,timeout.Token);
        // TCP flush proves only dispatch to the local socket; the server may not
        // have executed SaveWorld. Never use this result to authorize DoExit.
        if(allowSaveWorld)return "SAVEWORLD_DISPATCHED_NOT_COMPLETED";
        // Mutating admin commands often return no RCON response. Waiting for
        // a read-only completion barrier would report a false failure after
        // the irreversible action already reached ASA. Never claim executed.
        if(adminDispatch && dispatchOnly)
            return "ADMIN_RCON_DISPATCHED_EXECUTION_NOT_VERIFIED";
        // The ASA process can close its RCON socket immediately after DoExit.
        // A successful flush proves dispatch, not durable game data. The
        // lifecycle caller verifies process exit separately.
        if(adminDispatch&&command=="DoExit")return "DOEXIT_DISPATCHED";
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
                if(totalBytes>250_000)throw new InvalidDataException("Oversized read-only RCON response");
                responses.Add(packet.Body);
                if(command=="ListPlayers" && responses.Count==1 &&
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
                if(responses.Count==0 || (!adminDispatch&&string.IsNullOrWhiteSpace(
                    string.Concat(responses))))
                    throw new InvalidDataException("Incomplete read-only RCON response");
                return string.Concat(responses);
            }
            else throw new InvalidDataException("Unmatched RCON reply ID");
        }
        throw new InvalidDataException("Read-only RCON completion not proven");
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
        string body;
        try{body=StrictUtf8.GetString(data,8,length-10);}
        catch(DecoderFallbackException ex){throw new InvalidDataException("Invalid RCON UTF-8",ex);}
        if(body.Contains('\0'))throw new InvalidDataException("Embedded RCON body terminator");
        return new Packet(id,type,body);
    }
    private static async Task<IPAddress> ResolveLocalAddress(string host,CancellationToken ct)
    {
        IPAddress Normalize(IPAddress address)=>address.IsIPv4MappedToIPv6?address.MapToIPv4():address;
        var resolved=(IPAddress.TryParse(host,out var literal)?new[]{literal}:
            await Dns.GetHostAddressesAsync(host,ct)).Select(Normalize).Distinct().ToArray();
        ct.ThrowIfCancellationRequested();
        if(resolved.Length==0)throw new UnauthorizedAccessException("RCON host must be a local interface");
        if(resolved.Any(a=>!IPAddress.IsLoopback(a)))
        {
            // Local interface ownership comes from the OS, not from a DNS
            // answer for this machine's own name.
            var local=NetworkInterface.GetAllNetworkInterfaces().SelectMany(n=>
                n.GetIPProperties().UnicastAddresses.Select(a=>Normalize(a.Address))).ToHashSet();
            if(resolved.Any(a=>!IPAddress.IsLoopback(a)&&!local.Contains(a)))
                throw new UnauthorizedAccessException("RCON host must be a local interface");
        }
        ct.ThrowIfCancellationRequested();
        return resolved.OrderBy(a=>a.AddressFamily==AddressFamily.InterNetwork?0:1).First();
    }
}

