using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// Read-only topology/transfer preflight for additional Managers. The
// historical two-peer ACTIVE/DONE protocol is deliberately not generalized
// or rewritten by this check. No writer lease or character-travel proof.
public sealed record HistoricalExpansionTransferWitness(string HostId,string DirectoryTag,
    Guid[] LocalMapIds,DateTimeOffset ObservedUtc);

public sealed record HistoricalExpansionTopologyResult(bool ReadyForFurtherReview,string StatusCode,int AdditionalHostCount)
{
    public bool StoreWriterAuthorized=>false;
    public bool CharacterTravelCertified=>false;
}

public static class HistoricalStoreExpansionTopology
{
    private static bool Hash(string? tag)=>tag is {Length:64}&&tag.All(Uri.IsHexDigit)&&tag.Any(c=>c!='0');
    private static bool Fresh(DateTimeOffset observed,DateTimeOffset now)=>
        observed>=now.AddMinutes(-5)&&observed<=now.AddMinutes(1);
    private static Guid Id(string? value)=>Guid.TryParse(value,out var id)?id:Guid.Empty;

    // On-demand, local filesystem probe; caller must bind the returned
    // witness to an authenticated peer status. Do not call on a UI poll.
    public static HistoricalExpansionTransferWitness CaptureLocal(ManagerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var host=Id(config.LocalHostId);
        var cluster=config.ClusterRegistry.Clusters.Single(x=>
            x.ClusterId.Equals(VaultProtocol.ClusterId,StringComparison.OrdinalIgnoreCase));
        string path=cluster.TransferDirectory;
        if(host==Guid.Empty||!path.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-HISTORICAL-EXPANSION-LOCAL-IDENTITY");
        var assignments=config.ClusterRegistry.MapAssignments.Where(x=>
            x.ClusterId.Equals(cluster.ClusterId,StringComparison.OrdinalIgnoreCase)).Select(x=>x.MapId).ToHashSet();
        var registered=config.HostRegistry.Maps.Where(x=>Id(x.HostId)==host&&assignments.Contains(x.MapId)).ToArray();
        var maps=config.LocalMaps.Where(x=>assignments.Contains(x.Id)).ToArray();
        if(maps.Length==0||maps.Length>128||maps.Any(x=>x.Id==Guid.Empty||x.ObservedReadOnlyImported||
           x.HistoricalStoreProtected||!string.IsNullOrWhiteSpace(x.HistoricalArchiveSha256)||
           !string.IsNullOrWhiteSpace(x.HistoricalMapKey)||!string.IsNullOrWhiteSpace(x.StoreTestRoot))||
           registered.Any(x=>!x.LocalConfigurationPresent)||
           !maps.Select(x=>x.Id).Order().SequenceEqual(registered.Select(x=>x.MapId).Order()))
            throw new InvalidDataException("UM-HISTORICAL-EXPANSION-LOCAL-ROSTER");
        return new(host.ToString("N"),StorePathIdentity.LocalDirectoryFingerprint(path),
            maps.Select(x=>x.Id).Order().ToArray(),DateTimeOffset.UtcNow);
    }

    // Additional witness objects must be authenticated together with their
    // corresponding peer packet by a future native route. This verifier is
    // currently a pure planning check and never confers runtime authority.
    public static HistoricalExpansionTopologyResult Review(ClusterRegistrySnapshot registry,
        string localAnchorHost,LegacyStoreV1PeerProof localAnchor,
        IEnumerable<LanPeerObservation> observations,
        IEnumerable<HistoricalExpansionTransferWitness> extraWitnesses,DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var local=Id(localAnchorHost);
        var peers=observations.ToArray();
        var witnesses=extraWitnesses.ToArray();
        HistoricalExpansionTopologyResult Fail(string code)=>new(false,code,0);
        if(local==Guid.Empty||!Hash(localAnchor.PhysicalDirectoryTag)||
           localAnchor.PhysicalObservedUtc is not DateTimeOffset localUtc||!Fresh(localUtc,now))
            return Fail("UM-HISTORICAL-EXPANSION-ANCHOR-PHYSICAL");
        var anchors=peers.Where(p=>p.IsFresh&&p.StateCode=="UM-PEER-ONLINE"&&
            p.Snapshot!=null&&Id(p.HostId)!=Guid.Empty&&Id(p.HostId)!=local&&Id(p.HostId)==Id(p.Snapshot.HostId)&&
            StatusFreshness.IsFresh(p.Snapshot.ObservedUtc,now)&&
            LegacyStoreV1PairComparison.Compare(localAnchor,p.Snapshot.OriginalStoreV1,true) is
                {CopiedMetadataPairMatches:true,SamePhysicalTransferDirectory:true,BothOriginalFiveRegisteredInManagers:true}).ToArray();
        if(anchors.Length!=1)return Fail("UM-HISTORICAL-EXPANSION-ANCHOR-AMBIGUOUS");
        var second=Id(anchors[0].HostId);
        var cluster=registry.ClusterRegistry.Clusters.Where(x=>x.ClusterId.Equals(VaultProtocol.ClusterId,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(cluster.Length!=1||!LegacyStoreV1RosterIdentity.MatchesOriginalSharedTransfer(cluster[0].TransferDirectory,localAnchor.SharedPathTag))
            return Fail("UM-HISTORICAL-EXPANSION-CLUSTER-PATH");
        var members=registry.ClusterRegistry.MapAssignments.Where(x=>x.ClusterId.Equals(VaultProtocol.ClusterId,StringComparison.OrdinalIgnoreCase)).Select(x=>x.MapId).ToArray();
        var maps=registry.HostRegistry.Maps.Where(x=>members.Contains(x.MapId)).ToArray();
        if(members.Any(x=>x==Guid.Empty)||members.Distinct().Count()!=members.Length||
           maps.Length!=members.Length||maps.Select(x=>x.MapId).Distinct().Count()!=maps.Length)
            return Fail("UM-HISTORICAL-EXPANSION-ROSTER");
        if(!registry.HostRegistry.Hosts.Any(h=>Id(h.HostId)==local&&h.IsLocal)||
           !registry.HostRegistry.Hosts.Any(h=>Id(h.HostId)==second&&h.IsPaired&&!h.IsLocal)||
           maps.Count(m=>Id(m.HostId)==local)<5||maps.Count(m=>Id(m.HostId)==second)<5)
            return Fail("UM-HISTORICAL-EXPANSION-ANCHOR-REGISTRY");
        var extras=maps.Where(x=>Id(x.HostId)!=local&&Id(x.HostId)!=second).GroupBy(x=>Id(x.HostId)).ToArray();
        if(extras.Length==0||extras.Length>14||witnesses.Length!=extras.Length)
            return Fail("UM-HISTORICAL-EXPANSION-ADDITIONAL-HOSTS");
        foreach(var group in extras)
        {
            if(group.Key==Guid.Empty||!registry.HostRegistry.Hosts.Any(h=>Id(h.HostId)==group.Key&&h.IsPaired&&!h.IsLocal))
                return Fail("UM-HISTORICAL-EXPANSION-UNPAIRED-HOST");
            var status=peers.Where(p=>Id(p.HostId)==group.Key).ToArray();
            var evidence=witnesses.Where(w=>Id(w.HostId)==group.Key).ToArray();
            if(status.Length!=1||evidence.Length!=1||!status[0].IsFresh||status[0].StateCode!="UM-PEER-ONLINE"||
               status[0].Snapshot==null||Id(status[0].Snapshot!.HostId)!=group.Key||
               !StatusFreshness.IsFresh(status[0].Snapshot!.ObservedUtc,now)||
               status[0].Snapshot!.OriginalStoreV1?.LocalMetadataValid==true||
               !Fresh(evidence[0].ObservedUtc,now)||!Hash(evidence[0].DirectoryTag)||
               !StringComparer.OrdinalIgnoreCase.Equals(evidence[0].DirectoryTag,localAnchor.PhysicalDirectoryTag)||
               evidence[0].LocalMapIds==null||
               !evidence[0].LocalMapIds.Order().SequenceEqual(group.Select(x=>x.MapId).Order())||
               status[0].Snapshot!.Maps==null||
               group.Any(m=>status[0].Snapshot!.Maps.Count(x=>Id(x.Id)==m.MapId)!=1))
                return Fail("UM-HISTORICAL-EXPANSION-ADDITIONAL-EVIDENCE");
        }
        return new(true,"HISTORICAL_EXPANSION_READ_ONLY_TOPOLOGY_MATCH",extras.Length);
    }
}
