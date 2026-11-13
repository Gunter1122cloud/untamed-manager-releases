# Untamed Kingdom ASA Manager v1.4.0 — RELEASE CANDIDATE / TEST ONLY

**Windows x64 · .NET 10 WPF · Neon Kingdom**  
**Only a separate Windows test machine. Do NOT install on UK-HOME-01 (HP) or UK-HOME-02 (Ryzen).**

This is a consolidated release-candidate development checkpoint, **not** a production-ready replacement for the Legacy Manager.

## Implemented, exercised by Windows CI against synthetic fixtures

- Original WPF Dashboard, local map editor, 10-map cluster overview, tray icon, Windows Supervisor, Windows Updater and transactional installer continue from v1.3.7.
- **Release capability gate**: real ASA lifecycle (Start/Stop/Restart/Kill), live SQL restore, Store writes and live SteamCMD update are never enabled by untrusted profile settings, fake SaveWorld evidence or UI toggles. A separate local FakeAsa test binary can be launched only after verified stopped-state and map ownership checks. The RCON preview control is restricted to local **ListPlayers**.
- **Watchdog / Recovery Center**: independently categorized failures, persistent synthetic restart event journal, no automatic duplicate launch, timeouts/cooldowns, fail-closed synthetic SaveWorld evidence, WAN-IP/DDNS read-only monitoring. No live save proof.
- **Offline MariaDB archives**: already-exported `arkshop.sql`, `arkpermissions.sql` and `lethalquests.sql` may be imported into an isolated package, SHA-256 validated and restored **only to new temporary files**. Neither a database connection nor mysqldump is attempted. This is not proof of a consistent SQL snapshot or import.
- **Legacy synthetic-fixture audit**: copy and verify only self-created isolated TEMP fixtures. No Legacy file-format parser, conversion, production Store read/write, character or tribe changes; 1:1 compatibility cannot yet be claimed.
- **SteamCMD Update Center**: offline grouped shared-installation preview, Steam build manifest if present, fixed disk and available-space gates, confirmed-stopped state requirements and a durable dry-run receipt. **SteamCMD is never executed.**
- **Plugin Center**: guarded DLL/PDB ZIP update, pre-update fully verified directory backup, manifest, SHA-based rollback preview and DLL-only rollback while preserving current config/license/database files. Restores are confined to registered local map plugin folders, never Legacy-related plugins. Rollback creates its own complete preimage backup.
- **Manager setup and preferences**: Setup Wizard with manual local map ownership, safe host-bound non-secret profile export/import; secrets, Store data and launch arguments are excluded. Imported preferences must match every registered local map identity.
- **Windows tests**: build FakeAsa, two Manager profiles, Supervisor/Updater, GUI startup and Recovery/Setup/Data/Steam navigation, backup/Store/SQL fixture integrity, plugin ZIP integrity/rollback, Steam dry-run, portable ZIP, Setup EXE and installer health-failure rollback.
- The existing safe Config/INI/mod assignment, CurseForge read-only metadata and local plugin inventories remain available.

## Strict restrictions — NOT implemented or NOT certified

- **Actual ASA save-completion proof**: an RCON OK, log line or file timestamp cannot be treated as conclusive SaveWorld evidence. Thus normal real-world Stop/Restart and emergency Kill→Start stay disabled.
- **LegacyCluster Store compatibility**: requires supplied original Legacy files, metadata and the existing two-host Store/character handling contract. Nothing has been converted, replaced or mutated.
- **Live MariaDB dump/restore**, multi-map consistent hot backups, automated timed full server backups, restoration of live characters/tribes: not validated and remain disabled.
- **Live SteamCMD downloads/updates**, official available-version comparison, mod downloading/automatic installation and unsupported CurseForge entitlement claims: remain disabled until vendor/API and real lifecycle validation.
- **WAN-IP-triggered server restarts and remote host commands**: deliberately disabled. The LAN peer is status-only.
- No actual Windows test-PC acceptance by the owner and no real ASA integration. A green GitHub Windows CI does **not** establish production safety.

## Acceptance before using the artifact

A successful Windows CI run on the **exact** v1.4.0 main commit is mandatory, including **all** existing integration and installer-rollback tests. Validate the ZIP and Setup EXE SHA-256 hashes from the run's artifacts. The GitHub workflow's production release step stays disabled; build artifacts are **TEST/PREVIEW only**.

## Open work after this checkpoint

1. Validate the LegacyCluster Store on read-only copies from both hosts, establish a written 1:1 compatibility contract and migration-free recovery test with actual source/target examples.
2. Prove genuine ASA SaveWorld completion, DoExit, API Loader process reattachment and start/stop behavior on isolated nonproduction Windows/ASA test installation.
3. Validate MariaDB transactional export/restore with throwaway databases and failure injection, including ArkShop/Permissions/quests tables.
4. Vendor-approved CurseForge mod download/update rights and live SteamCMD update workflow with proven save/stop and full restorable protection.
5. Complete owner-led separate Windows test-PC acceptance, then consider a separately approved production release.

*This document distinguishes demonstrable local or synthetic behavior from actual production capabilities. No real hosts were accessed during this development cycle.*
