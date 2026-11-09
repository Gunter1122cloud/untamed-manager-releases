using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

public static class MultiHostClusterBackupBridgeTests
{
    private static string SourceScope(string hostId, Guid? mapId, string? clusterId,
        MultiHostBackupComponentKind kind)
    {
        if (mapId.HasValue) return Path.Combine("maps", mapId.Value.ToString("N"));
        return kind switch
        {
            MultiHostBackupComponentKind.HostConfiguration => Path.Combine("hosts", hostId, "configuration"),
            MultiHostBackupComponentKind.HostStoreV1File => Path.Combine("hosts", hostId, "clusters", clusterId!, "Store-v1"),
            MultiHostBackupComponentKind.ClusterStoreV1File => Path.Combine("clusters", clusterId!, "Store-v1", hostId),
            MultiHostBackupComponentKind.ClusterTransferManifest or
            MultiHostBackupComponentKind.ClusterTransferFile => Path.Combine("clusters", clusterId!, "Transfer", hostId),
            _ => throw new InvalidDataException("Synthetic source scope is not defined")
        };
    }

    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException("NATIVE MULTI-HOST BRIDGE TEST FAILED: " + reason);
        Console.WriteLine("PASS native multi-host bridge: " + reason);
    }

    private static async Task<JsonElement> Call(DesktopMultiHostClusterBackupBridge bridge,
        string method, object? args = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString("D"),
            ["method"] = method
        };
        if (args != null)
        {
            var extra = JsonSerializer.SerializeToElement(args);
            foreach (var field in extra.EnumerateObject()) request[field.Name] = field.Value.Clone();
        }
        using var document = JsonDocument.Parse(await bridge.HandleAsync(JsonSerializer.Serialize(request)));
        return document.RootElement.Clone();
    }

    private static string Catalog(string root, ClusterService service,
        out MultiHostBackupCatalog model)
    {
        var registry = service.Registry.Snapshot();
        var cluster = registry.ClusterRegistry.Clusters.Single();
        var map = registry.HostRegistry.Maps.Single();
        var host = registry.HostRegistry.Hosts.Single(x => x.HostId == map.HostId);
        string[] specs =
        {
            $"{MultiHostBackupComponentKind.HostConfiguration}|host.json|host configuration",
            $"{MultiHostBackupComponentKind.MapWorld}|Saved/world.ark|world bytes",
            $"{MultiHostBackupComponentKind.MapProfile}|Saved/player.arkprofile|player bytes",
            $"{MultiHostBackupComponentKind.MapTribe}|Saved/tribe.arktribe|tribe bytes",
            $"{MultiHostBackupComponentKind.GameIni}|WindowsServer/Game.ini|[ServerSettings]",
            $"{MultiHostBackupComponentKind.GameUserSettingsIni}|WindowsServer/GameUserSettings.ini|[ServerSettings]",
            $"{MultiHostBackupComponentKind.ModConfiguration}|Manager/mods.json|{{}}",
            $"{MultiHostBackupComponentKind.AsaApiPluginConfiguration}|Manager/plugins.json|{{}}",
            $"{MultiHostBackupComponentKind.PluginPersistentData}|ASAApi/empty-state.json|{{\"files\":[]}}",
            $"{MultiHostBackupComponentKind.HostStoreV1File}|host-state.json|synthetic host state",
            $"{MultiHostBackupComponentKind.ClusterStoreV1File}|cluster-state.json|synthetic cluster state",
            $"{MultiHostBackupComponentKind.ClusterTransferManifest}|snapshot.json|{{\"files\":[\"data.bin\"]}}",
            $"{MultiHostBackupComponentKind.ClusterTransferFile}|data.bin|synthetic transfer bytes"
        };
        var items = new List<MultiHostBackupCatalogItem>();
        foreach (string spec in specs)
        {
            string[] fields = spec.Split('|', 3);
            var kind = Enum.Parse<MultiHostBackupComponentKind>(fields[0]);
            Guid? mapId = kind is MultiHostBackupComponentKind.HostConfiguration or
                MultiHostBackupComponentKind.HostStoreV1File or
                MultiHostBackupComponentKind.ClusterStoreV1File or
                MultiHostBackupComponentKind.ClusterTransferManifest or
                MultiHostBackupComponentKind.ClusterTransferFile ? null : map.MapId;
            string? clusterId = kind == MultiHostBackupComponentKind.HostConfiguration ? null : cluster.ClusterId;
            string owner = host.HostId;
            string relative = Path.Combine(SourceScope(owner, mapId, clusterId, kind),
                fields[1].Replace('/', Path.DirectorySeparatorChar)).Replace('\\', '/');
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, fields[2]);
            items.Add(new(owner, mapId, clusterId, kind, fields[1], relative));
        }
        model = new(1, Guid.NewGuid(), new[] { new MultiHostBackupHost(host.HostId, host.HostName) },
            new[] { new MultiHostBackupCluster(cluster.ClusterId, cluster.Name) },
            new[] { new MultiHostBackupMap(map.MapId, map.HostId, cluster.ClusterId, map.MapName) },
            items.ToArray(), false, false, MultiHostClusterBackupArchive.OfflineCopyCatalogScope);
        string catalogPath = Path.Combine(root, "multi-host-backup-catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(model));
        return catalogPath;
    }

    private static async Task<JsonElement> WaitFor(DesktopMultiHostClusterBackupBridge bridge,
        string method, string jobId)
    {
        for (int i = 0; i < 120; i++)
        {
            var result = await Call(bridge, method, new { jobId });
            if (result.GetProperty("ok").GetBoolean())
            {
                var state = result.GetProperty("data").GetProperty("state").GetString();
                if (state is "completed" or "needsOperator") return result;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Synthetic multi-host bridge job did not settle");
    }

    private static bool Error(JsonElement response, string code) =>
        !response.GetProperty("ok").GetBoolean() &&
        response.GetProperty("error").GetString() == code;

    public static async Task Run(string testRoot)
    {
        string root = Path.Combine(testRoot, "multi-host-bridge");
        string profile = Path.Combine(root, "manager-profile");
        string liveMapRoot = Path.Combine(root, "registered-live-map-marker-only");
        string transferRoot = Path.Combine(root, "registered-live-transfer-marker-only");
        string backupRoot = Path.Combine(root, "registered-backup-marker-only");
        string sourceRoot = Path.Combine(root, "offline-source-copies");
        string outputRoot = Path.Combine(root, "offline-archive-output");
        Directory.CreateDirectory(liveMapRoot);
        Directory.CreateDirectory(transferRoot);
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        string liveMarker = Path.Combine(liveMapRoot, "must-remain-untouched.txt");
        File.WriteAllText(liveMarker, "synthetic test marker only");

        await using var service = new ClusterService("multi-host-bridge-test", profile,
            inspect: _ => new ProcessCheck(false, "synthetic stopped", null, true));
        var map = new LocalMap
        {
            Name = "Synthetic offline cluster map",
            InstallationPath = liveMapRoot,
            ExecutablePath = Path.Combine(liveMapRoot, "not-a-real-server.exe"),
            Arguments = "--synthetic-only"
        };
        service.AddMap(map);
        string clusterId = "synthetic-cluster";
        service.Registry.CreateCluster(clusterId, "Synthetic cluster", transferRoot, backupRoot);
        var membership = service.Registry.PreviewMapAssignments(clusterId, new[] { map.Id });
        service.Registry.ApplyMapAssignments(clusterId, new[] { map.Id }, membership.BaseRevision);
        string catalogPath = Catalog(sourceRoot, service, out var catalogModel);
        string protectedCatalogPath = Path.Combine(liveMapRoot, "multi-host-backup-catalog.json");
        File.Copy(catalogPath, protectedCatalogPath);
        var choices = new Queue<string?>();
        using var bridge = new DesktopMultiHostClusterBackupBridge(service, _ => choices.Dequeue());

        choices.Enqueue(liveMapRoot);
        var protectedSource = await Call(bridge, "cluster.multiHostBackupPreview");
        Check(Error(protectedSource, "UM-MULTIHOST-BACKUP-SAFETY-CHECK-FAILED"),
            "catalog stored under the registered live map root is denied by the native bridge");
        File.Delete(protectedCatalogPath);
        choices.Enqueue(sourceRoot);
        choices.Enqueue(liveMapRoot);
        var protectedDestination = await Call(bridge, "cluster.multiHostBackupPreview");
        Check(Error(protectedDestination, "UM-MULTIHOST-BACKUP-DESTINATION-UNSAFE"),
            "archive output under the registered live map root is denied before a write");

        choices.Enqueue(sourceRoot);
        choices.Enqueue(outputRoot);

        var preview = await Call(bridge, "cluster.multiHostBackupPreview");
        Check(preview.GetProperty("ok").GetBoolean() &&
              preview.GetProperty("data").GetProperty("maps").GetInt32() == 1 &&
              preview.GetProperty("data").GetProperty("rconUsed").GetBoolean() == false,
            "native preview reads only the offline catalog and current registry");
        string staleToken = preview.GetProperty("data").GetProperty("token").GetString()!;
        service.Registry.UpdateCluster(clusterId, "Synthetic cluster", transferRoot, backupRoot);
        var staleApproval = await Call(bridge, "cluster.multiHostBackupApply",
            new { token = staleToken, confirm = true });
        Check(Error(staleApproval, "UM-MULTIHOST-BACKUP-REGISTRY-CHANGED"),
            "registry revision change after preview invalidates the stale backup approval");

        choices.Enqueue(sourceRoot);
        choices.Enqueue(outputRoot);
        preview = await Call(bridge, "cluster.multiHostBackupPreview");
        Check(preview.GetProperty("ok").GetBoolean(),
            "fresh preview is available after the prior operator-approved registry update");
        string token = preview.GetProperty("data").GetProperty("token").GetString()!;
        var noApproval = await Call(bridge, "cluster.multiHostBackupApply", new { token });
        Check(!noApproval.GetProperty("ok").GetBoolean(),
            "archive creation requires explicit confirmation after native preview");
        var started = await Call(bridge, "cluster.multiHostBackupApply", new { token, confirm = true });
        Check(started.GetProperty("ok").GetBoolean(),
            "approved offline archive starts without touching a server process");
        string jobId = started.GetProperty("data").GetProperty("jobId").GetString()!;
        var finished = await WaitFor(bridge, "cluster.multiHostBackupStatus", jobId);
        Check(finished.GetProperty("data").GetProperty("state").GetString() == "completed" &&
              File.ReadAllText(liveMarker) == "synthetic test marker only",
            "multi-host backup job completes and registered map marker stays byte-identical");
        string archivePath = Directory.GetDirectories(outputRoot).Single();
        var archive = MultiHostClusterBackupArchive.Verify(archivePath);
        Check(archive.Maps.Length == 1 && archive.Components.Length > 0,
            "native bridge publishes an archive verified by the shared Core reader");

        // Preserve an existing 0 KB character profile EXACTLY in a separate
        // offline N-host archive. This is not proof that the character is
        // healthy, and the restored copy must remain inside a TEMP sandbox.
        string zeroProfile = Path.Combine(sourceRoot, "maps", map.Id.ToString("N"),
            "Saved", "player.arkprofile");
        string zeroWorld = Path.Combine(sourceRoot, "maps", map.Id.ToString("N"),
            "Saved", "world.ark");
        byte[] originalProfileBytes = File.ReadAllBytes(zeroProfile);
        byte[] originalWorldBytes = File.ReadAllBytes(zeroWorld);
        byte[] originalCatalogBytes = File.ReadAllBytes(catalogPath);
        string zeroArchivePath = Path.Combine(outputRoot, "zero-profile-preserved");
        string zeroSandbox = Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(zeroProfile, Array.Empty<byte>());
            File.WriteAllText(catalogPath, JsonSerializer.Serialize(
                catalogModel with { BatchId = Guid.NewGuid() }));
            var zeroPreview = MultiHostClusterBackupArchive.InspectCatalog(catalogPath);
            Check(zeroPreview.Components == archive.Components.Length,
                "zero-byte profile is included in the offline source catalog without deletion");
            var preserved = MultiHostClusterBackupArchive.BuildFromCatalog(
                catalogPath, zeroArchivePath);
            var profileComponent = MultiHostClusterBackupArchive.Verify(zeroArchivePath)
                .Components.Single(x => x.Kind == MultiHostBackupComponentKind.MapProfile);
            Check(profileComponent.Length == 0 &&
                  profileComponent.Sha256 ==
                    "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855" &&
                  !preserved.IsProductionRestoreCertified,
                "zero-byte arkprofile is SHA-256 verified without claiming valid character recovery");
            MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(zeroArchivePath, zeroSandbox);
            string zeroRestored = Path.Combine(zeroSandbox, "data", "hosts",
                catalogModel.Hosts[0].HostId, "maps", map.Id.ToString("N"),
                "Saved", "player.arkprofile");
            Check(File.Exists(zeroRestored) && new FileInfo(zeroRestored).Length == 0 &&
                  MultiHostClusterBackupArchive.VerifyIsolatedSandbox(zeroSandbox)
                      .SourceBatchId == preserved.BatchId &&
                  new FileInfo(zeroProfile).Length == 0,
                "TEMP-only restore retains the exact 0 KB profile without touching its offline source");
            File.WriteAllBytes(zeroWorld, Array.Empty<byte>());
            bool emptyWorldRejected = false;
            try
            {
                _ = MultiHostClusterBackupArchive.BuildFromCatalog(catalogPath,
                    Path.Combine(outputRoot, "invalid-empty-world"));
            }
            catch (InvalidDataException) { emptyWorldRejected = true; }
            Check(emptyWorldRejected && new FileInfo(zeroProfile).Length == 0,
                "empty world hard-fails archive publication while zero-byte profile remains preserved");
        }
        finally
        {
            File.WriteAllBytes(zeroProfile, originalProfileBytes);
            File.WriteAllBytes(zeroWorld, originalWorldBytes);
            File.WriteAllBytes(catalogPath, originalCatalogBytes);
            if (Directory.Exists(zeroSandbox)) Directory.Delete(zeroSandbox, true);
        }

        string replacementSourceWorld = Path.Combine(sourceRoot, "maps", map.Id.ToString("N"),
            "Saved", "world.ark");
        byte[] originalWorld = File.ReadAllBytes(replacementSourceWorld);
        byte[] originalCatalog = File.ReadAllBytes(catalogPath);
        string replacementOutputRoot = Path.Combine(root, "replacement-archive-output");
        Directory.CreateDirectory(replacementOutputRoot);
        string replacementArchivePath = Path.Combine(replacementOutputRoot, "replacement-archive");
        MultiHostBackupManifest replacementArchive;
        try
        {
            File.WriteAllText(replacementSourceWorld, "synthetic replacement world bytes");
            var replacementCatalog = catalogModel with { BatchId = Guid.NewGuid() };
            File.WriteAllText(catalogPath, JsonSerializer.Serialize(replacementCatalog));
            replacementArchive = MultiHostClusterBackupArchive.BuildFromCatalog(
                catalogPath, replacementArchivePath);
        }
        finally
        {
            File.WriteAllBytes(replacementSourceWorld, originalWorld);
            File.WriteAllBytes(catalogPath, originalCatalog);
        }
        Check(replacementArchive.BatchId != archive.BatchId &&
              MultiHostClusterBackupArchive.Verify(replacementArchivePath).BatchId == replacementArchive.BatchId,
            "native replacement uses a distinct, verified synthetic archive batch");

        choices.Enqueue(archivePath);
        var restorePreview = await Call(bridge, "cluster.multiHostRestorePreview");
        Check(restorePreview.GetProperty("ok").GetBoolean() &&
              restorePreview.GetProperty("data").GetProperty("productionRestoreAllowed").GetBoolean() == false,
            "restore preview verifies current registry ownership and identifies the TEMP-only target");
        string restoreToken = restorePreview.GetProperty("data").GetProperty("token").GetString()!;
        service.Registry.UpdateCluster(clusterId, "Changed synthetic cluster", transferRoot, backupRoot);
        var staleRestore = await Call(bridge, "cluster.multiHostRestoreApply",
            new { token = restoreToken, confirm = true });
        Check(Error(staleRestore, "UM-MULTIHOST-RESTORE-REGISTRY-CHANGED"),
            "registry revision change invalidates a previously reviewed TEMP restore");
        choices.Enqueue(archivePath);
        var changedOwnership = await Call(bridge, "cluster.multiHostRestorePreview");
        Check(Error(changedOwnership, "UM-MULTIHOST-RESTORE-OWNERSHIP-MISMATCH"),
            "archive preview rejects a cluster name that differs from current ClusterRegistry");
        service.Registry.UpdateCluster(clusterId, "Synthetic cluster", transferRoot, backupRoot);
        choices.Enqueue(archivePath);
        restorePreview = await Call(bridge, "cluster.multiHostRestorePreview");
        Check(restorePreview.GetProperty("ok").GetBoolean(),
            "fresh TEMP restore preview is available after current registry identity is restored");
        restoreToken = restorePreview.GetProperty("data").GetProperty("token").GetString()!;
        var restoreStart = await Call(bridge, "cluster.multiHostRestoreApply",
            new { token = restoreToken, confirm = true });
        Check(restoreStart.GetProperty("ok").GetBoolean() &&
              restoreStart.GetProperty("data").GetProperty("target").GetString() == "NEW TEMP SANDBOX ONLY",
            "approved restore starts only in an isolated TEMP sandbox");
        string restoreJob = restoreStart.GetProperty("data").GetProperty("jobId").GetString()!;
        var restoreDone = await WaitFor(bridge, "cluster.multiHostRestoreStatus", restoreJob);
        string sandboxName = restoreDone.GetProperty("data").GetProperty("artifactName").GetString()!;
        Check(restoreDone.GetProperty("data").GetProperty("state").GetString() == "completed" &&
              Directory.Exists(Path.Combine(Path.GetTempPath(), sandboxName)) &&
              File.ReadAllText(liveMarker) == "synthetic test marker only",
            "sandbox restore verifies bytes while the original map path remains untouched");
        Directory.Delete(Path.Combine(Path.GetTempPath(), sandboxName), true);

        string replacementTarget = Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-" + Guid.NewGuid().ToString("N"));
        MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(archivePath, replacementTarget);
        choices.Enqueue(replacementArchivePath);
        choices.Enqueue(liveMapRoot);
        var unsafeReplacement = await Call(bridge, "cluster.multiHostRestoreReplacePreview");
        Check(!unsafeReplacement.GetProperty("ok").GetBoolean(),
            "replacement preview refuses a registered server installation as its destination");

        choices.Enqueue(replacementArchivePath);
        choices.Enqueue(replacementTarget);
        var replacementPreview = await Call(bridge, "cluster.multiHostRestoreReplacePreview");
        Check(replacementPreview.GetProperty("ok").GetBoolean() &&
              replacementPreview.GetProperty("data").GetProperty("restoreTarget").GetString() ==
                "EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK" &&
              replacementPreview.GetProperty("data").GetProperty("previousBatchId").GetString() ==
                archive.BatchId.ToString(),
            "native replacement preview binds the verified current sandbox and replacement batch");
        string replacementToken = replacementPreview.GetProperty("data").GetProperty("token").GetString()!;
        var replacementStart = await Call(bridge, "cluster.multiHostRestoreReplaceApply",
            new { token = replacementToken, confirm = true });
        Check(replacementStart.GetProperty("ok").GetBoolean() &&
              replacementStart.GetProperty("data").GetProperty("target").GetString() ==
                "EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK",
            "native bridge starts only the explicitly approved, sandbox-scoped replacement transaction");
        string replacementJob = replacementStart.GetProperty("data").GetProperty("jobId").GetString()!;
        var replacementDone = await WaitFor(bridge, "cluster.multiHostRestoreStatus", replacementJob);
        string rollbackName = replacementDone.GetProperty("data").GetProperty("rollbackArtifactName").GetString()!;
        string rollbackPath = Path.Combine(Path.GetTempPath(), rollbackName);
        Check(replacementDone.GetProperty("data").GetProperty("state").GetString() == "completed" &&
              replacementDone.GetProperty("data").GetProperty("stage").GetString() ==
                "TEMP_SANDBOX_REPLACED_ROLLBACK_RETAINED" &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(replacementTarget).SourceBatchId ==
                replacementArchive.BatchId &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(rollbackPath).SourceBatchId ==
                archive.BatchId && File.ReadAllText(liveMarker) == "synthetic test marker only",
            "native replacement verifies the new target, retains the previous batch for rollback, and leaves the registered map unchanged");

        var recoveryListing = await Call(bridge, "cluster.multiHostRestoreRecoveryList");
        string recoveryTargetName = Path.GetFileName(replacementTarget);
        Check(recoveryListing.GetProperty("ok").GetBoolean() &&
              recoveryListing.GetProperty("data").GetProperty("transactions").EnumerateArray()
                  .Any(candidate => candidate.GetProperty("targetDirectoryName").GetString() == recoveryTargetName),
            "native recovery listing discovers the durable TEMP journal without opening a folder picker");
        var invalidRecoveryTarget = await Call(bridge, "cluster.multiHostRestoreRecoveryPreview",
            new { targetDirectoryName = liveMapRoot });
        Check(Error(invalidRecoveryTarget, "UM-MULTIHOST-RESTORE-RECOVERY-CANDIDATE-INVALID"),
            "native recovery preview rejects arbitrary or registered filesystem paths");
        var recoveryPreview = await Call(bridge, "cluster.multiHostRestoreRecoveryPreview",
            new { targetDirectoryName = recoveryTargetName });
        Check(recoveryPreview.GetProperty("ok").GetBoolean() &&
              recoveryPreview.GetProperty("data").GetProperty("phase").GetString() == "committed" &&
              recoveryPreview.GetProperty("data").GetProperty("canRollback").GetBoolean() &&
              recoveryPreview.GetProperty("data").GetProperty("protectiveCopyName").GetString() == rollbackName,
            "native recovery preview reattaches to the durable journal and verified protective copy");
        string recoveryToken = recoveryPreview.GetProperty("data").GetProperty("token").GetString()!;
        var missingRecoveryApproval = await Call(bridge, "cluster.multiHostRestoreRecoveryApply",
            new { token = recoveryToken });
        Check(!missingRecoveryApproval.GetProperty("ok").GetBoolean(),
            "native sandbox rollback requires a separate explicit operator confirmation");
        var recoveryStart = await Call(bridge, "cluster.multiHostRestoreRecoveryApply",
            new { token = recoveryToken, confirm = true });
        Check(recoveryStart.GetProperty("ok").GetBoolean() &&
              recoveryStart.GetProperty("data").GetProperty("target").GetString() ==
                "TEMP SANDBOX ROLLBACK ONLY",
            "native bridge starts only the selected TEMP transaction rollback after approval");
        string recoveryJob = recoveryStart.GetProperty("data").GetProperty("jobId").GetString()!;
        var recoveryDone = await WaitFor(bridge, "cluster.multiHostRestoreStatus", recoveryJob);
        string quarantinedName = recoveryDone.GetProperty("data").GetProperty("rollbackArtifactName").GetString()!;
        string quarantinedPath = Path.Combine(Path.GetTempPath(), quarantinedName);
        Check(recoveryDone.GetProperty("data").GetProperty("state").GetString() == "completed" &&
              recoveryDone.GetProperty("data").GetProperty("stage").GetString() == "TEMP_SANDBOX_ROLLED_BACK" &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(replacementTarget).SourceBatchId == archive.BatchId &&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(quarantinedPath).SourceBatchId == replacementArchive.BatchId &&
              File.ReadAllText(liveMarker) == "synthetic test marker only",
            "native rollback restores the protective batch and keeps the replaced clone in a verified TEMP quarantine");
        Directory.Delete(replacementTarget, true);
        Directory.Delete(quarantinedPath, true);
        File.Delete(replacementTarget + ".replace-transaction-v1.json");

        // Two independent Manager processes must not both publish into the
        // same TEMP sandbox. The OS handle lease is exercised *inside* an
        // active replacement stage: a pending journal check alone is not proof.
        var ownership = new MultiHostBackupOwnershipSnapshot(
            catalogModel.Hosts[0].HostId,
            catalogModel.Hosts.Select(h =>
                new MultiHostBackupHostOwnership(h.HostId,h.Name,true,false)).ToArray(),
            catalogModel.Clusters.Select(c =>
                new MultiHostBackupClusterOwnership(c.ClusterId,c.Name)).ToArray(),
            catalogModel.Maps.Select(m =>
                new MultiHostBackupMapOwnership(m.MapId,m.HostId,m.Name,m.ClusterId)).ToArray());
        string exclusiveTarget = Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-" + Guid.NewGuid().ToString("N"));
        MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(archivePath,exclusiveTarget);
        bool concurrentWriterDenied=false,concurrentRollbackDenied=false;
        bool previewJournalDenied=false,inspectionBlocked=false;
        var exclusiveReceipt=MultiHostSandboxRestoreTransaction.ReplaceVerifiedSandbox(
            replacementArchivePath,exclusiveTarget,ownership,true,
            afterPhaseForTesting:phase=>{
                if(phase!="prepared")return;
                try
                {
                    _=MultiHostSandboxRestoreTransaction.PreviewReplacement(
                        replacementArchivePath,exclusiveTarget,ownership);
                }
                catch(IOException ex) when(ex.Message.Contains(
                    "UM-TEMP-RESTORE-PENDING-JOURNAL",StringComparison.Ordinal))
                { previewJournalDenied=true; }
                try
                {
                    _=MultiHostSandboxRestoreTransaction.ReplaceVerifiedSandbox(
                        replacementArchivePath,exclusiveTarget,ownership,true);
                }
                catch(IOException ex) when(ex.Message.Contains(
                    "UM-TEMP-RESTORE-LEASE-BUSY",StringComparison.Ordinal))
                { concurrentWriterDenied=true; }
                try
                {
                    _=MultiHostSandboxRestoreTransaction.Rollback(
                        exclusiveTarget,Guid.NewGuid(),true);
                }
                catch(IOException ex) when(ex.Message.Contains(
                    "UM-TEMP-RESTORE-LEASE-BUSY",StringComparison.Ordinal))
                { concurrentRollbackDenied=true; }
                var busy=MultiHostSandboxRestoreTransaction.InspectRecovery(exclusiveTarget);
                inspectionBlocked=busy.NeedsOperator&&!busy.CanRollback&&busy.Phase=="busy";
            });
        Check(previewJournalDenied&&concurrentWriterDenied&&
              concurrentRollbackDenied&&inspectionBlocked&&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(exclusiveTarget)
                  .SourceBatchId==replacementArchive.BatchId,
            "OS lease blocks concurrent TEMP replace/rollback and read-only recovery never offers an active transaction");
        Check(!File.Exists(exclusiveTarget+".replace-transaction-v1.json.lease"),
            "successful TEMP transaction automatically releases its OS deletion-on-close lease");
        bool committedPreviewDenied=false;
        try
        {
            _=MultiHostSandboxRestoreTransaction.PreviewReplacement(
                replacementArchivePath,exclusiveTarget,ownership);
        }
        catch(IOException ex) when(ex.Message.Contains(
            "UM-TEMP-RESTORE-PENDING-JOURNAL",StringComparison.Ordinal))
        { committedPreviewDenied=true; }
        Check(committedPreviewDenied,
            "a second TEMP replacement cannot discard the durable committed rollback journal");
        var exclusiveRollback=MultiHostSandboxRestoreTransaction.Rollback(
            exclusiveTarget,exclusiveReceipt.TransactionId,true);
        Check(exclusiveRollback.Phase=="rolledBack"&&
              MultiHostClusterBackupArchive.VerifyIsolatedSandbox(exclusiveTarget)
                  .SourceBatchId==archive.BatchId&&
              !File.Exists(exclusiveTarget+".replace-transaction-v1.json.lease"),
            "the committed TEMP transaction can be explicitly rolled back after the exclusive lease is released");
        if(exclusiveRollback.QuarantinedReplacementName is string quarantined)
        {
            string quarantinedRoot=Path.Combine(Path.GetTempPath(),quarantined);
            if(Directory.Exists(quarantinedRoot))Directory.Delete(quarantinedRoot,true);
        }
        Directory.Delete(exclusiveTarget,true);
        File.Delete(exclusiveTarget+".replace-transaction-v1.json");

        // Different actual Windows processes (not callbacks/threads) compete
        // on the SAME TEMP target. Kill the holder at previousMoved, verify the
        // OS releases its lock and the old batch can be restored via journal.
        await MultiHostTempProcessLeaseTests.Run(root, archivePath,
            replacementArchivePath, ownership);

        // A previously copied offline file changing while the rest of the
        // N-host batch is assembled must fail closed without ever publishing
        // an archive. Even same-length replacements must be detectable via
        // the original source's observed write epoch.
        string offlineWorld = Path.Combine(sourceRoot, "maps", map.Id.ToString("N"),
            "Saved", "world.ark");
        byte[] protectedWorldBytes = File.ReadAllBytes(offlineWorld);
        DateTime protectedWorldWrite = File.GetLastWriteTimeUtc(offlineWorld);
        string movingSourceBatch = Path.Combine(outputRoot, "unpublished-source-change");
        bool epochRejected = false;
        try
        {
            try
            {
                _ = MultiHostClusterBackupArchive.BuildFromCatalog(
                    catalogPath, movingSourceBatch,
                    afterCopiedForTesting: componentIndex =>
                    {
                        if (componentIndex != catalogModel.Items.Length - 1) return;
                        // The world was already SHA-256 copied at this point.
                        // Change one original source even though every other
                        // copy and journal checkpoint has completed.
                        File.WriteAllBytes(offlineWorld,
                            protectedWorldBytes.Concat(new byte[] { 0x41 }).ToArray());
                        File.SetLastWriteTimeUtc(offlineWorld,
                            protectedWorldWrite.AddMinutes(1));
                    });
            }
            catch (IOException ex) when (ex.Message.Contains(
                "UM-MULTIHOST-OFFLINE-SOURCE-EPOCH-CHANGED", StringComparison.Ordinal))
            { epochRejected = true; }
            Check(epochRejected && !Directory.Exists(movingSourceBatch) &&
                  Directory.Exists(movingSourceBatch + ".incomplete") &&
                  MultiHostClusterBackupArchive.InspectInterrupted(movingSourceBatch).NeedsOperator,
                "changed offline world bytes after copy fail closed; N-host batch remains quarantined and cannot be published");
        }
        finally
        {
            File.WriteAllBytes(offlineWorld, protectedWorldBytes);
            File.SetLastWriteTimeUtc(offlineWorld, protectedWorldWrite);
            if (Directory.Exists(movingSourceBatch + ".incomplete"))
                Directory.Delete(movingSourceBatch + ".incomplete", true);
        }
        Check(File.ReadAllBytes(offlineWorld).SequenceEqual(protectedWorldBytes),
            "offline mutation fixture restores the original source and never modifies the registered live map");

        string[] liveFiles = Directory.GetFiles(liveMapRoot, "*", SearchOption.AllDirectories);
        Check(liveFiles.Length == 1 && liveFiles[0] == liveMarker && File.ReadAllText(liveMarker) == "synthetic test marker only",
            "restore and backup never create files under the registered server installation");
        _ = catalogPath;
    }
}
