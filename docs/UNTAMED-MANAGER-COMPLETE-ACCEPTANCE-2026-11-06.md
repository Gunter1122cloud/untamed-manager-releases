# UNTAMED KINGDOM — OBSIDIAN COMMAND / COMPLETE MANAGER

**Végleges, elfogadott termék- és megvalósítási specifikáció — 2026-11-06.**
**Meglévő projekt:** GitHub \`Gunter1122cloud/untamed-manager-releases\`, PR #11, a meglévő natív .NET 10/C# core és Windows desktop host, lokális WebView2 + React felület. Nem új projekt, nem webes szolgáltatás, nem csupán demo.
**Állapot:** teljes fejlesztési program; az összes sor NINCS automatikusan teljesítve. Kód jelenléte, zöld szintetikus CI és végfelhasználói elfogadás külön fogalom.

## Változtathatatlan követelmények
1. Megőrizni az Untamed Kingdom 10 mapját és kétgépes topológiáját: UK-HOME-01 (HP): Astraeos, Genesis Part 1, Valguero, The Island, Extinction; UK-HOME-02 (Ryzen): Scorched Earth, Lost Colony, Ragnarok, The Center, Aberration. Mindkét gazda saját helyi Managerrel rendelkezik; távolról csak hitelesített, read-only állapot. A tényleges mappák, portok és INI-k nem találhatók ki: a helyi gépről igazoltan kerülnek be.
2. A korábban jól működő történeti Store-v1/karakter-/tribe-/clustertravel adatstruktúrát **1:1** meg kell tartani, konvertálás nélkül, a nullabájtos/profilhiányos utazást is védve. Nem írható felül éles történeti adat, és a korábbi manager neve nem jelenhet meg az új termékarculatban. Az adatok átvételénél az eredeti fájlok hash- és byteazonos megőrzése, multiwriter tesztek.
3. Külön üzemelő MariaDB 11.4.13 LTS az eddigi helyén marad; **nincs SQL kezelő, adatbázismotor, beágyazott MariaDB vagy migráció a Managerben**. A pluginok meglévő külső SQL-kapcsolata megmarad.
4. Qwen-chat és automatikus AI-felügyelet, Discord-bot jogosultság, mobilalkalmazás később; egyik sem előfeltétele a Windows Manager teljes kiadásának.
5. Nem szabad az éles UK-HOME-01 / UK-HOME-02 gépeket vagy Store-v1 fájlokat módosítani. Először izolált Windows tesztgép, valódi de új ASA map, majd külön kifejezett jóváhagyással többgépes elfogadási próba.
6. Az összes létező, valóban működő funkció és támogatott adatformátum megmarad; nincs újraindított demo vagy megszakadt fejlesztési ág eldobása. Hibakereséskor közvetlenül a meglévő forrásban dolgozunk.
7. Azonos lokális natív desktop és arculat: Unity-szerű vagy böngésző/webes vezérlés helyett takarékos Windows EXE, saját ikon, X→tray, tray menü, egyetlen WebView2 host, tiszta fókusz/scroll/teljes kijelző. Titkok DPAPI-ban, nem kerülnek Reactbe és logba.

## Megjelenés, UX és navigáció
- Saját *Untamed Kingdom Obsidian Command* arculat: matt grafit #0D171B, kártya #1D3338, akcentus #34CBA3, másodlagos #7DCCD0, szöveg #E4F1EF; egységes dinós mapborítók, finom (nem erőforrás-pazarló) animáció, egységes sidebar/header, nagy és olvasható állapotgombok. A jóváhagyott képernyőreferenciákhoz pixelközeli összehasonlítás 1280/1600/1920/4K felbontáson, kézi görgetéssel.
- Nyitólap: helyi és read-only távoli mapok, CPU/RAM/lemeztárhely ahol mérhető, tényleges futó mapok, játékosok csak igazolt RCON alapján. Állapot/host/kártya–lista szűrők; elhagyandó a nagy, haszontalan üres táblázat.
- Mapkártya: azonosítható artwork, mapnév, gazdagép, aktív/leállt/települ/hiba, UDP/RCON, PID/uptime/build, RAM/CPU, RCON-játékosok, mentési státusz. Egyetlen elsődleges kontextusfüggő gomb (Letöltés / Beállítás / Indítás / Leállítás / Hiba vizsgálata), másodlagos Kezelés, biztonságosan jóváhagyott Restart/SaveWorld/Update. Távoli kártyán nincs írási gomb.
- Mapadatlap fejléc és egységes Start/Stop/Restart/SaveWorld/Update műveletsáv, ez alatt áttekinthető fülek: Áttekintés, Modok, Pluginok/ASA API, RCON/Konzol, INI, Mentések, Naplók, Karbantartás.
- Csoportosított menü: Központ (Áttekintés/Szerverek/Cluster), Kezelés (Modok/Pluginok/Mentések/RCON/Naplók), Rendszer (Frissítések/Ütemező/Untamed Cluster Vault/Beállítások). SteamCMD a szervertelepítésben és frissítésekben, a haladó diagnosztika elérhető.
- Biztonsági állapotok: az indítás/leállítás/mentés *megrendelve*, *RCON-küldés megtörtént*, *Windows PID igazolt*, *disk SaveComplete ténylegesen megfigyelve*, *game join ellenőrzött* külön jelölendő. Nem szabad valós adatnak álcázni a szimuláltat.

## Végrehajtási backlog (P0 és P1)

### P0/1 — Új ASA map letöltése és regisztrálása
Map kiválasztása + üres helyi mappa (natív Windows picker) + **ASA letöltés indítása**; háttérben automatikus preflight, Valve SteamCMD és ASA dedicated app 2430930 beszerzése, disk/build/appmanifest/hash ellenőrzése, beállítások megőrzése; automatikus helyi regisztráció. Mappa/Store ütközés és symlink, maradék letöltés, SteamCMD meghibásodás kizárása; tényleges feladatállapot, megállítás/folytatás. Port/RCON/mod megadása csak letöltés után.
**Elfogadás:** izolált Windows-gépen map+mappa+Letöltés után ellenőrzött valódi ASA fájlok és új regisztrált map; 2. alkalommal nincs káros ismételt letöltés; megszakított eset biztonságosan rendezhető.

### P0/2 — Mapkártyák és saját mapadatlap
Prémium mapgaléria és lista egységes számlálókkal, szűrőkkel; saját gazdagép szerinti csoportok; indítás/leállítás/újraindítás/mentés külön natív jóváhagyás és életciklus-napló. Megőrzött processz reattach Manager UI restart után, PID+start time+path+args identitás.
**Elfogadás:** új, nem Store mapon tényleges Start/Stop/Restart/SaveWorld, állapot pontos, helytelen gomb letiltott, nincs önkényes kill vagy újrapróbálás.

### P0/3 — RCON és valós állapotfigyelés
ListPlayers, GetChat, GetGameLog, Broadcast, ServerChat, adminparancsok, PID, Game/Query/RCON port, erőforrás CPU/RAM, uptime, verzió, napló; titokszűrés és hitelesítés utáni folyamat-újraellenőrzés; eredmény nem felnagyítva.
**Elfogadás:** új valódi ASA szerveren hitelesített RCON parancs/válasz, játékoskliens tesztje; nem olvasható adat „Nem ellenőrizhető”.

### P0/4 — Mentés, restore, történeti Store-v1 és cluster travel
SaveWorld és SaveComplete külön igazolása, hash és ZIP/full-world mentések, atomikus/journalolt write/restore, visszaállítás előtti ellenőrzött checkpoint, adatmegőrző plugin backup; központi Untamed Cluster Vault, két-host Store-v1 írási konfliktus és 0-byte profil elleni védelem, valós utazás tesztje.
**Elfogadás:** izolált valódi ASA world/profile/tribe roundtrip, byteazonos történeti minta, próbált crash/restore recovery és többgépes travel. E nélkül NINCS production-ready címke.

### P0/5 — Kétgépes Cluster
Két külön Manager, hitelesített LAN peer (párosítás, kapcsolat és verzió), lokális 5+5 map; távoli megfigyelés, időbélyegzett stale detection, helyi parancsok nem proxyzódhatnak távolra; egymástól független karbantartási sor.
**Elfogadás:** izolált kétgépes teszt reconnect/offline/network swap/multiwriter védelemmel; valódi kétgazdás 10 map csak külön felhasználói engedéllyel.

### P1/6 — Teljes Mod Center
Hivatalos CurseForge API-kulcs DPAPI-ban, kategória/fa, search/pagination/featured, képek, verziók, dependencies/crossplay/fizetős flag ahol elérhető; maponként aktív/passzív/sorrend, drag+keyboard, piszkozat/undo/redo/CAS, többmapos másolás és közös modleltár; ellenőrzött \`-mods\` indítás, valódi betöltés naplóból.
**Elfogadás:** valós API kapcsolat, valós új ASA mapon a megfelelő modok és sorrend, újraindítás és visszaállítás sikeres.

### P1/7 — Plugin / ASA API Center
Loader (AsaApiLoader vagy kompatibilis Version.dll), ASA API-verzió, per-map DLL/plugin inventory és load log, ZIP telepítés, engedélyezés, config edit, verzióellenőrzés, plugin backup és visszagörgetés; nincs titok/licenc elvesztés.
**Elfogadás:** valódi izolált ASA folyamatban plugin betöltés, frissítés és rollback; pluginadatok hash-azonosan őrződnek meg.

### P1/8 — Ütemező, watchdog, automatikák
Mentés és restart ablak, egymást kizáró karbantartás, ütemezett SteamCMD, biztonságos watchdog/kill→start csak dokumentált és kifejezetten engedélyezett végső helyreállításként; változó WAN IP figyelése; szabályos shutdown és processz reattach.
**Elfogadás:** megszakítás, operátori override, áramkimaradás/pid reuse/network flap után nincs duplikált vagy meg nem engedett művelet; pontos auditlog.

### P1/9 — Teljes prémium megjelenés
Valamennyi képernyő közös tipográfia, mapkártya, ikon és státuszlogika, egérgörgő/fókusz, scroll, DPI scaling, magyar szövegek, dark mode, egységes grafika és finom animáció; nincs fake data. Kézi Windows UI összevetés jóváhagyott mockuppal és automatizált képernyőtesztek.
**Elfogadás:** 1280–4K kijelzőkön használható, minden funkció elérhető, nincs túlcsordulás, nincs végtelen spinner vagy felesleges többlépcsős párbeszéd.

### P1/10 — GitHub updater, EXE, Setup, Portable
GitHub Releases ellenőrzés, aláírás/SHA-256 és állomány-hitelesség, tényleges letöltés, külső updater, rollback, telepített adatokat/store/map konfigurációkat nem módosító csere, tálcás működés.
**Elfogadás:** offline kontroll, hibás/csonka release rollback, sikeres Setup/Portable feltelepítés majd frissítés, megőrzött állapot.

## Kiadási kapuk és státusz
- Mind a tíz szakaszhoz követhető issue/checklist, source commit, gépi test, izolált Windows test, reprodukálható UI screenshot, ahol szükséges valódi ASA/RCON.
- Csak GitHub Action **zöld fordítás** és szimulált teszt esetén PREVIEW artifact, soha nem „végleges”.
- A **végleges EXE/Setup/Portable** csak minden szakasz és a valódi ASA/kétgépes karakterutazás/mentés/restore felhasználóval jóváhagyott elfogadási tesztje után kaphat release címkét. Nem használható az éles UK-HOME-01/02 teszthez engedély nélkül.
- Kivitelezés sorrendje P0/1–5 majd P1/6–10; a prémium vizuális kialakítás és a regressziók minden részfeladattal párhuzamosan haladnak. Az egyes részek „kész” címkéjét igazolni kell, százalékot nem találunk ki.
- Minden hibás tesztet javítani kell, semmilyen kontrollt nem szabad csak a zöld CI kedvéért kikapcsolni.
- Az engedélyezett kezdő következő feladat: **P0/1 + P0/2 szerverlista/mapkártyák és a mappa→letöltés→regisztráció teljes natív + vizuális bekötése** a meglévő PR #11-ben.
