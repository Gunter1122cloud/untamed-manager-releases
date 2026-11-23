using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UntamedManager.Core;

/// <summary>
/// PURE in-memory construction of the five local metadata changes required to
/// append an independently CONVERTED map to an existing shared Store-v1 host.
/// It does not convert an ASA world, certify a backup/peer, touch the UNC,
/// write any file or confer Store writer rights. A later transaction executor
/// must independently authenticate every input, secure a peer-exclusive lock,
/// durably stage the exact before/after images and publish them atomically.
/// </summary>
public sealed record FutureStoreConversionWitness(
    Guid MapId,string MapKey,string InstallationRoot,Guid BackupId,
    DateTimeOffset PreparedUtc,DateTimeOffset AttemptedUtc,
    DateTimeOffset ConfirmedUtc,string PreConversionBackupSha256,
    string ConvertedStoreSha256);

public sealed record FutureStoreAppendBundle(
    string ClusterJson,string StoreJson,string LockJson,
    string LaunchJson,string LocalDonePeerJson,
    string OriginalRosterHash,string NewRosterHash,
    string OriginalSourceHash,string NewSourceHash,
    string Evidence)
{
    // Never a production authorization; all five strings are candidate
    // offline images and need a separate irreversible-write coordinator.
    public bool StoreWriteAuthorized=>false;
    public bool ConversionExecuted=>false;
    public bool CharacterTravelCertified=>false;
}

public static class FutureStoreAppendPlanner
{
    private static void Deny(string code)=>throw new InvalidDataException(code);
    private static bool Hash(string? s)=>s?.Length==64&&s.All(Uri.IsHexDigit);
    private static JsonObject Object(string s,string name)
    {
        if(string.IsNullOrWhiteSpace(s)||s.Length>1_048_576)
            Deny("UM-FUTURE-STORE-"+name+"-SIZE");
        return JsonNode.Parse(s) as JsonObject ??
            throw new InvalidDataException("UM-FUTURE-STORE-"+name+"-SHAPE");
    }
    private static string Text(JsonObject row,string key)=>
        row[key] is JsonValue node&&node.TryGetValue<string>(out var value)?
            value:throw new InvalidDataException("UM-FUTURE-STORE-FIELD-"+key.ToUpperInvariant());
    private static string Id(JsonObject row,string key)
    {
        string id=Text(row,key);
        if(!Guid.TryParseExact(id,"D",out var parsed)||parsed==Guid.Empty)
            Deny("UM-FUTURE-STORE-GUID-"+key.ToUpperInvariant());
        return parsed.ToString("D");
    }
    private static JsonObject Child(JsonObject root,string key)=>
        root[key] as JsonObject??throw new InvalidDataException("UM-FUTURE-STORE-OBJECT-"+key.ToUpperInvariant());
    private static JsonArray Array(JsonObject root,string key)=>
        root[key] as JsonArray??throw new InvalidDataException("UM-FUTURE-STORE-ARRAY-"+key.ToUpperInvariant());
    private static JsonObject Item(JsonNode? node)=>
        node as JsonObject??throw new InvalidDataException("UM-FUTURE-STORE-ARRAY-ITEM");
    private static string HashText(string text)=>Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static JsonElement Element(JsonNode node)=>JsonSerializer.SerializeToElement(node);
    private static string ConfigHash(JsonNode node)=>StoreV1Fingerprint.Compute(Element(node));
    private static void SameHash(JsonNode a,JsonNode b,string code)
    {
        if(ConfigHash(a)!=ConfigHash(b))Deny(code);
    }
    private static bool SafeMap(string key)=>key.Length is >0 and <=80&&
        key.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-' or '.');
    private static int Port(JsonObject row,string name)
    {
        int port=0;
        if(row[name] is not JsonValue v||!v.TryGetValue<int>(out port)||
           port is <1024 or >65535)Deny("UM-FUTURE-STORE-PORT-"+name);
        return port;
    }
    private static string[] Mods(JsonObject row,string key)
    {
        var items=Array(row,key);
        if(items.Count>500)Deny("UM-FUTURE-STORE-MOD-COUNT");
        var ids=items.Select(x=>x is JsonValue v&&v.TryGetValue<string>(out var s)?
            s:"").ToArray();
        if(ids.Any(x=>x.Length is <1 or >12||!x.All(char.IsAsciiDigit))||
           ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length)
            Deny("UM-FUTURE-STORE-MOD-ID");
        return ids;
    }

    /// <summary>
    /// Stages a complete append-only JSON bundle from one LOCAL host's
    /// already checked source. Does not interpret strings as a trustworthy
    /// conversion receipt: a future native converter must issue/verify
    /// the witness independently before calling this offline planner.
    /// </summary>
    public static FutureStoreAppendBundle Stage(
        string localCluster,string localStore,string localLock,
        string localLaunch,string localDonePeer,string localDeviceId,
        FutureStoreConversionWitness converted,int gamePort,int queryPort,
        string[] enabledMods,string[] passiveMods)
    {
        ArgumentNullException.ThrowIfNull(converted);
        ArgumentNullException.ThrowIfNull(enabledMods);
        ArgumentNullException.ThrowIfNull(passiveMods);
        if(converted.MapId==Guid.Empty||converted.BackupId==Guid.Empty||
           !SafeMap(converted.MapKey)||!Hash(converted.PreConversionBackupSha256)||
           !Hash(converted.ConvertedStoreSha256)||
           converted.PreparedUtc.Year<2020||
           converted.PreparedUtc>converted.AttemptedUtc||
           converted.AttemptedUtc>converted.ConfirmedUtc||
           converted.ConfirmedUtc>DateTimeOffset.UtcNow.AddMinutes(5))
            Deny("UM-FUTURE-STORE-CONVERSION-WITNESS-INVALID");
        string root=converted.InstallationRoot;
        if(string.IsNullOrWhiteSpace(root)||root.Length>900||
           !Path.IsPathFullyQualified(root)||root.StartsWith(@"\\",StringComparison.Ordinal)||
           root.Any(char.IsControl)||root.Contains('~'))
            Deny("UM-FUTURE-STORE-LOCAL-ROOT-INVALID");
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if(gamePort is <1024 or >65535||queryPort is <1024 or >65535||
           gamePort==queryPort)
            Deny("UM-FUTURE-STORE-PORT-INVALID");
        if(enabledMods.Length>100||passiveMods.Length>100||
           enabledMods.Distinct(StringComparer.Ordinal).Count()!=enabledMods.Length||
           passiveMods.Distinct(StringComparer.Ordinal).Count()!=passiveMods.Length||
           enabledMods.Concat(passiveMods).Any(m=>m.Length is <1 or >12||!m.All(char.IsAsciiDigit))||
           passiveMods.Except(enabledMods,StringComparer.Ordinal).Any())
            Deny("UM-FUTURE-STORE-MODS-INVALID");

        var cluster=Object(localCluster,"CLUSTER");
        var store=Object(localStore,"STORE");
        var locked=Object(localLock,"LOCK");
        var launch=Object(localLaunch,"LAUNCH");
        var peer=Object(localDonePeer,"PEER");
        if(Text(cluster,"id")!=VaultProtocol.ClusterId||
           Text(cluster,"mode")!="shared"||
           !Text(cluster,"path").StartsWith(@"\\",StringComparison.Ordinal)||
           Text(peer,"phase")!="done"||
           Id(peer,"id")!=Guid.Parse(localDeviceId).ToString("D")||
           Id(peer,"session")!=Id(locked,"session")||
           Text(Child(peer,"config"),"path")!=Text(cluster,"path"))
            Deny("UM-FUTURE-STORE-IDENTITY-INVALID");
        var members=Array(cluster,"members");
        var selected=Array(locked,"selected");
        var peerSelected=Array(peer,"selected");
        var peerMembers=Array(peer,"members");
        var records=Child(store,"maps");
        var rows=Array(launch,"rows");
        if(members.Count is <5 or >31||selected.Count!=5||
           peerSelected.Count!=5||peerMembers.Count!=members.Count||
           records.Count!=members.Count||rows.Count!=members.Count||
           Text(Child(locked,"config"),"id")!=VaultProtocol.ClusterId||
           Text(Child(locked,"config"),"path")!=Text(cluster,"path"))
            Deny("UM-FUTURE-STORE-ROSTER-INVALID");
        if(!StoreV1Fingerprint.Matches(Element(Child(locked,"config")),
                 Text(locked,"configHash"))||
           !StoreV1Fingerprint.Matches(Element(cluster),
                 Text(locked,"configHash"))||
           !StoreV1Fingerprint.Matches(Element(Child(peer,"config")),
                 Text(peer,"configHash")))
            Deny("UM-FUTURE-STORE-HASH-MISMATCH");
        SameHash(cluster,Child(peer,"config"),"UM-FUTURE-STORE-PEER-CONFIG-CHANGED");
        SameHash(members,peerMembers,"UM-FUTURE-STORE-PEER-MEMBERS-CHANGED");

        var selectedIds=selected.Select(x=>x is JsonValue v&&v.TryGetValue<string>(out var id)?
            id:"").ToArray();
        var peerIds=peerSelected.Select(x=>x is JsonValue v&&v.TryGetValue<string>(out var id)?
            id:"").ToArray();
        if(selectedIds.Length!=5||selectedIds.Distinct(StringComparer.Ordinal).Count()!=5||
           !selectedIds.SequenceEqual(peerIds,StringComparer.Ordinal))
            Deny("UM-FUTURE-STORE-SELECTED-CHANGED");
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports=new HashSet<int>();
        var launchIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string session=Id(locked,"session");
        foreach(var item in members)
        {
            var map=Item(item);
            string id=Id(map,"id"),key=Text(map,"map"),path=Text(map,"root");
            JsonNode? rawRecord=null;
            if(!SafeMap(key)||!ids.Add(id)||!keys.Add(key)||!roots.Add(path)||
               !records.TryGetPropertyValue(id,out rawRecord)||
               rawRecord is not JsonObject)
                Deny("UM-FUTURE-STORE-RECORD-INVALID");
            var record=rawRecord as JsonObject ??
                throw new InvalidDataException("UM-FUTURE-STORE-RECORD-INVALID");
            if(Text(record,"phase")!="active"||
               Text(record,"map")!=key||Text(record,"root")!=path)
                Deny("UM-FUTURE-STORE-RECORD-INVALID");
            _=Id(record,"backupId");
            if(selectedIds.Contains(id,StringComparer.OrdinalIgnoreCase) &&
               Id(record,"sharedSession")!=session)
                Deny("UM-FUTURE-STORE-ORIGINAL-SESSION-INVALID");
        }
        if(ids.Count!=members.Count||!selectedIds.All(x=>ids.Contains(x)))
            Deny("UM-FUTURE-STORE-SELECTION-INVALID");
        foreach(var item in rows)
        {
            var row=Item(item);string id=Id(row,"id");
            if(!ids.Contains(id)||!launchIds.Add(id))Deny("UM-FUTURE-STORE-LAUNCH-ROSTER");
            string path=Text(row,"root");
            var launchData=Child(row,"launch");
            if(!members.Any(m=>Id(Item(m),"id")==id&&
                 Text(Item(m),"root")==path&&
                 Text(Item(m),"map")==Text(launchData,"map")))
                Deny("UM-FUTURE-STORE-LAUNCH-BINDING");
            if(!ports.Add(Port(launchData,"gamePort"))||
               !ports.Add(Port(launchData,"queryPort")))
                Deny("UM-FUTURE-STORE-PORT-CONFLICT");
            var mods=Mods(launchData,"mods");
            var passive=Mods(launchData,"passiveMods");
            if(passive.Except(mods,StringComparer.Ordinal).Any())
                Deny("UM-FUTURE-STORE-LAUNCH-MOD-CONFLICT");
        }
        if(launchIds.Count!=ids.Count||!ports.Add(gamePort)||!ports.Add(queryPort)||
           !ids.Add(converted.MapId.ToString("D"))||
           !keys.Add(converted.MapKey)||!roots.Add(root))
            Deny("UM-FUTURE-STORE-CANDIDATE-CONFLICT");

        string beforeRoster=ConfigHash(members);
        string beforeSource=HashText(string.Join("\n",new[]{
            localCluster,localStore,localLock,localLaunch,localDonePeer}));
        string mapId=converted.MapId.ToString("D");
        members.Add(new JsonObject{
            ["id"]=mapId,["map"]=converted.MapKey,["root"]=root});
        records[mapId]=new JsonObject{
            ["phase"]="active",["root"]=root,["map"]=converted.MapKey,
            ["backupId"]=converted.BackupId.ToString("D"),
            ["preparedAt"]=converted.PreparedUtc.ToUniversalTime().ToString("O"),
            ["attemptedAt"]=converted.AttemptedUtc.ToUniversalTime().ToString("O"),
            ["confirmedAt"]=converted.ConfirmedUtc.ToUniversalTime().ToString("O")
        };
        var modArray=new JsonArray();
        foreach(var mod in enabledMods)modArray.Add(mod);
        var passiveArray=new JsonArray();
        foreach(var mod in passiveMods)passiveArray.Add(mod);
        rows.Add(new JsonObject{
            ["id"]=mapId,["root"]=root,
            ["launch"]=new JsonObject{
                ["map"]=converted.MapKey,["gamePort"]=gamePort,["queryPort"]=queryPort,
                ["mods"]=modArray,["passiveMods"]=passiveArray
            }
        });
        locked["config"]=cluster.DeepClone();
        locked["configHash"]=ConfigHash(cluster);
        peer["config"]=cluster.DeepClone();
        peer["configHash"]=ConfigHash(cluster);
        peer["members"]=members.DeepClone();
        // Crucially: NEVER append to locked.selected or peer.selected. The
        // original five remain the only original-conversion session owners.
        var output=new[]{
            cluster.ToJsonString(),store.ToJsonString(),locked.ToJsonString(),
            launch.ToJsonString(),peer.ToJsonString()};
        return new(output[0],output[1],output[2],output[3],output[4],
            beforeRoster,ConfigHash(members),beforeSource,
            HashText(string.Join("\n",output)),
            "IN_MEMORY_APPEND_IMAGES_ONLY_NO_STORE_OR_WORLD_AUTHORITY");
    }
}
