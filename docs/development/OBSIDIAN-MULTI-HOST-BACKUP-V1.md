# Obsidian multi-host offline backup v1

## Scope

The v1 implementation adds a versioned, SHA-256 verified N-host archive to the existing Obsidian React/WebView2 application. It reads operator-prepared local copies and checks their host, map, and cluster ownership against the current registries. The archive does not assert that ASA saved the world or that a Store-v1 writer was stopped.

Restore materialization creates a verified TEMP-only sandbox. An optional replacement transaction is restricted to a previously verified `TEMP/Untamed-MultiHost-Restore-{guid}` sandbox: it builds and hashes a separate candidate first, retains the previous sandbox under another generated TEMP name, publishes with same-volume directory renames, and records durable phase state. The React recovery view lists at most 128 direct-TEMP transaction journal names without reading payloads; selecting an entry triggers full journal, checksum, ownership, and sandbox verification. This can rediscover a `previousMoved` transaction even when the original target directory is absent. Explicit operator rollback and recovery after interruption are available through the shared Core. This is not a live Manager restore; process quiescence, Store-writer ownership, live SaveComplete, and production restore remain unverified and blocked.

The historical two-host/five-map Store-v1 archive remains separate and unchanged. The v1 archive batch has explicit safety bounds: up to 256 hosts, 512 clusters, 4096 maps, and 200,000 components. These are versioned per-archive metadata and workload limits, not product topology caps. HostRegistry/ClusterRegistry have no such fixed-count limit; a synthetic object and JSON-clone regression validates 300 hosts, 520 clusters, and 4100 maps. Archive v2 paging or chunking can raise archive-batch capacity without changing the Manager topology.

## Offline source-copy layout

The selected folder contains `multi-host-backup-catalog.json` and every source file below. Each file is listed exactly once by its archive-relative path. A missing file or any unlisted file below a declared scope rejects the backup before archive staging.

```text
offline-copy/
  multi-host-backup-catalog.json
  hosts/{hostId}/configuration/{relativePath}
  hosts/{hostId}/clusters/{clusterId}/Store-v1/{relativePath}
  maps/{mapGuidN}/{relativePath}
  clusters/{clusterId}/Store-v1/{hostId}/{relativePath}
  clusters/{clusterId}/Transfer/{hostId}/{relativePath}
```

`{mapGuidN}` is the stable map GUID without separators. Host and cluster identifiers use the registry IDs. The `RelativeSource` catalog field is relative to `offline-copy/`; each `RelativePath` field is relative to its host, map, Store, or transfer scope shown above. The file at those two paths must resolve to the same canonical layout.

The catalog is UTF-8 JSON with schema version `1`, a nonempty `BatchId`, the complete selected `Hosts`, `Clusters`, and `Maps` arrays, and an `Items` array. Each item contains `HostId`, nullable `MapId`, nullable `ClusterId`, `Kind`, `RelativePath`, and `RelativeSource`. The scope must equal `OBSIDIAN-MULTI-HOST-COPY-CATALOG-V1`; `LiveConsistencyProven` and `StoreWriterOwnershipProven` must both be `false`.

Every map needs one nonempty world (`MapWorld`, `.ark`), one `GameUserSettingsIni`, one mod configuration, one ASA API plugin configuration, and one plugin persistent-data snapshot. `GameIni` is optional and may occur once. Player profiles, tribes, and other saved files are preserved as their corresponding map payload kinds. Each host needs a host configuration file. Each nonempty cluster needs cluster Store-v1 files and exactly one transfer manifest, plus a host Store-v1 snapshot for every host that owns a map in that cluster.

The catalog can be prepared from an already isolated copy. It must not point at a registered installation, Store, transfer, or backup directory. The app does not create an online snapshot or copy from a live ASA/Kova path.

## Archive and interruption behavior

The archive is staged beside its final output as `.incomplete`, copied with bounded memory, hashed while streaming, checkpointed every 64 files, then verified before an atomic directory rename. The manifest contains stable ownership and object indexes, lengths, and SHA-256 digests; absolute source paths are not serialized. A partial stage requires operator inspection and is never automatically resumed.

Restore verifies the complete archive and current registry ownership. A new sandbox is staged, every payload is SHA-256 checked, then it is published by atomic directory rename. Replacement preflight requires the same host, map-owner, and cluster assignment identities as the prior sandbox and the current registries. The new candidate is fully materialized before the prior target moves; a durable checksummed journal records `prepared`, `previousMoved`, `replacementPublished`, `committed`, or `rolledBack`. The verified previous sandbox is retained after commit. Rollback uses the journal transaction ID and explicit approval, quarantines the replacement instead of deleting it, and restores the verified previous directory. A crash leaves artifacts for explicit recovery; the Core never auto-resumes or deletes an uncertain stage. Recovery requires selecting a journal from the bounded TEMP list, then passing full verification before the UI offers rollback approval.

- `dotnet run --project wpf/Testing/MultiHostBackup/MultiHostBackup.csproj -c Release` — archive, hash, interrupted-stage, sandbox replacement, explicit rollback, and crash-recovery Core regressions.
- `dotnet build desktop/Tests/DesktopBridgeTests.csproj -c Release` and `dotnet run --project desktop/Tests/DesktopBridgeTests.csproj -c Release --no-build` — synthetic registry scale and native bridge integration, including rejection of production-root targets.
- `npm run build` and `npm run test:ui -- --workers=1` — React/TypeScript and single-worker browser regressions.
- `.github/workflows/build-obsidian-desktop.yml` runs `desktop/Tests/ObsidianSetupPathE2E.ps1` on Windows: invalid target rejection, quoted special/long install path, installed WebView2 smoke, and uninstall in the isolated runner temp directory.

All test data is synthetic. The replacement transaction is limited to generated TEMP sandboxes; tests do not start ASA/Kova, bind configured ASA ports, or issue live RCON. These results do not establish real ASA server, Store-v1 writer, character travel, SaveComplete, or live restore behavior.
