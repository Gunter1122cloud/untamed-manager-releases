# Obsidian Discord Control API v1 — separate experimental branch

Disabled by default. Existing bot remains on its own server; no bot token is
needed by Obsidian. No ClusterChat. Host API is a private IPv4 TLS listener
running only while Manager runs (not during isolated smoke/acceptance mode).
Never forward its port through the internet router. Each host needs its own
certificate, random credential and IP allowlist. Restrict Windows Firewall
inbound to the bot address. Configure the bot with the SHA-256 certificate
pin, reject all other certificates. Do not use this API without pinning.

## Provision (Windows PowerShell on EACH host, as the Manager Windows user)

Run scripts/Configure-DiscordControl.ps1 with the actual Manager profile
root, host's private IPv4, bot's private IPv4, port, GuildId
and RoleIds (both owner and admin role IDs, equal access); pass ControlChannelId and StatusChannelId separately (not ChannelIds). The script prints
the one-time bearer and certificate pin; put these into the bot's local
secret store, NEVER GitHub, logs, Discord messages or command arguments.
Restart Manager only. Remove settings.json and restart Manager to disable.
Credentials are hashed at rest in Manager; certificate private key is in
CurrentUser My store. Use ACLs on the Manager profile and bot secrets.

## Bot adapter contract

TLS 1.2/1.3, exactly one request per connection. Frame = four-byte unsigned
big-endian payload length followed by UTF-8 JSON. Request <= 16384 bytes.
Read reply by the same framing. Certificate SHA-256 pin MUST be checked
before sending bearer. Server verifies exact remote bot IP, token,
guild ID, distinct control/status channel scope, role ID, UTC timestamp within +/-60 seconds. The configured service identity can only read status/players/jobs from the status channel. Human admin requests must originate in the control channel. The API independently rejects @everyone as an authorized role.
Discord bot is the trusted identity authority: obtain actual guild member
roles again for every interaction (including confirmation), never from
button custom IDs or user-controlled strings. Verify private category
permissions, no @everyone view. Both designated roles have equal access.
No DM, arbitrary guild/channel, or public interaction. Status read access
also requires those roles. Suppress mentions and escape player-supplied text.

Envelope: {requestId: UUID, credential: 64-character hex string,
guildId: string, channelId: string, userId: string, roleIds: string[],
sentUtc: ISO8601, kind: string, ...operation fields}.

* status: local map IDs/names, OS running/stopped/unknown, PID, uptime, RAM,
  sample-derived normalized CPU, disk free/total, API mode, Store, last
  measured player count (unknown if stale). Query both hosts, aggregate by
  host ID; no cross-host writes in Manager. Unknown isn't zero.
* players: mapId. Native ListPlayers only, format-checked count and redacted
  list. Poll selected running maps (stagger requests); status uses cached
  player counts at most 65 seconds old for same PID/start. Refresh at
  ~30 sec cadence; never flood RCON or retry a busy host tightly.
* backupHistory: mapId; verified stopped-world backup catalog.
* preview: mapIds array (explicit), action start/stop/restart/save/wipe/
  rcon/backup/restore/update. Optional useApi boolean for start/restart;
  message for custom RCON; point for restore; delaySeconds 0..300 for batch.
  Multiple maps only start/stop/restart, using saved launch modes.
  Response names exact map owners, token, expiry, command and mode.
* apply: token and confirm:true. New UUID; same actor/channel. One-shot
  approval consumed and durable request claim before dispatch. Do not retry
  unknown effects. Native leases/approvals remain mandatory. Start with a
  different mode changes persistent map launch preference first, on a
  stopped map, then re-previews Start; if Start fails the changed mode may
  remain, so inspect before further action. Restart in different mode must
  be explicit Stop, wait confirmed completed, then Start in chosen mode.
* job: operationId = apply request UUID. Reads the recorded native job
  status if available, never resends commands. For a long synchronous backup
  wait for the actual response; no automatic retries. Store-v1 production
  restore remains denied by the native restore path; isolated staging is
  desktop-only. Steam update is stopped-only and manual. A native receipt
  is not proof of game client readiness or save fsync.

Bot UI: private status/controls/alerts/audit/backup/update channels, edit
one status message instead of spamming. Host + map selection must use
current IDs, never IP/path provided by Discord user. For batch across both
hosts request separate previews and confirmations, stop if any outcome
is uncertain. Never auto-approve. Show accurate status vs dispatched.
No raw arbitrary WebView methods, paths, passwords, RCON credentials,
firewall changes, Manager update, or OS commands over the endpoint.
Native custom RCON restrictions still apply.

## Bot transport helper

`scripts/obsidian_discord_client.py` contains a Python standard-library pinned TLS client and async wrapper. The bot must persist its apply request UUID before sending, defer Discord interaction responses for long operations, and recheck Discord roles/channel at each action. Do not log the request envelope. The helper is not a complete Discord bot.

## Acceptance

This branch introduces the listener and a typed adapter. Real Discord bot
integration remains in the separate local Codex project. Windows TLS,
native integration, real role/permission and stopped disposable ASA tests
are still required. No live servers or router changes performed.

## Existing Discord bot extension

Copy `scripts/obsidian_discord_client.py` and `scripts/obsidian_discord_cog.py` alongside the existing bot. Python 3.11+ and discord.py 2.x reference integration; if your bot uses another stack, Codex must port the module without replacing its existing handlers. Use the example JSON only as a template, actual secrets in ACL-protected local config outside Git. Verify both current host addresses. In the existing asynchronous setup_hook add `await obsidian_discord_cog.install(self, LOCAL_CONFIG_PATH)` once, no new bot.run or token. Set provision_channels to true only on explicit local setup; the module creates a separate private category and six channels, stores IDs on disk, never adopts a channel by name. Restrict permissions; guild owner and Discord Administrator privileges inherently bypass channel overrides. Do not assign Administrator to unintended users. The bot needs Manage Channels, View Channel, Send Messages, Read Message History, Embed Links and ability to fetch guild members/roles, not blanket Administrator. After channel creation use saved status/control channel IDs and the existing bot account's user ID as ServiceUserId in both host provisioning scripts. Service identity is status/ListPlayers/existing job receipts only; human admin actions require freshly fetched member roles. The existing bot token never goes into Manager. Restart Manager only, not ASA. Status polling uses the bot identity; per-host API pins/keys are distinct. For long jobs use the recorded UUID button, no apply retry. Multi-host operations are individually approved per host. Alternate API-mode restart requires stop then start in target mode. Backup and restore only within existing native supported scope. Actual bot project is not available in this branch and must be integrated/tested locally.

### Two-step setup and job feedback

Set `provision_only: true` and `provision_channels: true` for the first launch; host credentials may remain placeholders while private channels are created. Read their saved IDs, then provision each Manager with status and control channel IDs plus the bot user ID. Fill actual host secrets locally, set `provision_only: false` and `provision_channels: false`, restart the existing bot. The background service can read existing operation receipts but cannot preview or dispatch. It polls recorded operation IDs and posts terminal native job state to private audit/backup/update channels. Native receipt-only operations (including Start) remain explicitly unverified for game readiness. Status interval is approximately 30 seconds after the previous bounded poll completes.


## Optional existing-bot runtime control inside Manager

The published Setup and Portable include a `DiscordIntegration` directory with the Python extension/client, requirements, example config, both provisioning scripts and this guide. They contain no real credentials. Codex integrates the extension into the **existing bot**; do not create another bot or change its token. Install Python dependencies in its existing virtual environment. Ensure the bot uses file logging (pythonw provides no console). Stop the old manually launched bot, then run the following once as the Windows account that owns it, on **one** host only:

```powershell
.\Register-DiscordBotRuntime.ps1 -Pythonw 'C:\path\to\venv\Scripts\pythonw.exe' -EntryScript 'C:\path\to\existing\bot.py' -StartAtLogon
```

Use the real paths chosen by Codex; omit StartAtLogon if manual start is preferred. Registration never starts the bot and refuses replacement of an existing task. In Obsidian's notification-area (tray) menu select **Discord bot** to see Windows task state and start/stop it without a console. The task uses the same interactive Windows user, not a Windows service; it does not run after logoff and logon autostart requires the option. Windows Task Scheduler prevents concurrent instances of this task and can retry a failed exit up to three times, one minute apart. It does not detect a hung or disconnected bot. A separately launched copy is not detected: disable other startup mechanisms first. The task survives Manager close/update; do not assume an API request interrupted by a Manager update succeeded. The API requires the Manager running. Actual Discord connectivity, bot application logs and task registration/control on the target Windows host remain local acceptance checks. Never place bot tokens in command line, task description, logs or GitHub. The control window only operates the fixed provisioned local task; no generic task/shell commands over Discord. Manual-test mode disables the task mutations. No task is registered in CI or by the installer.


### Authorization trust boundary
The API trusts the configured bot process to attest freshly fetched Discord identities. The per-host bearer is a privileged secret: someone who controls the bot process, its secret or an authorized Discord account is outside this guarantee. Do not make existing unrelated bot commands capable of assigning the designated owner/admin roles or exposing the credential. Verify guild role hierarchy, permission to manage roles, and the existing bot's role-grant paths locally. Never give the API bearer to Discord users. The tightened configuration requires explicit distinct ControlChannelId and StatusChannelId plus ServiceUserId; older configuration is rejected until reprovisioned. The bot re-fetches actor roles immediately before preview/apply after acquiring the host lane. Role revocation cannot cancel a command already dispatched.

### Dynamic local map inventory (status response)
`maps` is the complete current registered-map snapshot (all native pages). Never hard-code five maps or map names in an adapter. Poll it afresh; compare GUID sets after a successful complete snapshot to announce new registered maps in the private alerts channel. An unavailable host is not an empty snapshot. Respect Discord embed limits by splitting complete rows; do not silently truncate maps. The separate additive `discoveryFresh`, `discoveryObservedUtc`, and `discoveredMaps` fields expose sanitized local ASA discovery observations, including unregistered Event and future maps, without registration or control authority. Discovered entries have PID, start time, map key, match state, Loader hint and false control/Store permissions. Never pass them to players/preview/apply; only registered map GUIDs may be selected and native guards remain authoritative. If discovery is stale, do not infer the map stopped or remove it as a confirmed absence. The existing Node.js bot adapter on Ryzen must consume these additive fields, compare successful snapshots, and render any continuation fields locally; the included Python reference cog does not modify the installed Node bot.


### Historical Store-v1 restart and mod drift
The process observer compares the complete launch grammar and validates actual ordered enabled/passive mod switches independently of the saved profile. The existing strict matcher remains available. During a production historical Restart, native preview captures mod order from the verified direct ASA or actual Loader PID (bound to the approved ASA child and process creation time). Apply re-captures under its installation lease and rejects changed evidence before Stop. A pending next-start draft takes precedence; otherwise the verified live order is selected. After verified graceful exit, only the approved mod profile fields are synchronized under configuration leases and saved before the new native launch planner is invoked. Changed lists or pending drafts require the locally approved original-source pin; the old read-only original source fallback is not rewritten. The planner's final order must equal the saved ordered profile. The submitted draft is cleared after an exact verified spawn, not a claim of game-side mod loading. This is not a hot-reload of ASA mods. A process stopped outside the Manager before any capture cannot be reconstructed from absent Windows process evidence; standalone Stop/Start or mode-switch workflows must be checked separately. The live Node adapter and every actual Ryzen map still require local testing.

The optional background local-profile reconciliation lane checks registered historical maps once per minute, including manual-test mode (not smoke fixtures). It requires an approved local source pin, valid local process/RCON ownership evidence and no pending explicit draft/journal; it skips busy/update lanes and writes only on actual ordered-list drift. It never sends RCON, starts/stops ASA, changes a running process, edits INI or touches shared Store files. The operations snapshot and Discord status distinguish live and next-start ordered lists; null live evidence is unknown. A failure does not retry a server action. Local acceptance of this feature remains required.

### Node.js / discord.js status adapter helper
The packaged `DiscordIntegration/obsidian_discord_status.cjs` is a dependency-free CommonJS status helper for the existing Node.js bot. Import it using require() or ESM default import. Call `buildStatusPanels(snapshots, previous)` on each successful polling cycle, where snapshots is an array of `{label,status}` (status is the authenticated API payload or null on transport failure), previous is the prior returned state or undefined. Consume `pages` as Discord embed JSON pages, `alerts` as private alert text, and `nextState` for the next poll. Persist that state if desired in the existing guarded bot state file; never put tokens in it. Use allowedMentions parse:[]; update or create the corresponding status messages and remove surplus prior panel messages only after successful rendering. Use only registered GUIDs for control selection. New observations include map key, numeric port if known, PID/time, process kind; a same-level Event instance is not suppressed simply because a registered map has the same level. Loader-only activity remains unknown, never online. No bot login, channel changes, network, RCON or admin action is performed by this helper. The actual installed Node adapter must call it locally; preserve all its existing guild/role/channel checks and token.

### Admin presentation and second-host inventory
The new packaged `obsidian_discord_admin.cjs` provides pure Node.js functions: `buildControlPanel(existingActionIds, options)`, `buildAudit({actorName,host,mapNames,action,state,errorCode,operationId,utc})`, `describeError(code)`, and `validateHostInventory(status,{expectedHostId,expectedMapIds})`. The installed Node adapter supplies its current custom IDs, resolves actor and target names via trusted existing sources, and edits its own messages. No handler or authorization is replaced by this module. Raw IDs stay in correlation footers/local journals. Native receipt is not game-effect certification. Use the validated dynamic inventory in place of any five-map assumption; provision the other host only after owner approval, pinned TLS, existing map-GUID identity and role/channel checks. The helper is not an actual HP deployment.

The desktop world-backup panel now also exposes `backup.worldStage`: verified archive-only copies in a unique Manager-profile folder, preserving zero-byte historical profiles, without accessing or replacing a live world's files. This does not constitute live Store-v1 restore, which still requires a coordinated full cluster/transfer transaction. The public backup error path now retains a narrow set of path-free error codes. The Event observation is still read-only until its actual local launch grammar, RCON/port ownership, save layout, and Store/cluster relationship are established; do not fabricate a profile or enable writer rights from discovery alone.

### Native audit and Event onboarding evidence (follow-up)
Apply receipts and asynchronous job replies include approved actor ID, host name, frozen map names/GUIDs and request time, so a bot can render readable historical logs even after a map rename. Existing audit files remain readable with optional fields. The bot resolves display names using its own trusted Discord member lookup. Status includes `controlCandidate`, `observedReadOnlyImported`, and `singleMapRestoreSupported`; none is an action approval. Discovery reports sanitized launch mod IDs, parser states, Store/cluster/transfer/conversion flag presence and a registration-state hint (unknown flags remain null). Use that evidence to identify the actual Event onboarding path; never pass these rows as API control targets. The current Ryzen Event has not yet been fully enrolled, and the native general live adoption path cannot authorize unknown Store/Loader ownership. Its complete real launch and RCON evidence is required locally before implementation can be verified.

## Single-request control (directControlVersion: 1)

An authenticated administrator or owner sends the usual TLS/length-prefixed JSON envelope with `kind: "execute"`, a unique `requestId`, `action`, and `mapIds`. Optional action arguments are unchanged: `useApi`, `message`, `point`, `delaySeconds`. Supported actions: start, stop, restart, save, wipe, rcon, backup, restore, update; multiple map IDs are supported for start/stop/restart. The Manager internally prepares and executes the same native operation in one request; the bot must not ask for a separate preview token or typed approval. The existing preview/apply contract remains compatible for old bots. Authorization still requires the configured admin/owner role, control channel and host credential. The service/status principal remains read-only. The response uses the existing apply receipt schema; query `kind: "job"` with the original request ID as `operationId` to track asynchronous execution. Do not replay a timed-out mutation under a new request ID. A duplicate request ID is rejected before preflight. A genuine same-map operation or installation conflict can return busy; no external manual approval is required by this API itself. Native legacy journal blockers, where still present, are separate issues.
