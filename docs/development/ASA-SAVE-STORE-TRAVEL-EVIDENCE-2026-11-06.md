# Untamed Manager – ASA Save / Store-v1 / Restart proof contract (2026-11-06)

Status: **SOURCE IMPLEMENTED / ISOLATED CI TEST ONLY**. This is not production
certification. Neither UK-HOME-01 nor UK-HOME-02 or their shared Store was
opened or altered. Existing Legacy Store-v1 session, hashes and original
configuration remain unchanged.

## Source-specific constraints

- Wildcard ASA 57.12: `-usestore` embeds player profiles and tribe data into
  the `.ark` world save; `-converttostore` is a one-off conversion flag,
  not a normal launch option.
  Source: https://ark.wiki.gg/wiki/57.12
- ASA server files: `ShooterGame/Saved/SavedArks/<map>/<map>.ark`, current
  log `ShooterGame/Saved/Logs/ShooterGame.log`.
  Sources: https://ark.wiki.gg/wiki/Server_Setup and
  https://github.com/WindowsGSH/WindowsGSH.ArkSurvivalAscended
- Windows PID alone is not process identity. The existing native core
  checks PID, start time, executable and arguments before process actions;
  one Manager only controls its locally owned maps. A durable Restart
  journal and installation lease prohibit duplicate automatic attempts.

## Implemented changes

1. **`LegacyCompatibleWorldBackup`:** a *stopped* historic `-usestore`
   map may have zero-byte loose `.arkprofile`/`.arktribe` leftovers. Keep
   every byte in the verified backup; never delete or treat the loose files as
   the authoritative embedded survivor population. A zero-byte `.ark`
   **remains invalid**, and Store-v1 one-map restore remains blocked.
2. **`DesktopStoreTravelEvidenceBridge`, snapshot schema 2:** inspect
   `-usestore` world existence, byte length and stability without reading
   the multi-GB world or inferring survivors from loose files. For a
   Store-v1 map, **never output `passed=true` for actual character travel**.
   Embedded identity, tribe ownership, A→B→A client join, items, implant and
   post-restart character persistence are not parseable from these snapshots.
   The UI reports these gaps instead of claiming that a missing loose file is
   a lost survivor. A zero-byte *loose* historical profile is reported but
   not auto-deleted or treated as a missing embedded character.
3. **`AsaWorldSaveEvidence`:** mark the current log end BEFORE dispatch,
   observe only NEW ordered `Saving world` and `World Save Complete` entries,
   and separately check that the correct `.ark` changes and stabilizes.
   Read bounded log increments, not a full world hash. Detect log
   truncation/rotation, file links and changed process identity. This is
   evidence of a server save event and file metadata, **not an ASA-internal
   fsync or client-world reload certificate**.
4. **`DesktopRconReadOnlyBridge`:** approved SaveWorld returns an optional
   5-minute witness ID; `rcon.saveStatus` observes server log and world
   without sending another command. A missing/changed process, reused token,
   old log lines or absent proof never result in a positive claim.
   Active or historical Store-v1 maps cannot receive an independently
   authorized WebView SaveWorld until shared writer ownership is certified.
5. **`AsaGracefulStop` / Stop/Restart bridges:** the real non-Store lifecycle
   waits for log+world evidence after SaveWorld and **BEFORE DoExit**. Timeout
   or missing evidence stops the sequence; the process is not killed or
   automatically restarted, and the durable interrupted journal requires
   review. The final DoExit save, actual client login and replacement server
   readiness are separate, **not asserted**. Synthetic injected test
   transports cannot be promoted to genuine log evidence.
6. **`DesktopWorldBackupBridge` UI contract:** exact SHA-256 file restore
   and retained protective backup are verified; gameplay reload, tribe
   ownership and survivor rejoin remain explicitly false. The visible
   `characters` value is a count of loose profile files, not actual
   embedded Store-v1 survivors.

## Isolation tests added

- Stale SaveComplete, out-of-order log marker, changed/stable world file,
  changed process, truncated log.
- Owned RCON SaveWorld boundary token and read-only status, wrong token;
  a Store-v1 map's manual RCON write blocked.
- Both hosts' 5+5 source metadata; snapshot schema 2; ten non-empty
  embedded worlds without any separate survivor profile; zero-byte legacy
  leftover; no invented transfer success or false missing-profile error.
- Verified stopped Store backup with empty old loose profile, and rejection
  of an empty world save.
- Original process journal and no automatic retry tests retained.

## Required REAL Windows test acceptance (NOT DONE)

- On an isolated new ASA cluster with two test maps: create one disposable
  character, join A, travel A→B, log back into B, reboot, log back into B,
  travel B→A; observe tribe, items and unique survivor through the actual
  game client. Preserve backup and compare source bytes and cross-host state.
- For a test map only: issue SaveWorld, observe fresh server log event and
  world change, complete DoExit, restore the verified world in a *separate*
  installation, then rejoin as the same character and verify a test building,
  tribe ownership and creature persistence.
- Interrupt the test Manager during saving, stopping and starting; simulate
  LAN/SMB interruption and PID reuse. Only the original process may be
  reattached; no duplicate Start, no Store session replacement/lock removal.
- Production two-peer writer ownership and multiworld/SQL-consistent restore
  are **NOT unlocked** by synthetic test success.

Do not publish operator archives, passwords, character data or live SMB
paths to the public repository. Do not run destructive experiments on the two
production ASA hosts.
