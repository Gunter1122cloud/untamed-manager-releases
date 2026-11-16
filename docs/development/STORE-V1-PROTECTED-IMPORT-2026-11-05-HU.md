# Store-v1 védett átvétel — megvalósított első szakasz (2026-11-05)

## Jóváhagyott üzemi elv

A működő, két Windows-gépes Untamed Kingdom / LegacyCluster karakterutazás és Store-v1 fájlformátuma változatlan. Nincs új `-converttostore`, automatikus Store-migráció, új session, reinitializálás, locktörlés, automatikus karakterjavítás vagy 0 bájtos `.arkprofile` törlés. A MariaDB 11.4.13 LTS külön szolgáltatás; nem része a Manager motorjának. Az éles két szervergéphez nem nyúltunk.

A felhasználó az éles átállásnál saját maga állítja le a tíz mapot, majd hostonként külön veszi fel a már meglévő helyi telepítéseket. Ez **későbbi, külön jóváhagyott művelet**, nem része a jelenlegi Preview tesztjének.

## Mit tartalmaz ténylegesen a privát Legacy Manager ZIP?

A korábban feltöltött `LegacyManager-Fontos.zip` privát mintájának (csak lokális olvasás, nem GitHub artifact) ellenőrzése szerint a `Legacy/launch-profiles-v1.json` egy map sorához `id`, `root`, valamint a `launch` objektumban `map`, `gamePort`, `queryPort`, `mods`, `passiveMods` tartozik. A `Legacy/cluster-store-v1.json` a megfelelő aktív mapazonosítókat, rootokat és sessionhivatkozást tartalmazza. A korábbi `HistoricalLaunchProfileReader` a 39 manifesttel ellenőrzött állományhoz kötötte az olvasást.

**A teljes, eredeti parancssor nincs ezekben a mezőkben.** Nem lehet automatikusan kitalálni az RCON-t, sessionnevet, loader opciókat, további ASA flageket vagy a kétgépes közös Store írójogát. A történeti ZIP *bizonyíték* a mapazonosítóról, portokról és modokról, de nem ad éles indítási jogosultságot.

## Bevezetett kód

- `DesktopHistoricalStoreImportBridge.cs` — három explicit engedélyezett WebView2 metódus:
  - `historical.list`: ellenőrzött privát, helyi ZIP-ből öt map azonosítóinak listázása;
  - `historical.preview`: pontos map GUID, eredeti telepítési gyökér, Game/Query port, aktív/passzív modlista és a jelenlegi EXE összevetése; futó vagy nem igazoltan leállított map tiltása; fájl/ZIP hash ellenőrzés;
  - `historical.register`: 7 perces, egyszer használható megerősítő token; újbóli hash/telepítés/folyamat/azonosság ellenőrzése; kizárólag a Manager saját `cluster-config.json` fájljában létrejövő zárolt maprekord.
- `LocalMap.HistoricalStoreProtected` és archivált forráshash/mapkulcs/modlista a Manager saját profiljában. A régi Store-fájlokat nem írjuk, import közben az ASA-t nem indítjuk.
- `ClusterService.Check` / `LaunchLocal`: történeti mapon a normál indítás és a FakeAsa rövid útvonala is zárolt.
- `DesktopInitialMapSetupBridge`: a már meglévő Store-map nem válhat friss, üres első indítási profillá.
- `DesktopModDraftBridge` és `DesktopIniBridge`: történeti mapra nem engedünk konfigurációírást a teljes hitelesítésig.
- `DesktopLocalMapRegistryBridge`: meglévő SavedArks/SavedArksLocal világadattal rendelkező telepítést nem lehet üres generikus mapként regisztrálni.
- `HistoricalStoreImport.tsx`: külön Store-v1 importálási kezelőfelület és egyértelmű „indítás zárolva” visszajelzés. A SteamCMD friss telepítése és a meglévő Legacy-map átvétele külön művelet marad.

## Bizonyíték és tesztelési határ

A natív szintetikus tesztgenerátor 39 állományos, manifestes Legacy Manager fixture-t készít, amelyben az első mapgyökér egy elkülönített ideiglenes Windows-könyvtár. Teszteli az előnézet változatlanságát, a jóváhagyás kötelező voltát, az eredeti map GUID/port/mod megőrzését, a ZIP és a világfájl SHA-256 egyezését, a tiltott Store-indítást és az INI/mod-írás elleni védelmet. A Playwright külön ellenőrzi a kétlépcsős, jóváhagyásos UI-t.

Ezek **nem valódi ASA karakterutazási tesztek**. Az élő Store-v1 writer ownership, az eltérő UNC aliasok, a teljes eredeti launch command line, a kétgépes és megállás utáni transferkonzisztencia, SaveWorld/DoExit, mentési folyamat és helyreállítás továbbra is NEM IGAZOLT.

A jelenlegi import eredménye szándékosan *zárolt* map: nem állítja vissza a teljes régi indítási sort és nem engedi elindítani a szervert. Teljes indítási profilhoz másik, független, hitelesített bizonyítékforrás és izolált valódi ASA-teszt szükséges.

## Következő végrehajtandó szakasz

1. A Legacy helyi utolsó tényleges indítási konfigurációjának (map/Store/cluster/RCON/API loader és összes mod flag) megbízható, titokszűrt, *teljes* olvasója, javasolt parancssor visszaalkotása nélkül. Az eredeti privát rekordokat nem commitoljuk.
2. A két host négy-négy helyi metadatafájljának, a közös operation/peer/attempted/lock állapotoknak, a történeti hash-eknek és az eltérő UNC aliasoknak **olvasásalapú** összefésülése. Ismeretlen alias vagy peer hiány esetén fail-closed.
3. Teljes eredeti argumentum- és binárisellenőrzés, független, *izolált* kétgépes ASA utazási és visszaút tesztek, beleértve az adatvesztési/0 KB-os regressziót.
4. Csak ezután dönthető el külön, pontosan körülhatárolt éles indítási jogosultság. `ReleaseCapabilityGate` globális feloldása tilos.

**Magyarul: a jelenlegi első szakasz a megőrzést és a rossz migráció megakadályozását fejleszti; még nem bizonyított karakterutazási helyettesítője a Kovának.**

## 2026-11-05: kétgépes offline Store-bizonyítékok beolvasása

A `historical.pairedEvidence` csak a két **helyi másolatként** megadott ZIP-et olvassa:
a közös koordináció manifestelt archívumát és a másik host pontosan négy
Store-v1 metaadatát tartalmazó ZIP-et. Az eredeti `ClusterVaultArchiveInspector.InspectPairedCopies`
validator két peer, tíz map, eredeti session, configHash, local lock és active
rekord szerint értékel. Az eredmények egyszerű jelzők és ellenőrzési kódok,
nem tartalmaznak eredeti privát elérési utat vagy azonosítót.

React felületen külön „Két host Store-adatainak összevetése” szakasz nyílik.
A teljes történeti metadata-egyezés **nem oldja fel a `ReleaseCapabilityGate`-et**,
nem ellenőrzi az élő SMB-írót és nem bizonyít valódi ASA karakterutazást.
A C# szintetikus pozitív 8+4 és negatív hiányzó-lock teszt az archívumok
SHA-256 változatlanságát és az éles írás tiltását ellenőrzi.

## 2026-11-05: eredeti ASA indítási sor szöveges összevetése (új)

A `historical.launchEvidence` szigorúan olvasási célú WebView2 metódus a
manifesttel igazolt, archivált map GUID + mapkulcs + Game/Query port +
aktív modlista mellett elemzi az **operátor által megadott** eredeti ASA
indítási szöveget. Nem keres a hálózaton Legacy-konfigurációt, nem fogad
el ismeretlen fájlrendszer-útvonalat, nem generál indítási parancsot,
nem módosít a Manager-profilon sem, és nem ad semmilyen írási jogosultságot.

Az ASA közösségi wiki szerint a karaktertárolási `-usestore` kapcsoló
a korábbi profilforma működését érinti; a `-converttostore` migrációs
paraméter, ezért újraalkalmazása tilos a már működő Store-v1 klaszteren.
A `-clusterid`, `-ClusterDirOverride`, `-mods` sorrend, a mapkulcs
és a portok külön vizsgálat alá esnek. Hiányzó, ismételt vagy eltérő
érték nem válhat csendben feltételezett alapértelmezéssé.

A kezelőfelület kizárólag állapotértékeket és nem érzékeny hibakódokat
kap vissza. Nincs eredeti parancssor, sessionnév, clusterazonosító,
transferútvonal vagy jelszó a válaszban. Az input a felületen ideiglenes,
nem kerül tartós mentésre, de a felhasználónak továbbra is titkos adatként
kell kezelnie az eredeti indítási sort.

**Bizonyítékhatár:** az operátor által bemásolt szöveg nem hitelesített
eredeti folyamatnapló. Az összes eredmény
`sourceAuthenticated=false`, `fullLaunchArgumentsVerified=false`,
`peerWriterVerified=false`, `serverStartAllowed=false` és
`productionUseAllowed=false`. Ez szándékos, nem hiányos
engedélyezőfunkció. Későbbi, független eredetellenőrzés és valódi
kétgépes utazási teszt nélkül nem lehet indítást feloldani.

Források:
- https://ark.wiki.gg/wiki/Server_Configuration
- https://ark.wiki.gg/wiki/Dedicated_server_setup
- https://docs.curseforge.com/rest-api/

**Az eddigi szintetikus natív és Playwright tesztek nem helyettesítik
a valódi ASA próbakört.**
