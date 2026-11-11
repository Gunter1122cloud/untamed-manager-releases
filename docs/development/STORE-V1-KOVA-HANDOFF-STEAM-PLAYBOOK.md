# Kova Store-v1 → Obsidian: ellenőrizhető, fail-closed átadási és SteamCMD-protokoll

**Állapot (2026-11-11): READ-ONLY / NOT CERTIFIED.** A \`main\` eredeti
\`9790158...\` kódja öt futó mapot is mutathat; ez nem igazolja a Kova
watchdog leállását, az Obsidian kizárólagos vezérlését vagy az A→B→A
játékbeli utazást. Ez a PR a bizonyítékok különválasztását és tesztjeit
adja hozzá, nem oldja fel a Store-v1 tiltásait.

## Rögzített identitás

- Cluster ID: \`KovaCluster\`; eredeti UNC gyökér:
  \`\\192.168.1.200\UKClusterRoot\`.
- Az öt *eredeti* map/host kézi bővítés nélkül:
  - UK-HOME-01: Astraeos, Genesis Part 1, Valguero, The Island, Extinction.
  - UK-HOME-02: Scorched Earth, Lost Colony, Ragnarok, The Center, Aberration.
- A Kova forrásazonosítók / game és query portok / aktív és passzív
  mod-sorrend / eredeti telepítési útvonal a kanonikus forrás.
  A Manager nem generálhat új eredeti GUID-ot.
- A kész \`LegacyStoreV1ActiveLaunch.Build\` és a
  \`LegacyStoreV1NativeStartPlanner\` már ellenőrzi a
  \`-clusterid=KovaCluster\`, a forrásból származó
  \`-ClusterDirOverride=...\` és az egyszeri \`-usestore\` jelenlétét.
  A \`-converttostore\` és \`-convertfromstore\` tiltott.
  **Forráskódellenőrzés ≠ valós indítási tanúsítás.**

## Három külön státusz (nem felcserélhető)

1. **OBSERVED**: a Windows szerint ASA vagy Loader→ASA fut.
   Nem igazol regisztrációt, RCON-vezérlési jogot vagy karaktermegőrzést.
2. **REGISTERED**: az eredeti öt map GUID-ja, telepítési mappája,
   portjai, mod-sorrendje és forráshash-e egyezik az Obsidian helyi
   regiszterével, a HostRegistry és a ClusterRegistry is konzisztens.
   Ez még nem vezérlésátadás.
3. **EXCLUSIVE CONTROL**: csak *független*, aktuális és visszaellenőrizhető
   tanúsítás után lenne megengedhető. Nem elég a checkbox, a "Fut"
   státusz, vagy a Kova és az Obsidian egyszerre online állapota.
   A jelenlegi kód nem rendelkezik teljes független, írásjogot feloldó
   Kova-watchdog/Windows ütemezés tanúsítással; ezért a read-only
   jelentésben \`false\`.

## Jelenleg engedélyezett UK-HOME-02 ellenőrzés

A következő kérdésekre csak *olvasással* szabad választ gyűjteni:
- Kova belső ötmapos \`autoStart\` / \`watchdog\` / maintenance állapota
  és az összes aktív restart, wipe, backup, update ütemezése.
- Windows process fa: valódi Manager/supervisor PID és szülőfolyamat;
  a node/electron Kova folyamat **nem** zárható ki csupán név alapján.
- Windows Task Scheduler \`Actions\`, \`Triggers\`, \`State\`; Windows
  services \`StartMode\` és \`State\`; Startup/Run autorun források.
- A Kova és az Obsidian közötti ugyanazon mapra vonatkozó RCON/Start/Stop/
  Restart/SteamCMD jogosultságokat külön kell leltározni.
- A két peer \`ACTIVE/DONE\` session, karakter/tribe/tribute állapot,
  a forrásállományok hash-e és a Loader/ASA indítási idő/PID egyezése.

A vizsgálat **nem írhat konfigurációt** és nem kapcsolhatja le a
Kova managert, watchdogot, Windows taskot, service-t vagy ASA-t. A
felmérést csak ellenőrzött telepített Windows hoston lehet ténylegesen
elvégezni; Playwright-fikszúra nem ilyen.

## Kizárólagos átadás, csak külön jövőbeli engedéllyel

1. Időbélyeges 5/5 natív identitás és mindkét peer forráshash,
   valamint teljes, megőrzött utolsó ép mentés ellenőrzése.
2. A régi Kova vezérlőjének, *külön watchdogjának*, automatikus
   indításának és valamennyi ütemezésének **független** letiltási
   tanúsítása. A folyamat túlélése közben is meg kell bizonyosodni,
   hogy másik vezérlő már nem indít műveletet.
3. Konkurens vezérlők esetén az új Manager minden írási és
   életciklusműveletet megtagad. Az óra-, PID- és token-újrahasználatot
   külön kell tesztelni. A hiba nem válthat át automatikus "takeover"-re.
4. Csak egy natív, tartós, egylépéses regiszter/lease átmenet
   hitelesítése után következhetne a külön engedélyezett RCON és
   szerver-életciklus; soha nem állhat helyre csendben két writer.
5. Ha a tanúsítás bármely pillanatban nem teljes, állapot:
   \`OBSERVATION_ONLY\`; nincs implicit írásjog, frissítés vagy start.

## Történeti Store-v1 SteamCMD: támogatási kapuk, nem kikerülhető tiltás

A jelenlegi \`SteamStoppedMapUpdater.DemandSafeLocalInstallation\` helyesen
blokkolja az eredeti Store-mapokat. Ennek **elvételét tiltjuk**. Egy
külön történeti maintenance executor csak a következő lánccal
tervezhető, és először kizárólag izolált FakeASA/FakeSteamCMD környezetben:

1. Független Kova-watchdog/autorun/ütemezés *exclusive control* bizonyíték.
2. Az összes érintett helyi map és ASA API Loader **pozitívan igazoltan
   leállított**, nulla ASA és nulla Loader PID. Közös telepítési mappában
   minden tulajdonos együtt vizsgálandó, átfedő és symlinkelt út tilos.
3. Valódi SaveWorld/SaveComplete csak autorizált karbantartási ablakban;
   az előző utolsó ép mentés külön védendő.
4. Védőmentés a telepítési gyökéren kívül: teljes világok, a
   Store-ba ágyazott karakter- és tribe-részek, INI, pluginbájtok
   és pluginadatok, indítási profil, eredeti Store lokális metadata,
   valamint a kapcsolódó peer-session/lock állapot. Hash- és
   visszaolvashatósági ellenőrzés; ne legyen élő Store visszaállítás.
5. Tartós, kétfolyamat-biztos, **telepítésenkénti** lock, előre
   naplózott állapotgép, írás előtti ismételt WMI/process/source ellenőrzés.
6. Csak ezután izoláltan kipróbált SteamCMD \`2430930 validate\` út.
   A hibás exit, részleges letöltés, checksum eltérés, kevert build,
   zárolásvesztés vagy váratlan ASA indítás mind fail-closed.
7. Steam után az összes védett fájl és Store-azonosító hash-e egyezik,
   install/build/ASA exe érvényes, külön boot/Loader/ASA API ellenőrzés
   kész. Automatikus újraindítás vagy karakter-restore NINCS.
8. Hiba: \`NEEDS_OPERATOR\`, napló és ellenőrzött rollback-terv, a
   posledő ép mentés érintetlen. Nem szabad a másik host aktív Store
   sessionjét régi másolatra visszaírni.

Ez **tervezett kapusor**, nem kész történeti Store-v1 frissítés;
a produkciós frissítő nem válhat engedélyezetté puszta UI-változtatástól.

## A→B→A minősítési kritériumok

Két gép / tíz map: A=eredeti UK-HOME-02 map, B=UK-HOME-01 map,
majd vissza ugyanarra az A-ra. A bizonyítékhoz szükséges:
- azonos survivor-id, implant, inventory, dinos és tribe/owner státusz;
- teljesen visszaolvasott, Store-ba ágyazott .ark karakter és tribe
  állapot, a nulla bájtos laza profilok átírása nélkül;
- az eredeti peer sessionök, lockok és transfer fájlok konzisztenciája;
- sikeres játékbeli *rejoin mindhárom szakaszban* és visszatéréskor
  ténylegesen használható karakter;
- az érintetlen mapok és az utolsó ép restore-pont változatlansága.

**Jelenlegi szintetikus tesztek:** forrás/azonosság/host/cluster
egyezőség és hibás út/mod/tételszám elutasítása. A StoreTravel
fájlhash alapú összehasonlítása nem igazol beágyazott survivort,
SaveComplete-et, élő writer-exkluzivitást vagy in-game rejoin-t.
Ezekhez elkülönített, explicit engedélyezett későbbi valós ASA
teszt szükséges. A jelenlegi UK-HOME-02-n semmilyen írás/stop/
restart/RCON/update nem engedélyezett.
