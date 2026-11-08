using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using UntamedManager.Core;

public static class MultiHostClusterBackupArchiveTests
{
    private sealed record Fixture(MultiHostBackupHost[] Hosts,
        MultiHostBackupCluster[] Clusters, MultiHostBackupMap[] Maps,
        MultiHostBackupSource[] Sources);

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception("Multi-host cluster archive test failed: " + reason);
        Console.WriteLine("PASS multi-host-backup " + reason);
    }

    private static bool Refused(Action work)
    {
        try { work(); return false; }
        catch (Exception e) when (e is IOException or InvalidDataException or
                                      UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return true; }
    }

    private static Fixture CreateFixture(string root, int hostCount, int mapCount, int clusterCount)
    {
        if (hostCount < 1 || mapCount < 1 || clusterCount < 1 || clusterCount > mapCount)
            throw new ArgumentOutOfRangeException(nameof(mapCount));
        Directory.CreateDirectory(root);
        var hosts = Enumerable.Range(0, hostCount)
            .Select(i => new MultiHostBackupHost($"host-{i:D3}", $"Synthetic host {i:D3}"))
            .ToArray();
        var clusters = Enumerable.Range(0, clusterCount)
            .Select(i => new MultiHostBackupCluster($"cluster-{i:D3}", $"Synthetic cluster {i:D3}"))
            .ToArray();
        var maps = Enumerable.Range(0, mapCount)
            .Select(i => new MultiHostBackupMap(Guid.NewGuid(), hosts[i % hostCount].HostId,
                clusters[i % clusterCount].ClusterId, $"Synthetic map {i:D3}"))
            .ToArray();
        var sources = new List<MultiHostBackupSource>();
        static string SourceScope(string host, Guid? map, string? cluster,
            MultiHostBackupComponentKind kind)
        {
            if (map.HasValue) return Path.Combine("maps", map.Value.ToString("N"));
            return kind switch
            {
                MultiHostBackupComponentKind.HostConfiguration => Path.Combine("hosts", host, "configuration"),
                MultiHostBackupComponentKind.HostStoreV1File => Path.Combine("hosts", host, "clusters", cluster!, "Store-v1"),
                MultiHostBackupComponentKind.ClusterStoreV1File => Path.Combine("clusters", cluster!, "Store-v1", host),
                MultiHostBackupComponentKind.ClusterTransferManifest or
                MultiHostBackupComponentKind.ClusterTransferFile => Path.Combine("clusters", cluster!, "Transfer", host),
                _ => throw new InvalidDataException("Synthetic source scope is not defined")
            };
        }
        void Add(string host, Guid? map, string? cluster, MultiHostBackupComponentKind kind,
            string relative, string content)
        {
            string path = Path.Combine(root, SourceScope(host, map, cluster, kind),
                relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            sources.Add(new(host, map, cluster, kind, relative, path));
        }

        foreach (var host in hosts)
            Add(host.HostId, null, null, MultiHostBackupComponentKind.HostConfiguration,
                "manager/profile.json", "synthetic manager configuration " + host.HostId);

        foreach (var map in maps)
        {
            Add(map.HostId, map.MapId, map.ClusterId, MultiHostBackupComponentKind.MapWorld,
                "Saved/world.ark", "synthetic world " + map.MapId.ToString("N"));
            Add(map.HostId, map.MapId, map.ClusterId, MultiHostBackupComponentKind.MapProfile,
                "Saved/Players/player.arkprofile", "synthetic profile bytes " + map.MapId.ToString("N"));
            Add(map.HostId, map.MapId, map.ClusterId, MultiHostBackupComponentKind.MapTribe,
                "Saved/Tribes/tribe.arktribe", "synthetic tribe bytes " + map.MapId.ToString("N"));
            Add(map.HostId, map.MapId, map.ClusterId, MultiHostBackupComponentKind.GameIni,
                "WindowsServer/Game.ini", "[ServerSettings]\nsynthetic=true\n");
            Add(map.HostId, map.MapId, map.ClusterId, MultiHostBackupComponentKind.GameUserSettingsIni,
                "WindowsServer/GameUserSettings.ini", "[ServerSettings]\nsynthetic=true\n");
            Add(map.HostId, map.MapId, map.ClusterId, MultiHostBackupComponentKind.ModConfiguration,
                "Manager/mods.json", "{\"orderedIds\":[]}");
            Add(map.HostId, map.MapId, map.ClusterId, MultiHostBackupComponentKind.AsaApiPluginConfiguration,
                "Manager/plugins.json", "{\"plugins\":[]}");
            Add(map.HostId, map.MapId, map.ClusterId, MultiHostBackupComponentKind.PluginPersistentData,
                "ASAApi/state/empty-snapshot.json", "{\"configured\":false,\"files\":[]}");
        }

        var pairs = maps.Select(m => (m.HostId, m.ClusterId))
            .Distinct().ToArray();
        foreach (var (host, cluster) in pairs)
            Add(host, null, cluster, MultiHostBackupComponentKind.HostStoreV1File,
                "host-state.json", "synthetic host store metadata " + host + " " + cluster);
        foreach (var cluster in clusters)
        {
            string owner = maps.First(m => m.ClusterId == cluster.ClusterId).HostId;
            Add(owner, null, cluster.ClusterId, MultiHostBackupComponentKind.ClusterStoreV1File,
                "cluster-state.json", "synthetic cluster store metadata " + cluster.ClusterId);
            Add(owner, null, cluster.ClusterId, MultiHostBackupComponentKind.ClusterTransferManifest,
                "transfer-snapshot.json", "{\"files\":[\"transfer.bin\"]}");
            Add(owner, null, cluster.ClusterId, MultiHostBackupComponentKind.ClusterTransferFile,
                "transfer.bin", "synthetic transfer bytes " + cluster.ClusterId);
        }
        return new(hosts, clusters, maps, sources.ToArray());
    }

    private static string SourceFor(Fixture fixture, MultiHostBackupComponentKind kind, Guid? mapId = null) =>
        fixture.Sources.Single(s => s.Kind == kind && (!mapId.HasValue || s.MapId == mapId)).SourceFile;

    public static void Run(string testRoot)
    {
        string root = Path.Combine(testRoot, "multi-host-cluster-v1");
        var fixture = CreateFixture(Path.Combine(root, "fixture"), 12, 100, 4);
        string archive = Path.Combine(root, "archive-12-host-100-map");
        Guid batch = Guid.NewGuid();
        var manifest = MultiHostClusterBackupArchive.BuildOffline(batch,
            fixture.Hosts, fixture.Clusters, fixture.Maps, fixture.Sources, archive);
        var verified = MultiHostClusterBackupArchive.Verify(archive);
        Check(manifest.Hosts.Length == 12 && manifest.Maps.Length == 100 &&
              manifest.Clusters.Length == 4 && verified.Components.Length == fixture.Sources.Length,
            "versioned archive accepts twelve hosts, one hundred unevenly assigned maps and independent clusters");
        Check(!verified.IsProductionRestoreCertified && !verified.LiveSaveConsistencyConfirmed &&
              !verified.StoreWriterOwnershipConfirmed,
            "offline package never claims live ASA or Store writer consistency");
        Check(fixture.Sources.All(s => File.Exists(s.SourceFile) && new FileInfo(s.SourceFile).Length > 0),
            "all synthetic source files remain present after backup");

        string mapWorld = SourceFor(fixture, MultiHostBackupComponentKind.MapWorld, fixture.Maps[0].MapId);
        string unlistedPath = Path.Combine(Path.GetDirectoryName(mapWorld)!, "unlisted.arkprofile");
        File.WriteAllText(unlistedPath, "synthetic file omitted from offline copy catalog");
        string partialCopyTarget = Path.Combine(root, "partial-copy-source");
        bool partialCopyRefused = Refused(() => MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(),
            fixture.Hosts, fixture.Clusters, fixture.Maps, fixture.Sources, partialCopyTarget));
        File.Delete(unlistedPath);
        Check(partialCopyRefused && !Directory.Exists(partialCopyTarget) &&
              !Directory.Exists(partialCopyTarget + ".incomplete"),
            "unlisted files in an offline map copy are rejected before archive staging");

        var allowed = MultiHostClusterBackupArchive.PreviewRestore(archive,
            fixture.Hosts, fixture.Clusters, fixture.Maps, true, true, true);
        var running = MultiHostClusterBackupArchive.PreviewRestore(archive,
            fixture.Hosts, fixture.Clusters, fixture.Maps, false, true, true);
        var locked = MultiHostClusterBackupArchive.PreviewRestore(archive,
            fixture.Hosts, fixture.Clusters, fixture.Maps, true, false, true);
        var notApproved = MultiHostClusterBackupArchive.PreviewRestore(archive,
            fixture.Hosts, fixture.Clusters, fixture.Maps, true, true, false);
        var foreignMaps = fixture.Maps.ToArray();
        foreignMaps[0] = foreignMaps[0] with { HostId = "foreign-host" };
        var wrongOwner = MultiHostClusterBackupArchive.PreviewRestore(archive,
            fixture.Hosts, fixture.Clusters, foreignMaps, true, true, true);
        Check(allowed.PreflightPassed && !allowed.CanRestoreToLiveManagers &&
              !allowed.WritesStarted && !allowed.ProductionRestoreCertified,
            "restore preflight is read-only and verifies the complete ownership snapshot");
        Check(!running.PreflightPassed && !locked.PreflightPassed &&
              !notApproved.PreflightPassed && !wrongOwner.PreflightPassed,
            "restore preflight fails closed for running maps, unknown locks, missing approval or owner mismatch");

        string interrupted = Path.Combine(root, "interrupted");
        bool interruptedThrown = Refused(() => MultiHostClusterBackupArchive.BuildOffline(
            Guid.NewGuid(), fixture.Hosts, fixture.Clusters, fixture.Maps, fixture.Sources,
            interrupted, afterCopiedForTesting: i => { if (i == 63) throw new IOException("synthetic copy interruption"); }));
        var recovery = MultiHostClusterBackupArchive.InspectInterrupted(interrupted);
        Check(interruptedThrown && recovery.NeedsOperator && recovery.VerifiedFiles == 64 &&
              !recovery.AutoResumeAllowed && !Directory.Exists(interrupted),
            "interrupted backup retains a compact hash-chain checkpoint and requires operator recovery without auto-resume");

        string invalid = Path.Combine(root, "invalid-owner");
        var badSources = fixture.Sources.ToArray();
        badSources[0] = badSources[0] with { HostId = "unknown-host" };
        Check(Refused(() => MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(),
                  fixture.Hosts, fixture.Clusters, fixture.Maps, badSources, invalid)) &&
              !Directory.Exists(invalid) && !Directory.Exists(invalid + ".incomplete"),
            "unknown host ownership is rejected before archive staging");

        string missing = Path.Combine(root, "missing-settings");
        var missingIni = fixture.Sources.Where(s => s.Kind != MultiHostBackupComponentKind.GameUserSettingsIni).ToArray();
        Check(Refused(() => MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(),
                  fixture.Hosts, fixture.Clusters, fixture.Maps, missingIni, missing)) &&
              !Directory.Exists(missing + ".incomplete"),
            "missing required map settings are rejected before any payload is copied");

        string emptyWorld = SourceFor(fixture, MultiHostBackupComponentKind.MapWorld, fixture.Maps[0].MapId);
        string oldWorldBytes = File.ReadAllText(emptyWorld);
        File.WriteAllBytes(emptyWorld, Array.Empty<byte>());
        string emptyTarget = Path.Combine(root, "empty-world");
        bool emptyRefused = Refused(() => MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(),
            fixture.Hosts, fixture.Clusters, fixture.Maps, fixture.Sources, emptyTarget));
        File.WriteAllText(emptyWorld, oldWorldBytes);
        Check(emptyRefused && !Directory.Exists(emptyTarget + ".incomplete"),
            "zero-byte world payload is refused without staging and source fixture is restored");

        string tampered = Path.Combine(archive, "objects", "000000.bin");
        byte[] originalObject = File.ReadAllBytes(tampered);
        File.WriteAllText(tampered, "tampered synthetic bytes");
        bool hashRefused = Refused(() => MultiHostClusterBackupArchive.Verify(archive));
        File.WriteAllBytes(tampered, originalObject);
        Check(hashRefused && MultiHostClusterBackupArchive.Verify(archive).BatchId == batch,
            "payload SHA-256 rejects alteration and the archive verifies again after fixture repair");

        string oneHostRoot = Path.Combine(root, "one-host-fixture");
        var oneHost = CreateFixture(oneHostRoot, 1, 1, 1);
        string oneArchive = Path.Combine(root, "one-host-one-map");
        var one = MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(), oneHost.Hosts,
            oneHost.Clusters, oneHost.Maps, oneHost.Sources, oneArchive);
        Check(one.Hosts.Length == 1 && one.Maps.Length == 1 && one.Clusters.Length == 1,
            "minimum supported one-host one-map archive is valid");
        var twoHost = CreateFixture(Path.Combine(root, "two-host-fixture"), 2, 3, 2);
        string twoArchive = Path.Combine(root, "two-host-three-map");
        var two = MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(), twoHost.Hosts,
            twoHost.Clusters, twoHost.Maps, twoHost.Sources, twoArchive);
        Check(two.Hosts.Length == 2 && two.Maps.Length == 3,
            "two hosts can own an uneven three-map multi-cluster backup");
        var fourHost = CreateFixture(Path.Combine(root, "four-host-fixture"), 4, 3, 2);
        string fourArchive = Path.Combine(root, "four-host-three-map");
        var four = MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(), fourHost.Hosts,
            fourHost.Clusters, fourHost.Maps, fourHost.Sources, fourArchive);
        Check(four.Hosts.Length == 4 && four.Maps.Length == 3,
            "four registered hosts support fewer maps and an unused host without forcing equal counts");

        Guid catalogBatch = Guid.NewGuid();
        string catalogPath = Path.Combine(oneHostRoot, "multi-host-backup-catalog.json");
        var catalog = new MultiHostBackupCatalog(1, catalogBatch, oneHost.Hosts,
            oneHost.Clusters, oneHost.Maps, oneHost.Sources.Select(s =>
                new MultiHostBackupCatalogItem(s.HostId, s.MapId, s.ClusterId, s.Kind,
                    s.RelativePath, Path.GetRelativePath(oneHostRoot, s.SourceFile).Replace('\\', '/'))).ToArray(),
            false, false, MultiHostClusterBackupArchive.OfflineCopyCatalogScope);
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog));
        var registry = new MultiHostBackupOwnershipSnapshot(oneHost.Hosts[0].HostId,
            new[] { new MultiHostBackupHostOwnership(oneHost.Hosts[0].HostId,
                oneHost.Hosts[0].Name, true, false) },
            new[] { new MultiHostBackupClusterOwnership(oneHost.Clusters[0].ClusterId,
                oneHost.Clusters[0].Name) },
            new[] { new MultiHostBackupMapOwnership(oneHost.Maps[0].MapId,
                oneHost.Maps[0].HostId, oneHost.Maps[0].Name, oneHost.Maps[0].ClusterId) });
        var catalogPreview = MultiHostClusterBackupArchive.InspectCatalog(catalogPath);
        string catalogArchive = Path.Combine(root, "catalog-built-archive");
        var registryPreview = MultiHostClusterBackupArchive.InspectCatalog(catalogPath, registry);
        var catalogResult = MultiHostClusterBackupArchive.BuildFromCatalog(catalogPath, catalogArchive, registry);
        Check(catalogPreview.BatchId == catalogBatch && catalogPreview.Hosts == 1 &&
              catalogPreview.Maps == 1 && registryPreview.Clusters == 1 &&
              catalogResult.BatchId == catalogBatch &&
              MultiHostClusterBackupArchive.Verify(catalogArchive).Components.Length == oneHost.Sources.Length,
            "offline catalog matches the current HostRegistry/ClusterRegistry before building the archive");
        var wrongRegistry = registry with
        {
            Maps = new[] { registry.Maps[0] with { ClusterId = "other-cluster" } }
        };
        Check(Refused(() => MultiHostClusterBackupArchive.InspectCatalog(catalogPath, wrongRegistry)),
            "catalog map-to-cluster ownership drift is refused");
        Check(Refused(() => MultiHostClusterBackupArchive.InspectCatalog(catalogPath,
                  new[] { Path.Combine(oneHostRoot, "maps") })),
            "configured protected data roots are refused as catalog payload sources");
        var traversalCatalog = catalog with
        {
            Items = catalog.Items.Select((item, index) => index == 0
                ? item with { RelativeSource = "../../outside.bin" } : item).ToArray()
        };
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(traversalCatalog));
        string traversalTarget = Path.Combine(root, "catalog-traversal-target");
        Check(Refused(() => MultiHostClusterBackupArchive.BuildFromCatalog(catalogPath, traversalTarget)) &&
              !Directory.Exists(traversalTarget) && !Directory.Exists(traversalTarget + ".incomplete"),
            "catalog path traversal is refused before creating any archive staging directory");

        string sandbox = Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-" + Guid.NewGuid().ToString("N"));
        var restored = MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(oneArchive, sandbox);
        string restoredWorld = Path.Combine(sandbox, "data", "hosts", oneHost.Maps[0].HostId,
            "maps", oneHost.Maps[0].MapId.ToString("N"), "Saved", "world.ark");
        Check(restored.SourceBatchId == one.BatchId && !restored.IsProductionRestoreAllowed &&
              File.Exists(restoredWorld) && File.ReadAllText(restoredWorld).Contains("synthetic world"),
            "verified restore atomically materializes only a new TEMP sandbox and preserves world bytes");
        Check(MultiHostClusterBackupArchive.Verify(oneArchive).BatchId == one.BatchId,
            "sandbox restore leaves its source archive unchanged");

        string replacementArchive = Path.Combine(root, "one-host-replacement");
        string syntheticWorldSource = SourceFor(oneHost, MultiHostBackupComponentKind.MapWorld,
            oneHost.Maps[0].MapId);
        byte[] originalSyntheticWorld = File.ReadAllBytes(syntheticWorldSource);
        MultiHostBackupManifest replacementManifest;
        try
        {
            File.WriteAllText(syntheticWorldSource, "replacement synthetic world bytes");
            replacementManifest = MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(),
                oneHost.Hosts, oneHost.Clusters, oneHost.Maps, oneHost.Sources, replacementArchive);
        }
        finally { File.WriteAllBytes(syntheticWorldSource, originalSyntheticWorld); }

        string transactionTarget = Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-" + Guid.NewGuid().ToString("N"));
        var transactionPrior = MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(
            oneArchive, transactionTarget);
        Check(Refused(() => MultiHostSandboxRestoreTransaction.ReplaceVerifiedSandbox(
                  oneArchive, transactionTarget, registry, true)) &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(transactionTarget).SourceBatchId == one.BatchId,
            "replacement refuses an already materialized batch before creating a staging copy");
        Check(Refused(() => MultiHostSandboxRestoreTransaction.ReplaceVerifiedSandbox(
                  replacementArchive, transactionTarget, registry, false)) &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(transactionTarget).SourceBatchId == one.BatchId,
            "sandbox replacement fails closed without explicit operator approval or touching the prior target");

        var replacementReceipt = MultiHostSandboxRestoreTransaction.ReplaceVerifiedSandbox(
            replacementArchive, transactionTarget, registry, true);
        string replacementWorld = Path.Combine(transactionTarget, "data", "hosts",
            oneHost.Maps[0].HostId, "maps", oneHost.Maps[0].MapId.ToString("N"), "Saved", "world.ark");
        var committedStatus = MultiHostSandboxRestoreTransaction.InspectRecovery(transactionTarget);
        Check(replacementReceipt.ExplicitOperatorApproval && !replacementReceipt.ProductionRestoreAllowed &&
              !replacementReceipt.ProcessStateChecked && !replacementReceipt.StoreWriterOwnershipChecked &&
              replacementReceipt.PreviousBatchId == one.BatchId &&
              replacementReceipt.ReplacementBatchId == replacementManifest.BatchId &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(replacementReceipt.RetainedBackupDirectory)
                  .SourceBatchId == one.BatchId &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(transactionTarget).SourceBatchId == replacementManifest.BatchId &&
              File.ReadAllText(replacementWorld).Contains("replacement synthetic world") &&
              committedStatus.Phase == "committed" && committedStatus.CanRollback,
            "approved TEMP replacement stages and verifies new bytes while retaining a verified protective sandbox and durable commit record");

        bool staleRollbackDenied = Refused(() => MultiHostSandboxRestoreTransaction.Rollback(
            transactionTarget, Guid.NewGuid(), true));
        Check(staleRollbackDenied &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(transactionTarget).SourceBatchId == replacementManifest.BatchId,
            "replacement rollback rejects stale operator transaction identities");
        var rolledBackStatus = MultiHostSandboxRestoreTransaction.Rollback(
            transactionTarget, replacementReceipt.TransactionId, true);
        Check(!rolledBackStatus.NeedsOperator && rolledBackStatus.Phase == "rolledBack" &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(transactionTarget).SourceBatchId == one.BatchId &&
              File.ReadAllText(Path.Combine(transactionTarget, "data", "hosts", oneHost.Maps[0].HostId,
                  "maps", oneHost.Maps[0].MapId.ToString("N"), "Saved", "world.ark"))
                  .Contains("synthetic world"),
            "explicit rollback restores the protective sandbox after a committed replacement without deleting the rejected clone");

        string interruptedReplacementTarget = Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-" + Guid.NewGuid().ToString("N"));
        MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(oneArchive, interruptedReplacementTarget);
        bool replacementInterrupted = false;
        try
        {
            MultiHostSandboxRestoreTransaction.ReplaceVerifiedSandbox(replacementArchive,
                interruptedReplacementTarget, registry, true,
                afterPhaseForTesting: phase =>
                {
                    if (phase == "previousMoved")
                        throw new OperationCanceledException("Synthetic process interruption after durable backup rename");
                });
        }
        catch (OperationCanceledException) { replacementInterrupted = true; }
        var interruptedReplacement = MultiHostSandboxRestoreTransaction.InspectRecovery(
            interruptedReplacementTarget);
        Check(replacementInterrupted && interruptedReplacement.NeedsOperator &&
              interruptedReplacement.CanRollback && interruptedReplacement.Phase == "previousMoved" &&
              !Directory.Exists(interruptedReplacementTarget),
            "simulated crash after the protective rename leaves a durable, verifiable transaction recovery point");
        var recoveryCandidates = MultiHostSandboxRestoreTransaction.ListRecoveryCandidates();
        Check(recoveryCandidates.Transactions.Any(candidate =>
                  candidate.TargetDirectoryName == Path.GetFileName(interruptedReplacementTarget) &&
                  candidate.Phase == "previousMoved" && candidate.CanRollback) &&
              !Directory.Exists(interruptedReplacementTarget),
            "bounded TEMP journal listing rediscovers an interrupted transaction even when its target directory is absent");
        var crashRollback = MultiHostSandboxRestoreTransaction.Rollback(
            interruptedReplacementTarget, interruptedReplacement.TransactionId!.Value, true);
        Check(!crashRollback.NeedsOperator && crashRollback.Phase == "rolledBack" &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(interruptedReplacementTarget).SourceBatchId == one.BatchId,
            "operator-approved restart recovery completes rollback after an interrupted sandbox replacement");

        string interruptedRestore = Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-" + Guid.NewGuid().ToString("N"));
        bool restoreFailed = Refused(() => MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(
            archive, interruptedRestore,
            afterCopiedForTesting: i => { if (i == 63) throw new IOException("synthetic restore interruption"); }));
        var restoreRecovery = MultiHostClusterBackupArchive.InspectInterruptedSandboxRestore(
            archive, interruptedRestore);
        Check(restoreFailed && restoreRecovery.NeedsOperator && restoreRecovery.VerifiedFiles == 64 &&
              !restoreRecovery.AutoResumeAllowed && !Directory.Exists(interruptedRestore),
            "interrupted sandbox restore retains a compact hash-chain checkpoint and refuses automatic replay");

        string unsafeRestoreTarget = Path.Combine(root, "not-a-temp-restore");
        Check(Refused(() => MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(
                  oneArchive, unsafeRestoreTarget)) && !Directory.Exists(unsafeRestoreTarget),
            "restore refuses a non-TEMP target before writing any content");

        string zeroMapTarget = Path.Combine(root, "zero-map");
        Check(Refused(() => MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(),
                  oneHost.Hosts, oneHost.Clusters, Array.Empty<MultiHostBackupMap>(),
                  oneHost.Sources, zeroMapTarget)) && !Directory.Exists(zeroMapTarget + ".incomplete"),
            "zero-map archive is refused before creating staging data");
        string zeroHostTarget = Path.Combine(root, "zero-host");
        Check(Refused(() => MultiHostClusterBackupArchive.BuildOffline(Guid.NewGuid(),
                  Array.Empty<MultiHostBackupHost>(), oneHost.Clusters, oneHost.Maps,
                  oneHost.Sources, zeroHostTarget)) && !Directory.Exists(zeroHostTarget + ".incomplete"),
            "zero-host archive is refused before creating staging data");
    }
}
