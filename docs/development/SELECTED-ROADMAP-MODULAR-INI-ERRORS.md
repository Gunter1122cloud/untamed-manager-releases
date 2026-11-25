# UI-08/09 incremental frontend modularization
Extract existing INI editor and common Panel out of App.tsx; preserve map/file request generations and native wire contract. Extend central Hungarian native error mapping for selected roadmap, retain stable safe codes, redact unknown raw values. Integration follow-ups: consume staged Game.ini before replacement to prevent revival after external rollback; omit null MapModProjectId from serialized legacy profiles to preserve existing source/profile digests. Not a complete extraction of every App.tsx feature or full translation of every native status.

## Complete top-level page extraction
Split the former 1,874-line App.tsx into separate page modules and shared map views. Keep the same components, props, bridge requests, controls, layout and navigation; no native behavior changes. App becomes a small navigation and snapshot shell. Existing TypeScript strict and Windows UI checks remain the integration gates.
