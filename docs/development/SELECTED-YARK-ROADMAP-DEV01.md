# Selected roadmap — DEV-01 first slice
Base: 1d8875761a377714fa13084a5070cc71203a3daa. User-selected scope: 33 capabilities, no expansion to deferred features.
Reference: YARK a9b5e0be72b3bd3618a917caf207bbd80c3451ef, `src/shared/createGenerationGate.ts`, `src/backend/domains/instances/install-health.ts` (GPL-3.0). Independent implementation; no copied GPL source or imported dependencies.
- DEV-01: serialize dashboard evidence refreshes; coalesce foreground requests, avoid stale overwrite; first shared Hungarian error catalog.
- DEV-02 partial: retry read-only lifecycle and job-receipt reconciliation after transient failures, without replaying game commands or removing native Store/PID/ownership guards.
- DEV-05 partial: distinguish empty, incomplete, inaccessible, unrelated and invalid/empty executable at import preview and apply. Profile-only import does not certify readiness.
Tests: Playwright refresh queue + error mapping; native synthetic fixed-drive import diagnostics. Windows CI and actual ASA acceptance are separate gates.
Remaining: all other selected implementation work, full automatic native reconciliation where evidence permits, certified Store enrollment/N-host, DLC/import/move, Downloads Center, INI draft/game balance, mods, player backup/RCON, scheduler/telemetry/logs, tray/autostart/Hosted Resources. Existing code and deferred capabilities remain untouched.
