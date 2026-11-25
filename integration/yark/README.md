# Full upstream engine integration — P0
The entire upstream YARK source is pinned as a Git submodule at a9b5e0be72b3bd3618a917caf207bbd80c3451ef. Checkout with `git submodule update --init --recursive`. Original source, tests, LICENSE and third-party notices remain together. GPL-3.0-only applies to the upstream code and corresponding derivative distribution obligations must be met before release.

This branch is an isolated integration baseline, not a production replacement. The Windows workflow installs dependencies, runs upstream tests and typecheck, applies the preview branding overlay, and builds without packaging or publishing. The preview has a separate application data directory; it must not control production installations.

## Required P0 acceptance before cutover
- One per-map lifecycle authority for start, stop, restart and crash recovery. No concurrent old/new supervisor for the same map.
- Independent map operations; bounded SteamCMD serialization, no stale terminal job blocking unrelated maps.
- Readiness and process reattach, including actual ASA API Loader/child identity.
- SteamCMD install/update/verify, stop/update/start and recoverable failures.
- Existing registered maps and future maps: non-destructive profile import, Windows path/port identity.
- Existing Store-v1 source and shared transfer identities preserved. YARK does not implement this historical protocol.
- Two-host authenticated status and control, existing Discord adapter; neither is replaced by upstream as-is.
- Reuse Obsidian visual layout and navigation; current overlay is only initial palette/window branding.
- YARK uses Electron and node:sqlite; original Obsidian uses .NET/WebView2 and no SQL database. Do not silently migrate live config or accept a database architecture change as completed.
- Real Windows isolated fixture tests, then explicit staged acceptance with one map. No production rollout from this branch until integration passes.
