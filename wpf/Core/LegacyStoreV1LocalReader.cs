using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        bool RootMatches,bool Selected,bool BackupIdentityPresent)
    {
        // Original source GUID never serialized into a LAN status packet.
        public Guid MapId {get;init;}
        public string OriginalRoot {get;init;}="";
        // Source-field classification only, NEVER independent-conversion proof.
        public string SessionBinding {get;init;}="unverified";
    }
    public sealed record Report(bool LocalFilesVerified,bool ClusterHashVerified,
        bool LocalLockVerified,bool AllLocalMapsActive,bool LocalSnapshotReady,
        bool LiveTwoHostVerified,bool CharacterTravelVerified,
        MapEvidence[] Maps,string Evidence)
    {
        // Opaque SHA-256 tags for authenticated read-only LAN peer parity.
        // Original UNC roots, device identities and session IDs stay local.
        public string SessionTag {get;init;}="";
        public string SharedPathTag {get;init;}="";
        public string DeviceTag {get;init;}="";
        // Complete four-file byte identity, including original map members,
        // ordered selection and backup IDs; never expose the source bytes.
        public string OriginalFourFilesTag {get;init;}="";
        public string OriginalMapRosterTag {get;init;}="";
        // Checked current member count, independent of the five selected
        // historical writer GUIDs; never grants Store writer ownership.
        public int CurrentLocalMemberCount {get;init;}
        // Supplemental metadata is evidence only, not part of Maps or
        // the immutable original Store ownership contract.
        public MapEvidence[] AdditionalMaps {get;init;}=Array.Empty<MapEvidence>();
    }
    private static string Tag(string input)=>Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(input.Trim().ToLowerInvariant())));

    // A later event map may be present in the current local cluster without
    // becoming a member of the ORIGINAL five-map Store-v1 writer selection.
    // This exception is valid only when the lock's own configuration hash
    // matches the original five members, in the original order, and those
    // exact member objects still occur in the current cluster in that order.
    // No new Store session, ownership or remote peer evidence is inferred.
    internal static bool MatchesPinnedHistoricalSubset(
        JsonElement current,JsonElement pinned,string[] selection)
    {
        if(Text(current,"id")!=Text(pinned,"id")||
           Text(current,"mode")!=Text(pinned,"mode")||
           Text(current,"path")!=Text(pinned,"path")||
           !pinned.TryGetProperty("members",out var original)||
           original.ValueKind!=JsonValueKind.Array||
           original.GetArrayLength()!=5)return false;
        var locked=original.EnumerateArray().ToArray();
        if(!locked.Select(x=>Text(x,"id")).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(selection))return false;
        var now=Required(current,"members").EnumerateArray()
            .Where(x=>selection.Contains(Text(x,"id"),
                StringComparer.OrdinalIgnoreCase)).ToArray();
        return now.Length==5&&locked.Zip(now).All(pair=>
            StoreV1Fingerprint.Compute(pair.First)==
            StoreV1Fingerprint.Compute(pair.Second));
    }

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
           !Guid.TryParse(Text(device,"id"),out var deviceId)||
           deviceId==Guid.Empty||
           !Guid.TryParse(Text(locked,"session"),out var session)||
           session==Guid.Empty)
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
        // Exactly five ORIGINAL selected map IDs, but a bounded number of
        // current local members may include independent event maps.
        if(memberArray.Length is <5 or >32||
           records.EnumerateObject().Count()!=memberArray.Length||
           selection.Length!=5 ||
           selection.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=5)
            throw new InvalidDataException("UM-LOCAL-STORE-LOCAL-MAP-COUNT");
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var memberRoots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence=new List<MapEvidence>();
        var additional=new List<MapEvidence>();
        foreach(var member in memberArray)
        {
            string id=Text(member,"id"),name=Text(member,"map"),install=Text(member,"root");
            if(!Guid.TryParseExact(id,"D",out var memberId)||
               memberId==Guid.Empty||!ids.Add(id)||
               !SafeMap(name)||
               (selection.Contains(id,StringComparer.OrdinalIgnoreCase)&&!mapNames.Add(name))||
               string.IsNullOrWhiteSpace(install)||
               !memberRoots.Add(name+"|"+install.TrimEnd('\\','/'))||
               !records.TryGetProperty(id,out var record))
                throw new InvalidDataException("UM-LOCAL-STORE-MAP-BINDING");
            bool active=Text(record,"phase")=="active";
            bool hasSession=record.ValueKind==JsonValueKind.Object&&
                record.TryGetProperty("sharedSession",out var sessionField);
            bool sameSession=hasSession&&sessionField.ValueKind==JsonValueKind.String&&
                Guid.TryParse(sessionField.GetString(),out var existing)&&existing==session;
            string binding=sameSession?"original-session-matched":
                !hasSession?"missing-shared-session":"foreign-or-invalid-session";
            bool sameRoot=string.Equals(Text(record,"root"),install,
                StringComparison.OrdinalIgnoreCase)&&Text(record,"map")==name;
            bool marked=selection.Contains(id,StringComparer.OrdinalIgnoreCase);
            bool backup=Guid.TryParse(Text(record,"backupId"),out var backupId)&&
                backupId!=Guid.Empty;
            // Unselected event maps are not automatically promoted to the
            // original Store session. Their local GUID/root/map binding is
            // still checked so malformed extra metadata cannot be ignored.
            if(!marked&&!sameRoot)
                throw new InvalidDataException("UM-LOCAL-STORE-EXTRA-MAP-BINDING");
            var entry=new MapEvidence(name,active,sameSession,sameRoot,marked,backup)
                {MapId=memberId,OriginalRoot=install,SessionBinding=binding};
            if(marked)evidence.Add(entry);else additional.Add(entry);
        }
        if(!selection.All(ids.Contains)||
           records.EnumerateObject().Any(x=>!ids.Contains(x.Name))||
           evidence.Count!=5)
            throw new InvalidDataException("UM-LOCAL-STORE-SELECTED-IDS");
        var savedConfig=Required(locked,"config");
        string expectedHash=Text(locked,"configHash");
        bool savedHash=StoreV1Fingerprint.Matches(savedConfig,expectedHash);
        bool currentHash=StoreV1Fingerprint.Matches(cluster,expectedHash);
        bool sameConfig=StoreV1Fingerprint.Compute(cluster)==
            StoreV1Fingerprint.Compute(savedConfig);
        bool historicalSubset=memberArray.Length>5&&
            MatchesPinnedHistoricalSubset(cluster,savedConfig,selection);
        bool hash=savedHash&&(currentHash&&sameConfig||historicalSubset);
        bool lockVerified=hash&&evidence.All(m=>m.Selected);
        bool localActive=evidence.All(m=>
            m.Active&&m.SessionMatches&&m.RootMatches&&m.BackupIdentityPresent);
        // Only the five selected historical maps are certified; event maps
        // are present in the full four-file SHA but not in the writer roster.
        // Neither this read nor a saved lock certifies the remote host.
        return new(true,hash,lockVerified,localActive,lockVerified&&localActive,
            false,false,evidence.ToArray(),historicalSubset&&!currentHash?
            "ORIGINAL_FIVE_SELECTED_PINNED_IN_EXPANDED_LOCAL_ROSTER_NO_PEER_CERTIFICATION":
            "ORIGINAL_LOCAL_STORE_V1_FOUR_FILES_READ_ONLY_NO_PEER_CERTIFICATION")
        {
            CurrentLocalMemberCount=memberArray.Length,
            AdditionalMaps=additional.ToArray(),
            SessionTag=Tag(session.ToString("D")),
            SharedPathTag=Tag(Text(cluster,"path").Replace('/','\\').TrimEnd('\\')),
            DeviceTag=Tag(Text(device,"id")),
            OriginalMapRosterTag=LegacyStoreV1RosterIdentity.Tag(
                evidence.Select(m=>(m.MapId,m.MapKey,m.OriginalRoot))),
            OriginalFourFilesTag=Convert.ToHexString(SHA256.HashData(
                Encoding.ASCII.GetBytes("STORE_V1_ORIGINAL_FOUR_FILES:"+
                    string.Join(":",Names.Select(n=>Convert.ToHexString(
                        SHA256.HashData(data[n])))))))
        };
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
