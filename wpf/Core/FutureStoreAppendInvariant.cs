using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UntamedManager.Core;

/// <summary>
/// Verifies that five offline Store-v1 metadata images contain exactly one
/// append and no historical rewrites. This is deliberately NOT an apply
/// authorization, peer lease, or ASA conversion proof.
/// </summary>
public static class FutureStoreAppendInvariant
{
    private static void Deny(string reason) =>
        throw new InvalidDataException("UM-FUTURE-APPEND-INVARIANT-"+reason);

    private static JsonObject Parse(string value)
    {
        if(string.IsNullOrWhiteSpace(value) || value.Length>1_048_576)
            Deny("SOURCE-SIZE");
        return JsonNode.Parse(value) as JsonObject ??
            throw new InvalidDataException("UM-FUTURE-APPEND-INVARIANT-JSON");
    }

    private static JsonObject Obj(JsonNode? node) =>
        node as JsonObject ??
            throw new InvalidDataException("UM-FUTURE-APPEND-INVARIANT-OBJECT");

    private static JsonArray Arr(JsonNode? node) =>
        node as JsonArray ??
            throw new InvalidDataException("UM-FUTURE-APPEND-INVARIANT-ARRAY");

    private static string Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ?
            text : throw new InvalidDataException("UM-FUTURE-APPEND-INVARIANT-FIELD");

    private static bool Equal(JsonNode? x, JsonNode? y) => JsonNode.DeepEquals(x,y);

    public static void Verify(string[] beforeImages,string[] afterImages,Guid appendedMapId)
    {
        ArgumentNullException.ThrowIfNull(beforeImages);
        ArgumentNullException.ThrowIfNull(afterImages);
        if(appendedMapId==Guid.Empty || beforeImages.Length!=5 || afterImages.Length!=5)
            Deny("INPUT");
        var before=beforeImages.Select(Parse).ToArray();
        var after=afterImages.Select(Parse).ToArray();
        string mapId=appendedMapId.ToString("D");
        var oldMembers=Arr(before[0]["members"]);
        var nextMembers=Arr(after[0]["members"]);
        var oldRows=Arr(before[3]["rows"]);
        var nextRows=Arr(after[3]["rows"]);
        var oldStore=Obj(before[1]["maps"]);
        var nextStore=Obj(after[1]["maps"]);
        var oldSelected=Arr(before[2]["selected"]);
        if(oldMembers.Count<5 || oldMembers.Count>=32 ||
           oldRows.Count!=oldMembers.Count || oldStore.Count!=oldMembers.Count ||
           oldSelected.Count!=5 || nextMembers.Count!=oldMembers.Count+1 ||
           nextRows.Count!=oldRows.Count+1 || nextStore.Count!=oldStore.Count+1 ||
           oldStore.ContainsKey(mapId))
            Deny("ROSTER-COUNT");

        var member=Obj(nextMembers[^1]);
        var row=Obj(nextRows[^1]);
        var record=Obj(nextStore[mapId]);
        string map=Str(member["map"]),root=Str(member["root"]);
        if(Str(member["id"])!=mapId || map.Length is <1 or >80 ||
           string.IsNullOrWhiteSpace(root) || Str(row["id"])!=mapId ||
           Str(row["root"])!=root || Str(Obj(row["launch"])["map"])!=map ||
           Str(record["phase"])!="active" || Str(record["map"])!=map ||
           Str(record["root"])!=root || record.ContainsKey("sharedSession") ||
           !Guid.TryParseExact(Str(record["backupId"]),"D",out var backupId) ||
           backupId==Guid.Empty)
            Deny("NEW-MEMBER-IDENTITY");

        var nextLockConfig=Obj(after[2]["config"]);
        var nextPeerConfig=Obj(after[4]["config"]);
        var nextPeerMembers=Arr(after[4]["members"]);
        if(!Equal(nextLockConfig,after[0]) || !Equal(nextPeerConfig,after[0]) ||
           !Equal(nextPeerMembers,nextMembers) ||
           !StoreV1Fingerprint.Matches(
               JsonSerializer.SerializeToElement(after[0]),Str(after[2]["configHash"])) ||
           !StoreV1Fingerprint.Matches(
               JsonSerializer.SerializeToElement(nextPeerConfig),Str(after[4]["configHash"])))
            Deny("CONFIG-FINGERPRINT");

        // Restore ONLY the permitted append fields. If any of the five
        // restored documents differs from the prior image, some unrelated
        // original owner, port, mod, path, lock, session, timestamp or peer
        // property was rewritten.
        var restored=after.Select(x=>Obj(x.DeepClone())).ToArray();
        Arr(restored[0]["members"]).RemoveAt(nextMembers.Count-1);
        Obj(restored[1]["maps"]).Remove(mapId);
        restored[2]["config"]=before[2]["config"]?.DeepClone();
        restored[2]["configHash"]=before[2]["configHash"]?.DeepClone();
        Arr(restored[3]["rows"]).RemoveAt(nextRows.Count-1);
        restored[4]["config"]=before[4]["config"]?.DeepClone();
        restored[4]["configHash"]=before[4]["configHash"]?.DeepClone();
        restored[4]["members"]=before[4]["members"]?.DeepClone();
        for(int i=0;i<restored.Length;i++)
            if(!Equal(restored[i],before[i]))
                Deny("HISTORICAL-REWRITE-"+i);
    }
}
