# Untamed Kingdom ASA Manager WPF v1.3.7 — RECOVERY TEST / PREVIEW

**Separate Windows test PC / GitHub Actions only. Do not deploy to UK-HOME-01 or UK-HOME-02.**
The project remains the original .NET 10 WPF Neon Kingdom Manager with its existing
Supervisor, Mod Center, Plugin Center, updater and KovaCluster safeguards.

## Implemented

- **Watchdog 2.0 diagnostics**: separate bounded, three-observation counters for
  uncertain/verified stopped processes, loader uncertainty, RCON timeout and
  authorization failure, stale Supervisor observations and network uncertainty.
  RCON errors do not prove that an ASA process is frozen. Maintenance suppresses
  all intervention recommendations. The existing Supervisor publishes diagnoses
  and only logs transitions; it cannot initiate server lifecycle actions.
- **Synthetic SaveWorld correlation**: requires matching FakeAsa start/completion
  event tokens, precise process PID + start time + EXE + arguments and coherent
  timestamps. RCON acknowledgment, file modification or arbitrary game logs cannot
  establish a real ASA SaveWorld proof.
- **Crash-resistant offline recovery simulation**: durable per-map journal,
  confirmed synthetic stages and safety failure states, 5-minute retry cooldown,
  three-attempts-per-hour limit, no replay/resumption of interrupted operations.
  No real Process.Start/Kill, DoExit, RCON, SteamCMD, SQL or Kova Store writes.
- **WAN IP and DDNS read-only monitoring**: optional user-clicked HTTP(S) checks
  with two independent IP sources, DNS lookup of untamedkingdom.ddns.net,
  confirmation via two samples separated by at least 30 seconds, mismatch alerts,
  cooldown. Never changes No-IP/router/port forwards or restarts maps.
- **Neon Kingdom Recovery Center**: locally registered map-only status view,
  maintenance toggle with confirmation, Watchdog events, synthetic restart,
  recovery journal and offline WAN-IP change demo. A WAN restart-candidate preview
  shows only locally registered maps with fresh, authenticated Supervisor observations.
- **Windows integration tests**: FakeAsa proof correlation, replay and PID reuse,
  no-proof/unknown-exit blocking, crashed operation/no auto-retry, ownership,
  maintenance, RCON vs process state, DDNS mismatch and two-sample WAN change.
  Recovery tab opens in the isolated WPF GUI smoke path.

## Still unavailable / intentionally disabled

- **Real ASA SaveWorld proof** is not implemented. No claim of production-safe
  SaveWorld → DoExit → Restart or live health/hang determination.
- No live kill/restart, IP change triggered restart, SteamCMD automated update,
  remote map control, real ASA Restore, SQL write/restore or KovaCluster Store
  read/write/conversion. The KovaCluster Store file format is unchanged.
- The offline fake successful restart cannot verify the production ASA server.
- Cloud IP endpoints may be unreachable; no network checking occurs automatically.
- Running this version on a real ASA host is not authorized; preview tests only.
- GitHub release publishing remains disabled, so only GitHub Actions artifacts
  are created on a successful Windows CI run.

## Acceptance

Do not label this build tested until the exact v1.3.7 commit completes the
Windows CI workflow with all steps green: WPF x64, independent Supervisor,
two simulated Managers/FakeAsa, Recovery UI smoke, updater and installer
rollback. An external Windows test-PC trial still requires the user to run it.

## Next version candidate

v1.3.8: evidence-backed, real ASA SaveWorld validation design with source
provenance, anti-replay and timeout handling; stronger watchdog freeze
classification, synthetic multi-map sequential scheduling and safe operator
review. Enable no production lifecycle actions before real server validation.
