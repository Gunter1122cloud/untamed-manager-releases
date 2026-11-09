using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

public sealed record MultiHostSandboxReplacementReceipt(Guid TransactionId,
    Guid PreviousBatchId, Guid ReplacementBatchId, string RetainedBackupDirectory,
    bool ExplicitOperatorApproval, bool ProductionRestoreAllowed,
    bool ProcessStateChecked, bool StoreWriterOwnershipChecked);

public sealed record MultiHostSandboxReplacementRecoveryStatus(bool NeedsOperator,
    bool CanRollback, Guid? TransactionId, string Phase, string Reason,
    string? QuarantinedReplacementName = null, string? ProtectiveCopyName = null);
public sealed record MultiHostSandboxRecoveryCandidate(string TargetDirectoryName,
    Guid? TransactionId, string Phase, bool NeedsOperator, bool CanRollback,
    string Reason);
public sealed record MultiHostSandboxRecoveryListing(bool Truncated,
    MultiHostSandboxRecoveryCandidate[] Transactions);

/// <summary>
/// Crash-recoverable replacement for an already verified TEMP restore sandbox.
/// It is intentionally incapable of targeting an ASA, Manager, Store-v1, or
/// transfer directory. Live restore remains a separate, uncertified feature.
/// </summary>
public static class MultiHostSandboxRestoreTransaction
{
    private const int CurrentSchema = 1;
    private const string SandboxPrefix = "Untamed-MultiHost-Restore-";
    private const string JournalSuffix = ".replace-transaction-v1.json";
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 32 };

    private sealed record Journal(int Schema, Guid TransactionId, string TargetName,
        string CandidateName, string PreviousName, string RejectedName,
        Guid PreviousBatchId, Guid ReplacementBatchId, string Phase,
        DateTimeOffset UpdatedUtc);
    private sealed record JournalEnvelope(string Payload, string Sha256);

    private static bool Within(string parent, string child)
    {
        string root = Path.TrimEndingDirectorySeparator(parent);
        return child.StartsWith(root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(root, child, StringComparison.OrdinalIgnoreCase);
    }

    private static string FixedLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new InvalidDataException("An explicit fixed local path is required");

        string[] parts = path.Replace('/', '\\').Split('\\');
        if (parts.Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Contains('~')) ||
            (path.Length >= 3 && path[3..].Contains(':')))
            throw new InvalidDataException("Path aliases and alternate streams are rejected");

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? volume = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(volume) || new DriveInfo(volume).DriveType != DriveType.Fixed)
            throw new InvalidDataException("Only fixed local disks are allowed");
        for (DirectoryInfo? parent = new DirectoryInfo(Path.GetDirectoryName(full) ?? full);
             parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked path ancestors are rejected");
        if ((File.Exists(full) || Directory.Exists(full)) &&
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked transaction paths are rejected");
        return full;
    }

    private static string TargetPath(string destination)
    {
        string target = FixedLocal(destination);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string name = Path.GetFileName(target);
        if (!string.Equals(Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(SandboxPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[SandboxPrefix.Length..], "N", out _))
            throw new UnauthorizedAccessException("Replacement is restricted to a named TEMP restore sandbox");
        return target;
    }

    private static string NewSandboxPath()
    {
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        return FixedLocal(Path.Combine(temp, SandboxPrefix + Guid.NewGuid().ToString("N")));
    }

    private static string JournalPath(string target) => FixedLocal(target + JournalSuffix);
    private static string JournalNextPath(string target) => FixedLocal(JournalPath(target) + ".next");
    private static string MutationLeasePath(string target) => FixedLocal(JournalPath(target) + ".lease");

    // Windows closes an abandoned file handle on process exit. DeleteOnClose
    // also removes this adjacent lock file: no permanent stale ownership or
    // manual lock-file deletion after a crash. A distinct Manager process
    // cannot enter the same TEMP replacement/rollback transaction concurrently.
    // This lease NEVER grants live ASA/Store access or ownership of a Kova lock.
    private static FileStream AcquireMutationLease(string target)
    {
        string path = MutationLeasePath(target);
        if (Directory.Exists(path))
            throw new IOException("UM-TEMP-RESTORE-LEASE-BUSY");
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new IOException("UM-TEMP-RESTORE-LEASE-BUSY", ex);
        }
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void WriteDurableNew(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(true);
    }

    private static void Persist(string target, Journal journal)
    {
        string path = JournalPath(target);
        string next = JournalNextPath(target);
        if (File.Exists(next) || Directory.Exists(next))
            throw new IOException("Interrupted journal publication requires operator review");
        string payload = JsonSerializer.Serialize(journal with { UpdatedUtc = DateTimeOffset.UtcNow }, Json);
        string envelope = JsonSerializer.Serialize(new JournalEnvelope(payload, Sha256(payload)), Json);
        WriteDurableNew(next, envelope);
        File.Move(next, path, true);
    }

    private static Journal ReadJournal(string target)
    {
        string path = JournalPath(target);
        if (File.Exists(JournalNextPath(target)) || Directory.Exists(JournalNextPath(target)))
            throw new InvalidDataException("A partial transaction journal requires manual review");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length is < 32 or > 64 * 1024)
            throw new InvalidDataException("Restore transaction journal is missing or invalid");
        JournalEnvelope envelope;
        Journal journal;
        try
        {
            envelope = JsonSerializer.Deserialize<JournalEnvelope>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Restore transaction journal envelope is missing");
            if (!StringComparer.OrdinalIgnoreCase.Equals(Sha256(envelope.Payload), envelope.Sha256))
                throw new InvalidDataException("Restore transaction journal checksum differs");
            journal = JsonSerializer.Deserialize<Journal>(envelope.Payload, Json)
                ?? throw new InvalidDataException("Restore transaction journal payload is missing");
        }
        catch (JsonException) { throw new InvalidDataException("Restore transaction journal JSON is invalid"); }
        ValidateJournal(target, journal);
        return journal;
    }

    private static void ValidateJournal(string target, Journal journal)
    {
        if (journal.Schema != CurrentSchema || journal.TransactionId == Guid.Empty ||
            journal.PreviousBatchId == Guid.Empty || journal.ReplacementBatchId == Guid.Empty ||
            journal.PreviousBatchId == journal.ReplacementBatchId ||
            !StringComparer.Ordinal.Equals(journal.TargetName, Path.GetFileName(target)) ||
            journal.Phase is not ("prepared" or "previousMoved" or "replacementPublished" or
                "committed" or "rolledBack"))
            throw new InvalidDataException("Restore transaction journal identity or phase is invalid");

        string[] names = { journal.CandidateName, journal.PreviousName, journal.RejectedName };
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            names.Any(name => string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name))
            throw new InvalidDataException("Restore transaction artifact names are invalid");
        foreach (string name in names)
        {
            string artifact = TargetPath(Path.Combine(Path.GetDirectoryName(target)!, name));
            if (StringComparer.OrdinalIgnoreCase.Equals(artifact, target))
                throw new InvalidDataException("Restore transaction artifact collides with its target");
        }
    }

    private static MultiHostRestoreSandboxManifest? ReadSandboxIfPresent(string path)
    {
        if (File.Exists(path)) throw new InvalidDataException("A restore transaction artifact is not a directory");
        if (!Directory.Exists(path)) return null;
        return MultiHostClusterBackupArchive.VerifyIsolatedSandbox(path);
    }

    private static MultiHostRestoreSandboxManifest? VerifyIfPresent(string path, Guid expectedBatch)
    {
        var manifest = ReadSandboxIfPresent(path);
        if (manifest == null) return null;
        if (manifest.SourceBatchId != expectedBatch)
            throw new InvalidDataException("A restore transaction artifact has an unexpected batch identity");
        return manifest;
    }

    private static void RequireSameOwnership(MultiHostRestoreSandboxManifest previous,
        MultiHostRestoreSandboxManifest replacement)
    {
        static string MapKey(MultiHostBackupMap map) =>
            $"{map.MapId:N}|{map.HostId.ToUpperInvariant()}|{map.ClusterId.ToUpperInvariant()}";
        if (!previous.Hosts.Select(h => h.HostId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(replacement.Hosts.Select(h => h.HostId)) ||
            !previous.Clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(replacement.Clusters.Select(c => c.ClusterId)) ||
            !previous.Maps.Select(MapKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(replacement.Maps.Select(MapKey)))
            throw new UnauthorizedAccessException("Replacement host, map, or cluster ownership differs from the existing sandbox");
    }

    public static MultiHostRestorePreview PreviewReplacement(string archiveDirectory,
        string destination, MultiHostBackupOwnershipSnapshot registry)
    {
        string target = TargetPath(destination);
        // A completed/partially committed journal may still hold the only
        // verified rollback copy. Never issue a second replacement preview
        // until the first transaction has had explicit operator review.
        if (File.Exists(JournalPath(target)) || File.Exists(JournalNextPath(target)) ||
            Directory.Exists(JournalPath(target)) || Directory.Exists(JournalNextPath(target)))
            throw new IOException("UM-TEMP-RESTORE-PENDING-JOURNAL");
        string archive = FixedLocal(archiveDirectory);
        var preview = MultiHostClusterBackupArchive.PreviewSandboxRestore(archive, registry);
        if (!preview.PreflightPassed) return preview;
        if (!Directory.Exists(target) || File.Exists(target))
            throw new IOException("Only a verified existing TEMP sandbox can be replaced");
        if (Within(target, archive) || Within(archive, target))
            throw new InvalidDataException("Archive and restore target may not overlap");
        var previous = MultiHostClusterBackupArchive.VerifyIsolatedSandbox(target);
        var manifest = MultiHostClusterBackupArchive.Verify(archive);
        if (previous.SourceBatchId == manifest.BatchId)
            throw new InvalidOperationException("The selected batch is already materialized in this TEMP sandbox");
        RequireSameOwnership(previous, new MultiHostRestoreSandboxManifest(
            manifest.SchemaVersion, manifest.BatchId, manifest.CreatedUtc, manifest.Hosts,
            manifest.Clusters, manifest.Maps, manifest.Components, false, "VERIFIED ARCHIVE PREVIEW"));
        return preview with
        {
            Reason = "Archive and existing sandbox ownership match; replacement retains a verified rollback copy in TEMP"
        };
    }

    /// <summary>
    /// Builds and verifies a complete replacement before moving the prior
    /// sandbox aside. A durable operation journal and same-volume directory
    /// renames make the replacement recoverable. No production path is accepted.
    /// </summary>
    public static MultiHostSandboxReplacementReceipt ReplaceVerifiedSandbox(
        string archiveDirectory, string destination,
        MultiHostBackupOwnershipSnapshot registry, bool explicitOperatorApproval,
        CancellationToken cancellationToken = default,
        Action<string>? afterPhaseForTesting = null)
    {
        if (!explicitOperatorApproval)
            throw new UnauthorizedAccessException("Explicit operator approval is required");
        cancellationToken.ThrowIfCancellationRequested();

        string target = TargetPath(destination);
        using var mutationLease = AcquireMutationLease(target);
        string archive = FixedLocal(archiveDirectory);
        if (!Directory.Exists(target) || File.Exists(target))
            throw new IOException("Only a verified existing TEMP sandbox can be replaced");
        if (Within(target, archive) || Within(archive, target))
            throw new InvalidDataException("Archive and restore target may not overlap");
        string journalPath = JournalPath(target);
        string journalNext = JournalNextPath(target);
        if (File.Exists(journalPath) || Directory.Exists(journalPath) ||
            File.Exists(journalNext) || Directory.Exists(journalNext))
            throw new IOException("An earlier restore transaction requires operator review");

        var preview = PreviewReplacement(archive, target, registry);
        if (!preview.PreflightPassed)
            throw new UnauthorizedAccessException("Restore preflight did not match the current registries");
        var previous = MultiHostClusterBackupArchive.VerifyIsolatedSandbox(target);
        var sourceManifest = MultiHostClusterBackupArchive.Verify(archive);
        var sourceIdentity = new MultiHostRestoreSandboxManifest(sourceManifest.SchemaVersion,
            sourceManifest.BatchId, sourceManifest.CreatedUtc, sourceManifest.Hosts,
            sourceManifest.Clusters, sourceManifest.Maps, sourceManifest.Components,
            false, "VERIFIED ARCHIVE PREVIEW");
        RequireSameOwnership(previous, sourceIdentity);

        string candidate = NewSandboxPath();
        string previousPath = NewSandboxPath();
        string rejectedPath = NewSandboxPath();
        if (new[] { candidate, previousPath, rejectedPath, target }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4 ||
            new[] { candidate, previousPath, rejectedPath }.Any(path => Directory.Exists(path) || File.Exists(path)))
            throw new IOException("Generated TEMP transaction artifact path collided");

        cancellationToken.ThrowIfCancellationRequested();
        MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(archive, candidate, cancellationToken);
        var staged = MultiHostClusterBackupArchive.VerifyIsolatedSandbox(candidate);
        RequireSameOwnership(previous, staged);

        var journal = new Journal(CurrentSchema, Guid.NewGuid(), Path.GetFileName(target),
            Path.GetFileName(candidate), Path.GetFileName(previousPath), Path.GetFileName(rejectedPath),
            previous.SourceBatchId, staged.SourceBatchId, "prepared", DateTimeOffset.UtcNow);
        Persist(target, journal);
        afterPhaseForTesting?.Invoke(journal.Phase);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(target, previousPath);
            journal = journal with { Phase = "previousMoved" };
            Persist(target, journal);
            afterPhaseForTesting?.Invoke(journal.Phase);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(candidate, target);
            journal = journal with { Phase = "replacementPublished" };
            Persist(target, journal);
            afterPhaseForTesting?.Invoke(journal.Phase);

            var published = MultiHostClusterBackupArchive.VerifyIsolatedSandbox(target);
            RequireSameOwnership(previous, published);
            if (published.SourceBatchId != journal.ReplacementBatchId)
                throw new InvalidDataException("Published sandbox batch changed during replacement");
            cancellationToken.ThrowIfCancellationRequested();
            journal = journal with { Phase = "committed" };
            Persist(target, journal);
            afterPhaseForTesting?.Invoke(journal.Phase);
            return new(journal.TransactionId, journal.PreviousBatchId,
                journal.ReplacementBatchId, previousPath, true, false, false, false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or
                                  UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            try { RollbackCore(target, journal); }
            catch (Exception rollbackError) when (rollbackError is IOException or InvalidDataException or
                UnauthorizedAccessException or ArgumentException or OverflowException)
            {
                throw new IOException("Replacement failed and automatic rollback needs operator recovery",
                    new AggregateException(e, rollbackError));
            }
            throw;
        }
    }

    private static void RollbackCore(string target, Journal journal)
    {
        ValidateJournal(target, journal);
        if (File.Exists(JournalNextPath(target)) || Directory.Exists(JournalNextPath(target)))
            throw new InvalidDataException("A partial transaction journal requires manual review");
        string candidate = FixedLocal(Path.Combine(Path.GetDirectoryName(target)!, journal.CandidateName));
        string previous = FixedLocal(Path.Combine(Path.GetDirectoryName(target)!, journal.PreviousName));
        string rejected = FixedLocal(Path.Combine(Path.GetDirectoryName(target)!, journal.RejectedName));

        var targetManifest = ReadSandboxIfPresent(target);
        var targetPrevious = targetManifest?.SourceBatchId == journal.PreviousBatchId ? targetManifest : null;
        var targetReplacement = targetManifest?.SourceBatchId == journal.ReplacementBatchId ? targetManifest : null;
        if (targetManifest != null && targetPrevious == null && targetReplacement == null)
            throw new InvalidDataException("Unexpected target identity prevents rollback");
        var retainedPrevious = VerifyIfPresent(previous, journal.PreviousBatchId);
        _ = VerifyIfPresent(candidate, journal.ReplacementBatchId);
        var retainedRejected = VerifyIfPresent(rejected, journal.ReplacementBatchId);

        if (targetPrevious != null)
        {
            if (retainedPrevious != null || targetReplacement != null)
                throw new InvalidDataException("Multiple prior or replacement sandboxes make rollback ambiguous");
            Persist(target, journal with { Phase = "rolledBack" });
            return;
        }
        if (retainedPrevious == null)
            throw new InvalidDataException("Verified protective sandbox is missing; rollback refused");

        if (targetReplacement != null)
        {
            if (retainedRejected != null || Directory.Exists(rejected) || File.Exists(rejected))
                throw new InvalidDataException("Rejected sandbox destination already exists");
            Directory.Move(target, rejected);
        }
        else if (Directory.Exists(target) || File.Exists(target))
            throw new InvalidDataException("Unexpected target identity prevents rollback");

        Directory.Move(previous, target);
        _ = MultiHostClusterBackupArchive.VerifyIsolatedSandbox(target);
        Persist(target, journal with { Phase = "rolledBack" });
    }

    public static MultiHostSandboxReplacementRecoveryStatus InspectRecovery(string destination)
    {
        string target = TargetPath(destination);
        if (File.Exists(MutationLeasePath(target)) ||
            Directory.Exists(MutationLeasePath(target)))
            return new(true, false, null, "busy",
                "Another process is changing this TEMP sandbox; recovery is blocked until it exits");
        string journalPath = JournalPath(target);
        string journalNext = JournalNextPath(target);
        if (!File.Exists(journalPath) && !File.Exists(journalNext) &&
            !Directory.Exists(journalPath) && !Directory.Exists(journalNext))
            return new(false, false, null, "none", "No sandbox replacement transaction is pending");
        try
        {
            var journal = ReadJournal(target);
            // Verify every artifact before offering a recovery action.
            _ = ReadSandboxIfPresent(target);
            _ = VerifyIfPresent(Path.Combine(Path.GetDirectoryName(target)!, journal.PreviousName),
                journal.PreviousBatchId);
            _ = VerifyIfPresent(Path.Combine(Path.GetDirectoryName(target)!, journal.CandidateName),
                journal.ReplacementBatchId);
            _ = VerifyIfPresent(Path.Combine(Path.GetDirectoryName(target)!, journal.RejectedName),
                journal.ReplacementBatchId);
            bool canRollback = journal.Phase == "committed" || journal.Phase is "prepared" or
                "previousMoved" or "replacementPublished";
            bool needsOperator = journal.Phase is not ("committed" or "rolledBack");
            string reason = journal.Phase switch
            {
                "committed" => "Replacement is verified; protective sandbox is retained and explicit rollback remains available",
                "rolledBack" => "Previous sandbox is restored and verified",
                _ => "Interrupted transaction artifacts are verified; explicit operator rollback is available"
            };
            string? rejectedPath = Path.Combine(Path.GetDirectoryName(target)!, journal.RejectedName);
            string? previousPath = Path.Combine(Path.GetDirectoryName(target)!, journal.PreviousName);
            return new(needsOperator, canRollback, journal.TransactionId, journal.Phase, reason,
                Directory.Exists(rejectedPath) ? journal.RejectedName : null,
                Directory.Exists(previousPath) ? journal.PreviousName : null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or
                                  ArgumentException or JsonException or OverflowException)
        {
            return new(true, false, null, "needs-review",
                "Transaction journal or sandbox artifacts are damaged; no automatic change was made");
        }
    }

    /// <summary>
    /// Lists only bounded journal metadata under the direct TEMP directory. It
    /// does not hash payloads; selecting one entry invokes full verification.
    /// </summary>
    public static MultiHostSandboxRecoveryListing ListRecoveryCandidates()
    {
        const int maximum = 128;
        string temp = FixedLocal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
        if (!Directory.Exists(temp)) return new(false, Array.Empty<MultiHostSandboxRecoveryCandidate>());
        string[] files = Directory.EnumerateFiles(temp,
                SandboxPrefix + "*" + JournalSuffix, SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(temp,
                SandboxPrefix + "*" + JournalSuffix + ".next", SearchOption.TopDirectoryOnly))
            .Take(maximum + 1).ToArray();
        bool truncated = files.Length > maximum;
        var candidates = new List<MultiHostSandboxRecoveryCandidate>(Math.Min(files.Length, maximum));
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files.Take(maximum))
        {
            string fileName = Path.GetFileName(file);
            string targetName = fileName.EndsWith(JournalSuffix + ".next", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^(JournalSuffix.Length + ".next".Length)]
                : fileName.EndsWith(JournalSuffix, StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^JournalSuffix.Length] : "";
            if (string.IsNullOrWhiteSpace(targetName) || !seen.Add(targetName)) continue;
            try
            {
                string target = TargetPath(Path.Combine(temp, targetName));
                if (File.Exists(MutationLeasePath(target)) ||
                    Directory.Exists(MutationLeasePath(target)))
                {
                    candidates.Add(new(targetName, null, "busy", true, false,
                        "Another process owns this TEMP sandbox transaction; rollback cannot start"));
                    continue;
                }
                var journal = ReadJournal(target);
                bool canRollback = journal.Phase is "prepared" or "previousMoved" or
                    "replacementPublished" or "committed";
                candidates.Add(new(targetName, journal.TransactionId, journal.Phase,
                    journal.Phase is not ("committed" or "rolledBack"), canRollback,
                    journal.Phase == "committed"
                        ? "Committed sandbox has a retained protective copy; select it for full verification"
                        : journal.Phase == "rolledBack"
                            ? "Sandbox is already rolled back"
                            : "Interrupted sandbox transaction; select it for full verification before rollback"));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or
                                      ArgumentException or JsonException or OverflowException)
            {
                candidates.Add(new(targetName, null, "needs-review", true, false,
                    "Transaction journal is incomplete or damaged; automatic recovery is unavailable"));
            }
        }
        return new(truncated, candidates.ToArray());
    }

    /// <summary>Rolls back an interrupted or committed sandbox replacement only after explicit approval.</summary>
    public static MultiHostSandboxReplacementRecoveryStatus Rollback(
        string destination, Guid transactionId, bool explicitOperatorApproval)
    {
        if (!explicitOperatorApproval)
            throw new UnauthorizedAccessException("Explicit rollback approval is required");
        string target = TargetPath(destination);
        using (var mutationLease = AcquireMutationLease(target))
        {
            var journal = ReadJournal(target);
            if (transactionId == Guid.Empty || journal.TransactionId != transactionId ||
                journal.Phase is "rolledBack")
                throw new InvalidOperationException("Restore transaction is stale or already rolled back");
            RollbackCore(target, journal);
        }
        return InspectRecovery(target);
    }
}
