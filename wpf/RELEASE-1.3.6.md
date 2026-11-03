# Untamed Kingdom ASA Manager v1.3.6 — TEST / PREVIEW

**Separate Windows test PC only.** Do not install on UK-HOME-01 (HP) or UK-HOME-02 (Ryzen). This is not an operational ASA/KovaCluster replacement.

## Implemented in this development cycle

- Strengthened verified ASA child-process reattachment after the original API loader exits. Windows can report a different orphan ParentProcessId. Reattachment requires the **persisted exact server and loader identities**, PID creation times, executable paths and complete arguments, previously captured as a verified pair. No uncertain state is treated as stopped and no automatic restart is enabled. Added synthetic identity/PID-reuse regression tests.
- Read-only multi-map backup overview for explicitly registered local maps, with per-map integrity-verified package count, corrupt/interrupted package count, verified total bytes and last verified date.
- Existing per-map backup history now exposes actual total bytes from a successfully verified manifest.
- Safe plugin ZIP preflight for selected local maps shows whether the map is confirmed stopped, the target plugin already exists, the DLL count and the non-binary files skipped. The actual updater revalidates its invariants independently.
- ZIP packages targeting Kova-related plugin names are rejected at validation time. Synthetic tests cover rejection and ensure the Store stays untouched.

## Inherited from v1.3.5

- Approved-key CurseForge API mod search, pagination and metadata inspection. No downloads or installed-mod version matching is claimed.
- Configured local mod assignments, common/single-map IDs and order editing, with fail-closed invalid argument detection.
- Read-only plugin version/hash inventory and Steam/ASA installation preflight, including shared installation directories.
- SHA-256 offline backup package verification, storage checks and incomplete package detection.
- Authenticated read-only peer observation, Supervisor/Updater, system-tray WPF UI.

## Required acceptance evidence

All GitHub Actions Windows workflow steps must pass on the exact v1.3.6 commit, including FakeAsa, two simulated Managers, Windows GUI startup, Updater/Supervisor and installer recovery. A successful run is **not** a real ASA server or Kova transfer compatibility proof. Download a specific run's artifacts and verify SHA-256 before testing.

## Still disabled / not claimed

- Live ASA SaveWorld completion detection, automatic server Stop/Restart, WAN-IP-triggered recovery, Kill-to-Start.
- Executing SteamCMD or downloading/installing mods or plugins without explicit guarded UI actions.
- Live SQL backup or restore, KovaCluster Store write/conversion, live world/character restore or Store 1:1 compatibility proof.
- Production release: the GitHub Release publication step remains disabled.

## Planned follow-up

Harden plugin update rollback and history, expand controlled SteamCMD update simulation and comparisons, improve metadata compatibility classification with verified vendor fields, and design a real SaveWorld completion adapter with fail-closed tests before considering any live lifecycle operation.
