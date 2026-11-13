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
public sealed record LegacyStoreV1OriginalLocalSelection(string FourFilesSha256,
    string LaunchProfileSha256,LegacyStoreV1OriginalMap[] Maps);

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
           members.ValueKind!=JsonValueKind.Array||members.GetArrayLength()!=5)
            throw new InvalidDataException("UM-STORE-DIRECT-FIVE-MAPS-REQUIRED");
        using var launchDoc=VaultReadSnapshot.Json(new Dictionary<string,byte[]>{
            ["launch-profiles-v1.json"]=launchBytes},"launch-profiles-v1.json");
        var launchRoot=launchDoc.RootElement;
        var version=Need(launchRoot,"version");
        var rows=Need(launchRoot,"rows");
        if(version.ValueKind!=JsonValueKind.Number||
           !version.TryGetInt32(out int schema)||schema!=1||
           rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()!=5)
            throw new InvalidDataException("UM-STORE-DIRECT-LAUNCH-SCHEMA");

        var byId=new Dictionary<Guid,JsonElement>();
        foreach(var item in rows.EnumerateArray())
        {
            if(!Guid.TryParseExact(Text(item,"id"),"D",out var id)||
               id==Guid.Empty||!byId.TryAdd(id,item))
                throw new InvalidDataException("UM-STORE-DIRECT-LAUNCH-MAP-ID");
        }

        var selected=new List<LegacyStoreV1OriginalMap>();
        var ids=new HashSet<Guid>();
        var mapKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports=new HashSet<int>();
        foreach(var row in members.EnumerateArray())
        {
            if(!Guid.TryParseExact(Text(row,"id"),"D",out var id)||
               id==Guid.Empty||!ids.Add(id)||!byId.TryGetValue(id,out var source))
                throw new InvalidDataException("UM-STORE-DIRECT-MAP-ID-INVALID");
            string name=Text(row,"map"),installation=Text(row,"root");
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
        var after=LegacyStoreV1LocalReader.Inspect(root);
        if(!after.LocalSnapshotReady||
           !string.Equals(after.OriginalFourFilesTag,tag,StringComparison.Ordinal))
            throw new IOException("UM-STORE-DIRECT-ORIGINAL-METADATA-CHANGED");
        return new(tag,Convert.ToHexString(SHA256.HashData(launchBytes)),
            selected.ToArray());
    }
}
