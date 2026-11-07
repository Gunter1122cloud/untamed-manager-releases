# OBSIDIAN COMMAND — Más ASA Managerekben igazolt funkciók kötelező megvalósítási szabálya

**Elfogadott felhasználói döntés: 2026-11-07.** Érvényes a teljes Obsidian Command projektre, kiemelten a karakter-/tribe-/tribute-/Store-v1 mentésekre és a kétgépes clusterre.

## 1. Kötelező státuszjelölés

**Felhasználói alapelv:** „Ami más ASA Managerben tesztelten működik, annak a mi Managerünkben is működnie KELL. Így jelöljük; ne kezeljük pusztán azért nem működőként, mert nálunk még nincs elvégezve ugyanaz a végponttól végpontig teszt.”

A korábbi „nem működik” / „nem megcsinált” minősítést nem szabad automatikusan alkalmazni **egy másik megoldásban ténylegesen működőnek igazolt, a mi termékünkben kötelező funkcióra**. Ehelyett a kötelező fejlesztési elvárást így kell jelölni:

> **REFERENCIÁVAL IGAZOLT – NÁLUNK IS KÖTELEZŐEN MŰKÖDNIE KELL**

A kötelező elvárás és a mi implementációnk aktuális állapota **két külön mező**. A kötelező elvárás nem lesz „opcionális” vagy „kutatás alatt”, de a saját futtatás/teszt igazolását sem szabad meghamisítani.

### Két független dimenzió minden releváns funkciónál

1. **Követelmény:** `KÖTELEZŐ / REFERENCIÁVAL IGAZOLT` — a külső Managerben bizonyított, átvett viselkedés a mi elkészítendő termékünk része.
2. **Saját műszaki bizonyíték:** `KÓDBAN MEGVAN`, `CI-TESZTELT`, `SAJÁT ASA-VAL IGAZOLT`, vagy `MÉG NINCS IMPLEMENTÁLVA`. Egyik sem változtatja a követelmény kötelező jellegét.

**Nem megengedett:** „Másnál működik, nálunk nem kell”; külső tesztet saját ASA-tesztnek hazudni; a Store-v1 éles írási védelmet pusztán külső referencia miatt feloldani; fájllétezést vagy RCON ACK-ot igazolt karakter-/tribe-/world-visszaállításnak nevezni.

## 2. Konkrét referenciák és nálunk kötelező eredmény

| Forrás és bizonyíték | Referenciával igazolt működés | Nálunk kötelező elvárás | Saját igazolás jelenlegi állapota |
| --- | --- | --- | --- |
| [YARK kétmapos A→B→A valós ASA-kliens teszt (2026-08-13)](https://github.com/gabomarin/yark/blob/main/docs/spikes/22-cluster-live-transfers.md) | Karakter, inventoryként feltöltött tárgyak és dinók átutazása, restart utáni megőrzés két map között **egy Windows hoston** | Teljes karakter-, tribe-, tárgy- és dinóátvitel, A→B→A, restart után is; **nálunk két fizikai gépen, 5+5 maphoz** | `KÓDBAN RÉSZLEGES / SAJÁT ASA-VAL NEM IGAZOLT` |
| [YARK backup-folyamat és player session watcher](https://github.com/gabomarin/yark/blob/main/docs/backups.md) | Maphoz kötött világmentés, session-alapú hagyományos profilmentések, INI-mentés, műveleti napló és visszaállítás | Map-, profil- és INI-mentések, érvényes megőrzéssel és tartós journallal; `-usestore` esetében a beágyazott karakterek a világmentés részei | `KÓDBAN RÉSZLEGES / CI-TESZTELT RÉSZLETEK` |
| [DiFuks BackupService](https://github.com/DiFuks/ark-manager/blob/main/src/ArkManager.Core/Services/Backups/BackupService.cs) | Teljes `ShooterGame/Saved` ZIP és restore előtti védőmentés | Teljes helyi Saved-tartalom megőrzése, védőmentés, ellenőrzött restore | `KÓDBAN RÉSZLEGES`; külső kódból nem igazolt kétgépes restore |
| [ChaosQK BackupManager](https://github.com/ChaosQK/Ark-Server-Manager/blob/main/core/backup_manager.py) | Teljes Saved-mappa mentése és visszahelyezése | Minden releváns fájl megőrzése, de a cél törlése és vak másolása **nem** vehető át | `KÓDBAN RÉSZLEGES` |
| [Linux ASA Manager — stopped backup és clusterútvonal](https://github.com/Zerschranzer/Linux-ASA-Server-Manager/blob/main/ark_instance_manager.sh) | Leállított map TAR-archívuma, cluster-útvonal helyes feloldása | Ellenőrzött leállított mentés, valós cluster-root feloldás, eredeti útvonalak megőrzése | `KÓDBAN RÉSZLEGES`; natív Windows/UNC saját validáció szükséges |
| [Arkparser embedded store](https://github.com/vertyco/arkparser/blob/master/arkparser/files/embedded_store.py), [Kakoen Store reader](https://github.com/Kakoen/ark-sa-save-tools/blob/master/src/main/java/net/kakoen/arksa/savetools/store/TribeAndPlayerData.java), [C# AsaSavegameToolkit tests](https://github.com/flintthatchwood/AsaSavegameToolkit/blob/main/tests/AsaSavegameToolkit.Tests/Plumbing/Records/GameModeCustomBytesTests.cs) | `GameModeCustomBytes` mezőből olvasható beágyazott karakter/tribe identitás; verziófüggő formátum | **Külön tesztmásolaton, csak olvasó** beágyazott karakter- és tribe-leltár, formátumismeretlenségnél is biztonságos hibajelzés | `KÓDBAN ELKÉSZÜLT: OFFLINE, CSUPÁN INDEXSZINTŰ C# LEKÉRDEZŐ / A JÁTÉKBELI TARTALOM NEM IGAZOLT` |

**Fontos bizonyítéki határ:** a táblázat forráskód- vagy dokumentált külső tesztalapját tünteti fel. Nem állítja, hogy minden felsorolt külső rendszerben teljes körűen, többgépes Store-v1-en tesztelték az összes műveletet.

## 3. A mi kötelező karakter/tribe/tribute viselkedésünk

- Mindkét éles gazdagép külön Manager, **5+5 helyileg birtokolt map**; külső peer csak hitelesített read-only állapotot adhat.
- Eredeti Store-v1 `-usestore` kompatibilitás **byte-ról byte-ra**, nincs `-converttostore`, automatikus konverzió, üres régi `.arkprofile` törlése, eredeti session/lock eltávolítása vagy saját adatbázis.
- Mapenkénti mentéshez az adott `.ark` world és valamennyi releváns kísérőfájl; a beágyazott karakter/tribe identitás nem következtethető külön `.arkprofile` számlálásából.
- A **közös tribute/transfer** könyvtár és a Store-v1 koordinációs állományok külön mentendő elemek; nem feltételezhető, hogy a Saved mappán belül vannak.
- A teljes cluster backup mind a 10 mapot, közös transferadatot és Store-v1 metaadatot összehangolt, írásmentes, ellenőrzött állapotban tartalmazza; SHA-256 manifest és tartós műveleti journal.
- A teljes restore csak védőmásolat, pontos forrás-/célazonosítás, kétgépes konzisztencia és megszakításbiztos journal mellett haladhat. Store-v1 egy-map restore nem válhat automatikusan élessé.
- SaveWorld ACK, `.ark` mtime, logmarker és folyamatkilépés külön bizonyíték; játékbeli belépés és tényleges world-visszatöltés külön ellenőrzés.
- Játékbeli A→B→A utazásnál ugyanazon karakter, tribe-tagság/tulajdon, implant, inventory, hotbar, dinók, feltöltött tárgyak és reboot utáni fennmaradás a kötelező elfogadási eredmény.
- Ismeretlen bizonyíték esetén **NEEDS_OPERATOR**, nem automatikus Kill, vak replay, profiljavítás vagy karakterújraírás.
- `-BackupTransferPlayerDatas` külön izolált kompatibilitási teszt tárgya, nem automatikus éles kapcsoló.

## 4. Kötelező implementációs sorrend (nem opcionális kívánságlista)

1. Külsőleg igazolt mentési és utazási viselkedést a fenti státusszal átvenni a termékkövetelményekbe; az egyes kódhelyekhez, tesztekhez és UI-funkciókhoz egyértelműen kötni.
2. A meglévő `desktop/DesktopStoreTravelEvidenceBridge.cs`, `desktop/DesktopWorldBackupBridge.cs`, `desktop/DesktopOfflineAsaArchiveBridge.cs`, valamint `wpf/Core/*` megvalósításokra építeni, nem új Store-formátumot alkotni.
3. Először kizárólag izolált másolatból ellenőrző karakter-/tribe-leltár, majd adatvesztésmentes kétgépes mentés- és restore-koordináció.
4. Integrációs tesztek két elkülönített Windows tesztgépen és valódi ASA klienssel, összes felsorolt játékbeli adattal.
5. Csak utána az éles átvétel engedélyezése; addig Store-v1 élő írásvédelmek megtartása.

A státuszkezelésben **a kötelező funkció elvárt működését jelöljük**, nem a saját teszt hiányát használjuk érvként a funkció kihagyására. A tesztbizonyíték igazmondási követelményét ezzel párhuzamosan változatlanul tartjuk.

## 5. Külön kiadási szabály

GitHub Actions siker ≠ teljes éles kétgépes karakterutazás. Amíg nincs saját végponttól végpontig bizonyíték, az UI és a release notes nem állíthatja `productionCertified=true` vagy `travelVerified=true` értéknek a Store-v1 műveletet. Ez **nem** a kötelező fejlesztési követelmény visszaminősítése.

## 6. Megvalósított natív lépés: offline embedded Store indexszkennelés

Az Obsidian Command natív C# `EmbeddedStoreReadOnlyInspector` és `DesktopEmbeddedStoreBridge` komponense `store.inspectCopy` metódussal, kizárólag **Windows natív fájlválasztón keresztül kiválasztott, helyi .ark MÁS OLATON** dolgozik. Nem fogad el frontend által beadott `path`, `file`, `root` paramétert, nem ír profilt, tribe-ot vagy MariaDB-t, és nem módosít Store-sessiont/lockot.

- ASA SQLite `custom.GameModeCustomBytes` mezőjéből **kis, legfeljebb 64 indexrekordos lapokat** olvas, nem tölti RAM-ba a teljes világot vagy a teljes profilokat.
- A nyilvános arkparser megoldás alapján a `PrimalPlayerData` / `PrimalTribeData` osztály-előtagokhoz indexrekord-jelölteket számol; felülírt (nullázott) kulcsok, azonos indexkulcsok, ismeretlen osztályok és indexhatár-hibák elkülönülnek.
- Ismeretlen tárolóformátum nem válik nullának értelmezett karakterlistává: `UNSUPPORTED_EMBEDDED_STORE_FORMAT`.
- Egy másik alkalmazás esetleges belépési jogosultságát, tribe tulajdonjogát és az A→B→A átvitelt **nem** következteti a világmentés fájljából; a saját valódi ASA-teszt külön kötelező.
- A beépített Windows natív és Playwright teszt csak mesterséges, külön SQLite .ark fixture-rel fut, a két éles gazdagépet nem érinti.
- A Microsoft.Data.Sqlite csomag itt kizárólag az ASA mentésállomány passzív olvasója; **nem** jelenti Manager SQL-adatbázis, DB-üzemeltetés vagy MariaDB-integráció visszavezetését.

Az indexszkenner elfogadását nem szabad összekeverni egy teljes, visszaállítható karakter- és tribe-mentéssel. A kötelező referencia-paritás és a teljes kétgépes restore/utazás a további P0 feladat.
