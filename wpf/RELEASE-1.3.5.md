# Untamed Kingdom ASA Manager WPF v1.3.5 – TEST / PREVIEW

> Development build for a **separate Windows test PC only**.
> Do not install on UK-HOME-01 (HP) or UK-HOME-02 (Ryzen).

## Improvements from v1.3.4

- Read-only CurseForge Core API browsing inside Mod Center with an approved
  API key protected using Windows-user DPAPI in the local Manager profile.
  Resolves the ASA game ID via authenticated Games API and displays provider
  metadata only. No mod downloads or installation.
- Synthetic HTTP tests for the CurseForge API and denied credentials.
  **Real API-key/live-catalog acceptance has not been performed.**
- Read-only local plugin inventory: DLL names, optional file version and SHA-256.
  Legacy plugin folders are not inspected.
- Read-only Steam/ASA installation preflight: groups maps sharing an install,
  checks the ASA executable and local steamapps/appmanifest_2430930.acf,
  and always keeps automatic updates blocked.
- Offline backup safety: free-disk-space check before copying; per-map
  history with SHA-256 integrity checks and incomplete/corrupt detection.
- v1.3.4 mod load-order and plugin ZIP safety checks are retained.
- Integration fixtures for CurseForge API, shared ASA installations,
  local plugin inventory and corrupted backup history.

## Strict safety limits

- No verified live ASA SaveWorld completion adapter: Stop/Restart,
  automated updates, Kill-to-Start and IP-change recovery remain disabled.
- No LegacyCluster Store read/write, Store conversion, SQL backup/restore
  or real ASA restoration. SHA-256 file integrity is not ASA consistency.
- Only local, explicitly registered map controls. The other host remains
  authenticated and read-only.
- Do not claim crossplay or premium classification without authoritative data.
- Plugin presence does not prove runtime loading or ASA API compatibility.
- SteamCMD and CurseForge integrations never automatically download content.
- FakeAsa and GUI CI smoke tests are not real cluster tests.
- Stable GitHub release generation remains deliberately disabled.

## Windows test artifacts

The GitHub Actions Windows workflow produces UntamedManager.exe,
UntamedUpdater.exe, UntamedSupervisor.exe, a portable ZIP,
SHA-256 checksums and an Inno Setup installer. Verify the exact
v1.3.5 CI run passes before using its artifacts on a separate test PC.
