# YARK RCON parity

Reviewed https://github.com/gabomarin/yark/blob/main/docs/rcon.md and docs/server-lifecycle.md, src/backend/infra/rcon/rcon-client.ts, rcon-session-manager.ts, src/backend/domains/instances/instance-rcon.ts, maintenance/maintenance-restart-runtime.ts and tests/maintenance-restart-runtime.test.ts. Independent C# adaptation, no GPL source copied verbatim.

- Scheduled fresh SaveWorld previously acquired the installation lease twice; remove outer duplicate, use native bridge.
- Admin and save-only dispatch journal starts after auth/ownership proof, before first command byte. Pre-auth rejection no longer becomes an uncertain mutation in these paths.
- Post-ready wipe on both supported fresh and historical maps: exact child epoch, reconnect/read-only ListPlayers up to 60s, at least 20s settle, recheck configuration and native owner. A failed wipe does not mark the restart failed.
- Wipe sends exact DestroyWildDinos (no cheat prefix). Match empty or standard ASA no-content acknowledgement; no replay on missing response. Receipt is not gameplay proof.
- Added isolated TCP acknowledgement tests. Existing SaveWorld remains exact SaveWorld; world/log evidence retained.

Remaining: persistent RCON sessions, optional separate post-restart SaveWorld before wipe, manual/rolling-restart wipe opt-in, full historical stop/restart pre-dispatch recovery audit, schedule-level regression and live Windows validation. No production operation executed.