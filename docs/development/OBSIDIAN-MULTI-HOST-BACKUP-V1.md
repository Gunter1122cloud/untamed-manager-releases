# Obsidian multi-host offline backup v1

## Scope

The v1 implementation adds a versioned, SHA-256 verified N-host archive to the existing Obsidian React/WebView2 application. It reads operator-prepared local copies and checks their host, map, and cluster ownership against the current registries. The archive does not assert that ASA saved the world or that a Store-v1 writer was stopped.

Restore materialization is currently a verification workflow for a newly created `TEMP/Untamed-MultiHost-Restore-{guid}` sandbox. It is not a live Manager restore or rollback. The preflight records that current registry identity matches; it does not claim to have checked map process or Store lock state.

The historical two-host/five-map Store-v1 archive remains separate and unchanged. The new archive supports 1–256 hosts, 1–512 clusters, 1–4096 maps, uneven map ownership, and up to 200,000 files. Archive source and output paths must be on a fixed local disk. UNC paths, reparse points, path traversal, incomplete source directories, and overlapping source/output roots are rejected.

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

Restore re-verifies the archive, checks the current registry revision at preview and apply, writes only a new TEMP child through a separate `.incomplete` stage, verifies every extracted payload, and atomically publishes the sandbox. Existing destinations are rejected. An interrupted TEMP stage requires operator review and is never automatically replayed.

## Validation performed

- `dotnet run --project wpf/Testing/MultiHostBackup/MultiHostBackup.csproj -c Release` — isolated Core regressions.
- `dotnet build desktop/Tests/DesktopBridgeTests.csproj -c Release` and `dotnet run --project desktop/Tests/DesktopBridgeTests.csproj -c Release --no-build` — synthetic local-file/native bridge regressions.
- `npm run build` and `npm run test:ui -- --workers=1` — React build and browser-only UI tests.

These tests use synthetic local fixtures. They do not establish real ASA server, Store-v1 writer, character travel, or live restore behavior.
