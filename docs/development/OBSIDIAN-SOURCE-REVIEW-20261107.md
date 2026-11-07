# Obsidian Command 2.0.0 Preview — forráskód-ellenőrzés és konkrét javítások (2026-11-07)

## Ellenőrzött állapot és határ

- **Kizárólagos éles irány:** `desktop-ui/` React/TS és `desktop/` Windows WebView2/WinForms/.NET 10, közös `wpf/Core/` és `wpf/Updater/`. A régi `wpf/` UI nem szállított alkalmazás.
- **Kiindulási főági commit:** `5578f90f5e404d77b43b2fa007bd9b797fe475d4`, Obsidian 2.0.0 Preview #1481, kiadási CI sikeres.
- **Fájlleltár:** teljes GitHub fa: 444 fájl; `desktop/` 49 fájl, `desktop-ui/` 49 fájl, `wpf/Core/` 129 C# fájl, `.github/workflows/` 3 workflow.
- **Statikus keresés:** az aktív `desktop/`, `desktop-ui/`, CI 80 nemteszt jellegű forrásfájlján és az összes 129 közös Core C# fájlon végigmentek célzott vizsgálatok: TODO/FIXME, letiltott API/stub, elnyelt kivétel, HTTP, véletlengenerátor, fájl- és processzműveletek. A két nagy integrációs tesztfájlt célzottan olvastuk és bővítettük. A statikus mintaegyezés önmagában sem helyes működést, sem teljes sornkénti szemantikai auditot nem garantál.
- **Mélységi kódvizsgálat:** WebView2 request-handler és privilegizált allowlist, native request-korreláció, Store-v1 előtte/utána bizonyíték, offline 10-map archive/restore, per-map backup/restore, lokális process és SQL kizárás, LAN read-only protokoll, GitHub updater/handoff, telepítő rollback, React bridge pending Promise-ek és a Windows build/release workflow.

## Megtalált és javított eltérések (PR #29)

| # | Hatás | Változtatás / teszt |
|---|---|---|
| 1 | Külső karakter-összevetési JSON-ban `null` map, profil vagy hash `NullReferenceException`-höz vezethetett. | Explicit teljes fa NULL-védelem a `DesktopStoreTravelEvidenceBridge.Read` belépési ponton, natív negatív tesztekkel; nincs Store-data módosítás. |
| 2 | A WebView2 `async void` eseménykezelőjéből kiszökő nem várt kivétel az egész Windows UI-t leállíthatta. | Külső `try/catch` és titokmentes `UM-NATIVE-UNEXPECTED-FAILURE` válasz, változatlan globális gate `finally`. |
| 3 | A natív updater kétszeres fájlhatárán SHA-256 TOCTOU maradt: az ellenőrzött letöltés és a külön indított Setup között cserélhető lehetett a csomag. | A release SHA-256 hash kötelezően átadva `--sha256`; a külön updater előzetesen, az app bezárulása után, és közvetlen Setup-indítás előtt újrahitelesít. Egyetlen megtagadott digest sem készíthet alkalmazássnapshotot. Isolált valódi Windows negatív E2E teszt. |
| 4 | A React `pending` RPC válasz nélküli esetben korlátlanul élt, így megakadhatott a képernyő vagy felhalmozódhattak Promise-ek. | Timeout, timer-takarítás válasz és postMessage hiba esetén, külön Store/Update nagy I/O timeoutok; explicit `UM-NATIVE-TIMEOUT-OPERATION-STATUS-UNKNOWN`, nem automatikus ismétlés. Playwright negatív teszt. |
| 5 | A SHA-ellenőrzött 10-map archívum összehasonlítása közben a globális mutációs gate lefoghatta a többi UI-hívást. | `store.compareArchives` felvéve a kizárólag olvasó, globális gate nélküli route-ok közé; C# regressziós ellenőrzés. |
| 6 | A frissítő új Windows CI tesztje meghiúsult, mert más TEMP gyökeret használt, mint a natív updater szigorú izolációs ellenőrzése. | Teszt: `[IO.Path]::GetTempPath()` a natív gyökérszabállyal azonos, és időtúllépésnél diagnosztika. |

## Szándékos letiltások, nem hiányzó implementációs hibák

- `LocalContentOperations.InstallLocalPluginFolder`: `NotSupportedException` szándékos, régi, ellenőrizetlen pluginmappa-másolás tiltása. Az érvényes út: `SafePluginZip`.
- Korábbi WPF/SQL tesztfixture vagy GUI-fájlok nem képezik az Obsidian EXE részét; soha nem szabad a régi WPF kiadásokat Obsidian frissítésnek tekinteni.
- Core takarítási `catch{}` néhány ponton csak best-effort, de ezek negatív tesztek és rendellenes fájlrendszer esetén ellenőrzendők; nem „minden hiba kizárva” bizonyítékok.

## Nem minősíthető automatikusan hibamentesnek

- **Összes funkció valódi ASA-val:** két elkülönített Windows host, 10 valódi ASA map, eredeti Store-v1 multiwriter/session/lock, A→B→A survivor/item/dino/tribe, SaveWorld/SaveComplete, restart és visszaállítás csak játékbeli próbával igazolható.
- A CI-ben jóváhagyott szintetikus világ/karakter fájl nem bizonyítja az éles játékbeli karakterutazást. Referenciával igazolt más Manager-képesség a mi Managerünkben **kötelező elvárás**, de saját élő, reprodukált siker hiányában nincs tanúsítás.
- Egy statikus forrásfájl-leltár és zöld fordítás nem garantál matematikailag nulla maradék hibát. Hibás időzítés, ritka IO-korlát, dinamikus plugin/CurseForge/ASA build-eltérés és hardver-specifikus Windows környezet további célzott vizsgálatot igényel.

## Kiadási minősítési szabály

A PR csak akkor zárható le, ha a teljes Windows workflow-ban **sikeres**:
React build + Playwright, C# native integration, self-contained .NET 10, valódi Windows WebView2 smoke/screenshot, Inno Setup telepítés/marker smoke, tampered updater SHA negatív E2E és SHA256 kiadási hash. Másodlagos eredmény az *aktuális main* új Preview kiadványa, amely továbbra sem *FINAL* Store-v1 migrációs tanúsítás.

**Tilos éles tesztnek tekinteni:** UK-HOME-01 és UK-HOME-02 szervereket, eredeti Store-v1 fájlokat, 0-bájtos karaktermentést vagy plugin-MariaDB-t; egyikre sem kerülhet automatikus tesztírás, konverzió, lock-feloldás, törlés vagy módosítás.

**Forráskód PR:** https://github.com/Gunter1122cloud/untamed-manager-releases/pull/29
**Központi teljes elfogadás:** https://github.com/Gunter1122cloud/untamed-manager-releases/issues/12
