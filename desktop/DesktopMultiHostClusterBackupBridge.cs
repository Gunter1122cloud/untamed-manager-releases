using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Offline multi-host backup and isolated restore routes for the Obsidian
/// WebView. It reads only operator-prepared copies outside registered ASA,
/// Store-v1 and transfer roots. Restore writes only a new TEMP sandbox.
/// </summary>
public sealed class DesktopMultiHostClusterBackupBridge : IDisposable
{
    private sealed record CatalogApproval(Guid Token,string Root,
        string Fingerprint,long RegistryRevision,DateTimeOffset ExpiresUtc);
    private CatalogApproval? catalogApproval;
    private sealed record BackupApproval(Guid Token, string CatalogFile,
        string CatalogRoot, string Parent, Guid BatchId, long RegistryRevision,
        DateTimeOffset ExpiresUtc);
    private sealed record RestoreApproval(Guid Token, string Archive,
        Guid BatchId, long RegistryRevision, DateTimeOffset ExpiresUtc,
        string? ExistingSandbox);
    private sealed record RecoveryApproval(Guid Token, string ExistingSandbox,
        Guid TransactionId, DateTimeOffset ExpiresUtc);
    private const string RestoreSandboxPrefix = "Untamed-MultiHost-Restore-";
    private sealed class Job
    {
        public Guid Id = Guid.NewGuid();
        public string Kind = "backup";
        public string State = "queued";
        public string Stage = "PREPARED";
        public string ArtifactName = "";
        public string RollbackArtifactName = "";
        public int Hosts;
        public int Clusters;
        public int Maps;
        public int Components;
        public long Bytes;
        public DateTimeOffset ChangedUtc = DateTimeOffset.UtcNow;
    }

    private readonly ClusterService cluster;
    private readonly Func<string, string?> pickFolder;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private BackupApproval? backupApproval;
    private RestoreApproval? restoreApproval;
    private RecoveryApproval? recoveryApproval;
    private Job? current;
    private CancellationTokenSource? currentCancellation;
    private bool disposed;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string CatalogFileName = "multi-host-backup-catalog.json";

    public DesktopMultiHostClusterBackupBridge(ClusterService clusterService,
        Func<string, string?>? directoryPicker = null)
    {
        cluster = clusterService ?? throw new ArgumentNullException(nameof(clusterService));
        pickFolder = directoryPicker ?? PickNative;
    }

    private static string? PickNative(string label)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = label,
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Local folder is required");
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new InvalidDataException("Fixed local folder is required");
        return full;
    }

    private static bool Within(string parent, string child) =>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase);

    private static string[] ProtectedRoots(ClusterService service)
    {
        var paths = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return;
            try { paths.Add(Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar)); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        foreach (var map in service.Config.LocalMaps)
            Add(map.InstallationPath);
        foreach (var registeredCluster in service.Config.ClusterRegistry.Clusters)
        {
            Add(registeredCluster.TransferDirectory);
            Add(registeredCluster.BackupDirectory);
        }
        Add(service.Store.Root);
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsProtected(string path, IReadOnlyCollection<string> roots) =>
        roots.Any(root => Within(root, path));

    private static string CatalogAt(string root)
    {
        string full = LocalPath(root);
        string file = Path.Combine(full, CatalogFileName);
        if (!File.Exists(file)) throw new FileNotFoundException("Offline copy catalog is missing", CatalogFileName);
        return file;
    }

    private static void Set(Job job, string state, string stage,
        MultiHostBackupCatalogPreview? preview = null, string artifactName = "")
    {
        lock (job)
        {
            job.State = state;
            job.Stage = stage;
            job.ArtifactName = artifactName;
            if (preview != null)
            {
                job.Hosts = preview.Hosts;
                job.Clusters = preview.Clusters;
                job.Maps = preview.Maps;
                job.Components = preview.Components;
                job.Bytes = preview.Bytes;
            }
            job.ChangedUtc = DateTimeOffset.UtcNow;
        }
    }

    private static object Snapshot(Job job)
    {
        lock (job)
            return new
            {
                jobId = job.Id,
                kind = job.Kind,
                state = job.State,
                stage = job.Stage,
                artifactName = job.ArtifactName,
                rollbackArtifactName = job.RollbackArtifactName,
                hosts = job.Hosts,
                clusters = job.Clusters,
                maps = job.Maps,
                components = job.Components,
                bytes = job.Bytes,
                changedUtc = job.ChangedUtc,
                productionRestoreAllowed = false,
                liveConsistencyCertified = false,
                storeWriterCertified = false,
                sqlUsed = false,
                rconUsed = false,
                serverProcessTouched = false,
                evidence = job.Kind == "restore"
                    ? "ISOLATED_TEMP_RESTORE_TRANSACTION_SHA256_VERIFIED"
                    : "OFFLINE_MULTI_HOST_COPY_SHA256_VERIFIED"
            };
    }

    private static MultiHostBackupOwnershipSnapshot OwnershipSnapshot(ClusterRegistrySnapshot registry)
    {
        var assignments = registry.ClusterRegistry.MapAssignments.GroupBy(x => x.MapId)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single().ClusterId :
                throw new InvalidDataException("Duplicate ClusterRegistry map assignment"));
        var maps = registry.HostRegistry.Maps.Select(map => new MultiHostBackupMapOwnership(
            map.MapId, map.HostId, map.MapName,
            assignments.TryGetValue(map.MapId, out string? clusterId) ? clusterId : null)).ToArray();
        return new(registry.HostRegistry.LocalHostId,
            registry.HostRegistry.Hosts.Select(host => new MultiHostBackupHostOwnership(
                host.HostId, host.HostName, host.IsLocal, host.IsPaired)).ToArray(),
            registry.ClusterRegistry.Clusters.Select(cluster => new MultiHostBackupClusterOwnership(
                cluster.ClusterId, cluster.Name)).ToArray(), maps);
    }

    private MultiHostBackupCatalogPreview Inspect(string catalogFile, ClusterRegistrySnapshot registry)
    {
        string[] protectedRoots = ProtectedRoots(cluster);
        return MultiHostClusterBackupArchive.InspectCatalog(catalogFile,
            OwnershipSnapshot(registry), protectedRoots);
    }

    private static string Ok(string id, object data) => JsonSerializer.Serialize(new { id, ok = true, data }, Json);
    private static string Fail(string? id, string code) => JsonSerializer.Serialize(new { id, ok = false, error = code }, Json);

    public async Task<string> HandleAsync(string raw)
    {
        string? id = null;
        try
        {
            if (disposed || raw.Length is < 2 or > 4096) return Fail(null, "UM-MULTIHOST-BACKUP-REQUEST");
            using var document = JsonDocument.Parse(raw);
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object ||
                !request.TryGetProperty("id", out var requestId) || requestId.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(requestId.GetString(), out var parsedId) || parsedId == Guid.Empty ||
                !request.TryGetProperty("method", out var methodRaw) || methodRaw.ValueKind != JsonValueKind.String)
                return Fail(null, "UM-MULTIHOST-BACKUP-REQUEST");
            id = parsedId.ToString("D");
            string? method = methodRaw.GetString();
            if (method is not ("cluster.multiHostCatalogPreview" or "cluster.multiHostCatalogApply" or
                "cluster.multiHostBackupPreview" or "cluster.multiHostBackupApply" or
                "cluster.multiHostBackupStatus" or "cluster.multiHostRestorePreview" or
                "cluster.multiHostRestoreApply" or "cluster.multiHostRestoreStatus" or
                "cluster.multiHostRestoreReplacePreview" or "cluster.multiHostRestoreReplaceApply" or
                "cluster.multiHostRestoreRecoveryList" or "cluster.multiHostRestoreRecoveryPreview" or
                "cluster.multiHostRestoreRecoveryApply"))
                return Fail(id, "UM-METHOD-DENIED");

            await gate.WaitAsync();
            try
            {
                if (method == "cluster.multiHostCatalogPreview")
                {
                    catalogApproval=null;
                    string? path=pickFolder("Válaszd ki a már leállított szerverekről ELKÜLÖNÍTETTEN kimásolt host/map/cluster/transfer mappákat");
                    if(string.IsNullOrWhiteSpace(path))
                        return Fail(id,"UM-MULTIHOST-AUTOCATALOG-CANCELLED");
                    string full=LocalPath(path);
                    var registry=cluster.Registry.Snapshot();
                    var inspected=MultiHostOfflineCatalogAssembler.Inspect(
                        full,OwnershipSnapshot(registry),ProtectedRoots(cluster));
                    catalogApproval=new CatalogApproval(Guid.NewGuid(),full,
                        inspected.Fingerprint,registry.Revision,DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id,new{
                        token=catalogApproval.Token,
                        hosts=inspected.Catalog.Hosts.Length,
                        clusters=inspected.Catalog.Clusters.Length,
                        maps=inspected.Catalog.Maps.Length,
                        components=inspected.FileCount,bytes=inspected.Bytes,
                        copiedTransferIncluded=inspected.Catalog.Items.Any(x=>
                            x.Kind==MultiHostBackupComponentKind.ClusterTransferFile),
                        copiedProfilesIncluded=inspected.Catalog.Items.Any(x=>
                            x.Kind==MultiHostBackupComponentKind.MapProfile),
                        copiedTribesIncluded=inspected.Catalog.Items.Any(x=>
                            x.Kind==MultiHostBackupComponentKind.MapTribe),
                        requiresExplicitApproval=true,offlineCopiesOnly=true,
                        liveConsistencyCertified=false,storeWriterCertified=false,
                        productionRestoreAllowed=false,originalStoreTouched=false,
                        evidence="CURRENT_NHOST_REGISTRY_AND_COMPLETE_OFFLINE_SOURCE_INVENTORY"
                    });
                }
                if (method == "cluster.multiHostCatalogApply")
                {
                    if(catalogApproval==null||
                       !request.TryGetProperty("token",out var ticket)||
                       ticket.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(ticket.GetString(),out var provided)||
                       catalogApproval.Token!=provided||
                       !request.TryGetProperty("confirm",out var consent)||
                       consent.ValueKind!=JsonValueKind.True)
                        return Fail(id,"UM-MULTIHOST-AUTOCATALOG-APPROVAL");
                    var approved=catalogApproval;
                    catalogApproval=null;
                    if(approved.ExpiresUtc<DateTimeOffset.UtcNow)
                        return Fail(id,"UM-MULTIHOST-AUTOCATALOG-EXPIRED");
                    var registry=cluster.Registry.Snapshot();
                    if(registry.Revision!=approved.RegistryRevision)
                        return Fail(id,"UM-MULTIHOST-AUTOCATALOG-REGISTRY-CHANGED");
                    string[] forbidden=ProtectedRoots(cluster);
                    var catalog=MultiHostOfflineCatalogAssembler.Publish(
                        approved.Root,OwnershipSnapshot(registry),
                        approved.Fingerprint,forbidden);
                    var preview=Inspect(Path.Combine(approved.Root,CatalogFileName),registry);
                    if(preview.BatchId!=catalog.BatchId||
                       preview.Components!=catalog.Items.Length)
                        throw new IOException("UM-MULTIHOST-AUTOCATALOG-FINAL-CHECK");
                    return Ok(id,new{
                        created=true,batchId=catalog.BatchId,hosts=preview.Hosts,
                        clusters=preview.Clusters,maps=preview.Maps,
                        components=preview.Components,bytes=preview.Bytes,
                        catalogName=CatalogFileName,offlineCopiesOnly=true,
                        originalStoreTouched=false,productionRestoreAllowed=false,
                        evidence="EXISTING_MULTIHOST_ARCHIVE_V1_VALIDATED_CATALOG_CREATED"
                    });
                }
                if (method is "cluster.multiHostBackupStatus" or "cluster.multiHostRestoreStatus")
                {
                    if (!request.TryGetProperty("jobId", out var jobRaw) || jobRaw.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(jobRaw.GetString(), out var jobId) || current == null || current.Id != jobId)
                        return Fail(id, "UM-MULTIHOST-BACKUP-JOB-NOT-FOUND");
                    bool expectsRestore = method == "cluster.multiHostRestoreStatus";
                    if (expectsRestore != (current.Kind == "restore"))
                        return Fail(id, "UM-MULTIHOST-BACKUP-JOB-KIND");
                    return Ok(id, Snapshot(current));
                }

                if (method == "cluster.multiHostRestoreRecoveryList")
                    return Ok(id, MultiHostSandboxRestoreTransaction.ListRecoveryCandidates());

                if (method == "cluster.multiHostBackupPreview")
                {
                    backupApproval = null;
                    string? inputRoot = pickFolder("Válaszd ki a külön elkészített OFFLINE host/map/cluster másolatokat és a multi-host-backup-catalog.json fájlt tartalmazó mappát");
                    if (string.IsNullOrWhiteSpace(inputRoot)) return Fail(id, "UM-MULTIHOST-BACKUP-CANCELLED");
                    string catalog = CatalogAt(inputRoot);
                    var registry = cluster.Registry.Snapshot();
                    var preview = Inspect(catalog, registry);
                    string? outputParent = pickFolder("Válassz egy új, helyi archívum célmappát az OFFLINE másolathoz");
                    if (string.IsNullOrWhiteSpace(outputParent)) return Fail(id, "UM-MULTIHOST-BACKUP-CANCELLED");
                    string parent = LocalPath(outputParent);
                    string sourceRoot = LocalPath(inputRoot);
                    string[] protectedRoots = ProtectedRoots(cluster);
                    if (!Directory.Exists(parent) || IsProtected(parent, protectedRoots) ||
                        Within(sourceRoot, parent) || Within(parent, sourceRoot))
                        return Fail(id, "UM-MULTIHOST-BACKUP-DESTINATION-UNSAFE");
                    var token = Guid.NewGuid();
                    backupApproval = new(token, catalog, sourceRoot, parent,
                        preview.BatchId, registry.Revision, DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id, new
                    {
                        token,
                        batchId = preview.BatchId,
                        hosts = preview.Hosts,
                        clusters = preview.Clusters,
                        maps = preview.Maps,
                        components = preview.Components,
                        bytes = preview.Bytes,
                        archiveName = "Untamed-MultiHost-" + preview.BatchId.ToString("N"),
                        requiresExplicitApproval = true,
                        offlineCopiesOnly = true,
                        liveConsistencyCertified = false,
                        storeWriterCertified = false,
                        sqlUsed = false,
                        rconUsed = false,
                        serverProcessTouched = false,
                        productionRestoreAllowed = false,
                        evidence = "NATIVE_CATALOG_AND_CURRENT_REGISTRY_PREFLIGHT"
                    });
                }

                if (method is "cluster.multiHostRestorePreview" or "cluster.multiHostRestoreReplacePreview")
                {
                    restoreApproval = null;
                    string? archive = pickFolder("Válaszd ki az ellenőrzött multi-host offline archívumot (csak TEMP sandboxba állítható vissza)");
                    if (string.IsNullOrWhiteSpace(archive)) return Fail(id, "UM-MULTIHOST-RESTORE-CANCELLED");
                    bool replaceSandbox = method == "cluster.multiHostRestoreReplacePreview";
                    string? existingSandbox = null;
                    if (replaceSandbox)
                    {
                        string? selected = pickFolder("Select the verified existing TEMP restore sandbox to replace");
                        if (string.IsNullOrWhiteSpace(selected)) return Fail(id, "UM-MULTIHOST-RESTORE-CANCELLED");
                        existingSandbox = LocalPath(selected);
                    }
                    var registry = cluster.Registry.Snapshot();
                    var ownership = OwnershipSnapshot(registry);
                    var manifest = replaceSandbox
                        ? MultiHostSandboxRestoreTransaction.PreviewReplacement(archive, existingSandbox!, ownership)
                        : MultiHostClusterBackupArchive.PreviewSandboxRestore(archive, ownership);
                    if (!manifest.PreflightPassed)
                        return Fail(id, "UM-MULTIHOST-RESTORE-OWNERSHIP-MISMATCH");
                    Guid? previousBatchId = replaceSandbox
                        ? MultiHostClusterBackupArchive.VerifyIsolatedSandbox(existingSandbox!).SourceBatchId
                        : null;
                    var token = Guid.NewGuid();
                    restoreApproval = new(token, LocalPath(archive), manifest.BatchId,
                        registry.Revision, DateTimeOffset.UtcNow.AddMinutes(5), existingSandbox);
                    return Ok(id, new
                    {
                        token,
                        batchId = manifest.BatchId,
                        archiveName = Path.GetFileName(Path.TrimEndingDirectorySeparator(archive)),
                        hosts = manifest.Hosts,
                        clusters = manifest.Clusters,
                        maps = manifest.Maps,
                        components = manifest.Components,
                        previousBatchId,
                        targetDirectoryName = existingSandbox == null ? null : Path.GetFileName(existingSandbox),
                        preflightScope = replaceSandbox
                            ? "CURRENT_REGISTRY_IDENTITY_AND_VERIFIED_TEMP_SANDBOX_REPLACEMENT_ONLY"
                            : "CURRENT_REGISTRY_IDENTITY_AND_TEMP_SANDBOX_ONLY",
                        requiresExplicitApproval = true,
                        restoreTarget = replaceSandbox
                            ? "EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK"
                            : "NEW TEMP SANDBOX ONLY",
                        productionRestoreAllowed = false,
                        liveConsistencyCertified = false,
                        storeWriterCertified = false,
                        evidence = "VERIFIED_ARCHIVE_PREVIEW_NO_LIVE_WRITES"
                    });
                }

                if (method == "cluster.multiHostRestoreRecoveryPreview")
                {
                    recoveryApproval = null;
                    if (!request.TryGetProperty("targetDirectoryName", out var targetNameField) ||
                        targetNameField.ValueKind != JsonValueKind.String)
                        return Fail(id, "UM-MULTIHOST-RESTORE-RECOVERY-CANDIDATE-REQUIRED");
                    string targetName = targetNameField.GetString() ?? "";
                    if (targetName.Length == 0 || !StringComparer.Ordinal.Equals(
                            Path.GetFileName(targetName), targetName) ||
                        !targetName.StartsWith(RestoreSandboxPrefix, StringComparison.Ordinal) ||
                        !Guid.TryParseExact(targetName[RestoreSandboxPrefix.Length..], "N", out _))
                        return Fail(id, "UM-MULTIHOST-RESTORE-RECOVERY-CANDIDATE-INVALID");
                    string target = Path.Combine(Path.GetTempPath(), targetName);
                    var status = MultiHostSandboxRestoreTransaction.InspectRecovery(target);
                    Guid? token = null;
                    if (status.CanRollback && status.TransactionId.HasValue)
                    {
                        token = Guid.NewGuid();
                        recoveryApproval = new(token.Value, target, status.TransactionId.Value,
                            DateTimeOffset.UtcNow.AddMinutes(5));
                    }
                    return Ok(id, new
                    {
                        token,
                        targetDirectoryName = Path.GetFileName(target),
                        transactionId = status.TransactionId,
                        phase = status.Phase,
                        needsOperator = status.NeedsOperator,
                        canRollback = status.CanRollback,
                        requiresExplicitApproval = status.CanRollback && token.HasValue,
                        quarantinedReplacementName = status.QuarantinedReplacementName,
                        protectiveCopyName = status.ProtectiveCopyName,
                        reason = status.Reason,
                        productionRestoreAllowed = false,
                        rconUsed = false,
                        serverProcessTouched = false,
                        evidence = "VERIFIED_TEMP_TRANSACTION_JOURNAL_ONLY"
                    });
                }

                if (current != null)
                {
                    lock (current)
                        if (current.State is "queued" or "running")
                            return Fail(id, "UM-MULTIHOST-BACKUP-BUSY");
                }

                bool backup = method == "cluster.multiHostBackupApply";
                bool replaceSandboxApply = method == "cluster.multiHostRestoreReplaceApply";
                bool recoveryApply = method == "cluster.multiHostRestoreRecoveryApply";
                if (!request.TryGetProperty("token", out var tokenField) || tokenField.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(tokenField.GetString(), out var tokenValue) ||
                    !request.TryGetProperty("confirm", out var confirm) || confirm.ValueKind != JsonValueKind.True)
                    return Fail(id, "UM-MULTIHOST-BACKUP-APPROVAL-REQUIRED");

                if (backup)
                {
                    var approval = backupApproval;
                    backupApproval = null;
                    if (approval == null || approval.Token != tokenValue || approval.ExpiresUtc < DateTimeOffset.UtcNow)
                        return Fail(id, "UM-MULTIHOST-BACKUP-APPROVAL-EXPIRED");
                    var registry = cluster.Registry.Snapshot();
                    if (registry.Revision != approval.RegistryRevision)
                        return Fail(id, "UM-MULTIHOST-BACKUP-REGISTRY-CHANGED");
                    string[] protectedRoots = ProtectedRoots(cluster);
                    if (IsProtected(approval.Parent, protectedRoots) ||
                        Within(approval.CatalogRoot, approval.Parent) || Within(approval.Parent, approval.CatalogRoot))
                        return Fail(id, "UM-MULTIHOST-BACKUP-DESTINATION-UNSAFE");
                    var ownership = OwnershipSnapshot(registry);
                    var preview = MultiHostClusterBackupArchive.InspectCatalog(
                        approval.CatalogFile, ownership, protectedRoots);
                    if (preview.BatchId != approval.BatchId)
                        return Fail(id, "UM-MULTIHOST-BACKUP-CATALOG-CHANGED");
                    var job = new Job { Kind = "backup", Hosts = preview.Hosts,
                        Clusters = preview.Clusters, Maps = preview.Maps,
                        Components = preview.Components, Bytes = preview.Bytes };
                    current = job;
                    currentCancellation?.Dispose();
                    currentCancellation = new CancellationTokenSource();
                    var cancellation = currentCancellation.Token;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            Set(job, "running", "COPYING_AND_HASHING_OFFLINE_FILES", preview);
                            cancellation.ThrowIfCancellationRequested();
                            string output = Path.Combine(approval.Parent,
                                "Untamed-MultiHost-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") +
                                "-" + approval.BatchId.ToString("N"));
                            var report = MultiHostClusterBackupArchive.BuildFromCatalog(
                                approval.CatalogFile, output, ownership, protectedRoots, cancellation);
                            Set(job, "completed", "ARCHIVE_SHA256_VERIFIED", preview,
                                Path.GetFileName(output));
                            lock (job)
                            {
                                job.Components = report.Components.Length;
                                job.ChangedUtc = DateTimeOffset.UtcNow;
                            }
                        }
                        catch (OperationCanceledException)
                        { Set(job, "needsOperator", "CANCELLED_PARTIAL_ARCHIVE_REQUIRES_REVIEW", preview); }
                        catch (Exception e) when (e is IOException or InvalidDataException or
                            UnauthorizedAccessException or ArgumentException or OverflowException or JsonException)
                        { Set(job, "needsOperator", "ARCHIVE_FAILED_REVIEW_OFFLINE_INPUT_AND_STAGE", preview); }
                    });
                    return Ok(id, new { jobId = job.Id, started = true,
                        productionRestoreAllowed = false, rconUsed = false,
                        serverProcessTouched = false, evidence = "OFFLINE_COPY_JOB_STARTED" });
                }
                else if (recoveryApply)
                {
                    var approval = recoveryApproval;
                    recoveryApproval = null;
                    if (approval == null || approval.Token != tokenValue ||
                        approval.ExpiresUtc < DateTimeOffset.UtcNow)
                        return Fail(id, "UM-MULTIHOST-RESTORE-RECOVERY-APPROVAL-EXPIRED");
                    var status = MultiHostSandboxRestoreTransaction.InspectRecovery(approval.ExistingSandbox);
                    if (!status.CanRollback || status.TransactionId != approval.TransactionId)
                        return Fail(id, "UM-MULTIHOST-RESTORE-RECOVERY-STATE-CHANGED");
                    var job = new Job { Kind = "restore", Hosts = 0, Clusters = 0, Maps = 0, Components = 0 };
                    current = job;
                    currentCancellation?.Dispose();
                    currentCancellation = new CancellationTokenSource();
                    var cancellation = currentCancellation.Token;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            Set(job, "running", "VERIFY_AND_ROLL_BACK_TEMP_SANDBOX");
                            cancellation.ThrowIfCancellationRequested();
                            var recovered = MultiHostSandboxRestoreTransaction.Rollback(
                                approval.ExistingSandbox, approval.TransactionId, true);
                            var restored = MultiHostClusterBackupArchive.VerifyIsolatedSandbox(
                                approval.ExistingSandbox);
                            Set(job, "completed", "TEMP_SANDBOX_ROLLED_BACK",
                                artifactName: Path.GetFileName(approval.ExistingSandbox) ?? "");
                            lock (job)
                            {
                                job.RollbackArtifactName = recovered.QuarantinedReplacementName ?? "";
                                job.Hosts = restored.Hosts.Length;
                                job.Clusters = restored.Clusters.Length;
                                job.Maps = restored.Maps.Length;
                                job.Components = restored.Components.Length;
                                job.ChangedUtc = DateTimeOffset.UtcNow;
                            }
                        }
                        catch (OperationCanceledException)
                        { Set(job, "needsOperator", "ROLLBACK_CANCELLED_REQUIRES_JOURNAL_REVIEW"); }
                        catch (Exception e) when (e is IOException or InvalidDataException or
                            UnauthorizedAccessException or InvalidOperationException or
                            ArgumentException or OverflowException or JsonException)
                        { Set(job, "needsOperator", "ROLLBACK_FAILED_REQUIRES_JOURNAL_REVIEW"); }
                    });
                    return Ok(id, new { jobId = job.Id, started = true,
                        target = "TEMP SANDBOX ROLLBACK ONLY", productionRestoreAllowed = false,
                        rconUsed = false, serverProcessTouched = false,
                        evidence = "OPERATOR_APPROVED_JOURNAL_ROLLBACK_STARTED" });
                }
                else
                {
                    var approval = restoreApproval;
                    restoreApproval = null;
                    if (approval == null || approval.Token != tokenValue || approval.ExpiresUtc < DateTimeOffset.UtcNow)
                        return Fail(id, "UM-MULTIHOST-RESTORE-APPROVAL-EXPIRED");
                    if (replaceSandboxApply != (approval.ExistingSandbox != null))
                        return Fail(id, "UM-MULTIHOST-RESTORE-APPROVAL-SCOPE");
                    var registry = cluster.Registry.Snapshot();
                    if (registry.Revision != approval.RegistryRevision)
                        return Fail(id, "UM-MULTIHOST-RESTORE-REGISTRY-CHANGED");
                    var ownership = OwnershipSnapshot(registry);
                    var manifest = replaceSandboxApply
                        ? MultiHostSandboxRestoreTransaction.PreviewReplacement(
                            approval.Archive, approval.ExistingSandbox!, ownership)
                        : MultiHostClusterBackupArchive.PreviewSandboxRestore(approval.Archive, ownership);
                    if (!manifest.PreflightPassed)
                        return Fail(id, "UM-MULTIHOST-RESTORE-OWNERSHIP-MISMATCH");
                    if (manifest.BatchId != approval.BatchId)
                        return Fail(id, "UM-MULTIHOST-RESTORE-ARCHIVE-CHANGED");
                    var job = new Job { Kind = "restore", Hosts = manifest.Hosts,
                        Clusters = manifest.Clusters, Maps = manifest.Maps,
                        Components = manifest.Components };
                    current = job;
                    currentCancellation?.Dispose();
                    currentCancellation = new CancellationTokenSource();
                    var cancellation = currentCancellation.Token;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (replaceSandboxApply)
                            {
                                Set(job, "running", "STAGE_VERIFY_AND_RETAIN_PROTECTIVE_TEMP_SANDBOX");
                                var receipt = MultiHostSandboxRestoreTransaction.ReplaceVerifiedSandbox(
                                    approval.Archive, approval.ExistingSandbox!, ownership, true, cancellation);
                                Set(job, "completed", "TEMP_SANDBOX_REPLACED_ROLLBACK_RETAINED",
                                    artifactName: Path.GetFileName(approval.ExistingSandbox) ?? "");
                                lock (job)
                                {
                                    job.Hosts = manifest.Hosts;
                                    job.Clusters = manifest.Clusters;
                                    job.Maps = manifest.Maps;
                                    job.Components = manifest.Components;
                                    job.RollbackArtifactName = Path.GetFileName(receipt.RetainedBackupDirectory);
                                    job.ChangedUtc = DateTimeOffset.UtcNow;
                                }
                            }
                            else
                            {
                                Set(job, "running", "VERIFYING_AND_MATERIALIZING_TEMP_SANDBOX");
                                string sandbox = Path.Combine(Path.GetTempPath(),
                                    "Untamed-MultiHost-Restore-" + Guid.NewGuid().ToString("N"));
                                var receipt = MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(
                                    approval.Archive, sandbox, cancellation);
                                Set(job, "completed", "TEMP_SANDBOX_SHA256_VERIFIED", artifactName:
                                    Path.GetFileName(sandbox));
                                lock (job)
                                {
                                    job.Hosts = receipt.Hosts.Length;
                                    job.Clusters = receipt.Clusters.Length;
                                    job.Maps = receipt.Maps.Length;
                                    job.Components = receipt.Components.Length;
                                    job.ChangedUtc = DateTimeOffset.UtcNow;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        { Set(job, "needsOperator", "CANCELLED_TEMP_STAGE_REQUIRES_REVIEW"); }
                        catch (IOException ex) when (ex.Message.Contains(
                            "UM-TEMP-RESTORE-LEASE-BUSY", StringComparison.Ordinal))
                        {
                            // A competing Manager's native lease is acquired
                            // BEFORE this operation can touch any TEMP artifact.
                            // Do not mislabel it as a failed partial restore.
                            Set(job, "needsOperator", "TEMP_SANDBOX_CONCURRENT_MANAGER_NO_WRITES");
                        }
                        catch (Exception e) when (e is IOException or InvalidDataException or
                            UnauthorizedAccessException or ArgumentException or OverflowException or JsonException)
                        { Set(job, "needsOperator", "RESTORE_SANDBOX_FAILED_REVIEW_STAGE"); }
                    });
                    return Ok(id, new { jobId = job.Id, started = true,
                        target = replaceSandboxApply
                            ? "EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK"
                            : "NEW TEMP SANDBOX ONLY", productionRestoreAllowed = false,
                        rconUsed = false, serverProcessTouched = false,
                        evidence = "ISOLATED_RESTORE_JOB_STARTED" });
                }
            }
            finally { gate.Release(); }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            OverflowException or JsonException)
        {
            return Fail(id, "UM-MULTIHOST-BACKUP-SAFETY-CHECK-FAILED");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { currentCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        currentCancellation?.Dispose();
        gate.Dispose();
    }
}
