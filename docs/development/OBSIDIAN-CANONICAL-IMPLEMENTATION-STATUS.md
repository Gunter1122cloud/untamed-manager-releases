# Obsidian Command 2.0.0 Preview – egyetlen kanonikus funkció- és integrációs állapot

Állapotfelvétel: **2026-11-08**. Kötelező termékspecifikáció: [#12 master](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/12), [#48 N-host](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/48), [#49 multi-cluster](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/49). Az alap alkalmazás a **React/TypeScript + WinForms/WebView2 + .NET 10** Obsidian Command; a natív WPF GUI véglegesen elvetett. A `wpf/Core/` és `wpf/Updater/` csak megosztott/történeti forrás.

## Hogyan jelöljük a készültséget?

- **KÓD: MAIN**: a kód ténylegesen a főágban van.
- **TESZT: ZÖLD CI**: izolált, szintetikus Windows/Playwright próbák az adott commiton sikeresek.
- **RÉSZLEGES**: van valódi forrásimplementáció, de az előírt teljes adatútvonal, működés vagy regressziós lefedettség nem bizonyított.
- **HIÁNYZIK**: a célképesség egy részének vagy egészének implementációja nem található a lezárt fejlesztésekben; következő munka.
- **ÉLES ELFOGADÁS: NINCS**: a két elkülönített Windows gépes valódi ASA/Store-v1/utazás tesztet még nem teljesítettük. **Soha ne értelmezd a zöld CI-t élő ASA-tanúsításnak.**

A funkciónak lehet **KÓD: MAIN + TESZT: ZÖLD CI + ÉLES ELFOGADÁS: NINCS** állapota egyszerre. Ezt soha ne rövidítsük egyszerű „100%-ban kész”-re. A külső ASA Managerekben működő, forrással igazolt képesség nálunk továbbra is **kötelező cél**, nem saját elfogadási bizonyíték.

## PR-konszolidáció – 2026-11-08

A felülvizsgálat kezdetén a repositoryban 46 PR volt: 35 már merge-elt, 8 lezárt/merge nélküli, 3 nyitott. A nyolc lezárt/merge nélküli PR (#3, #5, #6, #7, #9, #10, #11, #13) **mindegyik head branchére** a `main...head` commitösszevetés `ahead_by=0` eredményt adott: a commitjaik a későbbi főági előzménybe bekerültek. **Nem kell újra cherry-pickelni vagy merge-elni ezeket.**

A Codex által elkészített és egyenként ellenőrzött új kód a megfelelő függőségi sorrendben került `main`-be:

| Cél / issue | PR | Code head | Windows CI | Main merge commit |
| --- | --- | --- | --- | --- |
| [#50 HostRegistry, ClusterRegistry](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/50) | [#54](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/54) | `a59cd037` | [#1558 PASS](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42417068776) | [`47f993f`](https://github.com/Gunter1122cloud/untamed-manager-releases/commit/47f993feb0bd2d9c0750172450c85640c25b059d) |
| [#51 Hitelesített többpeer-es LAN](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/51) | [#55](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/55) | `2ea67f56` | [#1560 PASS](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42461556209) | [`a635990`](https://github.com/Gunter1122cloud/untamed-manager-releases/commit/a6359906b717995700313c6a4392bfd0c33ab3cd) |
| [#52 N-host React UI](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/52) | [#56](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/56) | `2c3e7526` | [#1561 PASS](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42462393991) | [`0210571`](https://github.com/Gunter1122cloud/untamed-manager-releases/commit/0210571954d9a36747741814803edfb7ef59625a) |

**Aktuális integráció (2026-11-08, frissített lekérdezés): 59 PR, 49 merge-elt, 10 lezárt merge nélkül, 0 nyitott.** PR #54–57, #58 és #59 a `main` része. A #58 ellenőrzött `caf8c6a` headjét a sikeres [Windows CI #1565](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42479884435) után [`eb3f4f6`](https://github.com/Gunter1122cloud/untamed-manager-releases/commit/eb3f4f671bff8b1810c806d63fdaaa139ef2bae7) merge commit integrálta. A korábbi 46/48 PR-os számok időbélyegzett pillanatképek; nem aktuális globális összesítés. A nyolc korábban lezárt, nem külön merge-elt PR történetét nem szabad ismételten integrálni.

## 2026-11-08 #58 utóintegrációs bizonyíték és korlátok

- [PR #58](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/58): a korábbi Draft állapotból Ready for Review lett, majd GitHub `merged=true`; merge SHA: `eb3f4f671bff8b1810c806d63fdaaa139ef2bae7`.
- Pontosan 21 módosított fájl; az archive, sandbox restore és bridge UI forrása integrálódott. A `LegacyStoreV1*.cs` fájlok nem szerepeltek a #58 módosításai között.
- [PR head Windows CI #1565](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42479884435): `success`, Windows preview, Core, natív bridge, Playwright, Setup és WebView2 ellenőrzések sikeresek. **A merge utáni `main` [Windows CI #1566](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42482209532) `SUCCESS` a pontos `eb3f4f6` merge SHA-n.**
- A #58 **offline, előzetesen elkészített másolatokból** dolgozik, csak TEMP sandboxba állít vissza; a production restore és az élő Store-v1/karakterutazás NINCS kész.
- Rendrakáskor a régi WPF GUI aktív kiadási útvonalait külön függőségvizsgálattal kell eltávolítani; a shared `wpf/Core/`, `wpf/Updater/`, Store-v1 és tesztek megőrzendők. A bizonyítatlanul halottnak gondolt kód NEM törölhető.

## Retired source cleanup – elkülönített PR #59

A [#59](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/59) **MAIN / MERGED** (`b72608b`): a `wpf/MainWindow.Sql.cs` elvetett WPF háromadatbázisos SQL GUI ténylegesen törölve. A pontos `b22cadf` headen a [Windows CI #1567](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42482924550) **SUCCESS**. A merge utáni [main CI #1568](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42483346382) szintén **SUCCESS** a `b72608b` merge commiton. A fájlt a `wpf/UntamedManager.csproj` már korábban kizárta a fordításból; a `desktop/UntamedDesktop.csproj` kizárólag a közös `wpf/Core/*.cs` részt használja. A `wpf/UntamedManager.csproj` redundáns compile-kizárása törölve, a `.github/workflows/build-obsidian-desktop.yml` pedig kompatibilitásvédő regressziós ellenőrzést kapott. A történeti SQL regressziós forrás, Store-v1 Core, updater és WPF referenciatesztek megmaradnak. Ez nem jelenti a teljes korábbi WPF GUI eltávolítását: annak a történeti tesztprojektekre és projekthivatkozásokra gyakorolt hatása további felülvizsgálatot igényel.

## 2026-11-08 – felhasználói P0/P1 regressziós követés

| Teszt | Jelenlegi állapot | Forrás, bizonyíték, hiány |
| --- | --- | --- |
| **TEST-003 – első ASA beállítás / hiányzó Start** | **#60 MAIN + CI, éles elfogadás NINCS** | [#60](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/60) és [főági Windows CI #1575](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42486421043) SUCCESS. Az első konfiguráció explicit blocker-státuszokat és szintetikus regressziót kapott; a Windows-felhasználói The Island reprodukciót még ismételni kell. Nem szabad ismeretlen folyamatállapotból hamis stopped igazolást készíteni. |
| **TEST-001 – friss telepítés hiányzó INI** | **#60 MAIN + CI, valós visszaellenőrzés NINCS** | Az `desktop/DesktopIniBridge.cs` hiányzó fájlt/mappát külön hibakóddal jelzi, a React UI magyar magyarázatot ad; nem hoz létre INI-t és nem ír Store-adatot. Az RCON első INI bootstrap a meglévő külön jóváhagyásos útvonalon marad. |
| **TEST-002 – többmodos CurseForge kosár** | **#61 MAIN + PR CI, főági elfogadás folyamatban** | [#61](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/61), [Windows PR CI #1574](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42486276277) SUCCESS; merge SHA `1933d44d`. Modkosár, több keresés/lapozás, natív ModDraftSession előnézet, operátori jóváhagyás és lokális Copy átadás; ASA-modbetöltés nincs igazolva. |
| **N-host modprofil átadás** | **#63 MAIN + PR CI; éles elfogadás NINCS** | A meglévő `DesktopModProfileBridge.cs` natív JSON-export/import és helyi, jóváhagyott apply használható. [#63](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/63) merge commit `b0308149`, [PR Windows CI #1580](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42487914706) SUCCESS; N-host feliratozás és legfeljebb 25 célmapos, explicit kötegenkénti kijelölés. A merge utáni [main Windows CI #1582](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42488328530) **SUCCESS** a `b0308149` SHA-n. **Nincs automatikus távoli írás vagy LAN-modszinkron.** |
| **Duplikált alternatív kosár** | **ELVETVE – #62 lezárva, nem merge-elve** | [#62](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/62) a meglévő #61 implementációval versenyzett; a történet megmaradt, kódja nincs a mainben. |

## P0 további célzott javítás

A [#64](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/64) eredeti P0 PR head CI #1581 **SUCCESS** volt, de a közben merge-elt #63 Playwright módosításaival ütközött; lezárva beolvasztás nélkül. Azonos megoldás és regresszió a friss `main`-re átültetve: [#65](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/65) **MERGED** (`4283c697d02b1332b488b8fbffbcc4e3e5459b6e`), [Windows PR CI #1583](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42488848005) **SUCCESS** a `400cf22b1adb56fb165a3d9c01643fff68c81a59` headen. A merge utáni [main Windows CI #1584](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42489501256) **SUCCESS** a `4283c697` merge SHA-n. Nem módosítja a natív process-guardot, Store-v1-t, és nem állít éles ASA-elfogadást. A valós Windows/ASA The Island visszateszt ettől is külön szükséges.

### #65 P0 képernyőjavítás implementációja

- `desktop-ui/src/InitialMapSetup.tsx`: új natív `servers.initialStatus` ellenőrző gomb, blokkoló státusz és valós `confirmedStopped` kijelzése; nincs mesterséges állapotátírás.
- `desktop-ui/tests/obsidian.spec.ts`: Playwright regresszió bizonytalan Windows-folyamat megfigyelése miatti letiltással és friss státuszra történő engedélyezéssel.
- Nem változott a `DesktopInitialMapSetupBridge`, az ASA indítási védőellenőrzés, a Store-v1 vagy az RCON/INI adatútvonal. A felhasználói elsőindítás éles elfogadása továbbra is külön P0 követelmény.

## 2026-11-08 – #66 RCON elsőbeállítás → Start UX-híd (MAIN)

- [PR #66](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/66) merge commit `ebb8460ff43c93c1fb14bfd0f835e992c19c6c5b`. A [Windows PR CI #1586](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42491175981) **SUCCESS** az `91f640ce1141d8e03ca13435e1a1ba8fbbce613a` headen, teljes React/Playwright + .NET 10/WebView2/Setup/Updater/Core tesztlánccal. A merge utáni [main Windows CI #1587](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42491686312) **SUCCESS** a pontos `ebb8460ff43c93c1fb14bfd0f835e992c19c6c5b` merge SHA-n.
- **TEST-003 / P0 React működési rés:** az első, leállított, beállított, nem Store-v1 map átvezetése az RCON bootstrap felületre, és a natív RCON-konfiguráció mentése után a Start panelhez. A manager snapshot frissül; a sikerüzenet a `configured=true` átmenet után sem tűnik el. `desktop-ui/src/App.tsx`, `FreshAsaStartPanel.tsx`, `RconReadOnlyPanel.tsx` és integrált `obsidian.spec.ts` teszt.
- A PR kizárólag UI és regresszió; nem változtatott a `DesktopInitialMapSetupBridge`, `DesktopIniBridge`, `DesktopRconConfigurationBridge`, `FreshAsaInstallationSafety` vagy Store-v1 őrző kódon. **Automatikus ASA Start nincs; a tényleges The Island elsőindítás helyszíni elfogadása továbbra is hiányzik.**
- A kezdeti PR CI #1585 hibát adott az új teszten: a konfigurációs szakasz `configured=true` állapota elrejtette a sikeres RCON panel gombját. A `91f640c` fix valóban külön, mindig látható sikerállapotba helyezte; a #1586 ismételt CI sikeres.

## 2026-11-08 – #67 INI ETag ütközés elkülönített natív hibakezelése (MAIN)

[PR #67](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/67) **MERGED**, merge SHA `7a92ff295961ce2566d5624267b544e81ab9120e`; [Windows PR CI #1588](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42491887030) **SUCCESS** a pontos `54de4d1c60fddf3727cbe9eb974e3b3f617c0cfd` head SHA-n. A merge utáni [main Windows CI #1589](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42492441344) **SUCCESS** a pontos `7a92ff295961ce2566d5624267b544e81ab9120e` merge SHA-n.

**TEST-001 / P0:** `wpf/Core/SafeConfigEditor.cs` az előnézet után megváltozott külső INI-t külön `ConfigEtagConflictException : IOException` típussal jelzi. A `desktop/DesktopIniBridge.cs` biztonságos `UM-INI-ETAG-MISMATCH` hibakódot ad; a `desktop-ui/src/App.tsx` magyar feloldási útvonalat mutat; a `desktop/Tests/Program.cs` natív regresszió igazolja, hogy a frissebb külső bájtok változatlanok maradnak. Az atomikus mentés, SHA-256, BOM/kódolás/sortörés, folyamatállapot- és Store-v1-védelem nem lazult. Valós felhasználói ASA teszt külön hátravan.

## 2026-11-08 – #68 végrehajtott elsőbeállítás és RCON commit-visszaigazolás (MAIN)

- [#68](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/68) **MERGED**, merge SHA `1d90fba39a5afdf60e7a4528f4e7cd296491424f`. A [PR Windows CI #1590](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42494482469) **SUCCESS** a pontos `0cc84cd383976b5eceee990d8d3ecb33eb27ce15` headen; a merge utáni [main Windows CI #1591](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42494934701) **SUCCESS** a pontos `1d90fba39a5afdf60e7a4528f4e7cd296491424f` merge SHA-n.
- `desktop-ui/src/InitialMapSetup.tsx` és `desktop-ui/src/RconReadOnlyPanel.tsx`: a sikeres natív profil/RCON beállítás mentése után jelentkező státuszlekérdezési hibát többé **nem** lehet sikertelen natív írásként kijelezni. Egyértelmű, nem ismétlő műveletet kérő figyelmeztetés jelenik meg; az egyszeri, jóváhagyott RCON bootstrap visszaigazolása megmarad.
- `desktop-ui/tests/obsidian.spec.ts`: szintetikus Playwright fault injection a már sikeresen megtörtént RCON konfiguráció utáni státuszolvasási hibára. Pontosan **1** natív RCON apply és **0** ASA auto Start a tesztben.
- A Core/Store-v1/INI-kódolás/BOM/ETag/atomikus fájlmentés és minden natív folyamat- és ownership-guard változatlan; valódi Windows-felhasználói The Island elfogadás **NINCS**.

## 2026-11-08 – #69 első ASA EXE-azonosság a telepítési gyökérhez kötve (MAIN)

[PR #69](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/69) **MERGED**, merge SHA `7c644513200b01d5793cad56384b35b5589e55a6`, pontos head `929efc32b90ffcd5af3747333e5b2ee8d0f2c5de`. [Windows PR CI #1595](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42495204564) **SUCCESS** (React/Playwright, natív bridge, Store/Core, WebView2, Setup). A merge utáni [main Windows CI #1596](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42495749935) **SUCCESS** a pontos `7c644513200b01d5793cad56384b35b5589e55a6` merge SHA-n (28 sikeres lépés).

- `desktop/DesktopInitialMapSetupBridge.cs`: a puszta `File.Exists(map.ExecutablePath)` helyett a már meglévő `AsaReadyProbe.IsRegisteredServerBinary(map)` követelmény érvényesül az elsőbeállítási státusznál és preview-nál, valamint közvetlenül a mentési jóváhagyáskor. Az idegen install EXE, UNC/link/junction és hiányzó helyi bináris elutasítva; nem állít be hamis stopped státuszt.
- `desktop-ui/src/InitialMapSetup.tsx`: külön, érthető magyar hiba szól az érvénytelen regisztrált EXE/telepítési gyökér esetéről.
- `desktop/Tests/Program.cs`: izolált rossz-gyökér regresszió valósan létező, de **másik** szintetikus executable-lel. Elutasított initial status és preview; az eredeti portok/argumentumok változatlanok.
- A mentés, natív ASA Start, Store-v1, RCON és eredeti Legacy adatok változatlanok; **valós The Island Windows-felhasználói újrateszt továbbra is szükséges**.

## Osztályozott forráskód – rendrakási ellenőrzés

| Forrás / komponens | Állapot | Indok |
| --- | --- | --- |
| `wpf/MainWindow.Sql.cs` | **ELVETVE / TÖRÖLVE #59** | Elavult WPF SQL GUI; a régi WPF csproj már kizárta a fordításból, az Obsidian csproj nem használja |
| `wpf/MainWindow*.cs`, `wpf/App.xaml*`, WPF styles | **LEGACY GUI / NEM SZÁLLÍTOTT** | Obsidian nem fordítja be, de WPF projektet még hivatkoznak történeti integrációs tesztek; nem bizonyított az egész GUI fájlszintű törölhetősége |
| `wpf/Core/LegacyStoreV1*.cs` | **LEGACY – MEGŐRZENDŐ** | Eredeti Legacy Store-v1 session/lock és karaktervédelem |
| `wpf/Core/MariaDb*.cs`, `wpf/Core/OfflineSqlArchive.cs` | **LEGACY TEST-ONLY** | Az Obsidian csproj kizárja; `wpf/Testing/Integration/Integration.csproj` hivatkozza; nem aktuális Manager SQL GUI |
| `wpf/Core/MultiHostClusterBackupArchive.cs`, `wpf/Core/MultiHostSandboxRestoreTransaction.cs` | **MAIN + CI, RÉSZLEGES #53** | #58 offline backup / TEMP restore; éles restore és karakterutazás nincs bizonyítva |
| `wpf/Updater/`, `wpf/Testing/` | **MEGŐRZENDŐ** | Aktív Obsidian külső updater és történeti regressziók |
| `desktop/`, `desktop-ui/` | **AKTÍV** | Kizárólagos React/WebView2 Windows termék; regressziós ellenőrzés kötelező |

## Funkciómátrix – ne fejlesszük újra, ami már a kódban van

| Funkció | Meglévő forrás / PR | Kód státusza | Bizonyíték / hiányzó elfogadás |
| --- | --- | --- | --- |
| Egyetlen Obsidian Windows alkalmazás, React/WebView2/.NET 10, egységes 2.0.0 Preview verzió | [#14](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/14), [#19](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/19), [#28](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/28), `desktop/`, `desktop-ui/` | **MAIN** | Kiadási build/Setup/Portable alapok kódban; valódi felhasználói telepítés, frissítés/rollback minden régi állapotról még külön elfogadás |
| HostRegistry, ClusterRegistry, több független cluster, tagsági előnézet, egyszer használatos apply | [#54](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/54), `wpf/Core/ClusterRegistry.cs`, `desktop/DesktopClusterRegistryBridge.cs` | **MAIN + ZÖLD CI** | Metaadat-regiszter és tagság; a tényleges `-clusterid`/`-ClusterDirOverride` futó ASA-hoz kötése, megosztott tároló és valós átállás külön elfogadás |
| N-host LAN, per-peer hitelesítés, DPAPI titok, távoli státusz csak olvasás | [#55](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/55), `desktop/DesktopLanSettingsBridge.cs`, `wpf/Core/ClusterBackend.cs` | **MAIN + ZÖLD CI** | Szintetikus multi-Manager/loopback teszt; több valódi Windows host és szélsőséges LAN-helyzet elfogadása hiányzik |
| N-host Dashboard, térkép/mapgaléria, hostszűrő, Cluster UI és tagsági kontroll | [#56](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/56), `desktop-ui/src/` | **MAIN + ZÖLD CI** | React build és 66/66 Playwright; tényleges WebView2 UX, 100+ map hosszú futás/erőforrásprofil és vizuális egyezés külön ellenőrzendő |
| Eredeti Legacy Store-v1 2 DONE peer × 5 map védelme, write lock, session/owner és konverziótilalom | [#33](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/33), [#36](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/36), [#38–47](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/47), `LegacyStoreV1*.cs` | **MAIN, RÉSZLEGES ELFOGADÁS** | Történeti Store-v1 őrző kód és regressziók; az eredeti Legacy 5+5 migráció/teljes karakterutazás élő tesztje NINCS |
| ASA API Loader historical ACTIVE indítás, PID/reattach, RCON/SaveWorld/DoExit és védett stop | [#38–47](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/47), `desktop/DesktopHistorical*Bridge.cs` | **MAIN, RÉSZLEGES ELFOGADÁS** | Valódi forrásimplementáció és natív tesztek; minden map/loader/process eset, hosszú futás és teljes ASA runtime elfogadása még hiányzik |
| Hagyományos 5+5 offline map/INI/transfer backup, hash, restore-lab és embedded survivor/tribe index | [#23–27](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/27), [#30–37](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/37), `OfflineAsaClusterArchive.cs`, `OfflineAsaRestoreLab.cs` | **MAIN, RÉSZLEGES ELFOGADÁS** | Történeti tízmapos archívum és negatív mentésellenőrzések; ez NEM az általános N-host archive |
| Általános N-host / többclusteres verziózott backup, TEMP-sandbox replacement/rollback | [#53](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/53), [#58 merge](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/58), [offline backup v1 leírás](OBSIDIAN-MULTI-HOST-BACKUP-V1.md) | **MAIN + ZÖLD PR CI; RÉSZLEGES #53** | Verziózott SHA-256 archive v1 és ownership preflight; a Manager-regiszter 300 host / 520 cluster / 4100 map synthetic clone tesztet teljesít. TEMP-only restore staging, checksummed transaction journal, rollback-másolat és bounded recovery UI a hiányzó célkönyvtárú megszakadt tranzakciót is megtalálja. A Windows Setup útvonalregresszió teszteli a hibás cél elutasítását, szóközös/speciális/hosszabb útvonalat, telepítést, WebView2 smoke-ot és eltávolítást. Élő SaveComplete/Store-writer bizonyíték, production restore és valódi A→B→A travel továbbra is hiányzik.
| SteamCMD maptelepítés, regisztráció, helyi map lifecycle | [#14](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/14), [#39–43](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/43) | **MAIN, RÉSZLEGES ELFOGADÁS** | Valós Windows és ASA telepítési/cancel/restart/reattach teszt még szükséges az összes map-típushoz |
| CurseForge Mod Center, lokális profilok, többmapos műveletek | [#9](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/9), [#14](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/14) | **MAIN, RÉSZLEGES** | Runtime modverzió/függőség, letöltés/telepítés visszaellenőrzése és több host közti biztonságos modszinkron/rollback hiányzik |
| ASA API / ZIP plugin-kezelés | [#14](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/14), [#39–47](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/47) | **MAIN, RÉSZLEGES** | Valós pluginkompatibilitás, frissítéskor adatvédelem és működő maponkénti loader ellenőrzés hiányzik |
| RCON, logok, SaveWorld, helyi folyamatmegfigyelés | [#38–44](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/44) | **MAIN, RÉSZLEGES ELFOGADÁS** | A natív olvasó/író műveletek és guardok léteznek; minden ASA-eset és SaveComplete független bizonyítéka nem teljes |
| Tartós read-only ütemező / leállított világok mentési ütemezése | [#32](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/32), [#35](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/35) | **MAIN, RÉSZLEGES** | Automatizált read-only ellenőrzések/mentés alapok vannak; teljes watchdog, restart, karbantartási és hibakezelési lefedettség hátravan |
| SHA-pinnelt frissítő, külső rollback, Setup/Portable | [#19](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/19), [#20](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/20), [#28–29](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/29) | **MAIN, RÉSZLEGES ELFOGADÁS** | Izolált Windows CI és csomagolás; valódi régi Obsidian 1.4→2.0 frissítési és helyreállítási felhasználói teszt szükséges |
| Jóváhagyott prémium UI / görgetés / modpanelek és összes gomb valódi bekötése | [#14](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/14), [#56](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/56) | **MAIN, RÉSZLEGES ELFOGADÁS** | Vizuális megjelenés és minden egyes tényleges műveleti route ellenőrzése hátravan; fake demó nem elfogadható |
| Helyi Qwen AI + Discord-bot hozzáférés | Későbbi külön igény; nincs elfogadott implementációs PR | **TERVEZETT, NEM KÉSZ** | Erőforráskorlátos, szabályozott AI integráció későbbi önálló specifikáció és teszt |

## Következő egyértelmű fejlesztési sorrend

1. **#53 – a #58-ban beolvasztott N-host offline backup/TEMP restore réteg befejezése**: az elkészült archívumot és tranzakciós visszaállítási réteget NE írd újra. Következő feladat a bizonyított SaveComplete/Store-writer, biztonságos production restore és az A→B→A karakter/tribe/tribute utazás elfogadási rétege; a történeti Store-v1 byteazonosan védett.
2. **Köztes integráció**: minden új PR előtt ütközésvizsgálat a `main`-nel és aktuális Core/LAN/React DTO-kkal. Nincs új HostRegistry, párhuzamos LAN-protokoll vagy új WPF GUI.
3. **P0 célzott hiányok**: tényleges dinamikus cluster indítási paraméterek és többgazdás UNC-konformitás, mentési/restore-fail-closed, Store karakter-/tribe-/tribute-bizonyíték, élő tesztkörnyezet.
4. **P1 célzott hiányok**: CurseForge runtime modszinkron, ASA API plugin lifecycle, teljessé tett ütemező/watchdog, installer/rollback és prémium UI valós E2E.
5. **FINAL**: elkülönített két vagy több valódi Windows tesztgép; tényleges ASA cluster és A→B→A karakter, inventory, hotbar, dinó, tribe, tribute, backup/restore/rollback, hosszú futás. Csak ez után nevezhető stabilnak.

## Tiltások / egységes szerkesztési szabály

- Az élő UK-HOME-01/02, Legacy/ASA/RCON/portok, Store-v1/UNC adatok, valódi mentések változatlanok.
- Nincs automatikus `-converttostore`, migráció, nullás karakterfájl-takarítás, távoli writer-proxy.
- Nincs SQL/MariaDB a Managerben. Külső ASA-plugin MariaDB érintetlen.
- Nincs WPF GUI visszavezetés és nincs párhuzamos verzió.
- **Minden sor mellé forrás/PR + kódállapot + tesztbizonyíték + élő elfogadás külön kerüljön.** A már `MAIN` státuszú funkciókat nem írjuk újra, csak a konkrétan hiányzó útvonalakat egészítjük ki.

A teljes elfogadási lista továbbra is [#12](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/12) és a [kötelező ASA referencia-paritás](https://github.com/Gunter1122cloud/untamed-manager-releases/blob/main/docs/development/ASA-REFERENCE-PROVEN-MANDATORY-20261107.md); ez az állapotmátrix a PR- és kódszintű készültséget követi.
