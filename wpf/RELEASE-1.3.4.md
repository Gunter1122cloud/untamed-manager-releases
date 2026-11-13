# Untamed Manager WPF v1.3.4 Development

This is a development-only build. Do not install it on UK-HOME-01/02.

## Implemented in the first v1.3.4 development increment

- Scoped, local, confirmed-stopped-map file backups for SavedArks/SaveGames, INI and plugin trees, using a staging directory, SHA-256 manifest, source recheck and incomplete marker.
- Map-owned backup UI, file integrity re-verification, clear disclaimer about ASA/SQL/Legacy consistency.
- Strict plugin ZIP inspection with traversal, symlink, archive-size and duplicate-path rejection.
- DLL/PDB-only local plugin replacement after confirmed stopped-state check; previous plugin data backed up outside the active Plugins tree, existing JSON/license/database files left intact.
- Windows integration tests for backup integrity, tampering, scope isolation and plugin data preservation.
- Retains v1.3.3 Supervisor, LAN encryption, scheduler, updater and its E2E tests.

## Additional development increment (pending Windows CI verification)

- Strict local `-mods=` inspection and order-only changes in the WPF Mod Center. An existing list must be unambiguous and the reordered list must contain exactly the original IDs; other arguments remain untouched.
- Added simulated integration tests for mod order, missing IDs, duplicate IDs, ambiguous arguments and preservation when adding new IDs.
- Read-only offline SteamCMD executable preflight: verifies local fixed-disk path, rejects links, reports file size, modification time and SHA-256. It does **not** execute SteamCMD, check online ASA build versions, install or update servers.
- Plugin ZIP preflight additionally rejects Windows device-name and trailing-dot path aliases, with isolated tests.

## Safety limits

- **No live ASA save consistency proof**: file-hash verified does not mean a consistent online game snapshot.
- Legacy Store/cluster transfer is not read or written by this backup flow. SQL backup is not implemented.
- Actual ASA restore remains disabled.
- Server Stop/Restart remain disabled until verified ASA SaveWorld completion evidence is available.
- Plugin ZIP updater supports conservative binaries-only archives in PluginName/* layout. Nonbinary files from a ZIP are deliberately skipped rather than overwriting existing configuration/data.
- The original folder plugin importer is not exposed by the active GUI; the ZIP updater is the enabled safe path.
- Actual ASA/SteamCMD/CurseForge remote integration not yet tested or activated.
- Do not publish as a stable release or assume FakeAsa tests prove live ASA compatibility.
