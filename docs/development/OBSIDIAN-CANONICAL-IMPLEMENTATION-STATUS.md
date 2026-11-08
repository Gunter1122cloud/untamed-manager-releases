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

**Aktuális integráció:** PR #54–57 és #58 már a `main` része. A #58 ellenőrzött `caf8c6a` headjét a sikeres [Windows CI #1565](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42479884435) után [`eb3f4f6`](https://github.com/Gunter1122cloud/untamed-manager-releases/commit/eb3f4f671bff8b1810c806d63fdaaa139ef2bae7) merge commit integrálta. A korábbi 46/48 PR-os számok időbélyegzett pillanatképek; nem aktuális globális összesítés. A nyolc korábban lezárt, nem külön merge-elt PR történetét nem szabad ismételten integrálni.

## 2026-11-08 #58 utóintegrációs bizonyíték és korlátok

- [PR #58](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/58): a korábbi Draft állapotból Ready for Review lett, majd GitHub `merged=true`; merge SHA: `eb3f4f671bff8b1810c806d63fdaaa139ef2bae7`.
- Pontosan 21 módosított fájl; az archive, sandbox restore és bridge UI forrása integrálódott. A `LegacyStoreV1*.cs` fájlok nem szerepeltek a #58 módosításai között.
- [PR head Windows CI #1565](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42479884435): `success`, Windows preview, Core, natív bridge, Playwright, Setup és WebView2 ellenőrzések sikeresek. **A merge utáni `main` [Windows CI #1566](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/42482209532) `SUCCESS` a pontos `eb3f4f6` merge SHA-n.**
- A #58 **offline, előzetesen elkészített másolatokból** dolgozik, csak TEMP sandboxba állít vissza; a production restore és az élő Store-v1/karakterutazás NINCS kész.
- Rendrakáskor a régi WPF GUI aktív kiadási útvonalait külön függőségvizsgálattal kell eltávolítani; a shared `wpf/Core/`, `wpf/Updater/`, Store-v1 és tesztek megőrzendők. A bizonyítatlanul halottnak gondolt kód NEM törölhető.

## Retired source cleanup – elkülönített PR #59

A [#59](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/59) külön ágon törli a `wpf/MainWindow.Sql.cs` elvetett WPF háromadatbázisos SQL GUI-t. A fájlt a `wpf/UntamedManager.csproj` már korábban kizárta a fordításból; a `desktop/UntamedDesktop.csproj` kizárólag a közös `wpf/Core/*.cs` részt használja. A törlés **még nem MAIN**, amíg #59 Windows CI és merge be nem fejeződik. A történeti SQL regressziós forrás, Store-v1 Core, updater és WPF referenciatesztek megmaradnak. Ez nem jelenti a teljes korábbi WPF GUI eltávolítását: annak a történeti tesztprojektekre és projekthivatkozásokra gyakorolt hatása további felülvizsgálatot igényel.

## Funkciómátrix – ne fejlesszük újra, ami már a kódban van

| Funkció | Meglévő forrás / PR | Kód státusza | Bizonyíték / hiányzó elfogadás |
| --- | --- | --- | --- |
| Egyetlen Obsidian Windows alkalmazás, React/WebView2/.NET 10, egységes 2.0.0 Preview verzió | [#14](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/14), [#19](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/19), [#28](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/28), `desktop/`, `desktop-ui/` | **MAIN** | Kiadási build/Setup/Portable alapok kódban; valódi felhasználói telepítés, frissítés/rollback minden régi állapotról még külön elfogadás |
| HostRegistry, ClusterRegistry, több független cluster, tagsági előnézet, egyszer használatos apply | [#54](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/54), `wpf/Core/ClusterRegistry.cs`, `desktop/DesktopClusterRegistryBridge.cs` | **MAIN + ZÖLD CI** | Metaadat-regiszter és tagság; a tényleges `-clusterid`/`-ClusterDirOverride` futó ASA-hoz kötése, megosztott tároló és valós átállás külön elfogadás |
| N-host LAN, per-peer hitelesítés, DPAPI titok, távoli státusz csak olvasás | [#55](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/55), `desktop/DesktopLanSettingsBridge.cs`, `wpf/Core/ClusterBackend.cs` | **MAIN + ZÖLD CI** | Szintetikus multi-Manager/loopback teszt; több valódi Windows host és szélsőséges LAN-helyzet elfogadása hiányzik |
| N-host Dashboard, térkép/mapgaléria, hostszűrő, Cluster UI és tagsági kontroll | [#56](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/56), `desktop-ui/src/` | **MAIN + ZÖLD CI** | React build és 66/66 Playwright; tényleges WebView2 UX, 100+ map hosszú futás/erőforrásprofil és vizuális egyezés külön ellenőrzendő |
| Eredeti Kova Store-v1 2 DONE peer × 5 map védelme, write lock, session/owner és konverziótilalom | [#33](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/33), [#36](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/36), [#38–47](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/47), `LegacyStoreV1*.cs` | **MAIN, RÉSZLEGES ELFOGADÁS** | Történeti Store-v1 őrző kód és regressziók; az eredeti Kova 5+5 migráció/teljes karakterutazás élő tesztje NINCS |
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

- Az élő UK-HOME-01/02, Kova/ASA/RCON/portok, Store-v1/UNC adatok, valódi mentések változatlanok.
- Nincs automatikus `-converttostore`, migráció, nullás karakterfájl-takarítás, távoli writer-proxy.
- Nincs SQL/MariaDB a Managerben. Külső ASA-plugin MariaDB érintetlen.
- Nincs WPF GUI visszavezetés és nincs párhuzamos verzió.
- **Minden sor mellé forrás/PR + kódállapot + tesztbizonyíték + élő elfogadás külön kerüljön.** A már `MAIN` státuszú funkciókat nem írjuk újra, csak a konkrétan hiányzó útvonalakat egészítjük ki.

A teljes elfogadási lista továbbra is [#12](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/12) és a [kötelező ASA referencia-paritás](https://github.com/Gunter1122cloud/untamed-manager-releases/blob/main/docs/development/ASA-REFERENCE-PROVEN-MANDATORY-20261107.md); ez az állapotmátrix a PR- és kódszintű készültséget követi.
