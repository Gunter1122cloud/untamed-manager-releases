# Untamed Manager — Store-v1 compatibility from operator-supplied runtime source

Date: 2026-11-06. Development reference only; **not permission to write live Store data**.

## Available evidence (not included in the repository)

The public `BnB-b/Legacy` GitHub repository contains only a README that explicitly says its application source is not published. Separately, the operator-provided local archive `Legacy-Belso-Mukodes-20261103-222643.zip` contains original JavaScript source modules (including `Forras/agent/cluster.mjs`, `Forras/agent/shared-store.mjs`, `Forras/agent/agent.mjs`, `Forras/agent/owned-process.mjs`, `Forras/profile-launch.cjs`, `Forras/scheduler.cjs`) and local runtime artifacts. The actual source archive is **not vendored into this public Untamed Manager repository**. It remains an operator-supplied research reference.

The existing `LegacyManager-Fontos.zip` and `LegacyCluster-Store-v1.zip` archives additionally supply historical Store-v1 metadata. These are distinct from the original application source. The source does not embed the current live HP/Ryzen save files, nor prove cross-host runtime writer ownership on 2026-11-06.

## Source-supported launch semantics

- The local manager binds each map to a stored launch profile, its original profile ID and install root; it rejects silent stale profile reuse.
- The original ASA argument builder begins with `MAP?listen`; requires validated `-Port=` and `-QueryPort=`; preserves passive mod IDs before other active mod IDs; appends the cluster identity and original shared directory only after checking the applied cluster binding.
- A map with an existing **active** Store record receives one `-usestore`. The source appends `-converttostore` **only** when that record is explicitly in the one-time `prepared` state. The historical, already-active Untamed Kingdom cluster must never enter `prepared` again.
- Original server startup requires the saved INI file, matching Game/Query ports, RCON enabled/password, and an actual local ASA process identity. An ASA API loader can substitute for the server executable when locally enabled.
- Shared-Store locks are durable and have no automatic TTL release. The two peers, shared session, map member identities, ordered configuration hash, and transaction markers must agree. A shared cluster restore is not safely inferred from one machine's data.
- Startup/restart guards use the current Store mode; a map in `review` or a pending shared transition is not authorized for ordinary launch.
- **ASA is responsible for the game-world/profile persistence**. The Manager coordinates the launch flags, shared Store state, backups, process identity and RCON graceful shutdown. A green GUI test is not evidence of genuine profile travel or SaveComplete on disk.

## Added in PR #11

`HistoricalLaunchEvidenceInspector` now compares operator-supplied historical launch text with the original source's expected switches (including `?listen`, `-log`, one `-usestore`, dedicated ports). It separately detects duplicate Store switches and either migration direction, without disclosing credentials or raw original commands to WebView2. The React historical import UI displays this read-only evidence.

Regression tests verify canonical legacy launch switch form, unsafe repeat/conversion flags and continued **failure to authorize production historical launch**. All historical Store/SQL/world/character writes remain blocked, with no source ZIP extraction to production paths.

## Still necessary before active historical migration

Preserve the original `LegacyCluster` wire identity and two-host paths, obtain real copied live metadata for *both* hosts with a consistent session, verify historical command provenance (not merely operator text), validate local process/loader reattach, RCON, Store transaction markers, saved world/profile/tribe hashes and a controlled real ASA two-host character travel on an isolated clone. Never auto-create or rewrite Store metadata, retry an interrupted migration, convert a second time, or declare an unverified profile travel successful.

**Current release state:** source-based compatibility work in progress, not the official ten-map production replacement.
