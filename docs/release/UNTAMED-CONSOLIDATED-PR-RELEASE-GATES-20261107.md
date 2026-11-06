# Untamed Manager — egységes kiadás: állapot és kapuk
**Kiadási forrás:** PR #14 → `main` · 2026-11-07

## Az összes korábbi PR elszámolása

| PR | Hozzájárulás | Átvételi döntés |
|---|---|---|
| #1 | ASA/Store log- és indítási paritás, RCON tesztek | Megtartva a közös ősfán |
| #3 | Store-v1 kétgépes, csak olvasó metadata-/ZIP-paritás | Megtartva |
| #5 | Kova/ASA read-only leltár és hibajegyzék | Referenciaként megtartva; nem éles aktiváló funkció |
| #6 | Műveleti zárolás, Store-másolatok, SQL | Műveleti védelem megtartva; SQL/MariaDB **nem része a kiadott desktopnak** |
| #7 | WPF Core, SteamCMD, Save, RCON, vizuális elemek | Átvéve; a PR #7 utólagos 9 commitjában érintett 7 fájlból hat bitazonos a PR #13-mal, a hetedik PR #13-ban további LAN-javításokat tartalmaz |
| #9 | CurseForge DPAPI, kulcs ellenőrzés, hivatalos API | Megtartva; titkok nem kerülnek kiadásba |
| #10 | #7 és #9 integráció | Megtartva, független helyi gépes update queue-val |
| #11 | .NET 10 offline React/WebView2 Windows asztali GUI és Core bridge | Megtartva; ez **nem tisztán WPF**, az eltérést nem titkoljuk el |
| #13 | LAN peer pin, Mod Copy, profilcsere, SQL-mentes offline archívum, plugin JSON | Megtartva |

## A kiadott programból kizárt

- Nincs beágyazott MariaDB/SQL motor, dump/restore, adatbázis-konfiguráció vagy 3-SQL kötelező clusterbackup.
- Nincs új Store-v1 konverzió, `-converttostore` vagy `-convertfromstore` bekapcsolása.
- Nincs szabad távoli Stop/Start/Restart; LAN-on csak hitelesített állapotadatok kérhetők le.
- Nincs új webes admin szerver. A helyi WebView2 a telepített Windows alkalmazás offline, csomagolt felülete.
- Nincs Codex vagy külső AI-szolgáltatás mint futási feltétel; a MariaDB, Qwen/Discord/mobile nem része a kötelező desktop magnak.

## Automatizált ellenőrzések

- Node 22 / offline React build; Playwright UI/scroll; Windows 2025 .NET 10 x64.
- Natív C# integration + Store hard-guard + ZIP byte-paritás és nulla bájtos, átmeneti arkprofile megőrzése.
- Release regresszió: tiltott MariaDB/SQL típusok **nem szerepelhetnek a desktop assemblyben**.
- Valódi Windows WebView2 indulás + képernyőkép.
- Telepítő + Portable + külső updater, SHA256SUMS.
- `main` push után *csak* sikeres CI mellett jelenhet meg automatikus **prerelease**, nem stabil release.

## Teljesen végleges, történeti Store-v1-es éles kiadás FELTÉTELEI

Ezeket nem teljesíti egy Windows CI egyetlen gépen:

1. Az eredeti ACTIVE/DONE 5+5 Store-v1 két gépen ugyanazzal az eredeti sessionnel, lockkal és valós közös UNC gyökérrel legyen igazolt.
2. A teljes történeti Store-v1 *Start/Stop/Restart* végponttól végpontig éles ASA-kompatibilis, character/tribe/world fájl-integritással; **nincs új konverzió**.
3. Az izolált kétgépes tesztgépen ugyanazzal a túlélővel *HP → Ryzen → HP*, inventory/hotbar/implant/tribe/dínó változatlanság bizonyítva.
4. A SaveWorld / DoExit utáni world save és tényleges újratöltés/join bizonyítva; sérült vagy bizonytalan mentésnél nincs automatikus Kill/Restart.
5. SQL nélküli, két gépen összehangolt 10 map + transfer/Store snapshot és teljes visszaállítás játékbeli ellenőrzéssel.
6. ASA API és CurseForge plugin/mod betöltöttség valódi, frissített ASA patchen igazolva.
7. A jóváhagyott végleges Windows GUI (natív WPF vagy tudatosan elfogadott WebView2 eltérés), frissítő/rollback és telepítő kipróbálva külön Windows gépen.
8. A szervereket tartalmazó éles UK-HOME-01/02 gépekhez csak a külön lab sikeres elfogadása után nyúlhat az operátor.

**Értelmezés:** a forráskód main-be integrálása nem jelent automatikus éles Store-átvételt. A Store-v1 zárolásokat feloldani és a csomagot 'stabil, éles' címkével publikálni ezen tanúsítások nélkül tilos.
