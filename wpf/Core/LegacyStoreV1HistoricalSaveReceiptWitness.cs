using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UntamedManager.Core;

/// <summary>
/// Explicit operator-triggered inspection of the FIVE Manager-owned, durable
/// historical SaveWorld receipts. Uses the original read-only ACTIVE/DONE
/// source guard, including shared coordination evidence, on explicit operator
/// request. Never writes a Legacy transfer file, sends RCON, grants Store writer
/// authority or creates a backup.
/// "Recent" means recent local LOG witnesses, NOT current writer quiescence,
/// an atomic cross-host snapshot, or replay-safe game character travel.
/// </summary>
public sealed record LegacyStoreV1HistoricalSaveReceiptWitness(
    string HostId, string SessionTag, string OriginalFourFilesTag,
    DateTimeOffset ObservedUtc, int RecentCompletedMapReceipts,
    bool AllFiveRecentLocalReceipts, string ReceiptsTag,
    bool SharedWriterCertified, bool BothHostsSaveCompleteCertified,
    bool ProductionRestoreAllowed);

public static class LegacyStoreV1HistoricalSaveReceiptProbe
{
    private static bool Sha(string? s) =>
        s is {Length:64} && s.All(Uri.IsHexDigit);

    public static LegacyStoreV1HistoricalSaveReceiptWitness Capture(
        string managerProfileRoot, string managerHostId,
        LegacyStoreV1PeerProof? originalMetadata,
        IReadOnlyCollection<LocalMap> registeredMaps,
        Func<LocalMap,string> readCurrentOriginalSourceEpoch,
        DateTimeOffset? observedAtUtc=null)
    {
        if(!Guid.TryParse(managerHostId,out var host)||host==Guid.Empty ||
           originalMetadata?.SourcePresent!=true ||
           originalMetadata.LocalMetadataValid!=true ||
           originalMetadata.ActiveMapCount!=5 ||
           !Sha(originalMetadata.SessionTag) ||
           !Sha(originalMetadata.OriginalFourFilesTag))
            throw new InvalidDataException("UM-STORE-SAVE-PAIR-ORIGINAL-NOT-VERIFIED");
        if(registeredMaps==null)
            throw new InvalidDataException("UM-STORE-SAVE-PAIR-MAPS-MISSING");
        ArgumentNullException.ThrowIfNull(readCurrentOriginalSourceEpoch);

        var maps=registeredMaps.Where(x=>x!=null && x.HistoricalStoreProtected)
            .OrderBy(x=>x.Id).ToArray();
        if(maps.Length!=5 ||
           maps.Any(x=>x.Id==Guid.Empty || string.IsNullOrWhiteSpace(x.HistoricalMapKey)) ||
           maps.Select(x=>x.Id).Distinct().Count()!=5 ||
           maps.Select(x=>x.HistoricalMapKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=5)
            throw new InvalidDataException("UM-STORE-SAVE-PAIR-FIVE-ORIGINAL-MAPS-REQUIRED");

        // The probe is deliberately read-only: a fresh receipt cannot be
        // fabricated merely because the source map/world exists on disk.
        var now=observedAtUtc??DateTimeOffset.UtcNow;
        if(now==default)
            throw new InvalidDataException("UM-STORE-SAVE-PAIR-INVALID-TIME");
        int recent=0;
        string? commonOriginalSourceSha=null;
        LocalMap? firstVerifiedMap=null;
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var map in maps)
        {
            var receipt=LegacyStoreV1SaveEvidenceJournal.Inspect(
                managerProfileRoot,map.Id);
            if(receipt?.Phase is "prepared" or "needsOperator")
                throw new InvalidOperationException(
                    "UM-STORE-SAVE-PAIR-UNFINISHED-LOCAL-RECEIPT");
            // An absent, completed-but-stale or not-yet-saved map is NOT an
            // accepted five-map receipt set. It never becomes a Store lease.
            if(receipt?.Phase!="completed" ||
               !receipt.LocalSaveCompleteObserved ||
               receipt.SaveObservedUtc is not DateTimeOffset savedAt ||
               savedAt < now.AddMinutes(-10) || savedAt > now.AddMinutes(1))
                continue;
            if(receipt.MapId!=map.Id || receipt.ProductionRestoreAllowed ||
               receipt.SharedStoreWriterCertified || receipt.BothHostsSaveCompleteCertified)
                throw new InvalidDataException(
                    "UM-STORE-SAVE-PAIR-RECEIPT-MAP-OR-AUTHORITY");
            // The journal's SourceStateSha256 covers the exact original
            // local four files plus both DONE peers and shared operation/
            // attempted records. OriginalFourFilesTag covers ONLY the local
            // four files: these two different hashes must never be directly
            // compared. Re-run the existing original ACTIVE/DONE source guard
            // for each qualifying receipt so FIVE mutually stale receipts
            // cannot certify the current source epoch.
            string currentEpoch=readCurrentOriginalSourceEpoch(map);
            if(!Sha(currentEpoch)||
               !string.Equals(currentEpoch,receipt.OriginalSourceSha256,
                   StringComparison.Ordinal))
                throw new InvalidDataException(
                    "UM-STORE-SAVE-PAIR-ORIGINAL-EPOCH-CHANGED");
            firstVerifiedMap??=map;
            if(commonOriginalSourceSha!=null &&
               !string.Equals(commonOriginalSourceSha,
                   receipt.OriginalSourceSha256,StringComparison.Ordinal))
                throw new InvalidDataException(
                    "UM-STORE-SAVE-PAIR-MIXED-ORIGINAL-SOURCE-EPOCHS");
            commonOriginalSourceSha=receipt.OriginalSourceSha256;
            recent++;
            byte[] bytes=Encoding.UTF8.GetBytes(
                map.Id.ToString("N")+":"+receipt.OperationId.ToString("N")+":"+
                receipt.OriginalSourceSha256+":"+
                savedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            digest.AppendData(BitConverter.GetBytes(bytes.Length));
            digest.AppendData(bytes);
        }
        // Detect a shared-source change during the sequential five-map
        // read. This is a fresh historical witness, never a writer lease;
        // a later source change requires the operator to probe again.
        if(firstVerifiedMap!=null &&
           !string.Equals(readCurrentOriginalSourceEpoch(firstVerifiedMap),
               commonOriginalSourceSha,StringComparison.Ordinal))
            throw new InvalidDataException(
                "UM-STORE-SAVE-PAIR-ORIGINAL-EPOCH-CHANGED");
        return new(host.ToString("N"),originalMetadata.SessionTag,
            originalMetadata.OriginalFourFilesTag,now,recent,
            recent==5,Convert.ToHexString(digest.GetHashAndReset()),
            false,false,false);
    }
}
