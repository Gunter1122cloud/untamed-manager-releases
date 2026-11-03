# Untamed Kingdom ASA Manager v1.6.1 — OPERATIONAL GAP CLOSURE / TEST PREVIEW

Windows 10/11 x64 · C#/.NET 10 WPF · önálló Desktop/Updater/Supervisor · külön Windows TESZTGÉPRE.

## Miért ez a kiadás?
A korábbi megbeszélések, a TELJES-KEZELOFULET-TERV-HU.md, a 2026.11.02-i auditok és a v1.3.x–v1.6.0 követelmények összevetéséből két azonnal javítható hiány és egy félrevezető készültségi probléma derült ki.

### 1. Biztonságos diagnosztika (új, valódi)
- A korábbi, a teljes WPF logmappát becsomagoló ZIP export megszűnt.
- Csak négy engedélyezett bejegyzés: summary.json, features.json, events-redacted.json, README.txt.
- Nem tartalmaz crash.logot, RCON/játékos/Store/SQL fájlokat, Discord webhookot, host/IP/útvonal/process indítási argumentumokat.
- Explicit lokális útvonal és SHA-256; ismert célfájl nem írható felül.
- Windows integrációs tesztek tesztjelszavakkal és rossz célú utak blokkolásával.

### 2. Discord státusz (új, manuális működés)
- Saját magyar Discord menü; csak a helyi Windows-profilhoz tartozó, DPAPI-val titkosított webhook.
- Kifejezetten egyirányú, kézi, megerősített üzenetküldés: a SUPERVISOR aktuális, hitelesített mapállapota és megbízhatóan ismert játékosszámok. Ismeretlen játékosszám ismeretlen marad.
- Csak discord.com HTTPS /api/webhooks/ID/TOKEN URL, allowed_mentions üres; a webhook nem kerül logba vagy támogatási csomagba.
- Discord wait=true, visszaigazolt message ID, 429 esetén automatikus újraküldés nélkül.
- Szándékosan nem küld automatikusan szerverállapotot, és sem Discord-admin, sem RCON-parancsot nem fogad.
- A küldési folyamat HTTP-állapotai Windows CI alatt **szintetikus HTTP handlerrel** teszteltek; valódi Discord szerverre nem küldtünk, így éles end-to-end Discord elfogadás még szükséges.

### 3. Kézi hálózati diagnosztika
- Új natív WPF menüpont: router és külső IPv4 célpont kézi ICMP mérése, 12+12 mintával.
- Válaszok/darab, csomagvesztés, medián, P95, csúcs, egymást követő válaszok átlagos abszolút eltérése (jitter).
- A válasz nélküli mintákat nem jeleníti meg nulla késleltetésként; ICMP nem egyenlő ASA-UDP vagy clustertransfer mérésével.
- Router/ASA/DDNS beállítások nem változnak, automatikus restart nem indul. Szintetikus CI statisztikai tesztek.

### 4. Őszinte termékkészültség
- Új 'Fejlesztési állapot / funkciók' WPF oldal 20 külön vizsgált területtel.
- A státuszjelzők LOCAL, READ_ONLY, MANUAL, TEST, OFFLINE, SIMULATED, BLOCKED; egyik sincs éles productionnak feltüntetve.
- Teljes, forrásokra és valódi kódra alapozott összevetés: [REQUIREMENTS-AUDIT-2026-11-03.md](../REQUIREMENTS-AUDIT-2026-11-03.md).
- Az új funkciók a meglévő Neon Kingdom felületen működnek, nem készült külön alkalmazás.

## A teljes ASA/Kova-helyettesítéshez hátralévő kemény feltételek
- A valós ASA/API Loader Start, SaveWorld befejezés, DoExit, Restart és állapottal bizonyított felügyelet tesztje.
- A két host 1:1 karakter/tribe/clustertransfer Store-v1 kompatibilitása és peer-zárolása másolt tesztclusteren; semmilyen konverzió vagy régi lock automatikus törlése.
- Teljes, konzisztens 2-host SavedArks/SaveGames/SQL/transfer/Vault backup és elkülönített restore; a jelenlegi 10 map+3 SQL OFFLINE archívum nem ilyen.
- Valós SteamCMD 2430930 staged updater, CurseForge entitlement, API loader release rollback, admin RCON és éles scheduler/watchdog.
- Cloudflare licenc, UPS, telefonos/VPN-es admin, Discord-bot szerepkörök és hálózati jitter még nincs kész.
- Authenticode kiadói tanúsítvány; a jelenlegi SHA-256 nem digitális aláírás.

## Teszt és kiadás
GitHub Windows Actions: build Windows Desktop+Updater+Supervisor; két FakeAsa-Manager, FakeSteamCMD, lifecycle és Kova/Cluster Vault mesterséges ZIP, Discord HTTP mock, titokszűrt ZIP privacy, WPF GUI render, Windows Inno Setup + updater recovery E2E.
A sikeres CI **TEST PREVIEW**, kizárólag harmadik Windows tesztgépen. Éles UK-HOME-01 vagy UK-HOME-02 gépen nincs engedélyezett telepítés, futtatás vagy adatírás.
