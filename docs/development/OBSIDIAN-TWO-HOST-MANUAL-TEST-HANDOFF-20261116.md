# Obsidian Command – HP/Ryzen installation acceptance (manual-test mode)

This is an operator-run test procedure, not production certification. Only the
owner may choose a live ASA operation. Never run tests against a production
survivor or change a shared Store/transfer record for this procedure.

## Download and install on both Windows hosts

1. Open the successful Windows **PR #164** Actions run and download the
   **Obsidian-Desktop-TEST-ONLY** artifact. Its internal files include
   `Untamed_Obsidian_Desktop_Preview_Setup.exe`,
   `Untamed_Obsidian_Desktop_Preview_Portable.zip`,
   `installed-release.txt` and `SHA256SUMS.txt`.
2. Confirm the SHA-256 checksum and `installed-release.txt` match the
   intended successful CI commit. Use the **same commit on both hosts**.
3. Keep the existing Manager installation, its original profiles and all ASA
   servers intact. The Preview Setup is separate from the old UI. Do not
   uninstall the old manager or delete original Store/cluster files.
4. On UK-HOME-01 and UK-HOME-02 install the Preview, or extract the Portable
   ZIP to a separate folder. Run `UntamedDesktop.exe --manual-test`
   by adding ` --manual-test` to the Windows shortcut **Target**, after
   the quoted executable path. This flag is supported by builds with the
   DesktopStartupMode feature, not earlier preview builds.
5. The native window/tray must say **MANUÁLIS TESZT**. If it does not, close
   the Manager using the **Kilépés a Managerből** tray action, check the
   shortcut/build, and relaunch with the flag. Closing the main window alone
   minimizes the application to the tray.

## What manual-test does and does not do

- Keeps real local ASA/Loader observation, map cards, settings and every
  manual native action wired to its normal bridge. It does **not** create a
  FakeAsa profile, mock RCON or unlock a failed lifecycle check.
- Does **not** start persisted scheduled ASA SaveWorld/Restart jobs or
  background scheduled stopped-world backups on Manager launch.
- Does **not** disable manually approved Start, Stop, Restart, RCON,
  backup, install/update, mod or INI actions. Operator approvals and all
  persistent Store-v1 guards remain in effect.
- The retired scheduled SteamCMD worker remains manual-only in every mode.
- To use deliberate scheduled features later, exit the Manager completely and
  launch normally **without** `--manual-test`, after reviewing its saved
  schedules. Never use this switch to bypass manual-operation safeguards.

## Non-destructive checks first (both hosts)

- Confirm **UK-HOME-01** and **UK-HOME-02** each show their own five local
  maps, and remote maps remain **monitor-only** until the remote Manager is
  separately authorized.
- Use running ASA discovery and original active Store-v1 **read-only
  preview**. Verify process PID, original map GUID, ASA API Loader child PID
  (when applicable), local ownership, and original cluster identity.
- Inspect Store-v1 source pin, original session/transfer identities and all
  pending operation journals. Do not clear or replace any ACTIVE/DONE or
  character files.
- Confirm the real Game/Query/RCON port and existing INI without changing
  them. Check read-only RCON ListPlayers on a correctly owned map.
- Check local plugin directory navigation: DLL file existence must not be
  represented as runtime-loaded verification.
- Check Mod Center, INI read, dashboard, logs, backup inventories and
  public Steam build check with no applied update.

## Explicit, scheduled-downtime operations after read-only acceptance

1. Make verified, external backups including **world, survivor, tribe,
   tribute, cluster and Store records**. Preserve even 0-byte files and
   hashes; do not test restore against live cluster files.
2. Choose **one noncritical map** and plan downtime. Do not let two
   different Managers or the retired manager issue a mutating command to
   the same process.
3. Only after verifying the correct ASA child PID/RCON listener and backups,
   issue one **manual SaveWorld**, inspect native job status, then one
   **manual Stop**. Verify the OS process has stopped before any Start.
4. When the map is stopped and launch source/mode is approved, test one
   manual Start, with ASA API Loader enabled only on a map where it is
   actually configured. Verify *new* child PID, RCON readiness, game join,
   original mods and original settings. Repeat with Restart only after
   completing and reviewing the original stop job.
5. Test manual SteamCMD update on a stopped, eligible installation only.
   Never enable automatic SteamCMD updates. Test restore in TEMP/offline
   staging, not on a running writer.
6. Perform the two-host gameplay round-trip with a **test survivor**, checking
   survivor, inventory and tribe. Obsidian does not replace ASA character
   travel or write transfer payloads.

Record exact map, host, feature, error code and local time for every failure;
supply no RCON password or original character file contents in public reports.

## Deployment boundary

A successful test build proves that Windows CI compiled and exercised the
synthetic contract. It does **not** prove the two original five-map hosts will
accept the exact live Store configuration or that character travel succeeds.
Keep PR #164 open and preserve rollback until real owner-operated tests pass.
