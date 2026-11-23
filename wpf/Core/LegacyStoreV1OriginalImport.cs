using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Local-only, source-pinned discovery of the ORIGINAL five maps in a Kova
/// ACTIVE/DONE profile, INCLUDING their original launch port/mod-order source.
/// No caller-chosen UNC path, Store writer or conversion. The five source
/// files are held read-only across both original metadata inspections.
/// </summary>
public sealed record LegacyStoreV1OriginalMap(Guid MapId,string MapKey,
    string OriginalInstallationRoot,int GamePort,int QueryPort,
    string[] EnabledMods,string[] PassiveMods);
public sealed record LegacyStoreV1AdditionalMapEvidence(Guid MapId,string MapKey,
    string OriginalInstallationRoot,bool Active,bool SessionMatches,bool BackupIdentityPresent,
    int? GamePort,int? QueryPort,string[]? EnabledMods,string[]? PassiveMods,
    string LaunchState)
{
    // Missing sharedSession is not proof of conversion; it is distinct
    // from a FOREIGN/INVALID sharedSession, and neither grants writer rights.
    public string SessionBinding {get;init;}="unverified";
}
public sealed record LegacyStoreV1OriginalLocalSelection(string FourFilesSha256,
    string LaunchProfileSha256,LegacyStoreV1OriginalMap[] Maps)
{
    // Verified lock-selected original five embedded in a later expanded
    // current local map roster. The full new source hash remains pinned.
    public bool SelectedHistoricalSubsetOfExpandedRoster {get;init;}
    public int CurrentLocalMemberCount {get;init;}
    public LegacyStoreV1AdditionalMapEvidence[] AdditionalMaps {get;init;}=
        Array.Empty<LegacyStoreV1AdditionalMapEvidence>();
}

public static class LegacyStoreV1OriginalImport
{
    private static readonly string[] Names={
        "cluster-v1.json","cluster-store-v1.json",
        "shared-store-device-v1.json","shared-store-lock-v1.json"
    };

    private static JsonElement Need(JsonElement owner,string field)
    {
        if(owner.ValueKind!=JsonValueKind.Object ||
           !owner.TryGetProperty(field,out var item))
            throw new InvalidDataException("UM-STORE-DIRECT-LAUNCH-FIELD-MISSING");
        return item;
    }
    private static string Text(JsonElement owner,string field)
    {
        var element=Need(owner,field);
        if(element.ValueKind!=JsonValueKind.String)
            throw new InvalidDataException("UM-STORE-DIRECT-LAUNCH-TEXT-INVALID");
        return element.GetString()??"";
    }
    private static int Port(JsonElement launch,string field)
    {
        var value=Need(launch,field);
        if(value.ValueKind!=JsonValueKind.Number ||
           !value.TryGetInt32(out int port)||port is <1024 or >65535)
            throw new InvalidDataException("UM-STORE-DIRECT-LAUNCH-PORT-INVALID");
        return port;
    }
    private static string[] Mods(JsonElement launch,string field)
    {
        var source=Need(launch,field);
        if(source.ValueKind!=JsonValueKind.Array||source.GetArrayLength()>100)
            throw new InvalidDataException("UM-STORE-DIRECT-MOD-COUNT-INVALID");
        var mods=new List<string>();
        foreach(var member in source.EnumerateArray())
        {
            string text=member.ValueKind switch{
                JsonValueKind.String=>member.GetString()??"",
                JsonValueKind.Number when member.TryGetInt64(out long i)=>i.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                _=>""
            };
            // Kova 1.8.3 profileLaunch permits 1–12 ASCII decimal digits.
            // Do not reject valid short original IDs during Store-v1 import.
            if(text.Length is <1 or >12||!text.All(char.IsAsciiDigit)||
               mods.Contains(text,StringComparer.Ordinal))
                throw new InvalidDataException("UM-STORE-DIRECT-MOD-ORDER-INVALID");
            mods.Add(text);
        }
        return mods.ToArray();
    }

    public static LegacyStoreV1OriginalLocalSelection Inspect(string localProfile)
    {
        string root=VaultReadSnapshot.LocalPath(localProfile,true);
        using var held=new VaultReadSnapshot();
        var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        foreach(string name in Names)
            files.Add(name,held.Read(Path.Combine(root,name),1_048_576));
        // Direct Start must NEVER invent mod order from an empty list. The
        // original Kova launch profile is the source of Game/Query and mod
        // order; if missing, the ZIP-backed importer remains the alternative.
        byte[] launchBytes=held.Read(Path.Combine(root,"launch-profiles-v1.json"),
            1_048_576);

        var original=LegacyStoreV1LocalReader.Inspect(root);
        if(!original.LocalSnapshotReady||original.Maps.Length!=5)
            throw new InvalidDataException("UM-STORE-DIRECT-ORIGINAL-NOT-READY");
        string tag=Convert.ToHexString(SHA256.HashData(
            Encoding.ASCII.GetBytes("STORE_V1_ORIGINAL_FOUR_FILES:"+
                string.Join(":",Names.Select(n=>Convert.ToHexString(
                    SHA256.HashData(files[n])))))));
        if(!string.Equals(tag,original.OriginalFourFilesTag,StringComparison.Ordinal))
            throw new IOException("UM-STORE-DIRECT-ORIGINAL-METADATA-CHANGED");

        using var clusterDoc=VaultReadSnapshot.Json(files,"cluster-v1.json");
        if(!clusterDoc.RootElement.TryGetProperty("members",out var members) ||
           members.ValueKind!=JsonValueKind.Array||
           members.GetArrayLength() is <5 or >32)
            throw new InvalidDataException("UM-STORE-DIRECT-FIVE-MAPS-REQUIRED");
        // The local reader has already validated the lock's five selected
        // historical GUIDs against the complete current roster. Never
        // incorporate additional local event maps into the old Store owner.
        var selectedMapIds=original.Maps.Select(x=>x.MapId).ToHashSet();
        var allMemberIds=new HashSet<Guid>();
        foreach(var member in members.EnumerateArray())
            if(!Guid.TryParseExact(Text(member,"id"),"D",out var memberId)||
               memberId==Guid.Empty||!allMemberIds.Add(memberId))
                throw new InvalidDataException("UM-STORE-DIRECT-MAP-ID-INVALID");
        if(!selectedMapIds.IsSubsetOf(allMemberIds))
            throw new InvalidDataException("UM-STORE-DIRECT-ORIGINAL-SELECTION-CHANGED");
        using var launchDoc=VaultReadSnapshot.Json(new Dictionary<string,byte[]>{
            ["launch-profiles-v1.json"]=launchBytes},"launch-profiles-v1.json");
        var launchRoot=launchDoc.RootElement;
        var version=Need(launchRoot,"version");
        var rows=Need(launchRoot,"rows");
        if(version.ValueKind!=JsonValueKind.Number||
           !version.TryGetInt32(out int schema)||schema!=1||
           rows.ValueKind!=JsonValueKind.Array||
           rows.GetArrayLength()<5||rows.GetArrayLength()>32)
            throw new InvalidDataException("UM-STORE-DIRECT-LAUNCH-SCHEMA");

        var byId=new Dictionary<Guid,JsonElement>();
        foreach(var item in rows.EnumerateArray())
        {
            if(!Guid.TryParseExact(Text(item,"id"),"D",out var id)||
               id==Guid.Empty||!allMemberIds.Contains(id)||
               !byId.TryAdd(id,item))
                throw new InvalidDataException("UM-STORE-DIRECT-LAUNCH-MAP-ID");
        }

        var selected=new List<LegacyStoreV1OriginalMap>();
        var ids=new HashSet<Guid>();
        var mapKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports=new HashSet<int>();
        foreach(var row in original.Maps)
        {
            Guid id=row.MapId;
            if(!ids.Add(id)||!byId.TryGetValue(id,out var source))
                throw new InvalidDataException("UM-STORE-DIRECT-MAP-ID-INVALID");
            string name=row.MapKey,installation=row.OriginalRoot;
            if(name.Length is <1 or >80||
               !name.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-' or '.')||
               !mapKeys.Add(name)||installation.Length is <3 or >1000 ||
               !Path.IsPathFullyQualified(installation)||
               installation.StartsWith(@"\\",StringComparison.Ordinal)||
               installation.Any(char.IsControl)||
               !string.Equals(Text(source,"root"),installation,StringComparison.Ordinal)||
               !string.Equals(Text(Need(source,"launch"),"map"),name,StringComparison.Ordinal))
                throw new InvalidDataException("UM-STORE-DIRECT-LOCAL-ROOT-INVALID");
            var launch=Need(source,"launch");
            int game=Port(launch,"gamePort"),query=Port(launch,"queryPort");
            if(game==query||!ports.Add(game)||!ports.Add(query))
                throw new InvalidDataException("UM-STORE-DIRECT-DUPLICATE-PORT");
            string[] enabled=Mods(launch,"mods"),passive=Mods(launch,"passiveMods");
            if(passive.Except(enabled,StringComparer.Ordinal).Any())
                throw new InvalidDataException("UM-STORE-DIRECT-PASSIVE-NOT-ENABLED");
            selected.Add(new(id,name,installation,game,query,enabled,passive));
        }
        // Additional maps need an independent onboarding path. Expose
        // original GUID, phase/session comparison and validated launch data
        // without placing them in the five-map authority collection.
        var additional=new List<LegacyStoreV1AdditionalMapEvidence>();
        foreach(var row in original.AdditionalMaps)
        {
            int? game=null,query=null;
            string[]? enabled=null,passive=null;
            string state="launch-missing";
            if(byId.TryGetValue(row.MapId,out var source))
            {
                try
                {
                    var launch=Need(source,"launch");
                    if(!Path.IsPathFullyQualified(row.OriginalRoot)||
                       row.OriginalRoot.StartsWith(@"\\",StringComparison.Ordinal)||
                       row.OriginalRoot.Any(char.IsControl)||
                       !string.Equals(Text(source,"root"),row.OriginalRoot,StringComparison.Ordinal)||
                       Text(launch,"map")!=row.MapKey)
                        throw new InvalidDataException("UM-STORE-EXTRA-LAUNCH-ROOT");
                    game=Port(launch,"gamePort");query=Port(launch,"queryPort");
                    enabled=Mods(launch,"mods");passive=Mods(launch,"passiveMods");
                    if(game==query||ports.Contains(game.Value)||ports.Contains(query.Value)||
                       passive.Except(enabled,StringComparer.Ordinal).Any())
                        throw new InvalidDataException("UM-STORE-EXTRA-LAUNCH-CONFLICT");
                    state="source-launch-matched";
                }
                catch(InvalidDataException)
                {game=null;query=null;enabled=null;passive=null;state="launch-conflicting";}
            }
            additional.Add(new(row.MapId,row.MapKey,row.OriginalRoot,row.Active,
                row.SessionMatches,row.BackupIdentityPresent,game,query,enabled,passive,state)
                {SessionBinding=row.SessionBinding});
        }
        var collisions=additional.Where(x=>x.GamePort.HasValue&&x.QueryPort.HasValue)
            .SelectMany(x=>new[]{x.GamePort!.Value,x.QueryPort!.Value})
            .GroupBy(x=>x).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
        var reviewed=additional.Select(x=>
            (x.GamePort.HasValue&&collisions.Contains(x.GamePort.Value)||
             x.QueryPort.HasValue&&collisions.Contains(x.QueryPort.Value))?
            x with{GamePort=null,QueryPort=null,EnabledMods=null,PassiveMods=null,
                LaunchState="launch-conflicting"}:x).ToArray();
        var after=LegacyStoreV1LocalReader.Inspect(root);
        if(!after.LocalSnapshotReady||
           !string.Equals(after.OriginalFourFilesTag,tag,StringComparison.Ordinal))
            throw new IOException("UM-STORE-DIRECT-ORIGINAL-METADATA-CHANGED");
        return new(tag,Convert.ToHexString(SHA256.HashData(launchBytes)),
            selected.ToArray()){
                SelectedHistoricalSubsetOfExpandedRoster=members.GetArrayLength()>5,
                CurrentLocalMemberCount=members.GetArrayLength(),
                AdditionalMaps=reviewed
            };
    }
}
