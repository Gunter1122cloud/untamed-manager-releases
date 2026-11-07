# Untamed Kingdom — Obsidian Command teljes forrásleltár és kiadásjavítás
**2026-11-07 · fő kiadási cél: React/TypeScript + WebView2 + C#/.NET 10**

## 1. Kiterjedés és bizonyíték
A főág teljes GitHub-fájfája: **435 fájl**, ebből `desktop/` **44**, `desktop-ui/` **49**, megosztott `wpf/Core/` **129** és tesztkönyvtárak alatt **83**. A teljes fájflistát áttekintettük; ez NEM jelenti, hogy mind a 435 fájl minden sorát dinamikusan/éles ASA környezetben teszteltük. Célzottan megvizsgáltuk az aktuális asztali host, a natív műveleti bridge-ek, a release/telepítő/updater, a React route szerződés, a jóváhagyott Dashboard/Mod Center CSS és a védett Store-v1 útvonalak forrását.

A React `desktop-ui/src/bridge.ts` **118** RPC metódusazonosítóját összevetettük a `desktop/ObsidianWindow.cs` allowlistjével és a `DesktopReadOnlyBridge` fallbackkel: statikus útvonal-hiányt nem találtunk. Ez nem bizonyítja a valós ASA API-, SteamCMD-, CurseForge- és RCON-hatásokat.

Windows CI WebView2 indítási képernyőképe és Playwright 1600px dashboard/modképek ténylegesen megvizsgálva; a képek **szintetikus tesztadatot** mutatnak. Az elfogadott, kompakt Dashboard és kéthasábos Mod Center a jelenlegi React-forrásban és képen megvan.

## 2. A korábbi fejlesztéssel ténylegesen elrontott dolgok

| Prioritás | Bizonyított eltérés | Javítás |
|---|---|---|
| P0 | A régi WPF bináris és az Obsidian két külön automatikus GitHub-kiadási folyamata versengett, miközben a végső UI már WebView2 | `build-untamed-wpf.yml` kizárólag kézi, történeti regressziós workflow; WPF release-publikáló lépések eltávolítva |
| P0 | A `v1.4.0-obsidian-preview.N` Setup és Portable ZIP nem hordozta az `installed-release.txt` markert; azonos főverziónál ismételten újnak mutathatott egy kiadást | Pontos GitHub run tag beépítve, ZIP és izolált Inno Setup ellenőrzi; önálló SHA-256 asset a kiadásban |
| P0 | Az Obsidian frissítőkód a régi WPF `ReleaseCatalog.InstalledTag` tulajdonságát használta | Saját, Obsidian-specifikus marker olvasás; idegen WPF/érvénytelen nem üres címke elutasítva |
| P1 | Kiadási tagből `int.Parse` túlcsordulás lehetséges a rossz GitHub-adattól | `TryParse`, hibás/túl hosszú azonosító figyelmen kívül hagyása |
| P1 | Frissítési központ alapértelmezett Stable, miközben a jelenlegi Obsidian kiadások Preview-k | Preview az alapértelmezett; Stable külön, ha majd van tényleges kiadás |
| P1 | A korábbi `desktop/README.md` csak két read-only route-ot és migrációs demót állított, a gyökér README pedig elavult v0.5.0/Cloudflare kiadást írt le | Mindkettő valós `desktop/` + `desktop-ui/` és jelenlegi funkcionalitás szerint újraírva |
| P1 | Nem volt tartós, kód mellett őrzött, egyértelmű UI-motor döntés; emiatt WPF felé ment a fejlesztés | Új gyökér `AGENTS.md`: kizárólag Obsidian Command aktuális termék |
| P2 | Az Obsidian frissítési UI még a régi WPF telepítőt emlegette | Felhasználói szöveg saját, SHA-val hitelesített Obsidian telepítőre frissítve |
| P1 | A kiadott Obsidian build a wildcard `wpf/Core/*.cs` miatt régi WPF-demo, FakeASA/FakeSteamCMD és szintetikus restore tesztharness osztályokat is fordított | Hat tesztfixture külön `DesktopBridgeTests` assemblybe került, az `IntegrationTestAccess` build-attribútum kizárva a termékből; reflection CI teszt védi |

## 3. Biztosan meglevő, de éles környezetben nem tanúsított elemek
- `desktop/ObsidianWindow.cs`: offline WebView2 virtuális host, engedélyezett natív metódusok, tálcaikon, kilépés a külső updater sikeres átadásakor.
- `desktop/DesktopReadOnlyBridge.cs`: hitelesített kétgépes státusz, dashboard, modok, pluginok, logok, fájlintegritási összefoglalók; Store-írást nem tesz lehetővé.
- Natív bridge-ek: új/tisztán telepített helyi ASA SteamCMD, kétlépcsős Start/Stop/Restart, RCON, INI, CurseForge API, plugin JSON/ZIP, Mod Copy, offline backup/TEMP restore.
- A történeti Store-v1 valódi másik gépre átutazásának, aktív session/locknak és karakter/tribe/inventory/dínó sértetlenségének teljes bizonyítása **NINCS KÉSZ**. A korábbi működő Kova-koordináció eredeti metaadatait és karakterfájljait átírni tilos; ne távolítsunk el biztonsági tiltást bizonyíték nélkül.
- Az 5+5 offline archívum SHA-256 fájlintegritást ellenőriz, **nem** atomi, élő kétgépes cluster-pillanatkép.
- Az ASA API/plugin, CurseForge modok valódi betöltöttsége és a SaveWorld/DoExit utáni játékbeli újratöltés nem igazolható szintetikus GitHub Actions futással.
- A forrásban látható Cloudflare/licenc korábbi v0.5 dokumentációja nem bizonyította, hogy a jelenlegi Obsidian `UntamedDesktop.exe` licencfolyamata kész; ezt külön éles elfogadásnak kell alávetni.

## 4. Mit tekintünk ténylegesen késznek?
Kizárólag a legfrissebb zöld **Obsidian React Desktop Preview (Windows)** CI után épített `Untamed_Obsidian_Desktop_Preview_Setup.exe` és Portable ZIP forrás- és build-szintű működését. A csomag nem egyenlő éles karakterutazási tanúsítvánnyal. Natív WPF-kiadást mostantól **nem** publikálunk automatikusan.

A további munkát az `AGENTS.md` és az elfogadott React-felület alapján kell folytatni; eltérő motorra átállás csak kifejezett új felhasználói döntéssel történhet.
