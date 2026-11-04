# P0 Store-v1: másolatokon végzett kompatibilitási fejlesztés

Kiinduló commit: `62dea92ce52e8270e57833fc976356ce199026d5`, PR #1.
Ág: `codex/p0-store-v1-parity`; célág a PR #1 fejlesztési ága.
Ez az első P0 fejlesztési szakasz, nem a teljes master roadmap lezárása.

## Éles gépen alkalmazott korlátok

Az éles Kova/ASA/MariaDB nem tesztcél. Nincs folyamatvezérlés, RCON-küldés,
SQL-kapcsolat, megosztáselérés, telepítés, Store-írás vagy locktörlés.
A helyi teszt csak a felhasználó korábban elkészített ZIP-jeit olvassa.
Hordozható .NET SDK a külön munkamappában; nincs rendszertelepítő.
WPF, Supervisor, telepítő és Updater futtatása kizárólag GitHub Windows runneren.
A privát ZIP-ek és tartalmuk nem kerülnek commitba vagy CI-artifactba.

## Ténylegesen azonosított és javított hibák

| Korábbi viselkedés | Javítás és bizonyíték |
|---|---|
| A Vault ZIP pontosan nyolc adatfájlt és rögzített Ryzen-könyvtárat fogadott. | Manifesttel lefedett, egy- vagy kéthostos másolat; hostazonosítás device ID alapján. |
| A mappaolvasó csak operation/peer/device/lock adatokat nézett; hiányzó helyi Store mellett is egyezést jelzett. | Mindkét host négy helyi fájlja, ACTIVE mapok, root/map/session/backup összerendelés szükséges a teljes egyezéshez. |
| A peer config és local lock teljes tagsága eltérhetett. | Teljes, sorrendtartó config-egyezés; helyi cluster config hashének ellenőrzése. |
| A hostokon át ismételt mapazonosító vagy gyökér nem volt kizárva. | Tíz egyedi mapnév, profilazonosító és gyökér; kiválasztás és tagság összevetése. |
| A pontos nyolcfájlos ZIP-séma miatt a koordinációs markerállapotok nem voltak érdemben jelenthetők. | running/cancelled/attempted/mutation.lock, fájl- és üres könyvtárzár felismerése. |
| A fájl külön parse- és hash-olvasás között megváltozhatott; történeti profilolvasó újranyitotta a ZIP-et. | Írást/törlést kizáró, az ellenőrzés végéig tartott Windows fájlhandle; memóriabeli ZIP; handle-útvonal, reparse és hardlink ellenőrzés. |
| JSON ismételt kulcs, ZIP útvonalalias és fájl/könyvtár ütközés nem volt egységesen tiltva. | Közös snapshot/manifest olvasó, korlátos bejegyzésolvasás, link- és ütközésellenőrzés. |
| A hashképzés minden objektumkulcsnál kizárólag a szöveges sorrendet követte. | JS array-index kulcsok numerikus sorrendje; a normál stringkulcsok eredeti sorrendje megmarad. |
| A WPF a részleges mintát is ellenőrzött archívumnak nevezte; nyers kivételszöveget írt ki. | Külön részleges/teljes történeti státusz, hostonkénti session/hash/lock/ACTIVE, típusos hibakódok, útvonal- és azonosítómentes jelentés. |

## Meglévő komponensek és bekötés

| Komponens | Állapot és hívó | Ellenőrzés |
|---|---|---|
| StoreV1Fingerprint | IMPLEMENTED; közös validator és Migration Vault | JS mezősorrend, UTF-8, indexkulcs, eltérő hash; ismeretlen numerikus config fail-closed |
| ClusterVaultArchiveInspector | READ_ONLY; meglévő WPF Vault ZIP oldal | Szintetikus teljes 5+5 és részleges minták, privát Store ZIP helyben |
| KovaStoreV1CopyReader | READ_ONLY; meglévő közös munkamenet WPF oldal | Ugyanaz az InspectFiles validator; mindkét helyi host szükséges |
| UntamedManagerArchiveInspector | READ_ONLY; Migration Vault | Közös biztonságos ZIP-olvasó és Store-validator; régi diagnosztikai mezők megmaradnak |
| HistoricalLaunchProfileReader | READ_ONLY; történeti profil-előnézet | Az ellenőrzés és profilolvasás idején ugyanaz a zárolt forrás; nincs import vagy indítás |
| Supervisor | A P0 Store-validatornak nincs automatikus hívója | Szándékosan nincs új éles megfigyelés vagy művelet |
| ReleaseCapabilityGate | BLOCKED az éles lifecycle/Store/SQL műveletekre | A kódja változatlan; metadataegyezés sem oldja fel |

## Privát referenciák, helyben ellenőrizve

| Csomag | Újra ellenőrzött manifest |
|---|---|
| Belső működés | 53 mellékelt fájl hash egyezik; a 80 leltározottból 27 nincs a ZIP-ben |
| Indítás/leállítás napló | 3 exportfájl hash egyezik |
| Store-v1 | 8/8 hash egyezik; .NET validator: 10 map, két peer, egyező hash/session, egy helyi host |
| Manager pillanatkép | 39/39 hash egyezik; .NET validator: 5 helyi és 10 közös map, importjog nélkül |

A két üzemi mintának a .NET olvasás előtti/utáni ZIP SHA-256 értéke egyezik.
A minták nem bizonyítják a másik host teljes helyi állapotát, az élő peer állapotot,
a karakterek sértetlenségét, a mentések tartalmát vagy a visszaállíthatóságot.
Az önmagában a csomagban lévő manifest sérülést jelezhet, a készítő hitelességét nem igazolja.

## Tesztelés

Külön, csomagfüggőség nélküli tesztprojekt: `wpf/Testing/StoreV1/StoreV1.csproj`.
Ugyanazokat a Core fájlokat fordítja, nincs második Store-motor.
Futtatja a meglévő ClusterVaultTests és ManagerMigrationArchiveTests teszteket is.

```powershell
dotnet run --project wpf/Testing/StoreV1/StoreV1.csproj -c Release
# Kizárólag helyben, opcionálisan a privát másolatok abszolút útvonalaival:
dotnet run --project wpf/Testing/StoreV1/StoreV1.csproj -c Release -- <Store-ZIP> <Manager-ZIP>
```

Helyi eredmény: 82 új szintetikus állítás + 4 privát referenciaállítás sikeres;
ezen felül a két meglévő archívumteszt-csoport is lefutott.
Az új teljes Windows build eredményét kizárólag a pontos commit CI-runja igazolja.
A workflow codex ágakon is fut; release továbbra is csak a korábbi szigorú main/push
és szándékos preview commit feltétellel történhet. Ez a PR nem publikál kiadást.

## Nyitott P0 korlátok

- Nincs VERIFIED_ON_ISOLATED_ASA vagy PRODUCTION_ACCEPTED státusz.
- Hiányzik a második host teljes történeti helyi metadatareferenciája.
- A tesztek félbeszakadt koordinációs/transfer-metaadatot modelleznek; nem valódi karaktertranszfert.
- A fájlhandle egyedi fájlokat véd; több gép élő állapotának atomikus pillanatképét nem garantálja.
- A mappák sorrendjéből nem következtetünk HP/Ryzen szerepre: a tárolt peerazonosság számít.
- Az írójog kizárása jelenleg teljes tiltás; nincs új éles, két Manageres writer lease implementáció.
- Mentésazonosító-egyezés nem egyenlő a világ/SQL/karaktermentés tartalmának ellenőrzésével.
- Nincs Store-írás, konverzió, automatikus session vagy karakteradat-helyreállítás.
- Következő elkülönített szakasz: teljes kétoldali privát referencia, metadata restore-próba,
  majd harmadik gépes valódi ASA SaveComplete/transfer és konzisztens backup/restore tesztterv.

A masterben felsorolt további dokumentumok önálló bemenetként nem voltak jelen:
Kova-Teljes-Dokumentacio ZIP, TELJES-KEZELOFELULET-TERV-HU.md,
Kutatas-es-Adatigeny, Belso_Mukodes_Forras_Audit, Megvalositasi_Terv_Vegleges,
Teljes_61_Muvelet_Fejesztesi_Lefedettseg. A repó korábbi összefoglalói nem helyettesítik ezeket.
A 61 művelet és 44 modul teljes, végrehajtással igazolt auditja nincs ezzel a P0 PR-rel lezárva.
