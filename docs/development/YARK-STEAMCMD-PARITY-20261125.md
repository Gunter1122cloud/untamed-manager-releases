# YARK SteamCMD parity — 2026-11-25

Reference (GPL-3.0): https://github.com/gabomarin/yark/blob/main/docs/updates-steamcmd.md ; https://github.com/gabomarin/yark/blob/main/docs/critical-job-recovery.md ; src/backend/domains/updates/steamcmd-run.ts, steamcmd-content-cache.ts, steamcmd-control.ts, steamcmd-operator.ts. Original C# adaptation; no external code copied, no new database or dependency.

Implemented: shared English SteamCMD argv and environment, bounded recognized transient failure retries, fixed error codes, no-active-map queue checkpoint archival under machine-wide lease, and safe direct per-map fallback only for insufficient cache staging space. Existing host-local cache is SHA-256 verified, uses official payload allowlist, never copies Saved/INI/API mods, and target always validates via Valve. No replay of dispatched work. Regression cases included for preflight failures and legacy idle receipts.

Remaining: rolling update cache integration, actual dispatched Steam transaction recovery with protected verification, safe rollback, disk progress/heartbeat and pause/cancel UI, cache cleanup, Windows CI and live HP/Ryzen validation. No release-ready claim.
