# Untamed Manager 1.3.1 — Process Safety Preview

For an isolated third Windows test PC only. This is a limited P0 safety increment, not a complete Kova replacement.

Implemented:
- Fix CS0103 in MainWindow.Bulk.cs by importing the WPF MessageBox alias.
- Require positively verified stopped state before local launch or bulk plugin installation.
- Fail closed on WMI failure, unreadable candidate identity, changed arguments, reused PID or multiple matching processes.
- Reject executable prefix collisions and serialize cooperating local launch requests with a named mutex.
- Preserve the existing Neon Kingdom UI, explicit local map ownership and process reattachment behavior.
- Package the Manager and independent updater together in a portable ZIP with SHA-256 checksum.

Validation in the Windows build: WPF publish, updater publish, FakeAsa integration and process safety regressions, offline startup smoke test, actual WPF screenshot and installer build. Check the linked Actions run for actual results.

Limits: FakeAsa is not live ASA. The simulated binary replacement test is not validation of the real updater or rollback. The injected failure tests validate the launch gate; real WMI tests cover argument mismatch and duplicate processes. API-loader child identity is not implemented. External tools can still race with a process scan; the named mutex serializes only cooperating Manager launches in the same Windows session.

Still incomplete: SaveWorld/DoExit Stop/Restart; independently running supervisor/watchdog/scheduler; encrypted LAN status (the existing channel authenticates but does not encrypt); Kova Store schema adapter and transfer validation; transactional backups/restore; plugin update data preservation; complete directory updater rollback and publisher verification; WAN recovery, Discord and diagnostics.

No production server, SMB share or Kova Store data is accessed by these tests. No Store conversion is performed. Do not install on UK-HOME-01/02.
