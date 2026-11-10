using System;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

/// <summary>
/// Optional Store-v1 metadata evidence carried over the already authenticated
/// and encrypted, strictly read-only two-Manager LAN status protocol.
/// No original UNC path, session ID, device ID, player, credential, character
/// or original application file bytes are sent over LAN.
/// </summary>
public sealed record LegacyStoreV1PeerProof(
    bool SourcePresent,bool LocalMetadataValid,int ActiveMapCount,
    string SessionTag,string SharedPathTag,string DeviceTag,
    bool LiveWriterCertified,bool CharacterTravelVerified,string Evidence)
{
    // Ephemeral, explicit read-only Windows file-ID witness. Never present
    // in a status packet unless the operator specifically probes the UNC
    // directory on that host. No raw UNC names or Store IDs leave the host.
    public string PhysicalDirectoryTag {get;init;}="";
    public DateTimeOffset? PhysicalObservedUtc {get;init;}
    public string OriginalFourFilesTag {get;init;}="";

    // Only explicitly requested historical, MANAGER-LOCAL SaveComplete
    // receipts. This is neither live ASA quiescence nor Store writer proof.
    public string LocalSaveReceiptOriginalFourTag {get;init;}="";
    public string LocalSaveReceiptSessionTag {get;init;}="";
    public DateTimeOffset? LocalSaveReceiptObservedUtc {get;init;}
    public int RecentLocalSaveReceipts {get;init;}
    public bool AllFiveRecentLocalSaveReceipts {get;init;}

    public static LegacyStoreV1PeerProof Unavailable {get;}=new(
        false,false,0,"","","",false,false,"STORE_V1_ORIGINAL_LOCAL_PROFILE_ABSENT");
    public static LegacyStoreV1PeerProof Invalid {get;}=new(
        true,false,0,"","","",false,false,"STORE_V1_ORIGINAL_LOCAL_PROFILE_INVALID");

    public static LegacyStoreV1PeerProof FromLocal(string directory)
    {
        if(string.IsNullOrWhiteSpace(directory)||!Directory.Exists(directory))
            return Unavailable;
        try
        {
            var report=LegacyStoreV1LocalReader.Inspect(directory);
            return new(true,report.LocalSnapshotReady,
                report.Maps.Count(m=>m.Active&&m.SessionMatches&&m.RootMatches&&m.Selected),
                report.SessionTag,report.SharedPathTag,report.DeviceTag,
                false,false,"ORIGINAL_FOUR_LOCAL_STORE_FILES_HASHED_READ_ONLY")
            {
                OriginalFourFilesTag=report.OriginalFourFilesTag
            };
        }
        catch(Exception)
        {
            // Never return paths, original filenames, or exception text on LAN.
            return Invalid;
        }
    }

    public static LegacyStoreV1PeerProof FromCurrentUserLegacyProfile()=>
        FromLocal(Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData),"Kova"));
}

public sealed record LegacyStoreV1PairObservation(
    bool BothOriginalProfilesPresent,bool SameSharedSession,bool SameSharedPath,
    bool DistinctDeviceIds,bool BothHaveFiveActiveMaps,
    bool CopiedMetadataPairMatches,bool LiveWriterCertified,
    bool CharacterTravelVerified,string StatusCode)
{
    public bool BothPhysicalWitnessesFresh {get;init;}
    public bool SamePhysicalTransferDirectory {get;init;}
    public bool BothHistoricalLocalSaveWitnessesFresh {get;init;}
    public bool BothHaveFiveRecentHistoricalLocalSaveReceipts {get;init;}
    // Authenticated Manager host identity, NOT original Kova device/session.
    // Only populated if exactly one fresh N-host peer matches original
    // Store metadata. Multiple possible owners must fail closed.
    public string OriginalPeerHostId {get;init;}="";
}

/// <summary>
/// Historical source parity only. LAN status does not prove an exclusive
/// SMB writer lease, character travel, SaveComplete, or authoritative
/// ownership of any shared Store-v1 directory.
/// </summary>
public static class LegacyStoreV1PairComparison
{
    private static bool Hash(string? s)=>s?.Length==64&&s.All(Uri.IsHexDigit)==true;
    /// <summary>
    /// N-host LAN views cannot use RemoteSnapshot (the FIRST online host):
    /// after adding a third, unrelated Manager it may precede the original
    /// Kova second peer in configuration order. Choose one and only one
    /// authenticated/fresh Manager that matches the ORIGINAL five-map,
    /// shared-session, shared-path, distinct-device evidence instead.
    /// This is historical read-only metadata matching, never writer proof.
    /// </summary>
    public static LegacyStoreV1PairObservation CompareAcrossPeers(
        LegacyStoreV1PeerProof? local,IEnumerable<LanPeerObservation> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var fresh=peers.Where(p=>p!=null&&p.IsFresh&&p.Snapshot!=null&&
                Guid.TryParse(p.HostId,out var peerId)&&peerId!=Guid.Empty&&
                Guid.TryParse(p.Snapshot.HostId,out var packetId)&&
                packetId==peerId&&
                StatusFreshness.IsFresh(p.Snapshot.ObservedUtc,DateTimeOffset.UtcNow))
            .ToArray();
        if(local?.SourcePresent!=true||local.LocalMetadataValid!=true)
            return Compare(local,null,fresh.Length>0);
        var matching=fresh.Select(p=>new{
                Peer=p,Result=Compare(local,p.Snapshot!.OriginalStoreV1,true)
            }).Where(x=>x.Result.CopiedMetadataPairMatches).ToArray();
        if(matching.Length==1)
            return matching[0].Result with{
                OriginalPeerHostId=Guid.Parse(matching[0].Peer.HostId).ToString("N")
            };
        if(matching.Length>1)
            return Compare(local,null,false) with{
                StatusCode="STORE_ORIGINAL_PEER_AMBIGUOUS"
            };
        if(fresh.Length==1)
            return Compare(local,fresh[0].Snapshot!.OriginalStoreV1,true);
        return Compare(local,null,false) with{
            StatusCode=fresh.Length==0?
                "STORE_PEER_NOT_FRESH":"STORE_ORIGINAL_PEER_NOT_FOUND"
        };
    }

    public static LegacyStoreV1PairObservation Compare(
        LegacyStoreV1PeerProof? local,LegacyStoreV1PeerProof? remote,bool remoteFresh)
    {
        bool present=local?.SourcePresent==true && remote?.SourcePresent==true;
        bool valid=present && remoteFresh &&
            local!.LocalMetadataValid && remote!.LocalMetadataValid &&
            Hash(local.SessionTag)&&Hash(remote.SessionTag)&&
            Hash(local.SharedPathTag)&&Hash(remote.SharedPathTag)&&
            Hash(local.DeviceTag)&&Hash(remote.DeviceTag);
        bool session=valid&&string.Equals(local!.SessionTag,remote!.SessionTag,
            StringComparison.Ordinal);
        bool shared=valid&&string.Equals(local!.SharedPathTag,remote!.SharedPathTag,
            StringComparison.Ordinal);
        bool distinct=valid&&!string.Equals(local!.DeviceTag,remote!.DeviceTag,
            StringComparison.Ordinal);
        bool five=valid&&local!.ActiveMapCount==5&&remote!.ActiveMapCount==5;
        bool matching=present&&valid&&session&&shared&&distinct&&five;
        DateTimeOffset now=DateTimeOffset.UtcNow;
        bool FreshPhysical(LegacyStoreV1PeerProof? proof)=>
            proof?.PhysicalObservedUtc is DateTimeOffset utc &&
            utc>=now.AddMinutes(-5)&&utc<=now.AddMinutes(1)&&
            Hash(proof.PhysicalDirectoryTag)&&
            Hash(proof.OriginalFourFilesTag)&&
            !proof.PhysicalDirectoryTag.All(x=>x=='0');
        // A matching Windows file ID alone is NOT sufficient: the original
        // shared Store protocol also pins one identical UNC spelling on both
        // peers. Alternate shares/aliases cannot override a path mismatch.
        bool bothPhysical=valid&&session&&shared&&distinct&&five&&
            FreshPhysical(local)&&FreshPhysical(remote);
        bool samePhysical=bothPhysical&&string.Equals(
            local!.PhysicalDirectoryTag,remote!.PhysicalDirectoryTag,
            StringComparison.Ordinal);
        // Recent local SaveComplete receipts are historical observations.
        // Cross-host comparison is permitted only with an independently
        // verified, fresh same-directory physical witness on BOTH peers.
        // Even then, this never asserts Store writer quiescence.
        bool FreshLocalSave(LegacyStoreV1PeerProof? proof)=>
            proof?.LocalSaveReceiptObservedUtc is DateTimeOffset at &&
            at>=now.AddMinutes(-5)&&at<=now.AddMinutes(1)&&
            proof.AllFiveRecentLocalSaveReceipts&&
            proof.RecentLocalSaveReceipts==5&&
            Hash(proof.LocalSaveReceiptOriginalFourTag)&&
            string.Equals(proof.LocalSaveReceiptOriginalFourTag,
                proof.OriginalFourFilesTag,StringComparison.Ordinal)&&
            string.Equals(proof.LocalSaveReceiptSessionTag,
                proof.SessionTag,StringComparison.Ordinal);
        bool bothSaveFresh=samePhysical&&FreshLocalSave(local)&&FreshLocalSave(remote);
        string code=!remoteFresh?"STORE_PEER_NOT_FRESH":
            !present?"STORE_LOCAL_OR_REMOTE_PROFILE_MISSING":
            !valid?"STORE_PEER_METADATA_INVALID":
            !session?"STORE_PEER_SESSION_DIFFERS":
            !shared?"STORE_PEER_UNC_DIFFERS":
            !distinct?"STORE_PEER_SAME_DEVICE":
            !five?"STORE_PEER_LOCAL_MAP_COUNT_INVALID":
            "HISTORICAL_TWO_HOST_METADATA_MATCH_ONLY";
        return new(present,session,shared,distinct,five,matching,
            false,false,code)
        {
            BothPhysicalWitnessesFresh=bothPhysical,
            SamePhysicalTransferDirectory=samePhysical,
            BothHistoricalLocalSaveWitnessesFresh=bothSaveFresh,
            BothHaveFiveRecentHistoricalLocalSaveReceipts=bothSaveFresh
        };
    }
}
