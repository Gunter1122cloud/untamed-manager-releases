using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Versioned host identity and map ownership catalog. Host IDs are durable
/// GUIDs; map IDs keep their existing Manager identity and cannot be rebound
/// to another host by a registration update.
/// </summary>
public sealed class HostRegistry
{
    public int SchemaVersion { get; set; }
    public string LocalHostId { get; set; } = "";
    public List<RegisteredHost> Hosts { get; set; } = new();
    public List<RegisteredMap> Maps { get; set; } = new();
}

/// <summary>
/// Versioned cluster definitions and map membership. Revision changes on
/// every host/map/cluster/assignment edit and protects reviewed previews.
/// </summary>
public sealed class ClusterRegistry
{
    public int SchemaVersion { get; set; }
    public long Revision { get; set; }
    public List<RegisteredCluster> Clusters { get; set; } = new();
    public List<ClusterMapAssignment> MapAssignments { get; set; } = new();
}

public sealed class RegisteredHost
{
    public string HostId { get; set; } = "";
    public string HostName { get; set; } = "";
    public bool IsLocal { get; set; }
    // Host registration is metadata. Authentication is added by the LAN
    // pairing work and is never inferred from a registered host ID.
    public bool IsPaired { get; set; }
    public DateTimeOffset RegisteredUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RegisteredMap
{
    public Guid MapId { get; set; }
    public string HostId { get; set; } = "";
    public string MapName { get; set; } = "";
    public bool LocalConfigurationPresent { get; set; }
    public DateTimeOffset RegisteredUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RegisteredCluster
{
    public string ClusterId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TransferDirectory { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ClusterMapAssignment
{
    public Guid MapId { get; set; }
    public string ClusterId { get; set; } = "";
}

public sealed class ClusterRegistrySnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public HostRegistry HostRegistry { get; set; } = new();
    public ClusterRegistry ClusterRegistry { get; set; } = new();
}

public sealed class ClusterMembershipChange
{
    public Guid MapId { get; set; }
    public string MapName { get; set; } = "";
    public string HostId { get; set; } = "";
    public string HostName { get; set; } = "";
    public string? PreviousClusterId { get; set; }
    public string? TargetClusterId { get; set; }
}

public sealed class ClusterMembershipPreview
{
    public long BaseRevision { get; set; }
    public string ClusterId { get; set; } = "";
    public Guid[] RequestedMapIds { get; set; } = Array.Empty<Guid>();
    public List<ClusterMembershipChange> Additions { get; set; } = new();
    public List<ClusterMembershipChange> Removals { get; set; } = new();
    public List<ClusterMembershipChange> Moves { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public bool IsValid => Errors.Count == 0;
}

public sealed class ClusterMembershipReceipt
{
    public string ClusterId { get; set; } = "";
    public long Revision { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int MovedCount { get; set; }
    public DateTimeOffset AppliedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ClusterRegistryService
{
    private readonly ClusterService owner;

    internal ClusterRegistryService(ClusterService cluster) =>
        owner = cluster ?? throw new ArgumentNullException(nameof(cluster));

    public ClusterRegistrySnapshot Snapshot() => owner.SnapshotClusterRegistry();

    /// <summary>
    /// Adds a known host identity without pairing or remote capability. Host
    /// authentication remains an explicit LAN operation.
    /// </summary>
    public RegisteredHost RegisterHost(string hostId, string hostName)
    {
        string id = ClusterRegistrySchema.NormalizeHostId(hostId);
        string name = ClusterRegistrySchema.NormalizeText(hostName, 100, "Host name");
        return owner.UpdateClusterRegistry(null, config =>
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(id, config.HostRegistry.LocalHostId))
                throw new InvalidOperationException("Local host identity is managed by the Manager profile");
            var current = config.HostRegistry.Hosts.SingleOrDefault(x =>
                StringComparer.OrdinalIgnoreCase.Equals(x.HostId, id));
            if (current == null)
            {
                current = new RegisteredHost { HostId = id, HostName = name, IsLocal = false, IsPaired = false };
                config.HostRegistry.Hosts.Add(current);
            }
            else
            {
                if (current.IsLocal) throw new InvalidDataException("Local host identity conflict");
                current.HostName = name;
            }
            return ClusterRegistrySchema.Clone(current);
        });
    }

    /// <summary>
    /// Registers map metadata to a known remote host. A previously registered
    /// map ID cannot change host owner; local maps are cataloged from LocalMaps.
    /// </summary>
    public RegisteredMap RegisterRemoteMap(string hostId, Guid mapId, string mapName)
    {
        string id = ClusterRegistrySchema.NormalizeHostId(hostId);
        if (mapId == Guid.Empty) throw new InvalidDataException("Map ID is required");
        string name = ClusterRegistrySchema.NormalizeText(mapName, 100, "Map name");
        return owner.UpdateClusterRegistry(null, config =>
        {
            var host = config.HostRegistry.Hosts.SingleOrDefault(x =>
                StringComparer.OrdinalIgnoreCase.Equals(x.HostId, id));
            if (host == null) throw new KeyNotFoundException("Host is not registered");
            if (host.IsLocal) throw new UnauthorizedAccessException("Local maps are managed by this Manager");
            var current = config.HostRegistry.Maps.SingleOrDefault(x => x.MapId == mapId);
            if (current != null && !StringComparer.OrdinalIgnoreCase.Equals(current.HostId, id))
                throw new UnauthorizedAccessException("Map ownership cannot be changed");
            if (current == null)
            {
                current = new RegisteredMap {
                    MapId = mapId, HostId = id, MapName = name,
                    LocalConfigurationPresent = false
                };
                config.HostRegistry.Maps.Add(current);
            }
            else
            {
                current.MapName = name;
                current.LocalConfigurationPresent = false;
            }
            return ClusterRegistrySchema.Clone(current);
        });
    }

    /// <summary>
    /// Reconciles only metadata received over an authenticated, HostId-pinned
    /// LAN status exchange. It never imports paths, credentials, launch args,
    /// or remote files, and does not advance registry revision for unchanged data.
    /// </summary>
    public bool SynchronizeAuthenticatedPeer(HostStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        string id=ClusterRegistrySchema.NormalizeHostId(status.HostId);
        if(StringComparer.OrdinalIgnoreCase.Equals(id,owner.Config.LocalHostId))
            throw new InvalidDataException("A local host cannot be registered as its own peer");
        string name=ClusterRegistrySchema.NormalizeText(status.HostName,100,"Host name");
        if(status.Maps==null)throw new InvalidDataException("Peer map list is missing");
        var maps=status.Maps.Select(m=>{
            if(m==null||!Guid.TryParse(m.Id,out var mapId)||mapId==Guid.Empty)
                throw new InvalidDataException("Peer map identity is invalid");
            return(MapId:mapId,MapName:ClusterRegistrySchema.NormalizeText(m.Name,100,"Peer map name"));
        }).ToArray();
        if(maps.Select(x=>x.MapId).Distinct().Count()!=maps.Length)
            throw new InvalidDataException("Peer map IDs are duplicated");

        var before=Snapshot().HostRegistry;
        var currentHost=before.Hosts.SingleOrDefault(x=>
            StringComparer.OrdinalIgnoreCase.Equals(x.HostId,id));
        bool changed=currentHost==null||currentHost.IsLocal||!currentHost.IsPaired||
            !StringComparer.Ordinal.Equals(currentHost.HostName,name);
        foreach(var map in maps)
        {
            if(owner.Config.LocalMaps.Any(x=>x.Id==map.MapId))
                throw new UnauthorizedAccessException("Remote peer cannot claim a locally owned map ID");
            var current=before.Maps.SingleOrDefault(x=>x.MapId==map.MapId);
            if(current!=null&&!StringComparer.OrdinalIgnoreCase.Equals(current.HostId,id))
                throw new UnauthorizedAccessException("Remote peer cannot change map ownership");
            if(current==null||current.LocalConfigurationPresent||
               !StringComparer.Ordinal.Equals(current.MapName,map.MapName))changed=true;
        }
        if(!changed)return false;

        owner.UpdateClusterRegistry(null,config=>{
            var host=config.HostRegistry.Hosts.SingleOrDefault(x=>
                StringComparer.OrdinalIgnoreCase.Equals(x.HostId,id));
            if(host==null)
            {
                host=new RegisteredHost{HostId=id,HostName=name,IsLocal=false,IsPaired=true};
                config.HostRegistry.Hosts.Add(host);
            }
            else
            {
                if(host.IsLocal)throw new InvalidDataException("Remote peer conflicts with local host identity");
                host.HostName=name;host.IsPaired=true;
            }
            foreach(var map in maps)
            {
                var current=config.HostRegistry.Maps.SingleOrDefault(x=>x.MapId==map.MapId);
                if(current!=null&&!StringComparer.OrdinalIgnoreCase.Equals(current.HostId,id))
                    throw new UnauthorizedAccessException("Remote peer cannot change map ownership");
                if(current==null)
                {
                    config.HostRegistry.Maps.Add(new RegisteredMap{
                        MapId=map.MapId,HostId=id,MapName=map.MapName,
                        LocalConfigurationPresent=false
                    });
                }
                else
                {
                    current.MapName=map.MapName;
                    current.LocalConfigurationPresent=false;
                }
            }
            return true;
        });
        return true;
    }

    public RegisteredCluster CreateCluster(string clusterId, string name,
        string transferDirectory, string? backupDirectory = null)
    {
        var candidate = ClusterRegistrySchema.NormalizeCluster(
            clusterId, name, transferDirectory, backupDirectory, DateTimeOffset.UtcNow);
        return owner.UpdateClusterRegistry(null, config =>
        {
            if (config.ClusterRegistry.Clusters.Any(x =>
                StringComparer.OrdinalIgnoreCase.Equals(x.ClusterId, candidate.ClusterId)))
                throw new InvalidOperationException("Cluster ID already exists");
            ClusterRegistrySchema.ValidateClusterPaths(config.ClusterRegistry.Clusters, candidate);
            config.ClusterRegistry.Clusters.Add(candidate);
            return ClusterRegistrySchema.Clone(candidate);
        });
    }

    /// <summary>
    /// One-time reconstruction of the LOCAL original Store-v1 membership in
    /// Obsidian's metadata registry. This is NOT the old shared Store writer:
    /// original file bytes, writer/session/lock and launch settings are NEVER
    /// mutated. The caller must read/pin the original four files with stable
    /// local handles and pass ONLY the source-derived five members and UNC.
    /// </summary>
    internal ClusterMembershipReceipt ImportHistoricalOriginalLocalMembership(
        string sourceClusterId,string originalTransferDirectory,
        string originalFourFilesSha256,
        IReadOnlyList<(Guid MapId,string MapKey)> originalFive,
        long expectedRevision)
    {
        string id=ClusterRegistrySchema.NormalizeClusterId(sourceClusterId);
        if(originalFourFilesSha256 is not {Length:64} ||
           !originalFourFilesSha256.All(Uri.IsHexDigit) ||
           originalFive==null||originalFive.Count!=5||
           originalFive.Any(m=>m.MapId==Guid.Empty||
               string.IsNullOrWhiteSpace(m.MapKey))||
           originalFive.Select(m=>m.MapId).Distinct().Count()!=5||
           originalFive.Select(m=>m.MapKey).Distinct(
               StringComparer.OrdinalIgnoreCase).Count()!=5)
            throw new InvalidDataException("UM-ORIGINAL-CLUSTER-FIVE-SOURCE-INVALID");
        var proposed=ClusterRegistrySchema.NormalizeCluster(id,
            "Original Legacy Store-v1",originalTransferDirectory,null,
            DateTimeOffset.UtcNow);
        if(!proposed.TransferDirectory.StartsWith(@"\\",
            StringComparison.Ordinal))
            throw new InvalidDataException("UM-ORIGINAL-CLUSTER-UNC-REQUIRED");
        var originalIds=originalFive.Select(m=>m.MapId).ToHashSet();

        static void RequireSourcePins(ManagerConfig config,
            IReadOnlyList<(Guid MapId,string MapKey)> members,string sha)
        {
            foreach(var item in members)
            {
                var local=config.LocalMaps.SingleOrDefault(m=>m.Id==item.MapId);
                if(local==null||!local.HistoricalStoreProtected||
                   !string.Equals(local.HistoricalArchiveSha256,sha,
                       StringComparison.OrdinalIgnoreCase)||
                   !string.Equals(local.HistoricalMapKey,item.MapKey,
                       StringComparison.Ordinal))
                    throw new UnauthorizedAccessException(
                        "UM-ORIGINAL-CLUSTER-MAP-SOURCE-DIFFERS");
            }
        }

        var snapshot=Snapshot();
        if(snapshot.Revision!=expectedRevision)
            throw new InvalidOperationException("UM-ORIGINAL-CLUSTER-REGISTRY-CHANGED");
        RequireSourcePins(owner.Config,originalFive,originalFourFilesSha256);
        var already=snapshot.ClusterRegistry.Clusters.SingleOrDefault(c=>
            StringComparer.OrdinalIgnoreCase.Equals(c.ClusterId,id));
        if(already!=null && !string.Equals(
            already.TransferDirectory,proposed.TransferDirectory,
                StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "UM-ORIGINAL-CLUSTER-TRANSFER-MISMATCH");
        if(snapshot.ClusterRegistry.MapAssignments.Any(a=>
            originalIds.Contains(a.MapId)&&
            !StringComparer.OrdinalIgnoreCase.Equals(a.ClusterId,id)))
            throw new UnauthorizedAccessException(
                "UM-ORIGINAL-CLUSTER-FOREIGN-ASSIGNMENT");
        int needed=originalIds.Count(mid=>!snapshot.ClusterRegistry.MapAssignments
            .Any(a=>a.MapId==mid&&
                StringComparer.OrdinalIgnoreCase.Equals(a.ClusterId,id)));
        if(already!=null&&needed==0)
            return new ClusterMembershipReceipt{
                ClusterId=id,Revision=expectedRevision,
                AddedCount=0,RemovedCount=0,MovedCount=0
            };
        return owner.UpdateClusterRegistry(expectedRevision,config=>
        {
            RequireSourcePins(config,originalFive,originalFourFilesSha256);
            var registered=config.ClusterRegistry.Clusters.SingleOrDefault(c=>
                StringComparer.OrdinalIgnoreCase.Equals(c.ClusterId,id));
            if(registered==null)
            {
                ClusterRegistrySchema.ValidateClusterPaths(
                    config.ClusterRegistry.Clusters,proposed);
                config.ClusterRegistry.Clusters.Add(proposed);
            }
            else if(!string.Equals(registered.TransferDirectory,
                proposed.TransferDirectory,StringComparison.Ordinal))
                throw new UnauthorizedAccessException(
                    "UM-ORIGINAL-CLUSTER-TRANSFER-MISMATCH");
            if(config.ClusterRegistry.MapAssignments.Any(a=>
                originalIds.Contains(a.MapId)&&
                !StringComparer.OrdinalIgnoreCase.Equals(a.ClusterId,id)))
                throw new UnauthorizedAccessException(
                    "UM-ORIGINAL-CLUSTER-FOREIGN-ASSIGNMENT");
            foreach(var member in originalFive)
                if(!config.ClusterRegistry.MapAssignments.Any(a=>
                    a.MapId==member.MapId))
                    config.ClusterRegistry.MapAssignments.Add(new ClusterMapAssignment{
                        MapId=member.MapId,ClusterId=id
                    });
            return new ClusterMembershipReceipt{
                ClusterId=id,Revision=checked(expectedRevision+1),
                AddedCount=needed,RemovedCount=0,MovedCount=0
            };
        });
    }

    public RegisteredCluster UpdateCluster(string clusterId, string name,
        string transferDirectory, string? backupDirectory = null)
    {
        string id = ClusterRegistrySchema.NormalizeClusterId(clusterId);
        return owner.UpdateClusterRegistry(null, config =>
        {
            var current = config.ClusterRegistry.Clusters.SingleOrDefault(x =>
                StringComparer.OrdinalIgnoreCase.Equals(x.ClusterId, id));
            if (current == null) throw new KeyNotFoundException("Cluster is not registered");
            var updated = ClusterRegistrySchema.NormalizeCluster(id, name,
                transferDirectory, backupDirectory, current.CreatedUtc);
            // The original Legacy Store cluster may already own a live external
            // transfer root. A generic metadata editor must never rebind or
            // rename that source-pinned identity. The same applies to any
            // imported historical Store member assigned to a differently
            // named cluster. No Store writer/transfer files are touched here.
            bool historicalMember=config.ClusterRegistry.MapAssignments.Any(a=>
                StringComparer.OrdinalIgnoreCase.Equals(a.ClusterId,id) &&
                config.LocalMaps.Any(m=>m.Id==a.MapId &&
                    (m.HistoricalStoreProtected ||
                     !string.IsNullOrWhiteSpace(m.HistoricalArchiveSha256))));
            if((OriginalLegacyCluster(id)||historicalMember) &&
               (!StringComparer.Ordinal.Equals(current.Name,updated.Name) ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    current.TransferDirectory,updated.TransferDirectory) ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    current.BackupDirectory,updated.BackupDirectory)))
                throw new UnauthorizedAccessException(
                    "UM-REGISTRY-ORIGINAL-STORE-CLUSTER-IMMUTABLE");
            ClusterRegistrySchema.ValidateClusterPaths(config.ClusterRegistry.Clusters, updated);
            current.Name = updated.Name;
            current.TransferDirectory = updated.TransferDirectory;
            current.BackupDirectory = updated.BackupDirectory;
            current.UpdatedUtc = DateTimeOffset.UtcNow;
            return ClusterRegistrySchema.Clone(current);
        });
    }

    public ClusterMembershipPreview PreviewMapAssignments(string clusterId,
        IEnumerable<Guid> requestedMapIds)
    {
        ArgumentNullException.ThrowIfNull(requestedMapIds);
        var snapshot = Snapshot();
        return CreatePreview(snapshot, clusterId, requestedMapIds.ToArray());
    }

    /// <summary>
    /// Applies only the exact map list reviewed at the supplied registry
    /// revision. The caller must first show and confirm PreviewMapAssignments.
    /// </summary>
    public ClusterMembershipReceipt ApplyMapAssignments(string clusterId,
        IEnumerable<Guid> requestedMapIds, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(requestedMapIds);
        string id = ClusterRegistrySchema.NormalizeClusterId(clusterId);
        Guid[] mapIds = requestedMapIds.ToArray();
        ClusterMembershipPreview reviewed = CreatePreview(Snapshot(), id, mapIds);
        if (reviewed.BaseRevision != expectedRevision)
            throw new InvalidOperationException("Registry changed after membership preview");
        if (!reviewed.IsValid)
            throw new InvalidDataException("Membership preview contains invalid map assignments");

        return owner.UpdateClusterRegistry(expectedRevision, config =>
        {
            var currentSnapshot = new ClusterRegistrySnapshot {
                Revision = config.ClusterRegistry.Revision,
                HostRegistry = ClusterRegistrySchema.Clone(config.HostRegistry),
                ClusterRegistry = ClusterRegistrySchema.Clone(config.ClusterRegistry)
            };
            var currentPreview = CreatePreview(currentSnapshot, id, mapIds);
            if (!currentPreview.IsValid)
                throw new InvalidDataException("Membership preview is no longer valid");

            var proposed = new HashSet<Guid>(mapIds);
            config.ClusterRegistry.MapAssignments.RemoveAll(x =>
                (StringComparer.OrdinalIgnoreCase.Equals(x.ClusterId, id) && !proposed.Contains(x.MapId)) ||
                proposed.Contains(x.MapId));
            foreach (Guid mapId in mapIds.Distinct())
                config.ClusterRegistry.MapAssignments.Add(new ClusterMapAssignment {
                    MapId = mapId, ClusterId = id
                });

            return new ClusterMembershipReceipt {
                ClusterId = id,
                Revision = checked(expectedRevision + 1),
                AddedCount = currentPreview.Additions.Count,
                RemovedCount = currentPreview.Removals.Count,
                MovedCount = currentPreview.Moves.Count
            };
        });
    }

    // The generic registry is metadata-only. It must not reassign historic
    // Store-v1 members or admit new maps into the original Legacy cluster.
    // That requires the separate source-pinned Store migration/peer approval.
    private static bool OriginalLegacyCluster(string? id) =>
        OriginalStoreRuntimeIdentity.IsOriginalClusterId(id);

    private ClusterMembershipPreview CreatePreview(ClusterRegistrySnapshot snapshot,
        string clusterId, Guid[] requestedMapIds)
    {
        string id;
        try { id = ClusterRegistrySchema.NormalizeClusterId(clusterId); }
        catch (InvalidDataException ex)
        {
            return new ClusterMembershipPreview {
                BaseRevision = snapshot.Revision, ClusterId = clusterId ?? "",
                RequestedMapIds = requestedMapIds, Errors = new List<string> { ex.Message }
            };
        }
        var preview = new ClusterMembershipPreview {
            BaseRevision = snapshot.Revision, ClusterId = id,
            RequestedMapIds = requestedMapIds
        };
        var cluster = snapshot.ClusterRegistry.Clusters.SingleOrDefault(x =>
            StringComparer.OrdinalIgnoreCase.Equals(x.ClusterId, id));
        if (cluster == null)
        {
            preview.Errors.Add("Cluster is not registered");
            return preview;
        }
        var duplicate = requestedMapIds.GroupBy(x => x).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null) preview.Errors.Add("Duplicate map ID in selection");

        var requested = new HashSet<Guid>(requestedMapIds);
        var hosts = snapshot.HostRegistry.Hosts.ToDictionary(x => x.HostId,
            StringComparer.OrdinalIgnoreCase);
        var maps = snapshot.HostRegistry.Maps.ToDictionary(x => x.MapId);
        var protectedLocal = owner.Config.LocalMaps.Where(map =>
            map.HistoricalStoreProtected ||
            !string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256))
            .Select(map => map.Id).ToHashSet();
        // Historical Store identity comes from the imported source, not
        // from an assumed literal LegacyCluster ID. Protect every cluster
        // holding a locally source-pinned historical member.
        var protectedClusterIds = snapshot.ClusterRegistry.MapAssignments
            .Where(a => protectedLocal.Contains(a.MapId))
            .Select(a => a.ClusterId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Guid mapId in requested.Distinct())
        {
            if (mapId == Guid.Empty || !maps.TryGetValue(mapId, out var map))
            {
                preview.Errors.Add("A selected map is not registered");
                continue;
            }
            hosts.TryGetValue(map.HostId, out var ownerHost);
            if (ownerHost?.IsLocal == true && !map.LocalConfigurationPresent)
            {
                preview.Errors.Add("A selected map is no longer registered on its host");
                continue;
            }
            var owner = snapshot.ClusterRegistry.MapAssignments.SingleOrDefault(x => x.MapId == mapId);
            if (owner != null && StringComparer.OrdinalIgnoreCase.Equals(owner.ClusterId, id)) continue;
            if (protectedLocal.Contains(mapId) || OriginalLegacyCluster(owner?.ClusterId) ||
                OriginalLegacyCluster(id) || protectedClusterIds.Contains(id) ||
                (owner != null && protectedClusterIds.Contains(owner.ClusterId)))
            {
                preview.Errors.Add("UM-REGISTRY-ORIGINAL-STORE-MEMBERSHIP-IMMUTABLE");
                continue;
            }
            hosts.TryGetValue(map.HostId, out var host);
            var change = new ClusterMembershipChange {
                MapId = mapId, MapName = map.MapName, HostId = map.HostId,
                HostName = host?.HostName ?? "Unknown host",
                PreviousClusterId = owner?.ClusterId, TargetClusterId = id
            };
            if (owner == null) preview.Additions.Add(change);
            else preview.Moves.Add(change);
        }

        foreach (var assignment in snapshot.ClusterRegistry.MapAssignments.Where(x =>
            StringComparer.OrdinalIgnoreCase.Equals(x.ClusterId, id) && !requested.Contains(x.MapId)))
        {
            if (protectedLocal.Contains(assignment.MapId) ||
                OriginalLegacyCluster(assignment.ClusterId) ||
                protectedClusterIds.Contains(assignment.ClusterId))
            {
                preview.Errors.Add("UM-REGISTRY-ORIGINAL-STORE-MEMBERSHIP-IMMUTABLE");
                continue;
            }
            maps.TryGetValue(assignment.MapId, out var map);
            string hostId = map?.HostId ?? "";
            hosts.TryGetValue(hostId, out var host);
            preview.Removals.Add(new ClusterMembershipChange {
                MapId = assignment.MapId, MapName = map?.MapName ?? "Unknown map",
                HostId = hostId, HostName = host?.HostName ?? "Unknown host",
                PreviousClusterId = id, TargetClusterId = null
            });
        }
        return preview;
    }
}

internal static class ClusterRegistrySchema
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions CloneOptions = new();

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(value, CloneOptions), CloneOptions)
        ?? throw new InvalidDataException("Registry copy failed");

    public static bool PrepareForSave(ManagerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string before = JsonSerializer.Serialize(new { config.HostRegistry, config.ClusterRegistry });
        string localId = NormalizeHostId(config.LocalHostId);
        string localName = NormalizeText(config.LocalHostName, 100, "Local host name");

        config.HostRegistry ??= new HostRegistry();
        config.ClusterRegistry ??= new ClusterRegistry();
        var hosts = config.HostRegistry.Hosts ?? new List<RegisteredHost>();
        var maps = config.HostRegistry.Maps ?? new List<RegisteredMap>();
        var clusters = config.ClusterRegistry.Clusters ?? new List<RegisteredCluster>();
        var assignments = config.ClusterRegistry.MapAssignments ?? new List<ClusterMapAssignment>();
        config.HostRegistry.Hosts = hosts;
        config.HostRegistry.Maps = maps;
        config.ClusterRegistry.Clusters = clusters;
        config.ClusterRegistry.MapAssignments = assignments;

        if (config.HostRegistry.SchemaVersion is 0 or CurrentVersion)
            config.HostRegistry.SchemaVersion = CurrentVersion;
        else throw new InvalidDataException("Unsupported HostRegistry schema version");
        if (config.ClusterRegistry.SchemaVersion is 0 or CurrentVersion)
            config.ClusterRegistry.SchemaVersion = CurrentVersion;
        else throw new InvalidDataException("Unsupported ClusterRegistry schema version");
        if (config.ClusterRegistry.Revision < 0)
            throw new InvalidDataException("Invalid ClusterRegistry revision");

        if (string.IsNullOrWhiteSpace(config.HostRegistry.LocalHostId))
            config.HostRegistry.LocalHostId = localId;
        else if (!StringComparer.OrdinalIgnoreCase.Equals(
            NormalizeHostId(config.HostRegistry.LocalHostId), localId))
            throw new InvalidDataException("Local host identity cannot be changed by registry migration");
        else config.HostRegistry.LocalHostId = localId;

        foreach (var host in hosts)
        {
            if (host == null) throw new InvalidDataException("Null host registry entry");
            host.HostId = NormalizeHostId(host.HostId);
            host.HostName = NormalizeText(host.HostName, 100, "Host name");
            if (host.IsLocal && !StringComparer.OrdinalIgnoreCase.Equals(host.HostId, localId))
                throw new InvalidDataException("A second local host identity is not allowed");
        }
        var local = hosts.SingleOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.HostId, localId));
        if (local == null)
        {
            local = new RegisteredHost { HostId = localId, HostName = localName, IsLocal = true, IsPaired = false };
            hosts.Add(local);
        }
        else
        {
            local.IsLocal = true;
            local.HostName = localName;
            local.IsPaired = false;
        }

        var mapsById = new Dictionary<Guid, RegisteredMap>(maps.Count);
        foreach (var map in maps)
        {
            if (map == null) throw new InvalidDataException("Null map registry entry");
            if (map.MapId == Guid.Empty) throw new InvalidDataException("Empty map registry ID");
            map.HostId = NormalizeHostId(map.HostId);
            map.MapName = NormalizeText(map.MapName, 100, "Map name");
            if (!mapsById.TryAdd(map.MapId, map))
                throw new InvalidDataException("Duplicate map registry ID");
        }
        foreach (var existing in maps.Where(x => StringComparer.OrdinalIgnoreCase.Equals(x.HostId, localId)))
            existing.LocalConfigurationPresent = false;
        foreach (var localMap in config.LocalMaps ?? new List<LocalMap>())
        {
            if (localMap == null || localMap.Id == Guid.Empty)
                throw new InvalidDataException("Invalid local map identity");
            string mapName = LocalMapNameRules.Normalize(localMap.Name);
            mapsById.TryGetValue(localMap.Id, out var existing);
            if (existing != null && !StringComparer.OrdinalIgnoreCase.Equals(existing.HostId, localId))
                throw new InvalidDataException("Local map ID is owned by another host");
            if (existing == null)
            {
                existing = new RegisteredMap {
                    MapId = localMap.Id, HostId = localId, MapName = mapName,
                    LocalConfigurationPresent = true
                };
                maps.Add(existing);
                mapsById.Add(existing.MapId, existing);
            }
            else
            {
                existing.MapName = mapName;
                existing.LocalConfigurationPresent = true;
            }
        }

        string after = JsonSerializer.Serialize(new { config.HostRegistry, config.ClusterRegistry });
        if (!StringComparer.Ordinal.Equals(before, after))
            config.ClusterRegistry.Revision = checked(config.ClusterRegistry.Revision + 1);
        return !StringComparer.Ordinal.Equals(before, after);
    }

    public static void Validate(ManagerConfig config)
    {
        var hostRegistry = config.HostRegistry;
        var clusterRegistry = config.ClusterRegistry;
        if (hostRegistry.SchemaVersion != CurrentVersion || clusterRegistry.SchemaVersion != CurrentVersion)
            throw new InvalidDataException("Registry schema migration is required");
        if (clusterRegistry.Revision < 0) throw new InvalidDataException("Invalid registry revision");
        string localId = NormalizeHostId(config.LocalHostId);
        if (!StringComparer.OrdinalIgnoreCase.Equals(NormalizeHostId(hostRegistry.LocalHostId), localId))
            throw new InvalidDataException("Local host registry ID does not match Manager profile");

        var hostIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int localCount = 0;
        foreach (var host in hostRegistry.Hosts)
        {
            host.HostId = NormalizeHostId(host.HostId);
            host.HostName = NormalizeText(host.HostName, 100, "Host name");
            if (!hostIds.Add(host.HostId)) throw new InvalidDataException("Duplicate host ID");
            if (host.IsLocal)
            {
                localCount++;
                if (!StringComparer.OrdinalIgnoreCase.Equals(host.HostId, localId))
                    throw new InvalidDataException("Unexpected local host ID");
            }
        }
        if (localCount != 1 || !hostIds.Contains(localId))
            throw new InvalidDataException("Exactly one registered local host is required");

        var mapsById = new Dictionary<Guid, RegisteredMap>(hostRegistry.Maps.Count);
        foreach (var map in hostRegistry.Maps)
        {
            if (map.MapId == Guid.Empty || !mapsById.TryAdd(map.MapId, map))
                throw new InvalidDataException("Empty or duplicate map ID");
            map.HostId = NormalizeHostId(map.HostId);
            if (!hostIds.Contains(map.HostId)) throw new InvalidDataException("Map owner is not registered");
            map.MapName = NormalizeText(map.MapName, 100, "Map name");
            if (map.LocalConfigurationPresent &&
                !StringComparer.OrdinalIgnoreCase.Equals(map.HostId, localId))
                throw new InvalidDataException("Invalid local map ownership");
        }
        foreach (var localMap in config.LocalMaps)
        {
            if (!mapsById.TryGetValue(localMap.Id, out var registered))
                throw new InvalidDataException("Local map is missing from HostRegistry");
            if (!StringComparer.OrdinalIgnoreCase.Equals(registered.HostId, localId) ||
                !registered.LocalConfigurationPresent)
                throw new InvalidDataException("Local map ownership mismatch");
        }

        var clusterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clusterPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cluster in clusterRegistry.Clusters)
        {
            string id = NormalizeClusterId(cluster.ClusterId);
            if (!StringComparer.Ordinal.Equals(id, cluster.ClusterId) || !clusterIds.Add(id))
                throw new InvalidDataException("Invalid or duplicate cluster ID");
            cluster.Name = NormalizeText(cluster.Name, 100, "Cluster name");
            cluster.TransferDirectory = NormalizeDirectory(cluster.TransferDirectory, "Transfer directory");
            if (!clusterPaths.Add(cluster.TransferDirectory))
                throw new InvalidDataException("Cluster directories must be unique");
            cluster.BackupDirectory = string.IsNullOrWhiteSpace(cluster.BackupDirectory)
                ? "" : NormalizeDirectory(cluster.BackupDirectory, "Backup directory");
            if (cluster.BackupDirectory.Length > 0 && !clusterPaths.Add(cluster.BackupDirectory))
                throw new InvalidDataException("Cluster directories must be unique");
        }
        var assigned = new HashSet<Guid>();
        foreach (var assignment in clusterRegistry.MapAssignments)
        {
            string id = NormalizeClusterId(assignment.ClusterId);
            if (!clusterIds.Contains(id)) throw new InvalidDataException("Assignment cluster is not registered");
            if (!mapsById.ContainsKey(assignment.MapId)) throw new InvalidDataException("Assignment map is not registered");
            if (!assigned.Add(assignment.MapId)) throw new InvalidDataException("A map can belong to only one cluster");
            assignment.ClusterId = id;
        }
    }

    public static string NormalizeHostId(string? raw)
    {
        if (!Guid.TryParse(raw?.Trim(), out var parsed) || parsed == Guid.Empty)
            throw new InvalidDataException("Host ID must be a non-empty persistent GUID");
        return parsed.ToString("N");
    }

    public static string NormalizeClusterId(string? raw)
    {
        string id = raw?.Trim().ToLowerInvariant() ?? "";
        if (id.Length is < 1 or > 64 || id.Any(c =>
            !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidDataException("Cluster ID may contain only letters, numbers, hyphen and underscore");
        return id;
    }

    public static string NormalizeText(string? raw, int max, string label)
    {
        string value = raw?.Trim() ?? "";
        if (value.Length is < 1 || value.Length > max || value.Any(char.IsControl))
            throw new InvalidDataException(label + " is empty or too long");
        return value;
    }

    public static RegisteredCluster NormalizeCluster(string clusterId, string name,
        string transferDirectory, string? backupDirectory, DateTimeOffset createdUtc)
    {
        var cluster = new RegisteredCluster {
            ClusterId = NormalizeClusterId(clusterId),
            Name = NormalizeText(name, 100, "Cluster name"),
            TransferDirectory = NormalizeDirectory(transferDirectory, "Transfer directory"),
            BackupDirectory = string.IsNullOrWhiteSpace(backupDirectory)
                ? "" : NormalizeDirectory(backupDirectory, "Backup directory"),
            CreatedUtc = createdUtc,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        if (cluster.BackupDirectory.Length > 0 && StringComparer.OrdinalIgnoreCase.Equals(
            cluster.TransferDirectory, cluster.BackupDirectory))
            throw new InvalidDataException("Backup and transfer directories must be separate");
        return cluster;
    }

    public static void ValidateClusterPaths(IEnumerable<RegisteredCluster> clusters,
        RegisteredCluster candidate)
    {
        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in clusters.Where(x =>
            !StringComparer.OrdinalIgnoreCase.Equals(x.ClusterId, candidate.ClusterId)))
        {
            existingPaths.Add(existing.TransferDirectory);
            if (!string.IsNullOrWhiteSpace(existing.BackupDirectory))
                existingPaths.Add(existing.BackupDirectory);
        }
        if (!existingPaths.Add(candidate.TransferDirectory) ||
            (candidate.BackupDirectory.Length > 0 && !existingPaths.Add(candidate.BackupDirectory)))
            throw new InvalidOperationException("Cluster directories must be unique across transfer and backup roles");
    }

    private static string NormalizeDirectory(string? raw, string label)
    {
        string value = raw?.Trim() ?? "";
        if (value.Length is < 1 or > 520 || value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value))
            throw new InvalidDataException(label + " must be a fully qualified local or UNC path");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
}

public sealed partial class ClusterService
{
    private readonly object _registryGate = new();

    internal ClusterRegistrySnapshot SnapshotClusterRegistry()
    {
        lock (_registryGate)
            return new ClusterRegistrySnapshot {
                Revision = Config.ClusterRegistry.Revision,
                HostRegistry = ClusterRegistrySchema.Clone(Config.HostRegistry),
                ClusterRegistry = ClusterRegistrySchema.Clone(Config.ClusterRegistry)
            };
    }

    internal T UpdateClusterRegistry<T>(long? expectedRevision, Func<ManagerConfig, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_registryGate)
        {
            long currentRevision = Config.ClusterRegistry.Revision;
            if (expectedRevision.HasValue && expectedRevision.Value != currentRevision)
                throw new InvalidOperationException("Registry changed after membership preview");
            var staged = ClusterRegistrySchema.Clone(Config);
            T result = update(staged);
            staged.ClusterRegistry.Revision = checked(currentRevision + 1);
            Store.Save(staged);
            Config.HostRegistry = ClusterRegistrySchema.Clone(staged.HostRegistry);
            Config.ClusterRegistry = ClusterRegistrySchema.Clone(staged.ClusterRegistry);
            return result;
        }
    }
}
