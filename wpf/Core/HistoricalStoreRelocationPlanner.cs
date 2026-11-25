using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UntamedManager.Core;

// Offline proposal only: never overwrites the immutable source pin, changes
// an enrolled profile or grants a historical Store writer lease.
public sealed record HistoricalStoreRelocationProposal(Guid MapId,string OldRoot,string NewRoot,
    string BeforeSourceSha256,string AfterSourceSha256,string[] AfterImages)
{
    public bool ProfileSwitched=>false;
    public bool StoreWriteAuthorized=>false;
    public bool CopyVerified=>false;
}

public static class HistoricalStoreRelocationPlanner
{
    private static JsonObject Obj(JsonNode? n)=>n as JsonObject??
        throw new InvalidDataException("UM-HISTORICAL-MOVE-SOURCE-OBJECT");
    private static JsonArray Arr(JsonNode? n)=>n as JsonArray??
        throw new InvalidDataException("UM-HISTORICAL-MOVE-SOURCE-ARRAY");
    private static string Text(JsonNode? n)=>n?.GetValue<string>()??
        throw new InvalidDataException("UM-HISTORICAL-MOVE-SOURCE-TEXT");
    private static JsonObject ById(JsonArray rows,Guid id)=>
        rows.Select(Obj).Single(x=>Guid.TryParse(Text(x["id"]),out var actual)&&actual==id);

    // The copy-and-verify phase must occur separately under native leases.
    // Every historical metadata field except the selected root and derived
    // config hashes is preserved. No path/port/map/backup/session substitution.
    public static HistoricalStoreRelocationProposal Stage(string[] images,Guid mapId,string target)
    {
        ArgumentNullException.ThrowIfNull(images);
        var audit=FutureStoreConversionSourceAudit.Inspect(images);
        if(mapId==Guid.Empty||!audit.MapIds.Contains(mapId.ToString("D"),StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-HISTORICAL-MOVE-MAP-NOT-OWNED");
        var nodes=images.Select(x=>Obj(JsonNode.Parse(x))).ToArray();
        var cluster=nodes[0];var store=nodes[1];var locked=nodes[2];var launch=nodes[3];var peer=nodes[4];
        var member=ById(Arr(cluster["members"]),mapId);
        string source=FutureStoreInstallationPath.Canonical(Text(member["root"]));
        string destination=FutureStoreInstallationPath.Canonical(target);
        if(audit.LocalRoots.Any(root=>FutureStoreInstallationPath.Overlaps(root,destination)))
            throw new InvalidDataException("UM-HISTORICAL-MOVE-TARGET-OVERLAP");
        var record=Obj(Obj(store["maps"])[mapId.ToString("D")]);
        var row=ById(Arr(launch["rows"]),mapId);
        member["root"]=destination;
        record["root"]=destination;
        row["root"]=destination;
        // Only the existing config/member images and their derived hash
        // change. Original selected lists and session IDs remain untouched.
        string hash=StoreV1Fingerprint.Compute(JsonSerializer.SerializeToElement(cluster));
        locked["config"]=cluster.DeepClone();
        locked["configHash"]=hash;
        peer["config"]=cluster.DeepClone();
        peer["configHash"]=hash;
        peer["members"]=cluster["members"]!.DeepClone();
        var after=nodes.Select(x=>x.ToJsonString()).ToArray();
        var proof=FutureStoreConversionSourceAudit.Inspect(after);
        if(proof.LocalSession!=audit.LocalSession||proof.ClusterPath!=audit.ClusterPath||
           !proof.MapIds.SequenceEqual(audit.MapIds)||!proof.MapKeys.SequenceEqual(audit.MapKeys)||
           !proof.UdpPorts.SequenceEqual(audit.UdpPorts))
            throw new InvalidDataException("UM-HISTORICAL-MOVE-IDENTITY-CHANGED");
        return new(mapId,source,destination,audit.OriginalSourceSha256,proof.OriginalSourceSha256,after);
    }

    public static void Verify(string[] before,HistoricalStoreRelocationProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var expected=Stage(before,proposal.MapId,proposal.NewRoot);
        if(proposal.OldRoot!=expected.OldRoot||proposal.NewRoot!=expected.NewRoot||
           proposal.BeforeSourceSha256!=expected.BeforeSourceSha256||
           proposal.AfterSourceSha256!=expected.AfterSourceSha256||
           proposal.AfterImages==null||!proposal.AfterImages.SequenceEqual(expected.AfterImages,StringComparer.Ordinal))
            throw new InvalidDataException("UM-HISTORICAL-MOVE-PROPOSAL-CHANGED");
    }
}
