# Lifecycle journal and false-blocker audit — 2026-11-25

## Confirmed source findings (baseline 1572fafbba1ea841764dbfc6a14a93520dae5af5)
- Restart-v1 ChangedUtc is overwritten on every phase change and at failure closure (AsaManualRestartJournal.Change). It is not a launch creation time.
- Historical and fresh replacement review previously compared live process start to ChangedUtc and rejected recorded NewPid even when it is the correct Loader or ASA. A failed readiness deadline can occur minutes after a healthy new process starts.
- DesktopHistoricalRestartBridge records the launched PID before readiness, and fresh restart does the same. With API mode this PID can be the Loader. Neither an owned PID nor a listening RCON port proves a successful historical restart.
- DesktopWatchdogRecovery, DesktopRollingSteamSupervisor, DesktopOwnedAsaKillBridge, and restart/stop paths consume durable journal gates. A false stale restart blocks independent operations. #189 separates update opt-in from actual dispatch.
- DesktopAsaLifecycleJournalBridge requires typed approval for historical recovery. This change adds a native automatic route for terminal restart-only records with an independently verified different current owner, retaining original failed bytes and the compatible audited terminal phase.
- Supervisor uses the existing host mutation gate and freeze lane, then the cross-process installation lease. Missing or unstable ownership, pending general/stop/save journals and maintenance remain fail-closed. No historical restart is replayed.
- Process identity is re-observed and pinned (ASA and Loader PID, creation time, executable and args), source and profile are rechecked. Production historical proof verifies exact registered command, Store source and RCON listener; fresh proof verifies exact executable/args/INI/binaries and port owner.

## External patterns inspected
- [CyberBelligerent/ArkServerManager reconciliation](https://github.com/CyberBelligerent/ArkServerManager/blob/main/internal/server/reconcile.go) ([MIT](https://github.com/CyberBelligerent/ArkServerManager/blob/main/LICENSE)): startup normalization of transient state, separate from operation execution. Its reset-to-Stopped is NOT suitable for a live Obsidian map and was not copied.
- [Cluster coordinator](https://github.com/CyberBelligerent/ArkServerManager/blob/main/internal/server/coordinator.go): independent per-server failure handling and staggered operations. #189 applies map isolation.
- Implementation is original C# adapting these architectural principles, not a code copy. No external dependencies introduced.

## Still open; do not describe this as full repository remediation
- Terminal old-owner and confirmed stopped-family recovery, interrupted Stopping/Starting with proof of abandonment; add durable operation owner/creation epoch.
- General Launch/Stop/SaveWorld/Configuration journal recovery including partial paired writes, corrupt journals, and disk/permission failures; do not auto-mark uncertain world or file mutations successful.
- Restart retry, watchdog and schedule receipts; rolling Steam in-flight transactions, install/backup/restore and plugin/mod mutations.
- Historical source approval vs routine runtime state, peer-offline behavior, fresh/event and future map paths; audit all UI/Discord/API/scheduler call sites and remove routine approval wording.
- Add regression matrix across manual/Discord/batch/scheduled/automatic entry points: stale receipts, missing/wrong Loader, wrong map/RCON owner, PID reuse, active operation, process swap, simultaneous managers, power-loss and partial disk writes; verify both 5-map hosts before claiming live readiness.
- This change has not been executed on live servers; Windows CI and isolated tests are required.
