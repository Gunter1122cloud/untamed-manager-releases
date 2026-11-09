using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// On-demand read-only Windows file-ID witness of the ORIGINAL Store-v1 UNC
/// transfer directory. No marker, session, lock, player, tribe or transfer
/// file is created, deleted or written. A matching UNC spelling alone is NOT
/// a physical match. The witness is short lived and lives only in Manager RAM.
/// </summary>
public sealed record LegacyStoreV1PhysicalTransferWitness(
    string HostId, string SessionTag, string SharedPathTag, string DeviceTag,
    string PhysicalDirectoryTag, DateTimeOffset ObservedUtc,
    string OriginalFourFilesTag, bool OriginalMetadataUnchanged,
    bool ProductionRestoreAllowed,
    bool LiveStoreWriterCertified);

public static class LegacyStoreV1PhysicalTransferEvidence
{
    private static readonly string[] OriginalNames =
    {
        "cluster-v1.json", "cluster-store-v1.json",
        "shared-store-device-v1.json", "shared-store-lock-v1.json"
    };

    private static string Tag(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            value.Trim().ToLowerInvariant())));

    private static bool Sha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static LegacyStoreV1PhysicalTransferWitness Capture(
        string originalLocalProfile, string managerHostId) =>
        Capture(originalLocalProfile, managerHostId,
            StorePathIdentity.LocalDirectoryFingerprint);

    // Used by isolated Windows native tests to simulate an UNC filesystem
    // without creating a network share. Production ONLY uses the real
    // GetFileInformationByHandleEx-based physical directory ID above.
    internal static LegacyStoreV1PhysicalTransferWitness Capture(
        string originalLocalProfile, string managerHostId,
        Func<string, string> physicalDirectoryIdentity)
    {
        ArgumentNullException.ThrowIfNull(physicalDirectoryIdentity);
        if (!Guid.TryParse(managerHostId, out var parsedHost) ||
            parsedHost == Guid.Empty)
            throw new InvalidDataException("UM-STORE-PHYSICAL-HOST-INVALID");
        string local = VaultReadSnapshot.LocalPath(originalLocalProfile, true);
        var reportBefore = LegacyStoreV1LocalReader.Inspect(local);
        if (!reportBefore.LocalSnapshotReady || reportBefore.Maps.Length != 5 ||
            !Sha256(reportBefore.SharedPathTag) ||
            !Sha256(reportBefore.SessionTag) || !Sha256(reportBefore.DeviceTag) ||
            !Sha256(reportBefore.OriginalFourFilesTag))
            throw new InvalidDataException("UM-STORE-PHYSICAL-ORIGINAL-NOT-VERIFIED");

        // Hold four ORIGINAL metadata files open with FileShare.Read while the
        // SMB directory ID is observed; bytes are read, never modified.
        using var held = new VaultReadSnapshot();
        var original = OriginalNames.ToDictionary(name => name,
            name => held.Read(Path.Combine(local, name), 1_048_576),
            StringComparer.Ordinal);
        using var doc = VaultReadSnapshot.Json(original, "cluster-v1.json");
        string path = VaultReadSnapshot.Text(doc.RootElement, "path");
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) ||
            path.Length > 1000 || path.Any(char.IsControl))
            throw new InvalidDataException("UM-STORE-PHYSICAL-UNC-REQUIRED");
        string originalPathTag = Tag(path.Replace('/', '\\').TrimEnd('\\'));
        if (!string.Equals(originalPathTag, reportBefore.SharedPathTag,
            StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-PHYSICAL-UNC-SOURCE-CHANGED");

        string directoryTag = physicalDirectoryIdentity(path);
        if (!Sha256(directoryTag) || directoryTag.All(c => c == '0'))
            throw new InvalidDataException("UM-STORE-PHYSICAL-DIRECTORY-ID-MISSING");
        var reportAfter = LegacyStoreV1LocalReader.Inspect(local);
        if (!reportAfter.LocalSnapshotReady ||
            reportAfter.SessionTag != reportBefore.SessionTag ||
            reportAfter.DeviceTag != reportBefore.DeviceTag ||
            reportAfter.SharedPathTag != reportBefore.SharedPathTag ||
            reportAfter.OriginalFourFilesTag != reportBefore.OriginalFourFilesTag)
            throw new IOException("UM-STORE-PHYSICAL-ORIGINAL-CHANGED");

        // Check all four original files, not only their session/path/device
        // tags. A changed member selection is also a hard blocker.
        using var second = new VaultReadSnapshot();
        foreach (string name in OriginalNames)
        {
            byte[] current = second.Read(Path.Combine(local, name), 1_048_576);
            if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(original[name]), SHA256.HashData(current)))
                throw new IOException("UM-STORE-PHYSICAL-ORIGINAL-CHANGED");
        }
        return new(parsedHost.ToString("N"), reportAfter.SessionTag,
            reportAfter.SharedPathTag, reportAfter.DeviceTag, directoryTag,
            DateTimeOffset.UtcNow, reportAfter.OriginalFourFilesTag,
            true, false, false);
    }
}
