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
                false,false,"ORIGINAL_FOUR_LOCAL_STORE_FILES_HASHED_READ_ONLY");
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
    bool CharacterTravelVerified,string StatusCode);

/// <summary>
/// Historical source parity only. LAN status does not prove an exclusive
/// SMB writer lease, character travel, SaveComplete, or authoritative
/// ownership of any shared Store-v1 directory.
/// </summary>
public static class LegacyStoreV1PairComparison
{
    private static bool Hash(string? s)=>s?.Length==64&&s.All(Uri.IsHexDigit)==true;
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
        string code=!remoteFresh?"STORE_PEER_NOT_FRESH":
            !present?"STORE_LOCAL_OR_REMOTE_PROFILE_MISSING":
            !valid?"STORE_PEER_METADATA_INVALID":
            !session?"STORE_PEER_SESSION_DIFFERS":
            !shared?"STORE_PEER_UNC_DIFFERS":
            !distinct?"STORE_PEER_SAME_DEVICE":
            !five?"STORE_PEER_LOCAL_MAP_COUNT_INVALID":
            "HISTORICAL_TWO_HOST_METADATA_MATCH_ONLY";
        return new(present,session,shared,distinct,five,matching,
            false,false,code);
    }
}
