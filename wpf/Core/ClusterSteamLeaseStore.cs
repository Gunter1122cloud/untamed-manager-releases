using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

public enum ClusterSteamLeasePhase
{
    Prepared, Committed, Completed, Cancelled, NeedsOperator
}
public sealed record ClusterSteamLeaseRecord(
    int Schema,Guid OperationId,string LeaderHostId,string FollowerHostId,
    ClusterSteamLeasePhase Phase,DateTimeOffset ChangedUtc,string Stage);
public sealed record ClusterSteamLeaseRequest(
    string Action,Guid OperationId,string LeaderHostId,string FollowerHostId,string Stage="");
public sealed record ClusterSteamLeaseReply(
    string HostId,bool Accepted,string Message,ClusterSteamLeaseRecord? Record);

/// <summary>
/// Durable two-host maintenance reservation. This is a *coordination fence*,
/// never permission to start, stop, save or update an ASA process. The same
/// two-host HMAC key authenticates messages in the existing LAN transport.
/// No TTL/clock expiry: a crashed or partitioned Manager stays blocked until
/// an operator reconciles both durable journals.
/// </summary>
public sealed class ClusterSteamLeaseStore
{
    private readonly string root;
    public string LocalHostId { get; }
    public static string DefaultRoot=>Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.CommonApplicationData),
        "UntamedManager","ClusterMaintenanceLease-v1");
    public ClusterSteamLeaseStore(string localHostId,string? rootOverride=null)
    {
        if(!IsId(localHostId))
            throw new InvalidDataException("Maintenance host identity must be a GUID");
        LocalHostId=localHostId.ToLowerInvariant();
        root=Path.GetFullPath(rootOverride??DefaultRoot);
        if(!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\")||
           root.Contains('~') ||
           root.Split(Path.DirectorySeparatorChar).Any(x=>x is "." or ".."))
            throw new InvalidDataException("Maintenance journal must be a fixed local path");
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Maintenance journal cannot use network storage");
    }
    private static bool IsId(string? id)=>Guid.TryParseExact(id,"N",out _) &&
        id!.Length==32 && id.All(c=>char.IsAsciiHexDigit(c));
    private string PathOfRecord=>Path.Combine(root,"lease-v1.json");
    private static bool IsActive(ClusterSteamLeasePhase state)=>
        state is ClusterSteamLeasePhase.Prepared or ClusterSteamLeasePhase.Committed
            or ClusterSteamLeasePhase.NeedsOperator;
    private void VerifyRoot()
    {
        for(DirectoryInfo? node=new(root);node!=null;node=node.Parent)
            if(node.Exists && (node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked maintenance lease storage rejected");
    }
    private ClusterSteamLeaseRecord? Load()
    {
        VerifyRoot();
        if(!File.Exists(PathOfRecord))return null;
        if((File.GetAttributes(PathOfRecord)&FileAttributes.ReparsePoint)!=0 ||
           new FileInfo(PathOfRecord).Length is <5 or >16384)
            throw new InvalidDataException("Maintenance lease journal is corrupt");
        ClusterSteamLeaseRecord? found;
        try{found=JsonSerializer.Deserialize<ClusterSteamLeaseRecord>(
            File.ReadAllText(PathOfRecord));}
        catch(JsonException ex){throw new InvalidDataException(
            "Maintenance lease journal is invalid",ex);}
        if(found==null || found.Schema!=1||found.OperationId==Guid.Empty ||
           !IsId(found.LeaderHostId)||!IsId(found.FollowerHostId)||
           found.LeaderHostId==found.FollowerHostId ||
           !Enum.IsDefined(found.Phase) || found.Stage.Length>80 ||
           found.ChangedUtc>DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("Maintenance lease identity/state is invalid");
        if(!string.Equals(found.LeaderHostId,LocalHostId,StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(found.FollowerHostId,LocalHostId,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Maintenance lease belongs to a different machine");
        return found;
    }
    private void Persist(ClusterSteamLeaseRecord entry)
    {
        VerifyRoot();
        Directory.CreateDirectory(root);
        VerifyRoot();
        string tmp=PathOfRecord+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,entry);stream.Flush(true);}
            File.Move(tmp,PathOfRecord,overwrite:true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    /// <summary>Read only; never clears a stale or interrupted lease.</summary>
    public ClusterSteamLeaseRecord? Observe()
    {
        using var guard=new Semaphore(1,1,@"Global\Untamed-ClusterSteamLease-v1");
        if(!guard.WaitOne(TimeSpan.FromSeconds(3)))
            throw new IOException("Maintenance lease held by another Manager");
        try{return Load();}
        finally{guard.Release();}
    }
    /// <summary>
    /// The initiator is deterministically the lower GUID host ID. Concurrent
    /// leaders and split-brain work are rejected without relying on wall clocks.
    /// A duplicate message is idempotent ONLY for the same exact operation.
    /// </summary>
    public ClusterSteamLeaseReply Apply(ClusterSteamLeaseRequest message)
    {
        if(message==null || !IsId(message.LeaderHostId) ||
           !IsId(message.FollowerHostId)||message.OperationId==Guid.Empty ||
           string.Compare(message.LeaderHostId,message.FollowerHostId,
               StringComparison.OrdinalIgnoreCase)>=0 ||
           !(message.LeaderHostId.Equals(LocalHostId,StringComparison.OrdinalIgnoreCase) ||
             message.FollowerHostId.Equals(LocalHostId,StringComparison.OrdinalIgnoreCase)) ||
           message.Stage.Length>80 ||
           message.Stage.Any(c=>char.IsControl(c)))
            return new(LocalHostId,false,"Invalid two-host maintenance identity",null);
        using var guard=new Semaphore(1,1,@"Global\Untamed-ClusterSteamLease-v1");
        if(!guard.WaitOne(TimeSpan.FromSeconds(3)))
            return new(LocalHostId,false,"Machine maintenance lock busy",null);
        try
        {
            var old=Load();
            bool same=old!=null && old.OperationId==message.OperationId &&
                string.Equals(old.LeaderHostId,message.LeaderHostId,StringComparison.OrdinalIgnoreCase)&&
                string.Equals(old.FollowerHostId,message.FollowerHostId,StringComparison.OrdinalIgnoreCase);
            ClusterSteamLeaseRecord next;
            switch(message.Action)
            {
                case "PREPARE":
                    if(old!=null && IsActive(old.Phase))
                        return new(LocalHostId,same&&old.Phase==ClusterSteamLeasePhase.Prepared,
                            "Maintenance reservation already exists",old);
                    next=new(1,message.OperationId,message.LeaderHostId.ToLowerInvariant(),
                        message.FollowerHostId.ToLowerInvariant(),ClusterSteamLeasePhase.Prepared,
                        DateTimeOffset.UtcNow,"Reserved");
                    break;
                case "COMMIT":
                    if(!same || old!.Phase is not (ClusterSteamLeasePhase.Prepared or
                        ClusterSteamLeasePhase.Committed))
                        return new(LocalHostId,false,"Reservation not prepared",old);
                    if(old.Phase==ClusterSteamLeasePhase.Committed)
                        return new(LocalHostId,true,"Already committed",old);
                    next=old with {Phase=ClusterSteamLeasePhase.Committed,
                        ChangedUtc=DateTimeOffset.UtcNow,Stage="Committed"};
                    break;
                case "OBSERVE":
                    return new(LocalHostId,same&&old!.Phase==ClusterSteamLeasePhase.Committed,
                        same?"Current maintenance lease":"Different or missing lease",old);
                case "NEEDS_OPERATOR":
                    if(!same || old!.Phase is not (ClusterSteamLeasePhase.Prepared or
                        ClusterSteamLeasePhase.Committed or ClusterSteamLeasePhase.NeedsOperator))
                        return new(LocalHostId,false,"Cannot mark unknown lease",old);
                    next=old with {Phase=ClusterSteamLeasePhase.NeedsOperator,
                        ChangedUtc=DateTimeOffset.UtcNow,Stage="Operator review required"};
                    break;
                case "CANCEL_PREPARE":
                    if(!same || old!.Phase is not (ClusterSteamLeasePhase.Prepared or
                        ClusterSteamLeasePhase.Cancelled))
                        return new(LocalHostId,false,"Committed lease cannot be cancelled",old);
                    next=old with {Phase=ClusterSteamLeasePhase.Cancelled,
                        ChangedUtc=DateTimeOffset.UtcNow,Stage="Cancelled before maintenance"};
                    break;
                case "COMPLETE":
                    if(!same || old!.Phase is not (ClusterSteamLeasePhase.Committed or
                        ClusterSteamLeasePhase.Completed) ||
                       message.Stage!="VERIFIED_COMPLETION")
                        return new(LocalHostId,false,"Verified completion evidence required",old);
                    next=old with {Phase=ClusterSteamLeasePhase.Completed,
                        ChangedUtc=DateTimeOffset.UtcNow,Stage="Verified completion"};
                    break;
                default:
                    return new(LocalHostId,false,"Unknown lease operation",old);
            }
            Persist(next);
            return new(LocalHostId,true,next.Phase.ToString(),next);
        }
        finally{guard.Release();}
    }
}
