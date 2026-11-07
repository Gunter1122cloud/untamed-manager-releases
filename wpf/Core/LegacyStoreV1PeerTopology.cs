using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UntamedManager.Core;

/// <summary>
/// Read-only consistency rules for the two ORIGINAL Store-v1 DONE peer records.
/// This does not acquire or release the original durable writer lock, grant
/// launch rights, or modify a shared Store. A completed five-plus-five source
/// record must never assign one map identity to two writer owners.
/// </summary>
public static class LegacyStoreV1PeerTopology
{
    public sealed record Peer(string DeviceId,IReadOnlyList<string> MemberIds,
        IReadOnlyList<string> MapNames,IReadOnlyList<string> SelectedIds);

    public static void Validate(string localDeviceId,IReadOnlyList<Peer> peers)
    {
        if(!Guid.TryParse(localDeviceId,out var local) ||
           peers==null||peers.Count!=2)
            throw new InvalidDataException("UM-STORE-TOPOLOGY-TWO-PEERS-REQUIRED");
        var devices=new HashSet<Guid>();
        var mapIds=new HashSet<Guid>();
        var mapNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var peer in peers)
        {
            if(peer==null||!Guid.TryParse(peer.DeviceId,out var device)||
               !devices.Add(device)||peer.MemberIds==null||
               peer.MapNames==null||peer.SelectedIds==null||
               peer.MemberIds.Count!=5||peer.MapNames.Count!=5||
               peer.SelectedIds.Count!=5)
                throw new InvalidDataException("UM-STORE-TOPOLOGY-PEER-INVALID");
            var owned=new HashSet<Guid>();
            for(int i=0;i<5;i++)
            {
                string? name=peer.MapNames[i];
                if(!Guid.TryParse(peer.MemberIds[i],out var mapId)||
                   !owned.Add(mapId)||!mapIds.Add(mapId)||
                   string.IsNullOrWhiteSpace(name)||name.Length>100||
                   !name.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-' or '.')||
                   !mapNames.Add(name))
                    throw new InvalidDataException("UM-STORE-TOPOLOGY-OVERLAPPING-MAP-OWNERS");
            }
            var selected=new HashSet<Guid>();
            foreach(string selectedId in peer.SelectedIds)
            {
                if(!Guid.TryParse(selectedId,out var parsed)||!selected.Add(parsed))
                    throw new InvalidDataException("UM-STORE-TOPOLOGY-DUPLICATE-SELECTION");
            }
            if(!owned.SetEquals(selected))
                throw new InvalidDataException("UM-STORE-TOPOLOGY-SELECTION-MISMATCH");
        }
        if(!devices.Contains(local)||devices.Count!=2||
           mapIds.Count!=10||mapNames.Count!=10)
            throw new InvalidDataException("UM-STORE-TOPOLOGY-LOCAL-OWNER-INVALID");
    }

    /// <summary>
    /// A shared peer record bearing the original local device ID must be the
    /// EXACT source configuration recorded in its local locked profile.
    /// UNC hostname aliases, member order, root or backup-path differences
    /// must not be silently normalized into writer authorization.
    /// </summary>
    /// <summary>
    /// Distinct historical UNC HOSTNAME aliases are not the same string, but
    /// may resolve to the exact SAME existing shared directory. Only a real
    /// Windows filesystem volume+file identity proof can bridge the spelling;
    /// DNS, string normalization and an operator claim are insufficient.
    /// </summary>
    public static bool SameOriginalUncDirectory(string? first,string? second)
    {
        if(string.IsNullOrWhiteSpace(first)||string.IsNullOrWhiteSpace(second)||
           !first.StartsWith(@"\\",StringComparison.Ordinal)||
           !second.StartsWith(@"\\",StringComparison.Ordinal))
            return false;
        if(string.Equals(first,second,StringComparison.OrdinalIgnoreCase))return true;
        var proof=StorePathIdentity.CompareDirectories(first,second);
        return proof.AliasEvidence&&proof.BothUncPaths&&proof.SameFileSystemObject;
    }

    public static void RequireSameOriginalConfiguration(
        JsonElement localCluster,JsonElement sharedPeerConfig)=>
        RequireSameOriginalConfiguration(localCluster,sharedPeerConfig,
            SameOriginalUncDirectory);

    // A verifier callback is injectable ONLY into isolated native tests;
    // production calls the actual Windows volume/file-ID probe above.
    internal static void RequireSameOriginalConfiguration(
        JsonElement localCluster,JsonElement sharedPeerConfig,
        Func<string,string,bool> verifiedPhysicalAlias)
    {
        ArgumentNullException.ThrowIfNull(verifiedPhysicalAlias);
        string original=StoreV1Fingerprint.Compute(localCluster);
        string remote=StoreV1Fingerprint.Compute(sharedPeerConfig);
        if(string.Equals(original,remote,StringComparison.Ordinal))return;
        if(localCluster.ValueKind!=JsonValueKind.Object||
           sharedPeerConfig.ValueKind!=JsonValueKind.Object ||
           !localCluster.TryGetProperty("path",out var localPathElement)||
           !sharedPeerConfig.TryGetProperty("path",out var peerPathElement)||
           localPathElement.ValueKind!=JsonValueKind.String||
           peerPathElement.ValueKind!=JsonValueKind.String)
            throw new InvalidDataException("UM-STORE-TOPOLOGY-CONFIG-PATH-MISSING");
        string localPath=localPathElement.GetString()??"";
        string remotePath=peerPathElement.GetString()??"";
        if(!verifiedPhysicalAlias(localPath,remotePath))
            throw new InvalidDataException("UM-STORE-TOPOLOGY-UNC-ALIAS-UNVERIFIED");
        // Compare every original JSON key/value in the exact historical
        // order; substitute ONLY the already physically-proven UNC spelling.
        // The original records and their stored hashes are NEVER rewritten.
        JsonObject patched=JsonNode.Parse(localCluster.GetRawText())?.AsObject()
            ??throw new InvalidDataException("UM-STORE-TOPOLOGY-CONFIG-INVALID");
        patched["path"]=remotePath;
        using var normalized=JsonDocument.Parse(patched.ToJsonString());
        if(!string.Equals(StoreV1Fingerprint.Compute(normalized.RootElement),
            remote,StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-TOPOLOGY-CONFIG-BESIDES-PATH-DIFFERS");
    }
}
