# Obsidian Command — historical desktop source integration

The user-provided installed desktop-source package (version 1.8.3) is an authorized reference for integration into Obsidian Command. Its original external service, installer, and browser backend are not dependencies of the product.

## Retained architecture
- `desktop-ui/`: React/TypeScript interface. No parallel Electron or historic WPF UI.
- `desktop/`: local .NET 10 host and typed native operation bridges.
- `wpf/Core/`: shared native ASA/Store coordination and compatibility layer; no new WPF deliverable.
- Two independently installed Managers; each can operate local ASA maps without a remote peer being online. Multi-host state and transfers are separate concerns.

## Source modules and required behavior
| Desktop source module | Obsidian native target | Required behavior |
| --- | --- | --- |
| `agent/owned-process.mjs`, `agent/lifecycle.mjs` | `WindowsProcessFamily`, local Start/Stop/Restart bridges | Reattach via PID + EXE + start time + command identity; prevent accidental orphan and wrong-process termination |
| `agent/shared-store.mjs`, `agent/cluster.mjs` | `LegacyStoreV1*`, `ClusterRegistry`, `OriginalStoreRuntimeIdentity` | Maintain original on-disk IDs, durable locks, two-host transfer identity, no automatic conversion replay |
| `agent/backups.mjs` | `UntamedWorldBackup`, multi-host archives and restore staging | Snapshot consistency, SHA-256, zero-byte character preservation, safe rollback and bounded retention |
| `agent/agent.mjs`, `agent/rcon-query.mjs` | native local operation and RCON bridges | Controlled RCON, explicit connection/identity validation, no cross-map command targeting |
| `agent/steam-install.mjs` | native SteamCMD install/update bridges | Manual updates only; no unapproved automatic update or server stop |
| `agent/asa-api.mjs` | native Loader selection and local plugin-folder workflow | Per-map optional ASA API Loader; plugins copied/configured manually by the operator |
| `agent/server-health.mjs`, `agent/telemetry.mjs` | status, host observation and diagnostics | Report observed facts, not inferred loaded plugin state |

Not imported into the ASA Manager: the source's unrelated Minecraft and remote account/licensing services; the Discord bot remains a separate project. Copying their modules would not make the requested ASA desktop functions work.

## Non-negotiable compatibility
The original two-host cluster ID, local profile directory, coordination directory, serialized map identity and transfer path are *data*, not branding. Existing values must remain byte-identical while product-facing labels, class names, documentation and file paths use Obsidian naming. No writing to the original profile during import. No automatic lock or ACTIVE/DONE reset. Previously pinned configuration belongs to Obsidian and must not rely permanently on the old application.

## Verification
This source-cleanup change uses an isolated GitHub working branch and preserves live ASA hosts. Windows CI must cover TypeScript, browser tests, native bridge tests, and self-contained setup. Green CI cannot certify real two-host character / tribute / tribe travel; that requires explicit operational testing before production approval.
