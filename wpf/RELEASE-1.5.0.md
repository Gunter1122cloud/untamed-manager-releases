# Untamed Kingdom ASA Manager v1.5.0 — OPERATIONAL FEATURES / TEST RELEASE

**Windows x64 · C#/.NET 10 WPF · Neon Kingdom**  
**TEST PC / GitHub Windows Actions only. Never deploy to the live HP or Ryzen ASA hosts.**

## Twelve feature areas — implemented scope

| Area | Available in v1.5.0 | Remaining real-world limitation |
|---|---|---|
| Active / Passive / Disabled mod list | Persistent map-specific states, `-mods` and `-passivemods`, ordering, local batch updates and pending flag | Real ASA launch/compatibility remains gated |
| CurseForge mod updates | Read-only approved-key file metadata, ID/dependency/version preview and local mod-folder observation | Local game mod file ID is not inferred; auto-download and updates are not authorized |
| ASA API | Read-only PE64 DLL/loader integrity, SHA256 and local runtime association | No certified live ASA API installation/update |
| Plugin state | Local stopped-map full-directory enable/disable, backups, persistent plugin data and restore | Runtime module loaded status cannot be inferred from disk; Legacy-related plugin modifications rejected |
| Plugin JSON | Hash-protected raw and structured edits, validation, preview, BOM/encoding preservation, backup and atomic replacement | Needs a verified stopped locally owned process; no live server tests |
| Game INI | Game.ini/GameUserSettings.ini parser/editor, comments/duplicate keys, BOM/CRLF, ETag and preimage | No automatic hot reload or forced restart |
| SteamCMD | Real spawned FakeSteamCMD app ID 2430930, timeout/error/INI tests and shared-installation dry-run | SteamCMD itself never executes; no real live update |
| Scheduler | Budapest daily/weekly/interval calendar, DST, templates, independent Supervisor read-only recurrence with durable claim, no catch-up, maintenance, ownership and receipts | Live restart/wipe/backup auto-execution deliberately disabled |
| Retention | Per-task SHA-verified managed backup quarantine, keep-latest/age/size, protected backup handling | No automatic deletion of live Legacy/ASA/SQL data |
| RCON | Validated ListPlayers/Broadcast/ServerChat/SaveWorld/Wild Dino Wipe drafts, persistent FakeRcon once-only receipts | Real mutating RCON remains disabled; SaveWorld reply is not save-completion proof |
| Telemetry | CPU/RAM/uptime from identified local processes, Supervisor observations, seven-day map history and WPF chart | Player count and RCON latency remain unknown unless independently verified |
| Legacy profile import | Read-only copied v1 JSON inspection, mod/port/root collision checks, inert local draft with AutoStart off | This is a controlled export shape, not proof of the live Legacy 1.8.3 Store schema or one-to-one character compatibility |

## Supervisor recurrent checks (added in the latest v1.5.0 development)

- **Actual background behavior**: the standalone Supervisor safely runs four local read-only task types: Observation, ModCheck, ApiCheck and SteamCheck; the WPF window need not be open.
- An opted-in task requires a stored local map identity and is recorded *before* execution in a bounded, atomic local journal.
- After interruption, a previously claimed slot is never retried automatically. Missed tasks are logged, not silently caught up.
- Maintenance, foreign maps, unsafe ServerStart/ServerStop/Restart/Wipe and all other live actions remain blocked. The WPF UI exposes opt-in, disable and journal viewing for the read-only tasks only.
- Read-only check failures do not trigger restart, RCON writes, SQL mutations or Legacy file operations.

## Key Legacy/ASA safety restrictions

- No production SaveWorld completion validator. A successful RCON reply or modified file timestamp is not sufficient proof.
- No direct live ASA Stop/Restart/Kill or unattended Live SteamCMD/CurseForge installation.
- No writes, conversion or deletion of existing `LegacyCluster` Store/session/peer metadata; no real character/tribe transfer validation.
- No live MariaDB restore. Already exported `.sql` files can be SHA-archived and rehearsed only in isolated local temporary folders.
- Do not install this build on `UK-HOME-01` or `UK-HOME-02`; do not run it alongside Legacy on production hosts.

## CI acceptance and delivery

The final exact v1.5.0 commit must complete all Windows Actions jobs: WPF x64, FakeAsa, isolated Supervisor, integration tests including the recurrent runner, GUI smoke, FakeSteamCMD, updater/installer failure rollback, Setup EXE and Portable ZIP.

Only link artifacts from the successful run that matches the current commit. All artifacts remain **TEST/PREVIEW**. GitHub CI does not replace a separate Windows test-PC trial or real ASA/Legacy compatibility investigation.

## Follow-up work still needing real evidence

- Read-only audit adapter over *actual copies* of `cluster-v1.json`, `cluster-store-v1.json`, lock records, peer JSON and real Legacy profile exports, including session equality and exact root bindings.
- Real isolated ASA save, stop, API loader, recovery and character-transfer tests, followed by owner-approved activation gates.
- Throwaway MariaDB test instance matching the installed compatible version, with consistent dump/import tests.
- CurseForge entitlement and distribution contract verification, genuine SteamCMD staging/backup/update and multi-map readyness.
- Release signing, ownership authorization and separate test-PC acceptance before any production Legacy replacement.

The presence of a WPF control or green FakeAsa CI test never authorizes an unsafe action.
