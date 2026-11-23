using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Strict, offline five-image Store-v1 source inspection used BEFORE preparing
/// a future ASA conversion. This checks only one host's coherent local source,
/// not the other host's writer state or UNC physical identity. Never writes.
/// </summary>
public sealed record FutureStoreLocalSourceAudit(
    string OriginalSourceSha256,string ClusterPath,string LocalSession,
    string[] MapIds,string[] MapKeys,string[] LocalRoots,int[] UdpPorts)
{
    public bool RemotePeerCertified=>false;
    public bool StoreWriterAuthorized=>false;
    public bool ConversionComplete=>false;
}
public static class FutureStoreConversionSourceAudit
{
    private static void Deny(string code)=>throw new InvalidDataException(
        "UM-FUTURE-SOURCE-"+code);
    private static bool ValidGuid(string? value)=>
        Guid.TryParseExact(value,"D",out var result)&&result!=Guid.Empty;
    private static JsonElement Field(JsonElement obj,string name)
    {
        if(obj.ValueKind!=JsonValueKind.Object||
           !obj.TryGetProperty(name,out var value))Deny("MISSING-"+name);
        return value;
    }
    private static string Text(JsonElement obj,string name)
    {
        var field=Field(obj,name);
        if(field.ValueKind!=JsonValueKind.String)Deny("TEXT-"+name);
        return field.GetString()!;
    }
    private static string Uid(JsonElement obj,string name)
    {
        string value=Text(obj,name);
        if(!ValidGuid(value))Deny("GUID-"+name);
        return Guid.Parse(value).ToString("D");
    }
    private static JsonElement Array(JsonElement obj,string name)
    {
        var field=Field(obj,name);
        if(field.ValueKind!=JsonValueKind.Array)Deny("ARRAY-"+name);
        return field;
    }
    private static int Port(JsonElement obj,string name)
    {
        var value=Field(obj,name);
        int port=0;
        if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt32(out port)||
           port is <1024 or >65535)Deny("PORT-"+name);
        return port;
    }
    private static bool SafeKey(string value)=>value.Length is >0 and <=80&&
        value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '_' or '-' or '.');
    private static bool Same(JsonElement a,JsonElement b)=>
        JsonElement.DeepEquals(a,b);
    private static string CanonicalLocal(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||!Path.IsPathFullyQualified(value)||
           value.StartsWith(@"\\",StringComparison.Ordinal)||
           value.Any(char.IsControl)||value.Contains('~'))
            Deny("LOCAL-ROOT");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
    private static string[] Mods(JsonElement obj,string name)
    {
        var raw=Array(obj,name);
        if(raw.GetArrayLength()>500)Deny("MOD-COUNT");
        var mods=raw.EnumerateArray().Select(x=>
            x.ValueKind==JsonValueKind.String?x.GetString()??"":"").ToArray();
        if(mods.Any(x=>x.Length is <1 or >12||
                !x.All(char.IsAsciiDigit))||
           mods.Distinct(StringComparer.Ordinal).Count()!=mods.Length)
            Deny("MOD-LIST");
        return mods;
    }
    public static FutureStoreLocalSourceAudit Inspect(string[] images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if(images.Length!=5||images.Any(x=>string.IsNullOrWhiteSpace(x)||
               x.Length>1_048_576))Deny("IMAGES");
        using var c=JsonDocument.Parse(images[0]);
        using var s=JsonDocument.Parse(images[1]);
        using var l=JsonDocument.Parse(images[2]);
        using var a=JsonDocument.Parse(images[3]);
        using var p=JsonDocument.Parse(images[4]);
        var cluster=c.RootElement;
        var locked=l.RootElement;
        var peer=p.RootElement;
        string path=Text(cluster,"path");
        if(Text(cluster,"id")!=VaultProtocol.ClusterId||
           Text(cluster,"mode")!="shared"||
           !path.StartsWith(@"\\",StringComparison.Ordinal)||
           path.StartsWith(@"\\?",StringComparison.Ordinal)||
           path.StartsWith(@"\\.",StringComparison.Ordinal)||
           path.Length>500||
           path.Any(x=>char.IsControl(x)||x is '"' or '<' or '>' or '|' or '?' or '*')||
           path[2..].Split('\\').Any(x=>x.Length==0||x is "." or ".."))
            Deny("CLUSTER-PATH");
        string session=Uid(locked,"session");
        if(session!=Uid(peer,"session")||Text(peer,"phase")!="done"||
           !Same(Field(locked,"config"),cluster)||
           !Same(Field(peer,"config"),cluster)||
           !StoreV1Fingerprint.Matches(cluster,Text(locked,"configHash"))||
           !StoreV1Fingerprint.Matches(cluster,Text(peer,"configHash"))||
           !Same(Field(peer,"members"),Field(cluster,"members")))
            Deny("LOCK-OR-PEER");
        var members=Array(cluster,"members");
        var selected=Array(locked,"selected");
        var peerSelected=Array(peer,"selected");
        var mapRecords=Field(s.RootElement,"maps");
        var launches=Array(a.RootElement,"rows");
        if(mapRecords.ValueKind!=JsonValueKind.Object||
           members.GetArrayLength() is <5 or >31 ||
           selected.GetArrayLength()!=5||
           peerSelected.GetArrayLength()!=5||
           launches.GetArrayLength()!=members.GetArrayLength()||
           mapRecords.EnumerateObject().Count()!=members.GetArrayLength()||
           !Same(selected,peerSelected))
            Deny("ROSTER-SHAPE");
        var original=selected.EnumerateArray().Select(x=>
            x.ValueKind==JsonValueKind.String?x.GetString()??"":"").ToArray();
        if(original.Any(x=>!ValidGuid(x))||
           original.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=5)
            Deny("ORIGINAL-SELECTION");
        var selectedIds=new HashSet<string>(original,StringComparer.OrdinalIgnoreCase);
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports=new HashSet<int>();
        var launchesById=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in launches.EnumerateArray())
        {
            string id=Uid(row,"id");
            if(!launchesById.TryAdd(id,row))Deny("DUPLICATE-LAUNCH");
            var detail=Field(row,"launch");
            if(!ports.Add(Port(detail,"gamePort"))||
               !ports.Add(Port(detail,"queryPort")))Deny("PORT-COLLISION");
            string[] mods=Mods(detail,"mods"),passive=Mods(detail,"passiveMods");
            if(passive.Except(mods,StringComparer.Ordinal).Any())
                Deny("PASSIVE-NOT-ACTIVE");
        }
        foreach(var row in members.EnumerateArray())
        {
            string id=Uid(row,"id"),key=Text(row,"map");
            string root=CanonicalLocal(Text(row,"root"));
            if(!SafeKey(key)||!ids.Add(id)||!keys.Add(key)||!roots.Add(root)||
               !mapRecords.TryGetProperty(id,out var record)||
               record.ValueKind!=JsonValueKind.Object||
               !launchesById.TryGetValue(id,out var launch))
                Deny("MEMBER-IDENTITY");
            if(Text(record,"phase")!="active"||
               Text(record,"map")!=key||
               !CanonicalLocal(Text(record,"root")).Equals(root,
                   StringComparison.OrdinalIgnoreCase)||
               !ValidGuid(Text(record,"backupId"))||
               !CanonicalLocal(Text(launch,"root")).Equals(root,
                   StringComparison.OrdinalIgnoreCase)||
               Text(Field(launch,"launch"),"map")!=key)
                Deny("MAP-RECORD");
            if(selectedIds.Contains(id))
            {
                if(Uid(record,"sharedSession")!=session)
                    Deny("ORIGINAL-SHARED-SESSION");
            }
            else if(record.TryGetProperty("sharedSession",out _))
                Deny("INDEPENDENT-SHARED-SESSION");
        }
        if(ids.Count!=members.GetArrayLength()||
           !selectedIds.IsSubsetOf(ids)||
           !launchesById.Keys.All(ids.Contains)||
           !mapRecords.EnumerateObject().All(x=>ids.Contains(x.Name)))
            Deny("ROSTER-MISMATCH");
        string hash=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n",images))));
        return new(hash,path,session,ids.ToArray(),keys.ToArray(),roots.ToArray(),
            ports.ToArray());
    }
}
