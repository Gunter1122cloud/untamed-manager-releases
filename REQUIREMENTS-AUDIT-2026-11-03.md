# Untamed Kingdom ASA Manager – korábbi követelmények teljes összevetése

**2026-11-03 · v1.6.x Windows TEST / PREVIEW – NEM production elfogadás**

## Források
A felhasználó TELJES-KEZELOFELULET-TERV-HU.md dokumentuma, Untamed-Manager-Kutatas-es-Adatigeny-20261102.md, a 2026-11-03-as v1.3.7/v1.5.0/v1.6.0 feladatok, a HP/Ryzen auditjelentések, a 159 Kova dokumentációs bejegyzés összefoglalója, az aktuális GitHub WPF/Core/Supervisor/CI-kód és a korábban szintetikus tesztekkel feldolgozott valódi Store-v1 metadata ZIP. A felhasználó eredeti Store ZIP-jét és a privát hostadatokat nem másoltuk a GitHubba.

## Tételes, 20 területes hiányosságlista

| Terület | Jelenleg igazolható | Nem kész / szükséges még |
|---|---|---|
| Saját magyar natív WPF/ikon/tray/telepítő | Windows CI és GUI smoke | Önálló Windows test-PC elfogadás |
| Két Manager és LAN-peer | Helyi map ownership, titkosított és HMAC-kal hitelesített státusz | Éles két host long-run; nincs hitelesített remote admin vagy TLS |
| Untamed Cluster Vault | ZIP metadata hash, session, peer, lock, 5+5 map; nincs forrásírás | Live Store-v1 és karakter/tribe oda-vissza teszt, két host valódi koordináció |
| Active/Passive/Disabled modok | Helyi per-map és globális mod argumentumok | Valódi ASA boot modlista / alkalmazás ACK |
| CurseForge | Read-only katalógus/API-kulcs és update preview | Telepített file ID, letöltési jogosultság, automatikus alkalmazás |
| ASA API és plugin | Loader/integritás leltár, manuális plugin állapot/ZIP rollback | Éles API update és runtime kompatibilitás |
| JSON/INI 2.0 | Hash/ETag/BOM/komment/diff/mentés | Beacon-jellegű mély spawn/engram/loot és üzemi validation |
| SteamCMD | FakeSteamCMD, install-root preflight | Valódi 2430930 staged frissítés és rollback |
| Start/Stop/SaveWorld/Restart | Valódi Windows FakeAsa folyamat és state machine | Igazi ASA SaveComplete/loader/Ready + karakterintegritás |
| Watchdog/WAN IP-váltás | Read-only supervisor és WAN/DDNS ellenőrzés | Valódi safe recovery, opt-in automata; nem kapcsolható be alapból |
| Világ-/karakter-/clusterbackup | Offline SHA mentés, 10 map + 3 SQL offline csomag | Két host Store/transfer/DB azonos konzisztenciahatár és restore |
| MariaDB ArkShop/Permissions/Quests | Offline SQL csomag és hash | Natív dump+teszt-restore valós MariaDB-vel, tábla motorok |
| RCON | ListPlayers és módosító parancsok előnézete | Éles Broadcast/Wipe/SaveWorld admin parancs engedélyezése teszttel |
| Supervisor ütemező | Háttérben futó read-only recurring, DST, napló | Éles Restart/Wipe/Backup automata még tiltva |
| CPU/RAM/player/latency | Helyi CPU/RAM és idősor, ismeretlen adatok nem hamis nullák | Valós player parser, RCON RTT, hálózati jitter, DNS feltöltés |
| Windows Updater | Windows integrációs teszt, Setup+Portable SHA/rollback | Valódi aláírt Preview/Stable GitHub Release és test-PC elfogadás |
| Discord | ÚJ: explicit webhook + DPAPI, kézi Supervisor-alapú státusz és HTTP-mock teszt | Live webhook E2E, automata értesítés és Discord admin/bot |
| Mobil / szerepkörök | Nincs hitelesített mobil üzem | VPN/Tailscale, RBAC, multi-party confirmation, audit |
| Saját Cloudflare licenc | GitHub frissítő lap, Cloudflare health lehetőség | Workers API szerződés, Ed25519 ticket, offline grace és valódi aktiválási teszt |
| UPS / hálózat | WAN/DDNS manuális read-only | UPS API, ping/jitter, router/host uplink monitor (nem automata restart) |

## Ebben a körben ténylegesen hozzáadott elemek

1. **Titokszűrt diagnosztika:** az előző Settings megoldás minden naplót vakon ZIP-be csomagolt. Ezt megszüntettük. SafeSupportBundle explicit whitelisttel 4 fájlt exportál: summary.json, features.json, events-redacted.json, README.txt. Sem RAW crash.log, sem hostnév/IP, Store, karakteradat, RCON/Discord/CurseForge token, teljes útvonal vagy indítási argumentum nem kerül bele. SHA-256 és adatvédelmi integrációs teszt.
2. **Valós képességállapot-áttekintő:** a WPF-ben Fejlesztési állapot / funkciók menü, 20 terület pontosan TEST/LOCAL/READ_ONLY/MANUAL/SIMULATED/OFFLINE/BLOCKED besorolással. Egyetlen nem validált éles funkció sem lehet késznek jelölve.
3. **Discord szerverstátusz:** új WPF menü és DPAPI-s titkosított webhook, külön előnézet, megerősített egyszeri HTTP POST a kizárólag hitelesített, friss, helyi Supervisor térképekről. Discord HTTP wait=true és mentett message ID; allowed_mentions üres, szigorú discord.com/HTTPS URL, 429-re nincs automatikus retry, nincs közvetlen RCON vagy parancsfogadás. Az első teszt szintetikus HTTP handler, éles Discord-üzenet NEM történt.

## Maradék feladatok és blokkolók – sorrend

**P0:** valós ASA SaveWorld-befejezési bizonyíték; egyetlen writer tulajdonjog és API Loader/process ID; teljes Kova/Untamed Vault 1:1 transzfer-rendszer és maradéktalan kétgépes karakter- és adatkonzisztencia; három SQL adatbázis konzisztens dump/restore másolaton; izolált valódi ASA tesztcluster. Ezek NEM dönthetők el kizárólag FakeAsa Windows CI alapján.

**P1:** SteamCMD és ASA API éles staged frissítési motor; telepített CurseForge-file ID, jogosultság és törés nélküli update; teljes backup retention karantén és WPF; automatikus ütemezett író feladatok csak P0 után; kiadási csatorna publikált és kiadói aláírt GitHub Release-szel.

**P2:** hitelesített mobil UI és Discord-admin szerepkörök; Cloudflare licencek és offline token/hibaelfogadás; UPS integráció és valós WAN feltöltés/jitter; mély INI/loot/spawn/engram editor.

## Kötelező invariánsok

- A HP helyi mapjai: Astraeos, Genesis Part 1, Valguero, The Island, Extinction. A Ryzené: Scorched Earth, Lost Colony, Ragnarok, The Center, Aberration.
- A tesztbuild **csak a külön Windows tesztgépre** való, az UK-HOME-01/02 éles gépekhez a fejlesztés alatt nem nyúlunk.
- A felületen a márkanév Untamed Cluster Vault. A már létező diskprotokoll azonosítóit belül megtartjuk, semmilyen konverzió vagy locktörlés nem történik.
- A felhasználó korábbi kézi WAN-kezelési döntését nem írjuk felül automatikus restarttal.
- A teljes telepítő a CI után TEST, nem production-ready, amíg az eredeti két hoston kívüli, valódi ASA/Kova teszt nincs lezárva.

Discord protokollhoz ellenőrzött elsődleges források:
https://github.com/discord/discord-api-docs/blob/main/developers/resources/webhook.mdx
https://github.com/discord/discord-api-docs/blob/main/developers/topics/rate-limits.mdx
