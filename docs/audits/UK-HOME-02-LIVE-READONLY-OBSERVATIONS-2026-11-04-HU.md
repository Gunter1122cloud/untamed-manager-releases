# UK-HOME-02 – élő read-only megfigyelések

2026-11-04, Europe/Budapest (CET, UTC+1). Gépi időbélyegek UTC-ben.
Feladat: [Issue #4](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/4).
Bázis: PR #3, `3b8b448fb906a02d45615a466ef2900ca40200f0`.
Első leltár 08:11 UTC körül; részletes leltár: 2026-11-04T08:15:40.4772478Z–2026-11-04T08:15:41.2400588Z.

## Lényegi eredmények

Öt valódi Kova→Loader→ASA lánc fut; mind az öt -usestore kapcsolóval, konverzió nélkül.
A profil és a processz modlistájának sorrendje 5/5 esetben egyezik: 28/29 mod, nulla passzív.
Ez argumentumegyezés, nem valamennyi mod tényleges betöltésének ACK-ja.
Négy Kova-processz Electron-folyamatcsalád, nem négy külön Manager bizonyítéka.

Öt helyi Store-rekord ACTIVE, a helyi selected/member/session összerendelés egyezik.
A közös élő peer-állapot, HP helyi adatok, karakter/tribe integritás és kizárólagos írójog nem igazoltak.

Tíz nem paused task van mentve: 1 restart, 5 wipe, 4 backup; utolsó mentett eredmény 8 success, 2 failed.
Az öt sync-error november 3. 06:46 UTC körüli; a taskeredmények november 4-iek.
Nem kezeljük őket azonos idejű aktuális hibáknak; a success felirat nem tartalmi backup-bizonyíték.

**Verzióeltérés:** Running/Auto MariaDB-szolgáltatás binárisa **10.11.19.0**;
Stopped/Disabled másik szolgáltatás binárisa **11.4.13.0**. A terv 11.4-et feltételezett.
Ez ProductVersion-bizonyíték, nem SELECT VERSION(). Nincs automatikus upgrade; előbb engine/séma/jogok tisztázása kell.

Telepített Kova **1.8.3**; **44/44 saját modul** bájtszinten egyezik a privát referenciával.
A belső agent külön verziószáma nem a desktop alkalmazás kiadási verziója.

## E01–E12 bizonyítékkatalógus

| ID | Forrás / módszer | Tény | Erő és korlát |
| --- | --- | --- | --- |
| E01 | Get-Process és egyszeri WMI processzleltár | 5 ASA, 5 Loader, 4 Kova, 1 mariadbd; öt Kova→Loader→ASA lánc | OBSERVED lét/PPID/indulási azonosság; nem RCON_AUTH vagy join-ready |
| E02 | Argumentumok memóriabeli profil-összevetése; helyi sockettáblák | 5/5 mods-sorrend, -usestore, nincs konverzió; 1 UDP és 1–2 TCP listener/map | Nyers argumentum/cím/port/PID nincs exportban. Listener nem bizonyít RCON-ready állapotot. |
| E03 | Nyolc kis JSON, megosztott read/write/delete olvasóhandle | 5 profil/member/ACTIVE/selected; helyi session egyezik | Nem atomikus többfájlos snapshot; közös UNC/HP nem megnyitott, live configHash nem újraszámított. |
| E04 | Maponként legfeljebb 128 KiB ShooterGame tail és aktuális PID-es API-log | 5/5 API-inicializálási szöveg; hét ismert pluginnév említése; ASA start/advertising szöveg | API-log idő/start kapcsolata életszerű, nem teljes eseménykorreláció. Névemlítés nem hibátlan betöltés. |
| E05 | schedules/supervisor/discord JSON összesítés | 10 task, 8/2 eredmény, 5 régi sync-error; 5 autoStart/watchdog; bot disabled, controls konfigurált | Konfiguráció/történeti eredmény; nem most végrehajtott művelet. |
| E06 | Pluginmappák, felső szintű DLL/JSON darabszám és kis backup-policy-k | 10 pluginmappa/map; 4 lastSuccessAt, egy mapon nincs külön policy | Cél más configban lehet: policyhiány nem backup-hiány. Játékosadat-fájlnevek nem exportáltak. |
| E07 | MariaDB service/process/bináris ProductVersion; plugin SQL-kulcs/sémanév-whitelist | Running: 10.11.19.0; disabled: 11.4.13.0. arkpermissions/lethalquests hivatkozás, ArkShop plugin | Nincs SQL-kapcsolat/credentialkiemelés. arkshop DB létezése, táblák/motorok és konzisztencia nem igazolt. |
| E08 | Helyi ASAR-fejléc és 44 saját modul bounded olvasása, asar.unpacked támogatással | 44/44 referencia-SHA egyezés; app verzió 1.8.3; 80 csomagbejegyzés | Implementációazonosság. A kódút végrehajtási sikere INFERRED/UNVERIFIED; eredeti forrás/asset nem kerül repóba. |
| E09 | Korábbi négy privát csomag és korábbi ellenőrzés | Store 8/8, Manager 39/39, 53 mellékelt forrás, 3 exportlog hash egyezett | Történeti bizonyíték, nem mai HP vagy live restore; 561 statikus jelölt nem 561 futott teszt. |
| E10 | Natív ablakleltár/UIA; engedélyezett menüfelirat-keresés | Látható Chromium és tray-host; UIA 9 leszármazott, használható menüfelirat nélkül. PrintWindow kivágat csak dekoratív háttér. | Teljes távoli GUI/fókusz nem megfigyelt. Nem aktiváltunk/kattintottunk. Képek nem publikáltak; takart képernyő első kivágata nem Kova-bizonyíték. |
| E11 | main/PR1/PR3 metadata és teljes diff; helyi Git-blob összevetés | PR1:19, PR3:14 fájl; 139 helyi szöveg, 14 exact +125 csak EOF-újsor; nincs .git/nested projekt | 23 preview/asset nincs helyben. A korábbi 141-es UI-számláló eredeti listája nélkül nem rekonstruálható. |
| E12 | Kis JSON és processzazonosság korlátozott előtte/utána összevetése | Eredmény a redaktált manifestben | Nem világ-/SQL-hashbizonyíték. Az élő rendszer saját írása nem auditmutáció. |

## Maponkénti állapot

| Map | Futó lánc | Modszám | Sorrend | Passzív | Store | API | Élő RCON/join |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Ragnarok_WP | Kova→Loader→ASA | 28 | egyezik | nincs | -usestore, nincs konverzió | aktuális PID-es inicializálási szöveg | UNVERIFIED; nem kérdeztük |
| LostColony_WP | Kova→Loader→ASA | 29 | egyezik | nincs | -usestore, nincs konverzió | aktuális PID-es inicializálási szöveg | UNVERIFIED; nem kérdeztük |
| ScorchedEarth_WP | Kova→Loader→ASA | 28 | egyezik | nincs | -usestore, nincs konverzió | aktuális PID-es inicializálási szöveg | UNVERIFIED; nem kérdeztük |
| TheCenter_WP | Kova→Loader→ASA | 28 | egyezik | nincs | -usestore, nincs konverzió | aktuális PID-es inicializálási szöveg | UNVERIFIED; nem kérdeztük |
| Aberration_WP | Kova→Loader→ASA | 29 | egyezik | nincs | -usestore, nincs konverzió | aktuális PID-es inicializálási szöveg | UNVERIFIED; nem kérdeztük |

## Fontos működési szabályok a telepített kódból – INFERRED E08

- `agent/owned-process.mjs:1–34`: PID/start/EXE/parancslenyomat, sajátprocessz-visszacsatlakozás, külső folyamat elkülönítése.
- `agent/lifecycle.mjs:3–25`: close-ra várás; induláskor RCON-probe, tízperces határ.
- `agent/agent.mjs:163–171`: SaveWorld/DoExit, recover-fallback Kill; ez nem külön hiteles SaveComplete.
- `agent/server-health.mjs:1–16`: 75 s frissesség, 3 hiba legalább 60 s alatt unresponsive; visszatéréshez 2 siker.
- `supervisor.cjs:1–35`, `scheduler.cjs:1–43`: indulási türelem, soros retry-limit, maintenance, interrupted/missed futás vak ismétlése nélkül.
- `agent/shared-store.mjs:1–180`: tartós local lock, két peer/session/hash/selected; nincs TTL-es vak locktörlés.
- `agent/backups.mjs:1–130`: manifest/hash/manual/protective/schedule, staging/rename/marker; nem bizonyít 10-map/SQL konzisztenciát.
- `agent/asa-api.mjs:29–60,109–127`: install/recover/loader-proxy konfliktus/runtime; `api-packages` korlátok.
- `main.cjs:31,49,92–94`: távoli licencoldal/renderer, Electron ablak; natív menü újracsatlakozás/frissítés/kilépés/névjegy. Nem kattintottuk végig.
- `updater.cjs:8–39`: aláírt manifest, darabolt hash, busy gate; update nem indult.

## Kihagyott ellenőrzések / határok

1. Nincs saját Manager/tesztbuild/FakeAsa/tesztfuttatás ezen a gépen.
2. Nincs RCON (ListPlayers sem), SteamCMD, /probe, /backups/test, mod/plugin toggle, backup/restore vagy adminparancs. A látszólagos check/probe művelet is írhat cache-t/fájlt.
3. Nincs közös UNC/HP kapcsolat vagy élő közös markerlekérdezés; nincs locktörlés/session/konverzió.
4. Igazolt meglévő SQL read-only account nem állt rendelkezésre; plugincredentialt nem használtunk új kapcsolathoz. Táblák/jogok/motorok UNVERIFIED.
5. Teljes GUI és modkattintási hiba: nincs biztonságos szemantikus vezérlőfa; éles modváltoztatással nem reprodukáljuk. Statikus WPF-vizsgálat és izolált tesztterv készült.
6. Nincs nagy map-/karakter-/backup-rekurzió/hash. LevelDB/session storage konzisztenciát nem állítunk.

## Hatás bizonyíthatósága

Az élő fájlokat FileAccess.Read + FileShare.ReadWrite/Delete móddal olvastuk, a Kova írásait nem zártuk ki.
A tesztmásolatra való VaultReadSnapshotot az élő auditban nem használtuk. Csak a fejlesztési munkamappába írtunk jelentést.
Átmeneti CPU/I/O-költség van; matematikailag nulla terhelést nem állítunk.
A nem mutáló parancsok, processzazonosság és kis fájlösszevetés az audit beavatkozásmentességét támasztja alá; a futó szerverek saját írásait nem igazolja változatlannak.
