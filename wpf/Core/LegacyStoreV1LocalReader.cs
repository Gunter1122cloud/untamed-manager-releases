using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Explicit on-demand, read-only inspection of the original local Store-v1
/// metadata at an operator-selected or known legacy profile directory.
/// Uses the existing Windows stable-handle reader: four files are opened
/// without permitting concurrent writers, never created, renamed or repaired.
/// UNC/SMB and remote host data are NOT opened here.
/// </summary>
public static class LegacyStoreV1LocalReader
{
    private static readonly string[] Names={
        "cluster-v1.json","cluster-store-v1.json",
        "shared-store-device-v1.json","shared-store-lock-v1.json"
    };
    public sealed record MapEvidence(string MapKey,bool Active,bool SessionMatches,
        bool RootMatches,bool Selected,bool BackupIdentityPresent);
    public sealed record Report(bool LocalFilesVerified,bool ClusterHashVerified,
        bool LocalLockVerified,bool AllLocalMapsActive,bool LocalSnapshotReady,
        bool LiveTwoHostVerified,bool CharacterTravelVerified,
        MapEvidence[] Maps,string Evidence);

    public static Report Inspect(string localDirectory)
    {
        string root=VaultReadSnapshot.LocalPath(localDirectory,true);
        if(!Directory.Exists(root))
            throw new DirectoryNotFoundException("UM-LOCAL-STORE-DIRECTORY-MISSING");
        var data=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        using(var source=new VaultReadSnapshot())
            foreach(string name in Names)
                data.Add(name,source.Read(Path.Combine(root,name),1_048_576));
        // This reader does not open, enumerate or mutate any historical map
        // save, transfer directory, remote host or shared coordination path.
        using var clusterDoc=VaultReadSnapshot.Json(data,"cluster-v1.json");
        using var storeDoc=VaultReadSnapshot.Json(data,"cluster-store-v1.json");
        using var deviceDoc=VaultReadSnapshot.Json(data,"shared-store-device-v1.json");
        using var lockDoc=VaultReadSnapshot.Json(data,"shared-store-lock-v1.json");
        var cluster=clusterDoc.RootElement;
        var store=storeDoc.RootElement;
        var device=deviceDoc.RootElement;
        var locked=lockDoc.RootElement;
        if(Text(cluster,"id")!=VaultProtocol.ClusterId||
           Text(cluster,"mode")!="shared"||
           !Text(cluster,"path").StartsWith(@"\\",StringComparison.Ordinal)||
           !Schema(store)||!Schema(device)||!Schema(locked)||
           !Guid.TryParse(Text(device,"id"),out _)||
           !Guid.TryParse(Text(locked,"session"),out var session))
            throw new InvalidDataException("UM-LOCAL-STORE-IDENTITY");
        var members=Required(cluster,"members");
        var records=Required(store,"maps");
        var selected=Required(locked,"selected");
        if(members.ValueKind!=JsonValueKind.Array||
           records.ValueKind!=JsonValueKind.Object||
           selected.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("UM-LOCAL-STORE-SHAPE");
        var memberArray=members.EnumerateArray().ToArray();
        var selection=selected.EnumerateArray().Select(x=>
            x.ValueKind==JsonValueKind.String?x.GetString()??"":"").ToArray();
        if(memberArray.Length!=5||records.EnumerateObject().Count()!=5||
           selection.Length!=5 ||
           selection.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=5)
            throw new InvalidDataException("UM-LOCAL-STORE-LOCAL-MAP-COUNT");
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence=new List<MapEvidence>();
        foreach(var member in memberArray)
        {
            string id=Text(member,"id"),name=Text(member,"map"),install=Text(member,"root");
            if(!Guid.TryParseExact(id,"D",out _)||!ids.Add(id)||
               !SafeMap(name)||!mapNames.Add(name)||
               string.IsNullOrWhiteSpace(install)||
               !records.TryGetProperty(id,out var record))
                throw new InvalidDataException("UM-LOCAL-STORE-MAP-BINDING");
            bool active=Text(record,"phase")=="active";
            bool sameSession=Guid.TryParse(Text(record,"sharedSession"),out var existing)&&
                existing==session;
            bool sameRoot=string.Equals(Text(record,"root"),install,
                StringComparison.OrdinalIgnoreCase)&&Text(record,"map")==name;
            bool marked=selection.Contains(id,StringComparer.OrdinalIgnoreCase);
            bool backup=Guid.TryParse(Text(record,"backupId"),out _);
            evidence.Add(new(name,active,sameSession,sameRoot,marked,backup));
        }
        if(!ids.SetEquals(selection))
            throw new InvalidDataException("UM-LOCAL-STORE-SELECTED-IDS");
        bool hash=StoreV1Fingerprint.Matches(cluster,Text(locked,"configHash")) &&
            StoreV1Fingerprint.Matches(Required(locked,"config"),Text(locked,"configHash"));
        bool sameConfig=StoreV1Fingerprint.Compute(
                JsonSerializer.SerializeToElement(new{data=cluster})) ==
            StoreV1Fingerprint.Compute(
                JsonSerializer.SerializeToElement(new{data=Required(locked,"config")}));
        bool lockVerified=hash&&sameConfig&&
            selection.Length==5&&evidence.All(m=>m.Selected);
        bool localActive=evidence.All(m=>
            m.Active&&m.SessionMatches&&m.RootMatches&&m.BackupIdentityPresent);
        // A local five-map Store copy cannot certify the other host, even if
        // all local records are ACTIVE and the local config hash matches.
        return new(true,hash,lockVerified,localActive,lockVerified&&localActive,
            false,false,evidence.ToArray(),
            "ORIGINAL_LOCAL_STORE_V1_FOUR_FILES_READ_ONLY_NO_PEER_CERTIFICATION");
    }

    private static bool SafeMap(string s)=>
        s.Length is >0 and <=80&&s.All(c=>
            char.IsAsciiLetterOrDigit(c)||c is '_' or '-' or '.');
    private static JsonElement Required(JsonElement v,string name)=>
        v.ValueKind==JsonValueKind.Object&&v.TryGetProperty(name,out var p)?
            p:throw new InvalidDataException("UM-LOCAL-STORE-REQUIRED-FIELD");
    private static string Text(JsonElement v,string name)=>VaultReadSnapshot.Text(v,name);
    private static bool Schema(JsonElement v)=>
        v.ValueKind==JsonValueKind.Object&&
        v.TryGetProperty("schema",out var value)&&
        value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out int n)&&n==1;
}
