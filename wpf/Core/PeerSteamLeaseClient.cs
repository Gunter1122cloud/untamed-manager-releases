using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

/// <summary>
/// Transport for the existing paired Manager LAN socket, using the same
/// authenticated encrypted framing as read-only GET_STATUS. No remote ASA
/// commands are accepted. Every response is bound to the request's nonce.
/// </summary>
public static class PeerSteamLeaseClient
{
    public static async Task<ClusterSteamLeaseReply> RequestAsync(
        string host,int port,string pairingSecret,ClusterSteamLeaseRequest request,
        CancellationToken cancellationToken=default)
    {
        if(request==null||request.OperationId==Guid.Empty ||
           request.Stage.Length>80 || pairingSecret.Length<24 ||
           string.IsNullOrWhiteSpace(host)||port is <1024 or >65535)
            throw new InvalidDataException("Invalid authenticated LAN maintenance request");
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(4));
        CancellationToken ct=limit.Token;
        byte[] key=SHA256.HashData(Encoding.UTF8.GetBytes(pairingSecret));
        byte[] command=JsonSerializer.SerializeToUtf8Bytes(request);
        if(command.Length>4096)throw new InvalidDataException("Maintenance request too large");
        var frame=new WireEnvelope
        {
            UnixSeconds=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            PayloadBase64=Convert.ToBase64String(StatusCipher.Encrypt(key,command))
        };
        frame.SignatureHex=Convert.ToHexString(HMACSHA256.HashData(key,
            Encoding.UTF8.GetBytes($"{frame.UnixSeconds}\n{frame.Nonce}\n{frame.PayloadBase64}")));
        using var socket=new TcpClient();
        await socket.ConnectAsync(host,port,ct);
        using var stream=socket.GetStream();
        await Framing.Write(stream,JsonSerializer.SerializeToUtf8Bytes(frame),ct);
        WireEnvelope response=JsonSerializer.Deserialize<WireEnvelope>(
            await Framing.Read(stream,ct)) ??
            throw new InvalidDataException("Peer returned no maintenance reply");
        if(response.Nonce!=frame.Nonce ||
           Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-response.UnixSeconds)>45)
            throw new InvalidDataException("Peer maintenance nonce/time mismatch");
        byte[] expected=HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(
            $"{response.UnixSeconds}\n{response.Nonce}\n{response.PayloadBase64}"));
        byte[] supplied=Convert.FromHexString(response.SignatureHex);
        if(!CryptographicOperations.FixedTimeEquals(expected,supplied))
            throw new InvalidDataException("Peer maintenance HMAC validation failed");
        var clear=StatusCipher.Decrypt(key,Convert.FromBase64String(response.PayloadBase64));
        if(clear.Length>8192)throw new InvalidDataException("Maintenance reply too large");
        var reply=JsonSerializer.Deserialize<ClusterSteamLeaseReply>(clear) ??
            throw new InvalidDataException("Invalid maintenance lease reply");
        if(reply.Record!=null && reply.Record.OperationId!=request.OperationId)
            throw new InvalidDataException("Peer maintenance operation changed unexpectedly");
        if(!string.Equals(reply.HostId,request.FollowerHostId,
             StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Peer host identity mismatch");
        return reply;
    }
}

/// <summary>
/// Two-party prepare/commit and durable release protocol. One deterministic
/// leader per configured pair; neither side may run maintenance without a
/// matching peer commit. A partition makes both sides require operator review.
/// Production launch/Steam writes are still separately denied by the release
/// capability gate.
/// </summary>
public sealed class ClusterSteamLeaseCoordinator
{
    private readonly ClusterSteamLeaseStore local;
    private readonly string peerHost,sharedKey,peerHostId;
    private readonly int peerPort;
    public ClusterSteamLeaseCoordinator(ClusterSteamLeaseStore local,
        string peerHost,int peerPort,string sharedKey,string peerHostId)
    {
        if(local==null||string.IsNullOrWhiteSpace(peerHost)||
           peerPort is <1024 or >65535||string.IsNullOrWhiteSpace(sharedKey)||
           sharedKey.Length<24||!Guid.TryParseExact(peerHostId,"N",out _)||
           string.Compare(local.LocalHostId,peerHostId,StringComparison.OrdinalIgnoreCase)>=0)
            throw new InvalidDataException(
                "Only the deterministic lower-ID leader can coordinate both paired hosts");
        this.local=local;
        this.peerHost=peerHost;
        this.peerPort=peerPort;
        this.sharedKey=sharedKey;
        this.peerHostId=peerHostId.ToLowerInvariant();
    }
    private ClusterSteamLeaseRequest Make(string action,Guid operation,string stage="")=>
        new(action,operation,local.LocalHostId,peerHostId,stage);
    private async Task<ClusterSteamLeaseReply> Remote(string action,Guid id,
        string stage,CancellationToken ct)
    {
        var reply=await PeerSteamLeaseClient.RequestAsync(
            peerHost,peerPort,sharedKey,Make(action,id,stage),ct);
        if(!reply.Accepted)
            throw new IOException("Peer maintenance lease refused: "+reply.Message);
        return reply;
    }
    /// <summary>
    /// A delegate may only perform safe work after BOTH durable journals
    /// show the same committed operation. Checkpoint() must be called between
    /// map steps; it rechecks the authenticated remote fence. Never auto-replay
    /// after an unclean exit.
    /// </summary>
    public async Task ExecuteAsync(Func<Func<CancellationToken,Task>,CancellationToken,Task> work,
        CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var id=Guid.NewGuid();
        var prepare=local.Apply(Make("PREPARE",id));
        if(!prepare.Accepted)
            throw new IOException("Local cluster maintenance lease is already occupied");
        bool committed=false;
        try
        {
            await Remote("PREPARE",id,"",cancellationToken);
            await Remote("COMMIT",id,"",cancellationToken);
            var applied=local.Apply(Make("COMMIT",id));
            if(!applied.Accepted)
                throw new IOException("Local commit refused");
            committed=true;
            async Task Checkpoint(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var self=local.Observe();
                if(self?.OperationId!=id||self.Phase!=ClusterSteamLeasePhase.Committed)
                    throw new IOException("Local lease is no longer committed");
                var peer=await Remote("OBSERVE",id,"",ct);
                if(peer.Record?.Phase!=ClusterSteamLeasePhase.Committed)
                    throw new IOException("Peer lease is no longer committed");
            }
            await Checkpoint(cancellationToken);
            await work(Checkpoint,cancellationToken);
            await Checkpoint(cancellationToken);
            // A future real maintenance adapter must certify its own
            // SaveComplete and restart before the work delegate returns.
            await Remote("COMPLETE",id,"VERIFIED_COMPLETION",cancellationToken);
            var done=local.Apply(Make("COMPLETE",id,"VERIFIED_COMPLETION"));
            if(!done.Accepted)
                throw new IOException("Local completion not persisted");
        }
        catch
        {
            // An uncertain remote commit may have succeeded. Never clear
            // either lease just because a TCP connection was lost.
            local.Apply(Make("NEEDS_OPERATOR",id));
            try{await Remote("NEEDS_OPERATOR",id,"",CancellationToken.None);}
            catch{/* peer may be offline; durable local fence remains blocked */}
            throw;
        }
    }
}
