using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UntamedManager.Core;

// Physical transfer agreement on the authenticated LAN status channel.
// No character-travel claim, remote commands or historical writer-lock changes.
public sealed record FreshStorePeerTransfer(string ClusterId,string DirectoryTag,Guid[] LocalMapIds);
public static class FreshStoreMultiHostTransfer
{
    private static bool Hash(string? value)=>value?.Length==64&&value.All(Uri.IsHexDigit)&&value.Any(c=>c!='0');
    private static bool Same(string? a,string? b)=>StringComparer.OrdinalIgnoreCase.Equals(a,b);
    public static FreshStorePeerTransfer[] LocalEvidence(ManagerConfig config)
    {
        var result=new List<FreshStorePeerTransfer>();
        foreach(var cluster in config.ClusterRegistry.Clusters.Take(16))
        {
            try
            {
                if(Same(cluster.ClusterId,"KovaCluster")||!cluster.TransferDirectory.StartsWith(@"\\",StringComparison.Ordinal))continue;
                var ids=config.ClusterRegistry.MapAssignments.Where(a=>Same(a.ClusterId,cluster.ClusterId)).Select(a=>a.MapId).ToHashSet();
                var local=config.LocalMaps.Where(m=>ids.Contains(m.Id)).ToArray();
                if(local.Length is <1 or >128||local.Any(m=>m.HistoricalStoreProtected||m.ObservedReadOnlyImported||
                    !string.IsNullOrWhiteSpace(m.HistoricalArchiveSha256)||!string.IsNullOrWhiteSpace(m.HistoricalMapKey)||
                    !string.IsNullOrWhiteSpace(m.StoreTestRoot)||
                    (FreshStoreEnrollmentPolicy.IsEnrolled(m)&&(!Same(m.FreshStoreClusterId,cluster.ClusterId)||
                    !Same(m.FreshStoreTransferDirectory,cluster.TransferDirectory)))||
                    (!FreshStoreEnrollmentPolicy.IsEnrolled(m)&&KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(m))))continue;
                string tag=StorePathIdentity.LocalDirectoryFingerprint(cluster.TransferDirectory);
                if(local.Any(m=>FreshStoreEnrollmentPolicy.IsEnrolled(m)&&!Same(m.FreshStoreTransferFileId,tag)))continue;
                result.Add(new(cluster.ClusterId,tag,local.Select(m=>m.Id).Order().ToArray()));
            }catch(Exception){/* no positive evidence for unavailable transfer */}
        }
        return result.ToArray();
    }
    public static bool Matches(ClusterRegistrySnapshot registry,string clusterId,string localHostId,string tag,
        IEnumerable<LanPeerObservation> observations,DateTimeOffset now)
    {
        if(!Hash(tag)||!Guid.TryParse(localHostId,out var owner)||owner==Guid.Empty)return false;
        var members=registry.ClusterRegistry.MapAssignments.Where(a=>Same(a.ClusterId,clusterId)).Select(a=>a.MapId).ToHashSet();
        var maps=registry.HostRegistry.Maps.Where(m=>members.Contains(m.MapId)).ToArray();
        if(maps.Length!=members.Count||maps.Select(m=>m.MapId).Distinct().Count()!=maps.Length)return false;
        var peers=observations.ToArray();
        var groups=maps.Where(m=>!Same(m.HostId,localHostId)).GroupBy(m=>m.HostId,StringComparer.OrdinalIgnoreCase).ToArray();
        if(groups.Length is <1 or >15)return false;
        foreach(var group in groups)
        {
            if(!Guid.TryParse(group.Key,out var host)||host==Guid.Empty||host==owner)return false;
            if(!registry.HostRegistry.Hosts.Any(h=>Same(h.HostId,group.Key)&&h.IsPaired&&!h.IsLocal))return false;
            var matches=peers.Where(p=>Guid.TryParse(p.HostId,out var id)&&id==host).ToArray();
            if(matches.Length!=1)return false;
            var p=matches[0];var packet=p.Snapshot;
            if(!p.IsFresh||p.StateCode!="UM-PEER-ONLINE"||packet==null||
                !Guid.TryParse(packet.HostId,out var packetId)||packetId!=host||
                !StatusFreshness.IsFresh(packet.ObservedUtc,now))return false;
            var proof=(packet.FreshStoreTransfers??Array.Empty<FreshStorePeerTransfer>()).Where(x=>x!=null&&Same(x.ClusterId,clusterId)).ToArray();
            if(proof.Length!=1||!Same(proof[0].DirectoryTag,tag)||proof[0].LocalMapIds==null||
                !proof[0].LocalMapIds.Order().SequenceEqual(group.Select(m=>m.MapId).Order()))return false;
            if(packet.Maps==null||group.Any(m=>!packet.Maps.Any(x=>Guid.TryParse(x.Id,out var id)&&id==m.MapId)))return false;
        }
        return true;
    }
    public static string Pin(string clusterId,string tag)=>Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("obsidian-fresh-multihost-v1\n"+clusterId+"\n"+tag.ToUpperInvariant())));
    public static string RequireEnrollment(ClusterService cluster,RegisteredCluster registered,string tag)
    {
        var registry=cluster.Registry.Snapshot();
        string owner=ClusterRegistrySchema.NormalizeHostId(cluster.Config.LocalHostId);
        var ids=registry.ClusterRegistry.MapAssignments.Where(a=>Same(a.ClusterId,registered.ClusterId)).Select(a=>a.MapId).ToHashSet();
        bool remote=registry.HostRegistry.Maps.Any(m=>ids.Contains(m.MapId)&&!Same(m.HostId,owner));
        if(!remote)return "";
        if(!registered.TransferDirectory.StartsWith(@"\\",StringComparison.Ordinal)||
           !Matches(registry,registered.ClusterId,owner,tag,cluster.PeerObservations,DateTimeOffset.UtcNow))
            throw new InvalidDataException("UM-FRESH-STORE-REMOTE-TRANSFER-NOT-CERTIFIED");
        return Pin(registered.ClusterId,tag);
    }
}
