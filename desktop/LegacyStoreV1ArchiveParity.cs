using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Compares the four ORIGINAL Store-v1 local metadata files from a verified
/// 39-file Manager archive against the matching four files in the operator's
/// own former Manager AppData folder. Neither source is extracted or modified.
/// Exact byte parity is not proof of live shared writer ownership or travel.
/// </summary>
public static class LegacyStoreV1ArchiveParity
{
    private static readonly string[] Names={
        "cluster-v1.json","cluster-store-v1.json",
        "shared-store-device-v1.json","shared-store-lock-v1.json"
    };
    public sealed record FileCheck(string Kind,bool ExactlySame);
    public sealed record Result(bool ArchiveVerified,bool LocalStoreActive,
        bool AllFourExactlyMatch,bool LiveTwoHostVerified,
        bool CharacterTravelVerified,FileCheck[] Files,string Evidence);

    public static Result Inspect(string verifiedArchive,string originalLocalDirectory)
    {
        if(string.IsNullOrWhiteSpace(verifiedArchive) ||
           !verifiedArchive.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-STORE-ARCHIVE-PARITY-ZIP-REQUIRED");
        string local=VaultReadSnapshot.LocalPath(originalLocalDirectory,true);
        if(!Directory.Exists(local))
            throw new DirectoryNotFoundException("UM-STORE-ARCHIVE-PARITY-LOCAL-MISSING");

        // Stable, read-shared Windows handles exclude concurrent write/delete
        // while the original four and archive bytes are captured.
        using var snapshot=new VaultReadSnapshot();
        byte[] archived=snapshot.Read(verifiedArchive,50_000_000);
        var files=VaultReadSnapshot.Archive(archived,"README.txt");
        var comparisons=new List<FileCheck>();
        foreach(string name in Names)
        {
            if(!files.TryGetValue(VaultProtocol.HistoricalManagerPrefix+name,
                out byte[]? inArchive))
                throw new InvalidDataException("UM-STORE-ARCHIVE-PARITY-FILE-MISSING");
            byte[] localBytes=snapshot.Read(Path.Combine(local,name),1_048_576);
            comparisons.Add(new(name.Replace("-v1.json","").Replace(".json",""),
                inArchive.Length==localBytes.Length &&
                CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(inArchive),SHA256.HashData(localBytes))));
        }

        // Performs full manifest, 5+5 map and Store session validation on the
        // same archive file, which remains locked against concurrent writers.
        var official=UntamedManagerArchiveInspector.Inspect(verifiedArchive);
        bool sameSource=string.Equals(official.SourceSha256,
            Convert.ToHexString(SHA256.HashData(archived)),StringComparison.OrdinalIgnoreCase);
        if(!sameSource||official.VerifiedFiles!=39)
            throw new InvalidDataException("UM-STORE-ARCHIVE-PARITY-UNVERIFIED");
        // This observation is separately validated; it is NEVER a start permit.
        var localProof=LegacyStoreV1LocalReader.Inspect(local);
        return new(true,localProof.LocalSnapshotReady,
            comparisons.All(x=>x.ExactlySame),
            false,false,comparisons.ToArray(),
            "ORIGINAL_LOCAL_FOUR_FILE_BYTE_PARITY_OFFLINE_NO_LIVE_WRITER");
    }
}
