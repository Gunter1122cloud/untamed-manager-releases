# Host and Cluster Registry API contract

This contract is the versioned hand-off for the multi-host LAN and React UI
work. The implementation lives in `wpf/Core/ClusterRegistry.cs` and is hosted
by the current WinForms/WebView2 app through
`desktop/DesktopClusterRegistryBridge.cs`. It does not add or change a WPF UI.

## Persistence and identity

- The persisted DTOs are `HostRegistry` and `ClusterRegistry`, both at schema
  version `1`. They are stored with the existing Manager profile in the same
  atomic `cluster-config.json` replacement as `LocalMaps`.
- Host IDs are durable GUIDs normalized to 32 hexadecimal characters. Map IDs
  are the existing Manager `Guid` values. Cluster IDs are 1–64 lowercase
  ASCII letters, digits, `-` or `_`.
- A map has one immutable host owner and at most one cluster assignment. A
  local map's ID, name and host are synchronized from `LocalMaps`; removing a
  local map marks its registry entry inactive and preserves its assignment for
  recovery. A map ID cannot be registered under another host.
- Schema-0 profiles migrate additively. The migration preserves existing
  host/map IDs and names, creates no directories and does not open or rewrite
  Legacy Store-v1 worlds, transfer files, backups or locks.
- `revision` increases whenever host, map, cluster or assignment metadata
  changes. Consumers use it to reject stale approval previews.

## C# DTOs

The public DTO names and properties are the source of truth. JSON uses the
WebView2 bridge's camel-case policy.

```text
HostRegistry { SchemaVersion:int, LocalHostId:string,
  Hosts:RegisteredHost[], Maps:RegisteredMap[] }
RegisteredHost { HostId:string, HostName:string, IsLocal:bool,
  IsPaired:bool, RegisteredUtc:DateTimeOffset }
RegisteredMap { MapId:Guid, HostId:string, MapName:string,
  LocalConfigurationPresent:bool, RegisteredUtc:DateTimeOffset }
ClusterRegistry { SchemaVersion:int, Revision:long,
  Clusters:RegisteredCluster[], MapAssignments:ClusterMapAssignment[] }
RegisteredCluster { ClusterId:string, Name:string, TransferDirectory:string,
  BackupDirectory:string, CreatedUtc:DateTimeOffset, UpdatedUtc:DateTimeOffset }
ClusterMapAssignment { MapId:Guid, ClusterId:string }
ClusterRegistrySnapshot { SchemaVersion:int, Revision:long,
  HostRegistry:HostRegistry, ClusterRegistry:ClusterRegistry }
ClusterMembershipChange { MapId:Guid, MapName:string, HostId:string,
  HostName:string, PreviousClusterId:string?, TargetClusterId:string? }
ClusterMembershipPreview { BaseRevision:long, ClusterId:string,
  RequestedMapIds:Guid[], Additions:ClusterMembershipChange[],
  Removals:ClusterMembershipChange[], Moves:ClusterMembershipChange[],
  Errors:string[], IsValid:bool }
ClusterMembershipReceipt { ClusterId:string, Revision:long,
  AddedCount:int, RemovedCount:int, MovedCount:int, AppliedUtc:DateTimeOffset }
```

## WebView2 RPC

Every request is `{ id: GUID, method: string, ...payload }`; every response is
`{ id, ok, data }` or `{ id, ok:false, error }`. The native bridge only allows
the methods below:

| Method | Payload | `data` |
| --- | --- | --- |
| `cluster.registry.snapshot` | none | `ClusterRegistrySnapshot` |
| `cluster.host.register` | `{ hostId, hostName }` | `RegisteredHost` |
| `cluster.map.register` | `{ hostId, mapId, mapName }` | `RegisteredMap` |
| `cluster.create` | `{ clusterId, name, transferDirectory, backupDirectory? }` | `RegisteredCluster` |
| `cluster.update` | `{ clusterId, name, transferDirectory, backupDirectory? }` | `RegisteredCluster` |
| `cluster.membership.preview` | `{ clusterId, mapIds:Guid[] }` | `{ token:string\|null, preview:ClusterMembershipPreview }` |
| `cluster.membership.apply` | `{ token }` | `ClusterMembershipReceipt` |

`transferDirectory` is required and `backupDirectory` is optional. Both are
absolute local or UNC paths stored as metadata only. Paths must be distinct
within and across clusters. Creating or editing a cluster never creates,
copies, reads or writes either directory.

An unpaired host registration sets `IsPaired=false`. Its maps are metadata
only. The registry API does not expose remote Start, Stop, RCON, ASA process,
Store-v1, transfer-file or filesystem-write operations. A future LAN pairing
implementation must keep remote runtime observation and local mutation
capabilities separate.

Membership preview reports additions, removals and moves. A valid preview
receives a one-use native token bound to the requested map IDs, cluster ID and
`BaseRevision`; applying it requires the unchanged revision. The UI must show
the preview and require an explicit operator action before calling apply.
