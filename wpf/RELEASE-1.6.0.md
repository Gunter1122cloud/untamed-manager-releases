# Untamed Kingdom ASA Manager v1.6.0 – Professional Operations / TEST PREVIEW

**Éles ASA/Legacy-helyettesítésre NEM jogosít.** Windows x64, C#/.NET 10, natív WPF Neon Kingdom felület. A fejlesztés alatt az UK-HOME-01/02 gépeket, a karakteradatokat, az éles Legacy Store-t és a valódi MariaDB-t nem módosítottuk.

## A v1.6.0-ban ténylegesen hozzáadott funkciók

### 1. Legacy kétgépes történeti audit (valódi helyi fájlolvasás)

- A 2026.11.02-én kapott, különböző HP és Ryzen `cluster-store.json` **titokszűrt auditexport** formátumainak read-only dekódolása.
- Két külön forrás SHA-256 ellenőrzése; mindkét hoston öt, összesen tíz egyedi map, helyi profile és root; két peer `done` státusz, session/configHash/path/selected ellenőrzési jelzők; operation/running/mutation.lock marker vizsgálat.
- Eredmény a WPF Data Centerben, saját gépen másolt JSON-fájlok kiválasztásával.
- Minden eredmény **történeti auditadat**: nem live peer-lekérdezés, nem Legacy shared-store-v1 parser és nem karakter/transfer átjárhatósági igazolás. Soha nincs Store-írás, session-létrehozás, konverzió vagy lock-törlés.

### 2. Valódi Windows-folyamatot használó lifecycle teszt

- Az `IsolatedFakeAsaSession` ténylegesen elindít egy elkülönített `FakeAsa.exe` gyermekfolyamatot, fájl-alapú tokenes command/ack csatornán SaveWorld → DoExit → kilépés → Restart sorrendet próbál.
- Az `SafeLifecycle.Stop` bizonyíték hiányában **nem küld DoExit-et**, és a FakeAsa ellenpélda élőben marad.
- Csak a GitHub Windows CI `Untamed-Integration-*` TEMP mappájában működhet; kifejezetten visszautasítja az ArkAscendedServer.exe-t és más végrehajtható programot.
- **Fontos:** a FakeAsa „SaveProof” jelzés nem ASA mentésigazolás. A valódi production Start/Stop/Restart/Kill és az automatikus Recovery továbbra is zárolt. ASA API SaveComplete hook megléte és megbízhatósága még külön kutatási/tesztelési kérdés.

### 3. Kétgépes OFFLINE backup archívum, tényleges fájlmásolással

- 5 már meglévő HP + 5 már meglévő Ryzen mapmentés, valamint `arkshop`, `arkpermissions`, `lethalquests` **korábban exportált SQL csomagok** egyetlen, helyi mentési csomagba másolása.
- Négy mappás WPF felület: HP, Ryzen, SQL offline gyökér, külön célmappa; az alkönyvtárak Untamed `manifest.json` / `data` / `complete` csomagok.
- Eredeti csomagok bemeneti és másolás utáni SHA-256 ellenőrzése, méret- és darabszám-ellenőrzés, 10+3 feltétel, tárhelytartalék, teljes gyökér- és reparsepoint-korlátok.
- Egyedi batch ID, `cluster-index.json` + SHA-256, állapotjelző és `.incomplete` karantén; megszakadt folyamat nem jelent kész backupot. Újraellenőrző WPF gomb.
- **Nem állítható:** live backup-konzekvens, Legacy Store, transfer és SQL tranzakcióban egy időpontra kerültek. A csomag megfelelő **offline fájlépségi archívum**, kizárólag külön Windows tesztgépen előállított adatokból.

### 4. Hivatalos ASA API kiadásellenőrző (valós HTTP, read-only)

- A `https://api.github.com/repos/ArkServerApi/AsaApi/releases/latest` hivatalos upstream lekérdezése a WPF Plugin Centerből.
- Tag, kiadásdátum, engedélyezett release asset metadata és opcionális hivatalos SHA-256 digest jelenik meg; nem dokumentált asset/eltérített domain visszautasítva.
- Ez NEM telepítő és nem állít API-betöltés vagy plugin-kompatibilitási bizonyítékot. A meglévő telepítési/PE64/rollback komponensek továbbfejlesztendők.

### 5. Build, dokumentáció, változatkezelés

- v1.6.0 verzió a WPF-ben, Inno Setupban, Updater E2E tesztben és Windows CI-ben.
- CI képernyőkép-feltöltése már nem ír új commitokat a főágra, így a tesztelt SHA egyértelmű.
- Egy kizárólag szándékos `Release v1.6.0 TEST preview` kezdetű commit után, az összes CI teszt sikerével engedélyezett GitHub prerelease publikáció, soha nem Stable.

## A v1.5.0-tól megmaradó működő modulok

Neon Kingdom WPF, tálcaikon, Updater és Supervisor, saját mapregisztráció és read-only LAN peer státusz, CPU/RAM idővonal, aktív/passzív/tiltott modlista, CurseForge read-only metaadatok, PE64 API leltár, SHA-védett plugin ZIP + DLL rollback, JSON/INI szerkesztés, leállított helyi mapok SHA ellenőrzött fájlbackupja, Offline SQL és Legacy szintetikus tesztek, FakeSteamCMD, tartós read-only ütemező, RCON előnézet és FakeRcon egyszeri parancsnapló.

## Amit továbbra SEM állítunk működő éles funkciónak

| Terület | Hiányzó valódi bizonyítás |
|---|---|
| Production ASA ServerStart/SaveWorld/DoExit/Restart/Kill | Izolált **valódi** ASA folyamat, SaveWorld-befejezés/API-jel, visszakeresett folyamatidentitás, Player/Tribe sértetlenség |
| LegacyCluster 1:1 Store | Valódi `.legacy-store-v1` fájlok/session/peer/lock formátum és kétirányú karaktertranszfer tesztmásolatokon |
| MariaDB | Valós MariaDB verzió/jogok/táblamotorok, `mariadb-dump` és izolált restore + engine-/row-count ellenőrzés |
| Élő kétgépes backup/restore | Azonos konzisztenciahatár mindkét hoston/DB-ben/Store-ban; restore marker, teljes rollback és jogosultság |
| SteamCMD/ASA API/Mod | Staging, a `2430930` éles letöltése, valódi ASA Stop/Restart gate és jóváhagyott CurseForge distribution entitlement |
| Watchdog/WAN/Wild Dino Wipe/RCON admin írás | Engedélyezett helyi ASA vezérlés, at-most-once command, játékosértesítés/ACK, teljes audit, Legacy exclusivity |
| Discord/mobil/UPS és licenc | Külön opt-in, hitelesítés, jogosultság, VPN és hardveres teszt; nem szerepelnek késznek minősítve |
| Code signing | Nincs hitelesített kiadói tanúsítvány; SHA-256 nem azonos Authenticode aláírással |

## Valós források és technikai döntés

- [ARK wiki dedikált ASA szerver / SteamCMD App 2430930](https://ark.wiki.gg/wiki/Server_Setup)
- [ASA szerver leállítás / Backup (közösségi leírás)](https://wikily.gg/ark-survival-ascended/guides/dedicated-server)
- [MariaDB dokumentált --single-transaction, InnoDB-only](https://mariadb.com/docs/server/clients-and-utilities/backup-restore-and-import-clients/mariadb-dump)
- [ArkServerApi hivatalos Releases](https://github.com/ArkServerApi/AsaApi/releases)
- [Legacy kétgépes eredeti auditarchívum](../wpf/RESEARCH-2026-11-03-UNWIRED-FEATURES.md) – belső, a felhasználó által adott dokumentációból; a teljes nyers private ZIP és titkok nincsenek a GitHubon.

## Kötelező elfogadás

Az új buildet kizárólag sikeres Windows GitHub Actions CI után adjuk ki **TEST PREVIEW** formában, külön Windows tesztgépre. A CI tartalmazza a régi FakeAsa/RCON/Plugin/Backup/Updater/GUI teszteket, plusz kétgépes offline archívum összegyűjtést, Legacy-audit hibák injektálását, valós FakeAsa SaveWorld/DoExit/Restart folyamatot és hivatalos API release JSON parszer-tesztet. A production feloldás NEM tartozik ehhez a release-hez.
