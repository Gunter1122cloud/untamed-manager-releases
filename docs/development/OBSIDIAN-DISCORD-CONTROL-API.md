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
root, host's private IPv4, bot's private IPv4, port, GuildId, ChannelIds
and RoleIds (both owner and admin role IDs, equal access). The script prints
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
guild/channel ID, role ID, UTC timestamp within +/-60 seconds.
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
