using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

/// <summary>
/// Data preserved in the versioned multi-host archive. There is deliberately
/// no SQL/MariaDB payload kind: the Manager archives ASA and its own files only.
/// </summary>
public enum MultiHostBackupComponentKind
{
    HostConfiguration,
    HostStoreV1File,
    MapWorld,
    MapProfile,
    MapTribe,
    MapSavedFile,
    GameIni,
    GameUserSettingsIni,
    ModConfiguration,
    AsaApiPluginConfiguration,
    PluginPersistentData,
    ClusterStoreV1File,
    ClusterTransferManifest,
    ClusterTransferFile
}

public sealed record MultiHostBackupHost(string HostId, string Name);
public sealed record MultiHostBackupCluster(string ClusterId, string Name);
public sealed record MultiHostBackupMap(Guid MapId, string HostId,
    string ClusterId, string Name);

/// <summary>
/// A payload entry has a stable object index and explicit host/map/cluster
/// ownership. Absolute source paths are never serialized into the archive.
/// </summary>
public sealed record MultiHostBackupComponent(int ObjectIndex, string HostId,
    Guid? MapId, string? ClusterId, MultiHostBackupComponentKind Kind,
    string RelativePath, long Length, string Sha256);

public sealed record MultiHostBackupSource(string HostId, Guid? MapId,
    string? ClusterId, MultiHostBackupComponentKind Kind,
    string RelativePath, string SourceFile);

public sealed record MultiHostBackupManifest(int SchemaVersion, Guid BatchId,
    DateTimeOffset CreatedUtc, MultiHostBackupHost[] Hosts,
    MultiHostBackupCluster[] Clusters, MultiHostBackupMap[] Maps,
    MultiHostBackupComponent[] Components, string Scope,
    bool LiveSaveConsistencyConfirmed, bool StoreWriterOwnershipConfirmed,
    bool ProductionRestoreCertified)
{
    public bool IsProductionRestoreCertified => false;
}

public sealed record MultiHostBackupRecoveryStatus(bool NeedsOperator,
    Guid? BatchId, int VerifiedFiles, string Reason)
{
    public bool AutoResumeAllowed => false;
}

public sealed record MultiHostBackupCatalogItem(string HostId, Guid? MapId,
    string? ClusterId, MultiHostBackupComponentKind Kind, string RelativePath,
    string RelativeSource);

public sealed record MultiHostBackupCatalog(int SchemaVersion, Guid BatchId,
    MultiHostBackupHost[] Hosts, MultiHostBackupCluster[] Clusters,
    MultiHostBackupMap[] Maps, MultiHostBackupCatalogItem[] Items,
    bool LiveConsistencyProven, bool StoreWriterOwnershipProven, string Scope);

// A small immutable projection of the current HostRegistry/ClusterRegistry.
// It carries identities and assignments only; it is not another registry.
public sealed record MultiHostBackupHostOwnership(string HostId, string Name,
    bool IsLocal, bool IsPaired);
public sealed record MultiHostBackupClusterOwnership(string ClusterId, string Name);
public sealed record MultiHostBackupMapOwnership(Guid MapId, string HostId,
    string Name, string? ClusterId);
public sealed record MultiHostBackupOwnershipSnapshot(string LocalHostId,
    MultiHostBackupHostOwnership[] Hosts,
    MultiHostBackupClusterOwnership[] Clusters,
    MultiHostBackupMapOwnership[] Maps);

public sealed record MultiHostBackupCatalogPreview(Guid BatchId, int Hosts,
    int Clusters, int Maps, int Components, long Bytes)
{
    public bool LiveConsistencyProven => false;
    public bool StoreWriterOwnershipProven => false;
    public bool ProductionRestoreCertified => false;
}

/// <summary>
/// Version-1 archive/batch safety limits. These guard one untrusted archive
/// and its metadata allocation; they do not cap HostRegistry, ClusterRegistry,
/// LAN membership, or the Manager's product topology.
/// </summary>
public static class MultiHostBackupArchiveV1SafetyLimits
{
    public const int MaxHostsPerArchive = 256;
    public const int MaxClustersPerArchive = 512;
    public const int MaxMapsPerArchive = 4096;
    public const int MaxComponentsPerArchive = 200_000;
    public const int MaxManifestBytes = 64 * 1024 * 1024;
    public const int MaxJournalBytes = 32 * 1024 * 1024;
    public const long MaxComponentBytes = 2L * 1024 * 1024 * 1024 * 1024;
    public const long MaxArchiveBytes = 16L * 1024 * 1024 * 1024 * 1024;
}

public sealed record MultiHostRestorePreview(bool PreflightPassed, string Reason,
    Guid BatchId, int Hosts, int Maps, int Clusters, int Components)
{
    public bool IsSandboxOnlyPreflight => true;
    public bool CanRestoreToLiveManagers => false;
    public bool WritesStarted => false;
    public bool ProductionRestoreCertified => false;
}

public sealed record MultiHostRestoreSandboxManifest(int SchemaVersion,
    Guid SourceBatchId, DateTimeOffset CreatedUtc, MultiHostBackupHost[] Hosts,
    MultiHostBackupCluster[] Clusters, MultiHostBackupMap[] Maps,
    MultiHostBackupComponent[] Components, bool ProductionRestoreAllowed,
    string Scope)
{
    public bool IsProductionRestoreAllowed => false;
}

public static class MultiHostClusterBackupArchive
{
    public const int CurrentSchemaVersion = 1;
    public const string OfflineCopyCatalogScope = "OBSIDIAN-MULTI-HOST-COPY-CATALOG-V1";
    private const string ManifestName = "multi-host-cluster-v1.json";
    private const string ManifestHashName = "multi-host-cluster-v1.sha256";
    private const string CompleteName = "complete";
    private const string ObjectsName = "objects";
    private const string JournalName = "copy-journal-v1.json";
    private const string Scope = "OFFLINE-COPY-ONLY; independent files, no live ASA or Store consistency proof";
    private const int CheckpointInterval = 64;
    private const int MaxHosts = MultiHostBackupArchiveV1SafetyLimits.MaxHostsPerArchive;
    private const int MaxClusters = MultiHostBackupArchiveV1SafetyLimits.MaxClustersPerArchive;
    private const int MaxMaps = MultiHostBackupArchiveV1SafetyLimits.MaxMapsPerArchive;
    private const int MaxComponents = MultiHostBackupArchiveV1SafetyLimits.MaxComponentsPerArchive;
    private const int MaxManifestBytes = MultiHostBackupArchiveV1SafetyLimits.MaxManifestBytes;
    private const int MaxJournalBytes = MultiHostBackupArchiveV1SafetyLimits.MaxJournalBytes;
    private const long MaxComponentBytes = MultiHostBackupArchiveV1SafetyLimits.MaxComponentBytes;
    private const long MaxTotalBytes = MultiHostBackupArchiveV1SafetyLimits.MaxArchiveBytes;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };

    private sealed record Journal(int SchemaVersion, Guid BatchId,
        int VerifiedFiles, DateTimeOffset UpdatedUtc, string ProgressSha256);

    private static string FixedLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new InvalidDataException("Explicit fixed local path required");

        string[] parts = path.Replace('/', '\\').Split('\\');
        if (parts.Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                           p.Contains('~')) ||
            (path.Length >= 3 && path[3..].Contains(':')))
            throw new InvalidDataException("Path alias or alternate stream rejected");

        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string? volume = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(volume) || new DriveInfo(volume).DriveType != DriveType.Fixed)
            throw new InvalidDataException("Only fixed local disks are allowed");
        for (DirectoryInfo? parent = new DirectoryInfo(Path.GetDirectoryName(full) ?? full);
             parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked path ancestor rejected");
        if ((File.Exists(full) || Directory.Exists(full)) &&
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked source or destination rejected");
        return full;
    }

    private static string SafeId(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) ||
            value is "." or "..")
            throw new InvalidDataException(label + " is invalid");
        return value;
    }

    private static string SafeName(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(char.IsControl))
            throw new InvalidDataException(label + " is invalid");
        return value.Trim();
    }

    private static string SafeRelative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            value.StartsWith('/') || value.StartsWith('\\') ||
            Path.IsPathFullyQualified(value) || value.Contains(':') ||
            value.Any(char.IsControl))
            throw new InvalidDataException("Backup-relative path is invalid");
        string normalized = value.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." ||
                              p.EndsWith('.') || p.EndsWith(' ') || p.Contains('~')))
            throw new InvalidDataException("Backup-relative path traversal rejected");
        return string.Join('/', segments);
    }

    private static string ComponentKey(MultiHostBackupSource x) =>
        $"{x.Kind}|{x.HostId.ToUpperInvariant()}|{x.MapId?.ToString("N") ?? ""}|" +
        $"{x.ClusterId?.ToUpperInvariant() ?? ""}|{SafeRelative(x.RelativePath).ToUpperInvariant()}";

    private static string ComponentKey(MultiHostBackupComponent x) =>
        $"{x.Kind}|{x.HostId.ToUpperInvariant()}|{x.MapId?.ToString("N") ?? ""}|" +
        $"{x.ClusterId?.ToUpperInvariant() ?? ""}|{SafeRelative(x.RelativePath).ToUpperInvariant()}";

    private static string OwnerPathKey(string hostId, Guid? mapId, string? clusterId,
        MultiHostBackupComponentKind kind, string relativePath)
    {
        string scope = IsMapKind(kind) ? $"map:{mapId:N}" :
            kind == MultiHostBackupComponentKind.HostConfiguration ? "host" :
            kind == MultiHostBackupComponentKind.HostStoreV1File ? $"host-cluster:{clusterId}" :
            $"cluster:{clusterId}:{kind}";
        return $"{scope}|{hostId.ToUpperInvariant()}|{SafeRelative(relativePath).ToUpperInvariant()}";
    }

    internal static string RestoreRelative(MultiHostBackupComponent component)
    {
        string relative = SafeRelative(component.RelativePath).Replace('/', Path.DirectorySeparatorChar);
        string host = SafeId(component.HostId, "Component host ID");
        string? cluster = component.ClusterId == null ? null : SafeId(component.ClusterId, "Component cluster ID");
        if (component.MapId.HasValue)
            return Path.Combine("hosts", host, "maps", component.MapId.Value.ToString("N"), relative);
        return component.Kind switch
        {
            MultiHostBackupComponentKind.HostConfiguration => Path.Combine("hosts", host, "configuration", relative),
            MultiHostBackupComponentKind.HostStoreV1File => Path.Combine("hosts", host, "clusters", cluster!, "Store-v1", relative),
            MultiHostBackupComponentKind.ClusterStoreV1File => Path.Combine("clusters", cluster!, "Store-v1", host, relative),
            MultiHostBackupComponentKind.ClusterTransferManifest => Path.Combine("clusters", cluster!, "TransferSnapshot", host, relative),
            MultiHostBackupComponentKind.ClusterTransferFile => Path.Combine("clusters", cluster!, "TransferFiles", host, relative),
            _ => throw new InvalidDataException("Unknown restore payload scope")
        };
    }

    private static string SortKey(MultiHostBackupSource x) =>
        $"{x.HostId.ToUpperInvariant()}|{x.ClusterId?.ToUpperInvariant()}|" +
        $"{x.MapId?.ToString("N")}|{(int)x.Kind:D2}|{SafeRelative(x.RelativePath).ToUpperInvariant()}";

    private static string SourceDirectory(MultiHostBackupSource source)
    {
        string host = SafeId(source.HostId, "Source host ID");
        string? cluster = source.ClusterId == null ? null : SafeId(source.ClusterId, "Source cluster ID");
        if (IsMapKind(source.Kind))
        {
            if (!source.MapId.HasValue || source.MapId.Value == Guid.Empty)
                throw new InvalidDataException("Map source requires an explicit MapId");
            return Path.Combine("maps", source.MapId.Value.ToString("N"));
        }
        return source.Kind switch
        {
            MultiHostBackupComponentKind.HostConfiguration when cluster == null && !source.MapId.HasValue =>
                Path.Combine("hosts", host, "configuration"),
            MultiHostBackupComponentKind.HostStoreV1File when cluster != null && !source.MapId.HasValue =>
                Path.Combine("hosts", host, "clusters", cluster, "Store-v1"),
            MultiHostBackupComponentKind.ClusterStoreV1File when cluster != null && !source.MapId.HasValue =>
                Path.Combine("clusters", cluster, "Store-v1", host),
            MultiHostBackupComponentKind.ClusterTransferManifest or
            MultiHostBackupComponentKind.ClusterTransferFile when cluster != null && !source.MapId.HasValue =>
                Path.Combine("clusters", cluster, "Transfer", host),
            _ => throw new InvalidDataException("Backup source has an unsupported owner scope")
        };
    }

    private static string ValidateSourceSets(IReadOnlyList<MultiHostBackupSource> sources,
        string? expectedRoot = null)
    {
        if (sources.Count == 0) throw new InvalidDataException("Offline source set is empty");
        string? commonRoot = expectedRoot == null ? null : FixedLocal(expectedRoot);
        foreach (var source in sources)
        {
            string full = FixedLocal(source.SourceFile);
            string scope = SourceDirectory(source);
            string relative = SafeRelative(source.RelativePath);
            string expected = Path.Combine(scope, relative.Replace('/', Path.DirectorySeparatorChar));
            var sourceInfo = new FileInfo(full);
            if (!sourceInfo.Exists || (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Missing or linked offline source component");
            DirectoryInfo? root = sourceInfo.Directory;
            int parentSteps = expected.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries).Length - 1;
            for (int step = 0; step < parentSteps; step++)
                root = root?.Parent;
            if (root == null) throw new InvalidDataException("Offline source root cannot be resolved");
            string fullRoot = FixedLocal(root.FullName);
            string actualRelative = Path.GetRelativePath(fullRoot, full)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (!actualRelative.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Offline source does not match its declared host/map/cluster path");
            if (commonRoot == null) commonRoot = fullRoot;
            else if (!commonRoot.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("All offline components must belong to one copied catalog root");
        }

        string catalogRoot = commonRoot!;
        var groups = sources.GroupBy(s => SourceDirectory(s), StringComparer.OrdinalIgnoreCase);
        int totalFiles = 0;
        foreach (var group in groups)
        {
            string scopeRoot = Path.GetFullPath(Path.Combine(catalogRoot, group.Key));
            if (!Within(catalogRoot, scopeRoot) || !Directory.Exists(scopeRoot) ||
                (File.GetAttributes(scopeRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Offline copy scope folder is missing or linked");
            var expected = group.Select(s => SafeRelative(s.RelativePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(scopeRoot);
            while (stack.Count > 0)
            {
                string current = stack.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked offline copy directory rejected");
                foreach (string directory in Directory.GetDirectories(current))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Linked offline copy directory rejected");
                    stack.Push(directory);
                }
                foreach (string file in Directory.GetFiles(current))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Linked offline copy file rejected");
                    string relative = SafeRelative(Path.GetRelativePath(scopeRoot, file));
                    if (!actual.Add(relative) || ++totalFiles > MaxComponents)
                        throw new InvalidDataException("Offline copy contains duplicate or excessive files");
                }
            }
            if (!actual.SetEquals(expected))
                throw new InvalidDataException("Offline copy has an unlisted, missing or partial file");
        }
        return catalogRoot;
    }

    private static bool HashValid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    // A zero-byte arkprofile may be an original ASA/Store-v1 placeholder.
    // Preserve it byte-for-byte in an OFFLINE archive, without declaring the
    // player recovered. An empty world, tribe, transfer or configuration is
    // still an invalid source component and can never authorize a restore.
    private static bool PermitsEmpty(MultiHostBackupComponentKind kind) =>
        kind == MultiHostBackupComponentKind.MapProfile;

    private static bool IsMapKind(MultiHostBackupComponentKind kind) => kind is
        MultiHostBackupComponentKind.MapWorld or MultiHostBackupComponentKind.MapProfile or
        MultiHostBackupComponentKind.MapTribe or MultiHostBackupComponentKind.MapSavedFile or
        MultiHostBackupComponentKind.GameIni or MultiHostBackupComponentKind.GameUserSettingsIni or
        MultiHostBackupComponentKind.ModConfiguration or
        MultiHostBackupComponentKind.AsaApiPluginConfiguration or
        MultiHostBackupComponentKind.PluginPersistentData;

    private static bool IsClusterKind(MultiHostBackupComponentKind kind) => kind is
        MultiHostBackupComponentKind.HostStoreV1File or
        MultiHostBackupComponentKind.ClusterStoreV1File or
        MultiHostBackupComponentKind.ClusterTransferManifest or
        MultiHostBackupComponentKind.ClusterTransferFile;

    private static void ValidateInventory(Guid batchId,
        IReadOnlyList<MultiHostBackupHost> hosts,
        IReadOnlyList<MultiHostBackupCluster> clusters,
        IReadOnlyList<MultiHostBackupMap> maps,
        IReadOnlyList<MultiHostBackupComponent> components)
    {
        if (batchId == Guid.Empty || hosts == null || clusters == null || maps == null || components == null)
            throw new InvalidDataException("Batch and complete ownership inventory are required");
        if (hosts.Count is < 1 or > MaxHosts || clusters.Count is < 1 or > MaxClusters ||
            maps.Count is < 1 or > MaxMaps || components.Count is < 1 or > MaxComponents)
            throw new InvalidDataException("Host, cluster, map or component inventory is outside supported limits");
        if (hosts.Any(h => h == null) || clusters.Any(c => c == null) ||
            maps.Any(m => m == null) || components.Any(c => c == null))
            throw new InvalidDataException("Null ownership or component row rejected");

        var hostIds = hosts.Select(h => SafeId(h?.HostId, "Host ID")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hostIds.Count != hosts.Count || hosts.Any(h => h == null || SafeName(h.Name, "Host name") != h.Name))
            throw new InvalidDataException("Host identity is duplicated or malformed");
        var clusterIds = clusters.Select(c => SafeId(c?.ClusterId, "Cluster ID"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (clusterIds.Count != clusters.Count || clusters.Any(c => c == null || SafeName(c.Name, "Cluster name") != c.Name))
            throw new InvalidDataException("Cluster identity is duplicated or malformed");
        if (maps.Select(m => m.MapId).Distinct().Count() != maps.Count ||
            maps.Any(m => m == null || m.MapId == Guid.Empty ||
                          !hostIds.Contains(SafeId(m.HostId, "Map host ID")) ||
                          !clusterIds.Contains(SafeId(m.ClusterId, "Map cluster ID")) ||
                          SafeName(m.Name, "Map name") != m.Name))
            throw new InvalidDataException("Map identity or ownership is invalid");

        var mapById = maps.ToDictionary(m => m.MapId);
        var mapKindCounts = components.Where(c => c.MapId.HasValue)
            .GroupBy(c => c.MapId!.Value)
            .ToDictionary(g => g.Key, g => g.GroupBy(c => c.Kind)
                .ToDictionary(k => k.Key, k => k.Count()));
        var hostConfigCounts = components.Where(c => c.Kind == MultiHostBackupComponentKind.HostConfiguration)
            .GroupBy(c => c.HostId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var clusterKindCounts = components.Where(c => c.ClusterId != null)
            .GroupBy(c => c.ClusterId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.GroupBy(c => c.Kind)
                .ToDictionary(k => k.Key, k => k.Count()), StringComparer.OrdinalIgnoreCase);
        var hostClusterStoreCounts = components.Where(c =>
                c.Kind == MultiHostBackupComponentKind.HostStoreV1File && c.ClusterId != null)
            .GroupBy(c => (c.HostId.ToUpperInvariant(), c.ClusterId!.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Count());
        var hostClusterPairs = maps.Select(m => (Host: m.HostId, Cluster: m.ClusterId))
            .ToHashSet(new HostClusterComparer());
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var component in components)
        {
            if (component == null || component.ObjectIndex < 0 || component.ObjectIndex >= components.Count ||
                !Enum.IsDefined(component.Kind) || component.Length < 0 ||
                (component.Length == 0 && !PermitsEmpty(component.Kind)) ||
                component.Length > MaxComponentBytes || !HashValid(component.Sha256))
                throw new InvalidDataException("Missing, empty or unverified component");
            string host = SafeId(component.HostId, "Component host ID");
            if (!hostIds.Contains(host)) throw new InvalidDataException("Component references an unknown host");
            if (component.ClusterId != null && !clusterIds.Contains(SafeId(component.ClusterId, "Component cluster ID")))
                throw new InvalidDataException("Component references an unknown cluster");
            string path = SafeRelative(component.RelativePath);
            if (!string.Equals(path, component.RelativePath.Replace('\\', '/'), StringComparison.Ordinal))
                throw new InvalidDataException("Component path is not in canonical form");
            if (!keys.Add(ComponentKey(component)))
                throw new InvalidDataException("Duplicate component path or identity");
            if (!destinationKeys.Add(RestoreRelative(component)))
                throw new InvalidDataException("Two backup components map to the same restored file");
            total = checked(total + component.Length);
            if (total > MaxTotalBytes) throw new InvalidDataException("Archive exceeds total size limit");

            if (IsMapKind(component.Kind))
            {
                if (!component.MapId.HasValue || component.ClusterId == null ||
                    !mapById.TryGetValue(component.MapId.Value, out var map) ||
                    !StringComparer.OrdinalIgnoreCase.Equals(map.HostId, host) ||
                    !StringComparer.OrdinalIgnoreCase.Equals(map.ClusterId, component.ClusterId))
                    throw new InvalidDataException("Map payload has a foreign host or cluster owner");
                if (component.Kind == MultiHostBackupComponentKind.MapWorld &&
                    !Path.GetExtension(path).Equals(".ark", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("World payload must retain its .ark extension");
                if (component.Kind is MultiHostBackupComponentKind.GameIni or
                    MultiHostBackupComponentKind.GameUserSettingsIni &&
                    !Path.GetFileName(path).Equals(component.Kind == MultiHostBackupComponentKind.GameIni
                        ? "Game.ini" : "GameUserSettings.ini", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("INI payload has an unexpected file name");
            }
            else if (component.Kind == MultiHostBackupComponentKind.HostConfiguration)
            {
                if (component.MapId.HasValue || component.ClusterId != null)
                    throw new InvalidDataException("Host configuration cannot be map or cluster scoped");
            }
            else if (IsClusterKind(component.Kind))
            {
                if (component.MapId.HasValue || component.ClusterId == null ||
                    !hostClusterPairs.Contains((host, component.ClusterId)))
                    throw new InvalidDataException("Store/transfer payload has no participating host/cluster owner");
            }
            else throw new InvalidDataException("Unsupported backup component scope");
        }

        if (components.Select(x => x.ObjectIndex).Distinct().Count() != components.Count ||
            !components.Select(x => x.ObjectIndex).Order().SequenceEqual(Enumerable.Range(0, components.Count)))
            throw new InvalidDataException("Archive object indexes are missing or duplicated");

        foreach (var host in hosts)
            if (!hostConfigCounts.ContainsKey(host.HostId))
                throw new InvalidDataException("Host configuration snapshot is missing");

        foreach (var map in maps)
        {
            mapKindCounts.TryGetValue(map.MapId, out var counts);
            int Count(MultiHostBackupComponentKind kind) => counts?.GetValueOrDefault(kind) ?? 0;
            bool Has(MultiHostBackupComponentKind kind) => Count(kind) == 1;
            if (!Has(MultiHostBackupComponentKind.MapWorld) ||
                !Has(MultiHostBackupComponentKind.GameUserSettingsIni) ||
                !Has(MultiHostBackupComponentKind.ModConfiguration) ||
                !Has(MultiHostBackupComponentKind.AsaApiPluginConfiguration) ||
                !Has(MultiHostBackupComponentKind.PluginPersistentData) ||
                Count(MultiHostBackupComponentKind.GameIni) > 1)
                throw new InvalidDataException("Map is missing a world, settings, mod or plugin snapshot");
        }

        foreach (var cluster in clusters)
        {
            var clusterMaps = maps.Where(m => StringComparer.OrdinalIgnoreCase.Equals(m.ClusterId, cluster.ClusterId)).ToArray();
            if (clusterMaps.Length == 0) throw new InvalidDataException("Empty cluster cannot be backed up");
            clusterKindCounts.TryGetValue(cluster.ClusterId, out var counts);
            int Count(MultiHostBackupComponentKind kind) => counts?.GetValueOrDefault(kind) ?? 0;
            if (Count(MultiHostBackupComponentKind.ClusterStoreV1File) == 0 ||
                Count(MultiHostBackupComponentKind.ClusterTransferManifest) != 1)
                throw new InvalidDataException("Cluster Store-v1 or explicit transfer snapshot is missing");
            foreach (string host in clusterMaps.Select(m => m.HostId).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!hostClusterStoreCounts.ContainsKey((host.ToUpperInvariant(), cluster.ClusterId.ToUpperInvariant())))
                    throw new InvalidDataException("Participating host Store-v1 snapshot is missing");
        }
    }

    private sealed class HostClusterComparer : IEqualityComparer<(string Host, string Cluster)>
    {
        public bool Equals((string Host, string Cluster) x, (string Host, string Cluster) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Host, y.Host) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Cluster, y.Cluster);
        public int GetHashCode((string Host, string Cluster) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Host),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Cluster));
    }

    private static void WriteDurable(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true);
        writer.Write(text);
        writer.Flush();
        stream.Flush(true);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string EmptyProgressDigest() =>
        Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));

    private static string AdvanceProgressDigest(string prior, string next)
    {
        if (!HashValid(prior) || !HashValid(next))
            throw new InvalidDataException("Progress checkpoint digest is invalid");
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(
            prior.ToUpperInvariant() + ":" + next.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void Checkpoint(string stage, Guid batchId, int verifiedFiles, string progressSha256)
    {
        string path = Path.Combine(stage, JournalName);
        string next = path + ".next";
        if (File.Exists(next)) throw new IOException("Interrupted checkpoint update requires operator review");
        WriteDurable(next, JsonSerializer.Serialize(new Journal(CurrentSchemaVersion,
            batchId, verifiedFiles, DateTimeOffset.UtcNow, progressSha256), Json));
        File.Move(next, path, true);
    }

    // The epoch guard alone can be bypassed by a same-length rewrite followed
    // by resetting NTFS timestamps. Re-hash each ORIGINAL OFFLINE source before
    // publishing the already-verified staged archive; use bounded reads and
    // cancellation to avoid taking an uninterruptible multi-GB hash pass.
    // This is offline copy parity only, not a two-host SaveComplete barrier.
    private static string HashSourceForPublication(string source,
        long expectedBytes, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("UM-MULTIHOST-OFFLINE-SOURCE-SHA256-CHANGED");
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (stream.Length != expectedBytes)
            throw new IOException("UM-MULTIHOST-OFFLINE-SOURCE-SHA256-CHANGED");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        long length = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                length = checked(length + count);
                if (length > expectedBytes)
                    throw new IOException("UM-MULTIHOST-OFFLINE-SOURCE-SHA256-CHANGED");
                sha.AppendData(buffer, 0, count);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        if (length != expectedBytes || stream.Length != expectedBytes)
            throw new IOException("UM-MULTIHOST-OFFLINE-SOURCE-SHA256-CHANGED");
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static (long Length, string Sha256) CopyAndHash(string source,
        string destination, MultiHostBackupComponentKind kind,
        CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked source file rejected");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        long sourceLength = input.Length;
        if ((sourceLength == 0 && !PermitsEmpty(kind)) ||
            sourceLength < 0 || sourceLength > MaxComponentBytes)
            throw new InvalidDataException("Empty or oversized source component rejected");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        long copied = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                copied = checked(copied + read);
                if (copied > sourceLength || copied > MaxComponentBytes)
                    throw new IOException("Source changed or exceeds component limit");
                sha.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            output.Flush(true);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        if (copied != sourceLength || input.Length != sourceLength)
            throw new IOException("Source changed while being copied");
        return (copied, Convert.ToHexString(sha.GetHashAndReset()));
    }

    private static (MultiHostBackupCatalog Catalog, MultiHostBackupSource[] Sources) ReadCatalog(
        string catalogFile, IReadOnlyCollection<string>? forbiddenRoots = null)
    {
        string file = FixedLocal(catalogFile);
        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(file) ||
            (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(file).Length is < 32 or > MaxManifestBytes)
            throw new InvalidDataException("Offline copy catalog is missing, linked or oversized");
        if (UnderAnyRoot(file, forbiddenRoots))
            throw new UnauthorizedAccessException("Catalog cannot be read from a registered production data root");
        string root = Path.GetDirectoryName(file)!;
        MultiHostBackupCatalog catalog;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            catalog = JsonSerializer.Deserialize<MultiHostBackupCatalog>(stream, Json)
                ?? throw new InvalidDataException("Offline copy catalog is empty");
        }
        catch (JsonException) { throw new InvalidDataException("Offline copy catalog JSON is invalid"); }
        if (catalog.SchemaVersion != CurrentSchemaVersion || catalog.BatchId == Guid.Empty ||
            catalog.LiveConsistencyProven || catalog.StoreWriterOwnershipProven ||
            catalog.Scope != OfflineCopyCatalogScope || catalog.Hosts == null || catalog.Clusters == null ||
            catalog.Maps == null || catalog.Items == null || catalog.Items.Length is < 1 or > MaxComponents ||
            catalog.Items.Any(x => x == null || !Enum.IsDefined(x.Kind)))
            throw new InvalidDataException("Offline copy catalog schema or safety flags are invalid");

        var sources = catalog.Items.Select(item =>
        {
            string relative = SafeRelative(item.RelativeSource);
            string source = Path.GetFullPath(Path.Combine(root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!Within(root, source) || !File.Exists(source) ||
                (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Catalog source is missing, linked or outside its root");
            if (UnderAnyRoot(source, forbiddenRoots))
                throw new UnauthorizedAccessException("Catalog source is inside a registered production data root");
            for (DirectoryInfo? node = new DirectoryInfo(Path.GetDirectoryName(source)!);
                 node != null; node = node.Parent)
            {
                if (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked catalog source directory rejected");
                if (string.Equals(node.FullName.TrimEnd(Path.DirectorySeparatorChar),
                        root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    break;
            }
            return new MultiHostBackupSource(item.HostId, item.MapId, item.ClusterId,
                item.Kind, item.RelativePath, source);
        }).OrderBy(SortKey, StringComparer.Ordinal).ToArray();
        ValidateSourceSets(sources, root);
        var preflight = sources.Select((x, index) =>
        {
            long length = new FileInfo(x.SourceFile).Length;
            return new MultiHostBackupComponent(index, x.HostId, x.MapId, x.ClusterId,
                x.Kind, x.RelativePath, length, new string('0', 64));
        }).ToArray();
        ValidateInventory(catalog.BatchId, catalog.Hosts, catalog.Clusters, catalog.Maps, preflight);
        return (catalog, sources);
    }

    public static MultiHostBackupCatalogPreview InspectCatalog(string catalogFile,
        IReadOnlyCollection<string>? forbiddenRoots = null)
    {
        var (catalog, sources) = ReadCatalog(catalogFile, forbiddenRoots);
        long bytes = 0;
        foreach (var source in sources)
            bytes = checked(bytes + new FileInfo(source.SourceFile).Length);
        return new(catalog.BatchId, catalog.Hosts.Length, catalog.Clusters.Length,
            catalog.Maps.Length, sources.Length, bytes);
    }

    public static MultiHostBackupCatalogPreview InspectCatalog(string catalogFile,
        MultiHostBackupOwnershipSnapshot registry, IReadOnlyCollection<string>? forbiddenRoots = null)
    {
        var (catalog, sources) = ReadCatalog(catalogFile, forbiddenRoots);
        ValidateAgainstRegistry(catalog, registry);
        long bytes = 0;
        foreach (var source in sources) bytes = checked(bytes + new FileInfo(source.SourceFile).Length);
        return new(catalog.BatchId, catalog.Hosts.Length, catalog.Clusters.Length,
            catalog.Maps.Length, sources.Length, bytes);
    }

    public static MultiHostBackupManifest BuildFromCatalog(string catalogFile,
        string destination, CancellationToken cancellationToken = default,
        Action<int>? afterCopiedForTesting = null,
        IReadOnlyCollection<string>? forbiddenRoots = null)
    {
        string fullDestination = FixedLocal(destination);
        if (UnderAnyRoot(fullDestination, forbiddenRoots))
            throw new UnauthorizedAccessException("Archive destination cannot be inside a registered production data root");
        var (catalog, sources) = ReadCatalog(catalogFile, forbiddenRoots);
        return BuildOffline(catalog.BatchId, catalog.Hosts, catalog.Clusters,
            catalog.Maps, sources, destination, cancellationToken, afterCopiedForTesting);
    }

    public static MultiHostBackupManifest BuildFromCatalog(string catalogFile,
        string destination, MultiHostBackupOwnershipSnapshot registry,
        IReadOnlyCollection<string>? forbiddenRoots = null,
        CancellationToken cancellationToken = default,
        Action<int>? afterCopiedForTesting = null)
    {
        string fullDestination = FixedLocal(destination);
        if (UnderAnyRoot(fullDestination, forbiddenRoots))
            throw new UnauthorizedAccessException("Archive destination cannot be inside a registered production data root");
        var (catalog, sources) = ReadCatalog(catalogFile, forbiddenRoots);
        ValidateAgainstRegistry(catalog, registry);
        return BuildOffline(catalog.BatchId, catalog.Hosts, catalog.Clusters,
            catalog.Maps, sources, destination, cancellationToken, afterCopiedForTesting);
    }

    private static void ValidateAgainstRegistry(MultiHostBackupCatalog catalog,
        MultiHostBackupOwnershipSnapshot? snapshot)
    {
        if (snapshot?.Hosts == null || snapshot.Clusters == null || snapshot.Maps == null ||
            string.IsNullOrWhiteSpace(snapshot.LocalHostId))
            throw new InvalidDataException("Current HostRegistry/ClusterRegistry snapshot is unavailable");

        var hosts = snapshot.Hosts.ToDictionary(h => SafeId(h.HostId, "Registered host ID"),
            StringComparer.OrdinalIgnoreCase);
        var clusters = snapshot.Clusters.ToDictionary(c => SafeId(c.ClusterId, "Registered cluster ID"),
            StringComparer.OrdinalIgnoreCase);
        var registeredMaps = snapshot.Maps.ToDictionary(m => m.MapId);
        string localId = SafeId(snapshot.LocalHostId, "Local host ID");
        if (!hosts.TryGetValue(localId, out var local) || !local.IsLocal ||
            snapshot.Hosts.Count(h => h.IsLocal) != 1)
            throw new InvalidDataException("Local Manager ownership is inconsistent");

        foreach (var host in catalog.Hosts)
        {
            if (!hosts.TryGetValue(host.HostId, out var registered) ||
                !StringComparer.Ordinal.Equals(host.Name, registered.Name))
                throw new UnauthorizedAccessException("Catalog host identity does not match HostRegistry");
            if (!registered.IsLocal && !registered.IsPaired)
                throw new UnauthorizedAccessException("Remote host must be paired before backup membership is accepted");
        }
        foreach (var cluster in catalog.Clusters)
            if (!clusters.TryGetValue(cluster.ClusterId, out var registered) ||
                !StringComparer.Ordinal.Equals(cluster.Name, registered.Name))
                throw new UnauthorizedAccessException("Catalog cluster identity does not match ClusterRegistry");

        var selectedClusterIds = catalog.Clusters.Select(c => c.ClusterId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<Guid>();
        foreach (var registeredMap in snapshot.Maps)
        {
            if (registeredMap.ClusterId == null || !selectedClusterIds.Contains(registeredMap.ClusterId)) continue;
            if (!expected.Add(registeredMap.MapId))
                throw new InvalidDataException("ClusterRegistry has a duplicate assigned map");
        }
        if (!expected.SetEquals(catalog.Maps.Select(m => m.MapId)))
            throw new UnauthorizedAccessException("Catalog must contain every registered map in each selected cluster");
        foreach (var map in catalog.Maps)
        {
            if (!registeredMaps.TryGetValue(map.MapId, out var registeredMap) ||
                !StringComparer.OrdinalIgnoreCase.Equals(registeredMap.HostId, map.HostId) ||
                !StringComparer.OrdinalIgnoreCase.Equals(registeredMap.Name, map.Name) ||
                !StringComparer.OrdinalIgnoreCase.Equals(registeredMap.ClusterId, map.ClusterId))
                throw new UnauthorizedAccessException("Catalog map owner or cluster differs from the current registries");
        }
        var expectedHosts = catalog.Maps.Select(m => m.HostId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedHosts.SetEquals(catalog.Hosts.Select(h => h.HostId)))
            throw new InvalidDataException("Catalog host list differs from registered map ownership");
    }

    public static MultiHostBackupManifest BuildOffline(Guid batchId,
        IReadOnlyList<MultiHostBackupHost> hosts,
        IReadOnlyList<MultiHostBackupCluster> clusters,
        IReadOnlyList<MultiHostBackupMap> maps,
        IReadOnlyList<MultiHostBackupSource> sources,
        string destination, CancellationToken cancellationToken = default,
        Action<int>? afterCopiedForTesting = null)
    {
        if (sources == null) throw new ArgumentNullException(nameof(sources));
        if (hosts == null || clusters == null || maps == null)
            throw new InvalidDataException("Host, cluster and map ownership are required");
        var input = sources.ToArray();
        if (input.Any(x => x == null) || input.Length is < 1 or > MaxComponents)
            throw new InvalidDataException("Backup source list is empty or excessive");

        var normalized = input.Select(x => x with
        {
            HostId = SafeId(x.HostId, "Source host ID"),
            ClusterId = x.ClusterId == null ? null : SafeId(x.ClusterId, "Source cluster ID"),
            RelativePath = SafeRelative(x.RelativePath),
            SourceFile = FixedLocal(x.SourceFile)
        }).OrderBy(SortKey, StringComparer.Ordinal).ToArray();
        if (normalized.Select(ComponentKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length ||
            normalized.Select(x => x.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new InvalidDataException("Duplicate source path or component identity");
        string sourceRoot = ValidateSourceSets(normalized);

        // Freeze the observable identity/metadata of EVERY offline source
        // before the first byte is copied. Individual FileShare.Read handles
        // prevent most concurrent writers only while each file is open; a
        // different file could otherwise change between copy operations.
        // This guards the existing offline batch, NOT live cross-host
        // SaveComplete, writer ownership, or atomic multi-machine snapshots.
        var sourceEpochs = normalized.Select(x =>
        {
            if (!File.Exists(x.SourceFile) ||
                (File.GetAttributes(x.SourceFile) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Missing or linked source file");
            var info = new FileInfo(x.SourceFile);
            return (info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
        }).ToArray();
        var preflightComponents = normalized.Select((x, index) =>
            new MultiHostBackupComponent(index, x.HostId, x.MapId, x.ClusterId,
                x.Kind, x.RelativePath, sourceEpochs[index].Length, new string('0', 64))
        ).ToArray();
        ValidateInventory(batchId, hosts, clusters, maps, preflightComponents);

        void DemandStableOfflineSourceEpochs()
        {
            for (int i = 0; i < normalized.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = normalized[i].SourceFile;
                if (!File.Exists(source) ||
                    (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("UM-MULTIHOST-OFFLINE-SOURCE-EPOCH-CHANGED");
                var info = new FileInfo(source);
                var epoch = sourceEpochs[i];
                if (info.Length != epoch.Length ||
                    info.CreationTimeUtc != epoch.CreationTimeUtc ||
                    info.LastWriteTimeUtc != epoch.LastWriteTimeUtc)
                    throw new IOException("UM-MULTIHOST-OFFLINE-SOURCE-EPOCH-CHANGED");
            }
        }

        string target = FixedLocal(destination);
        if (Within(sourceRoot, target) || Within(target, sourceRoot))
            throw new InvalidDataException("Archive destination cannot overlap the offline source-copy root");
        string stage = FixedLocal(target + ".incomplete");
        if (Directory.Exists(target) || File.Exists(target) || Directory.Exists(stage) || File.Exists(stage))
            throw new IOException("Existing destination or incomplete batch requires operator review");
        for (int i = 0; i < normalized.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = normalized[i].SourceFile;
            if (!File.Exists(source) ||
                source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Missing or overlapping source file");
            var info = new FileInfo(source);
            if ((info.Length == 0 && !PermitsEmpty(normalized[i].Kind)) ||
                info.Length < 0 || info.Length > MaxComponentBytes)
                throw new InvalidDataException("Empty or oversized source component rejected");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(stage);
        string objects = Path.Combine(stage, ObjectsName);
        Directory.CreateDirectory(objects);
        string progressSha256 = EmptyProgressDigest();
        int verifiedFiles = 0;
        Checkpoint(stage, batchId, verifiedFiles, progressSha256);
        var components = new List<MultiHostBackupComponent>(normalized.Length);
        long total = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = normalized[i];
            string objectPath = Path.Combine(objects, i.ToString("D6") + ".bin");
            var (length, sha) = CopyAndHash(row.SourceFile, objectPath, row.Kind, cancellationToken);
            total = checked(total + length);
            if (total > MaxTotalBytes) throw new InvalidDataException("Archive exceeds total size limit");
            components.Add(new(i, row.HostId, row.MapId, row.ClusterId,
                row.Kind, row.RelativePath, length, sha));
            progressSha256 = AdvanceProgressDigest(progressSha256, sha);
            verifiedFiles = i + 1;
            if (verifiedFiles % CheckpointInterval == 0 || verifiedFiles == normalized.Length)
                Checkpoint(stage, batchId, verifiedFiles, progressSha256);
            afterCopiedForTesting?.Invoke(i);
        }

        ValidateSourceSets(normalized);
        DemandStableOfflineSourceEpochs();
        // Keep the durable .incomplete checkpoint UNTIL parity is proved.
        // If an offline exporter changed bytes while spoofing metadata, the
        // operator can still inspect the last verified checkpoint; never
        // leave an unjournaled half-finalized archive.
        for (int i = 0; i < normalized.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string actualSha256 = HashSourceForPublication(normalized[i].SourceFile,
                components[i].Length, cancellationToken);
            if (!actualSha256.Equals(components[i].Sha256,
                StringComparison.OrdinalIgnoreCase))
                throw new IOException("UM-MULTIHOST-OFFLINE-SOURCE-SHA256-CHANGED");
        }
        DemandStableOfflineSourceEpochs();
        ValidateInventory(batchId, hosts, clusters, maps, components);
        File.Delete(Path.Combine(stage, JournalName));
        var manifest = new MultiHostBackupManifest(CurrentSchemaVersion, batchId,
            DateTimeOffset.UtcNow, hosts.ToArray(), clusters.ToArray(), maps.ToArray(),
            components.ToArray(), Scope, false, false, false);
        string manifestPath = Path.Combine(stage, ManifestName);
        WriteDurable(manifestPath, JsonSerializer.Serialize(manifest, Json));
        if (new FileInfo(manifestPath).Length > MaxManifestBytes)
            throw new InvalidDataException("Manifest exceeds size limit");
        WriteDurable(Path.Combine(stage, ManifestHashName), HashFile(manifestPath));
        WriteDurable(Path.Combine(stage, CompleteName), batchId.ToString("N"));
        Verify(stage);
        DemandStableOfflineSourceEpochs();
        DemandStableOfflineSourceEpochs();
        Directory.Move(stage, target);
        return Verify(target);
    }

    public static MultiHostBackupRecoveryStatus InspectInterrupted(string destination)
    {
        string target = FixedLocal(destination);
        string stage = FixedLocal(target + ".incomplete");
        if (!Directory.Exists(stage)) return new(false, null, 0, "No incomplete archive");
        string journalPath = Path.Combine(stage, JournalName);
        try
        {
            if (!File.Exists(journalPath) || (File.GetAttributes(journalPath) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(journalPath).Length is < 30 or > MaxJournalBytes)
                throw new InvalidDataException("Missing or oversized checkpoint");
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath), Json)
                ?? throw new InvalidDataException("Missing checkpoint");
            if (journal.SchemaVersion != CurrentSchemaVersion || journal.BatchId == Guid.Empty ||
                journal.VerifiedFiles < 0 || journal.VerifiedFiles > MaxComponents ||
                !HashValid(journal.ProgressSha256))
                throw new InvalidDataException("Checkpoint metadata invalid");
            string progress = EmptyProgressDigest();
            for (int i = 0; i < journal.VerifiedFiles; i++)
            {
                string path = Path.Combine(stage, ObjectsName, i.ToString("D6") + ".bin");
                if (!File.Exists(path) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(path).Length > MaxComponentBytes)
                    throw new InvalidDataException("Checkpoint hash differs from copied file");
                progress = AdvanceProgressDigest(progress, HashFile(path));
            }
            if (!progress.Equals(journal.ProgressSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checkpoint progress digest differs from copied objects");
            return new(true, journal.BatchId, journal.VerifiedFiles,
                "Copied files verified; operator review required and automatic resume is disabled");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new(true, null, 0, "Damaged or inaccessible checkpoint; automatic recovery refused");
        }
    }

    public static MultiHostBackupManifest Verify(string archiveDirectory)
    {
        string root = FixedLocal(archiveDirectory);
        if (!Directory.Exists(root)) throw new InvalidDataException("Archive directory is missing");
        string manifestPath = Path.Combine(root, ManifestName);
        string hashPath = Path.Combine(root, ManifestHashName);
        string completePath = Path.Combine(root, CompleteName);
        string objectsPath = Path.Combine(root, ObjectsName);
        if (!File.Exists(manifestPath) || !File.Exists(hashPath) || !File.Exists(completePath) ||
            !Directory.Exists(objectsPath) || new FileInfo(manifestPath).Length is < 32 or > MaxManifestBytes ||
            new FileInfo(hashPath).Length is < 64 or > 80 || new FileInfo(completePath).Length is < 32 or > 40)
            throw new InvalidDataException("Archive is incomplete or exceeds metadata limits");
        foreach (string path in new[] { manifestPath, hashPath, completePath, objectsPath })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked archive member rejected");
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        string expectedHash = File.ReadAllText(hashPath).Trim();
        if (!HashValid(expectedHash) || !Convert.ToHexString(SHA256.HashData(manifestBytes))
                .Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest SHA-256 mismatch");
        MultiHostBackupManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MultiHostBackupManifest>(manifestBytes, Json)
                ?? throw new InvalidDataException("Manifest missing");
        }
        catch (JsonException) { throw new InvalidDataException("Manifest JSON is invalid"); }
        if (manifest.SchemaVersion != CurrentSchemaVersion || manifest.BatchId == Guid.Empty ||
            manifest.Scope != Scope || manifest.LiveSaveConsistencyConfirmed ||
            manifest.StoreWriterOwnershipConfirmed || manifest.ProductionRestoreCertified ||
            manifest.Hosts == null || manifest.Clusters == null || manifest.Maps == null || manifest.Components == null)
            throw new InvalidDataException("Manifest version, scope or unsupported certification state");
        if (!File.ReadAllText(completePath).Trim().Equals(manifest.BatchId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Completion marker does not match batch identity");
        ValidateInventory(manifest.BatchId, manifest.Hosts, manifest.Clusters, manifest.Maps, manifest.Components);
        if (Directory.GetFiles(root).Length != 3 || Directory.GetDirectories(root).Length != 1 ||
            Directory.GetDirectories(objectsPath).Length != 0)
            throw new InvalidDataException("Unexpected archive members");
        var expectedObjects = manifest.Components.Select(c => c.ObjectIndex.ToString("D6") + ".bin")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualObjects = Directory.GetFiles(objectsPath).Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualObjects.SetEquals(expectedObjects)) throw new InvalidDataException("Object membership differs from manifest");
        foreach (var component in manifest.Components)
        {
            string objectPath = Path.Combine(objectsPath, component.ObjectIndex.ToString("D6") + ".bin");
            if ((File.GetAttributes(objectPath) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(objectPath).Length != component.Length ||
                !HashFile(objectPath).Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive payload length or SHA-256 mismatch");
        }
        return manifest;
    }

    /// <summary>
    /// Restore preflight is read-only. Callers must provide a fresh local
    /// registry snapshot; no single-map restore of shared Store-v1 state is allowed.
    /// </summary>
    public static MultiHostRestorePreview PreviewRestore(string archiveDirectory,
        IReadOnlyList<MultiHostBackupHost> currentHosts,
        IReadOnlyList<MultiHostBackupCluster> currentClusters,
        IReadOnlyList<MultiHostBackupMap> currentMaps,
        bool allOwnedMapsConfirmedStopped, bool noStoreLocks,
        bool operatorApproved)
    {
        var manifest = Verify(archiveDirectory);
        MultiHostRestorePreview Reject(string reason) => new(false, reason,
            manifest.BatchId, manifest.Hosts.Length, manifest.Maps.Length, manifest.Clusters.Length,
            manifest.Components.Length);
        if (!operatorApproved) return Reject("Operator approval is required");
        if (!allOwnedMapsConfirmedStopped) return Reject("Every owned map must be independently confirmed stopped");
        if (!noStoreLocks) return Reject("Store-v1 or cluster writer lock is present or unknown");
        if (currentHosts == null || currentClusters == null || currentMaps == null)
            return Reject("Current ownership snapshot is unavailable");
        static string HostKey(MultiHostBackupHost x) => SafeId(x.HostId, "Host ID");
        static string ClusterKey(MultiHostBackupCluster x) => SafeId(x.ClusterId, "Cluster ID");
        static string MapKey(MultiHostBackupMap x) => $"{x.MapId:N}|{SafeId(x.HostId, "Host ID")}|{SafeId(x.ClusterId, "Cluster ID")}";
        if (!manifest.Hosts.Select(HostKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(currentHosts.Select(HostKey)) ||
            !manifest.Clusters.Select(ClusterKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(currentClusters.Select(ClusterKey)) ||
            !manifest.Maps.Select(MapKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(currentMaps.Select(MapKey)))
            return Reject("Host, map or cluster ownership differs from the verified archive");
        return new(true, "Read-only preflight passed; live Manager restore remains blocked",
            manifest.BatchId, manifest.Hosts.Length, manifest.Maps.Length, manifest.Clusters.Length,
            manifest.Components.Length);
    }

    /// <summary>
    /// Confirms that a verified archive still belongs to the current registries
    /// before an explicitly approved TEMP-only extraction. This does not inspect
    /// process state or Store locks and never authorizes restoration to a Manager.
    /// </summary>
    public static MultiHostRestorePreview PreviewSandboxRestore(string archiveDirectory,
        MultiHostBackupOwnershipSnapshot registry)
    {
        var manifest = Verify(archiveDirectory);
        MultiHostRestorePreview Reject(string reason) => new(false, reason,
            manifest.BatchId, manifest.Hosts.Length, manifest.Maps.Length, manifest.Clusters.Length,
            manifest.Components.Length);
        try
        {
            var catalogProjection = new MultiHostBackupCatalog(manifest.SchemaVersion,
                manifest.BatchId, manifest.Hosts, manifest.Clusters, manifest.Maps,
                Array.Empty<MultiHostBackupCatalogItem>(), false, false, OfflineCopyCatalogScope);
            ValidateAgainstRegistry(catalogProjection, registry);
        }
        catch (Exception e) when (e is InvalidDataException or UnauthorizedAccessException or
                                  ArgumentException or InvalidOperationException)
        {
            return Reject("Current HostRegistry/ClusterRegistry ownership does not match the archive");
        }
        return new(true,
            "Registry ownership matched; only a new TEMP sandbox can be materialized. Running-map and Store-lock state were not checked.",
            manifest.BatchId, manifest.Hosts.Length, manifest.Maps.Length, manifest.Clusters.Length,
            manifest.Components.Length);
    }

    private static string IsolatedRestoreTarget(string destination)
    {
        string target = FixedLocal(destination);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string name = Path.GetFileName(target);
        const string prefix = "Untamed-MultiHost-Restore-";
        if (!string.Equals(Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new UnauthorizedAccessException("Restore materialization is restricted to a new named TEMP sandbox");
        return target;
    }

    private static bool Within(string parent, string child) =>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase);

    private static bool UnderAnyRoot(string path, IReadOnlyCollection<string>? roots)
    {
        if (roots == null) return false;
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (Within(fullRoot, path)) return true;
        }
        return false;
    }

    private static void CopyVerified(string source, string destination,
        MultiHostBackupComponent component, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked archive payload rejected");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        for (DirectoryInfo? node = new DirectoryInfo(Path.GetDirectoryName(destination)!);
             node != null && node.Exists; node = node.Parent)
        {
            if ((node.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked sandbox directory rejected");
            if (Path.GetDirectoryName(node.FullName)?.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true)
                break;
        }

        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        if (input.Length != component.Length)
            throw new InvalidDataException("Archive object changed after preflight");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        long copied = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                copied = checked(copied + read);
                if (copied > component.Length) throw new InvalidDataException("Restore payload grew during copy");
                sha.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            output.Flush(true);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        string actual = Convert.ToHexString(sha.GetHashAndReset());
        if (copied != component.Length || input.Length != component.Length ||
            !actual.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restore payload hash mismatch");
    }

    /// <summary>
    /// Materializes a verified archive into a new TEMP-only clone. The existing
    /// cluster and source archive are never replaced; publication is one atomic
    /// directory rename after all payloads and the staging manifest verify.
    /// </summary>
    public static MultiHostRestoreSandboxManifest RestoreToIsolatedSandbox(
        string archiveDirectory, string destination,
        CancellationToken cancellationToken = default,
        Action<int>? afterCopiedForTesting = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archive = Verify(archiveDirectory);
        string target = IsolatedRestoreTarget(destination);
        string stage = FixedLocal(target + ".incomplete");
        if (Directory.Exists(target) || File.Exists(target) || Directory.Exists(stage) || File.Exists(stage))
            throw new IOException("Restore target or interrupted sandbox already exists; operator review required");
        string sourceRoot = FixedLocal(archiveDirectory);
        if (Within(sourceRoot, target) || Within(target, sourceRoot))
            throw new InvalidDataException("Archive and restore sandbox may not overlap");

        Directory.CreateDirectory(stage);
        string dataRoot = Path.Combine(stage, "data");
        Directory.CreateDirectory(dataRoot);
        string progressSha256 = EmptyProgressDigest();
        int verifiedFiles = 0;
        Checkpoint(stage, archive.BatchId, verifiedFiles, progressSha256);
        for (int i = 0; i < archive.Components.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var component = archive.Components[i];
            string relative = RestoreRelative(component);
            string output = Path.GetFullPath(Path.Combine(dataRoot, relative));
            if (!Within(dataRoot, output)) throw new InvalidDataException("Sandbox payload escaped its root");
            string source = Path.Combine(sourceRoot, ObjectsName, component.ObjectIndex.ToString("D6") + ".bin");
            CopyVerified(source, output, component, cancellationToken);
            progressSha256 = AdvanceProgressDigest(progressSha256, component.Sha256);
            verifiedFiles = i + 1;
            if (verifiedFiles % CheckpointInterval == 0 || verifiedFiles == archive.Components.Length)
                Checkpoint(stage, archive.BatchId, verifiedFiles, progressSha256);
            afterCopiedForTesting?.Invoke(i);
        }

        File.Delete(Path.Combine(stage, JournalName));
        var sandbox = new MultiHostRestoreSandboxManifest(CurrentSchemaVersion,
            archive.BatchId, DateTimeOffset.UtcNow, archive.Hosts, archive.Clusters,
            archive.Maps, archive.Components, false,
            "ISOLATED TEMP CLONE ONLY; no live Manager or production restore");
        string manifestPath = Path.Combine(stage, "restore-sandbox-v1.json");
        WriteDurable(manifestPath, JsonSerializer.Serialize(sandbox, Json));
        if (new FileInfo(manifestPath).Length > MaxManifestBytes)
            throw new InvalidDataException("Restore manifest exceeds size limit");
        WriteDurable(Path.Combine(stage, "restore-sandbox-v1.sha256"), HashFile(manifestPath));
        WriteDurable(Path.Combine(stage, CompleteName), archive.BatchId.ToString("N"));
        VerifyIsolatedSandbox(stage);
        Directory.Move(stage, target);
        return VerifyIsolatedSandbox(target);
    }

    public static MultiHostRestoreSandboxManifest VerifyIsolatedSandbox(string directory)
    {
        string root = IsolatedRestoreTarget(directory.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory) + Path.DirectorySeparatorChar +
              Path.GetFileName(directory)[..^".incomplete".Length]
            : directory);
        if (!Directory.Exists(root))
        {
            // Internal verification is permitted only for the exact matching
            // staging sibling of the isolated TEMP target.
            string candidate = FixedLocal(directory);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string name = Path.GetFileName(candidate);
            const string prefix = "Untamed-MultiHost-Restore-";
            if (!string.Equals(Path.GetDirectoryName(candidate), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(name[prefix.Length..^".incomplete".Length], "N", out _))
                throw new UnauthorizedAccessException("Staging verification is restricted to a TEMP restore sandbox");
            root = candidate;
        }
        string manifestPath = Path.Combine(root, "restore-sandbox-v1.json");
        string hashPath = Path.Combine(root, "restore-sandbox-v1.sha256");
        string completePath = Path.Combine(root, CompleteName);
        string dataRoot = Path.Combine(root, "data");
        if (!File.Exists(manifestPath) || !File.Exists(hashPath) || !File.Exists(completePath) ||
            !Directory.Exists(dataRoot) || new FileInfo(manifestPath).Length is < 32 or > MaxManifestBytes ||
            !HashFile(manifestPath).Equals(File.ReadAllText(hashPath).Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restore sandbox manifest is incomplete or corrupt");
        MultiHostRestoreSandboxManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MultiHostRestoreSandboxManifest>(File.ReadAllBytes(manifestPath), Json)
                ?? throw new InvalidDataException("Restore sandbox manifest missing");
        }
        catch (JsonException) { throw new InvalidDataException("Restore sandbox manifest is invalid"); }
        if (manifest.SchemaVersion != CurrentSchemaVersion || manifest.SourceBatchId == Guid.Empty ||
            manifest.ProductionRestoreAllowed || manifest.Components == null || manifest.Hosts == null ||
            manifest.Clusters == null || manifest.Maps == null ||
            !File.ReadAllText(completePath).Trim().Equals(manifest.SourceBatchId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restore sandbox scope or completion marker is invalid");
        ValidateInventory(manifest.SourceBatchId, manifest.Hosts, manifest.Clusters, manifest.Maps, manifest.Components);

        var expected = manifest.Components.Select(RestoreRelative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(dataRoot);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked restore sandbox directory rejected");
            foreach (string child in Directory.GetDirectories(current)) stack.Push(child);
            foreach (string file in Directory.GetFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked restore sandbox file rejected");
                string relative = Path.GetRelativePath(dataRoot, file);
                if (!actual.Add(relative)) throw new InvalidDataException("Duplicate sandbox payload path");
            }
        }
        if (!actual.SetEquals(expected) || Directory.GetFiles(root).Length != 3 || Directory.GetDirectories(root).Length != 1)
            throw new InvalidDataException("Restore sandbox has missing or unexpected members");
        foreach (var component in manifest.Components)
        {
            string file = Path.Combine(dataRoot, RestoreRelative(component));
            if (new FileInfo(file).Length != component.Length ||
                !HashFile(file).Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Materialized restore payload failed SHA-256 verification");
        }
        return manifest;
    }

    public static MultiHostBackupRecoveryStatus InspectInterruptedSandboxRestore(
        string archiveDirectory, string destination)
    {
        string target = IsolatedRestoreTarget(destination);
        string stage = FixedLocal(target + ".incomplete");
        if (!Directory.Exists(stage)) return new(false, null, 0, "No incomplete restore sandbox");
        try
        {
            var archive = Verify(archiveDirectory);
            string journalPath = Path.Combine(stage, JournalName);
            if (!File.Exists(journalPath) || new FileInfo(journalPath).Length is < 30 or > MaxJournalBytes)
                throw new InvalidDataException("Restore journal missing");
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath), Json)
                ?? throw new InvalidDataException("Restore journal missing");
            if (journal.BatchId != archive.BatchId || journal.VerifiedFiles < 0 ||
                journal.VerifiedFiles > archive.Components.Length ||
                !HashValid(journal.ProgressSha256))
                throw new InvalidDataException("Restore journal identity invalid");
            string dataRoot = Path.Combine(stage, "data");
            string progress = EmptyProgressDigest();
            for (int i = 0; i < journal.VerifiedFiles; i++)
            {
                var component = archive.Components[i];
                string file = Path.Combine(dataRoot, RestoreRelative(component));
                if (!File.Exists(file) || new FileInfo(file).Length != component.Length ||
                    !HashFile(file).Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Restore journaled payload differs");
                progress = AdvanceProgressDigest(progress, component.Sha256);
            }
            if (!progress.Equals(journal.ProgressSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Restore progress digest differs from materialized data");
            return new(true, journal.BatchId, journal.VerifiedFiles,
                "Sandbox restore checkpoint verified; manual recovery required and no automatic replay is allowed");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return new(true, null, 0, "Restore sandbox or journal is damaged; automatic recovery refused");
        }
    }
}
