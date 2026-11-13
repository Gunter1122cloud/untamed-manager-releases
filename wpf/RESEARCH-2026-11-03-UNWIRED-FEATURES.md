# Untamed Kingdom ASA Manager – v1.5.0 hiányosság-, bekötési és megoldáskutatás
**Dátum:** 2026-11-03 · **Státusz:** kutatási/audit dokumentum, NEM kódjavítás és NEM production-engedély  
**Ellenőrzött projekt:** https://github.com/Gunter1122cloud/untamed-manager-releases  
**Utolsó igazolt működési CI:** https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/41697040819 – sikeres v1.5.0, commit 96b1ce7c6b5384b490b52b4287227b31040ea874. Az ezt követő ellenőrzött commitok képernyőkép-dokumentációk voltak.  
**Források:** a GitHub aktuális wpf/Core, wpf/MainWindow*, wpf/Supervisor és Actions workflow kódjai; a felhasználó Legacy 1.8.3 dokumentációs ZIP-je és 2026-11-02 HP/Ryzen passzív auditjai; nyilvános ASA, SteamCMD, CurseForge, MariaDB, Microsoft, GitHub, YARK, HaruHost és Cousin dokumentáció. A Legacy-archívum nem nyilvános adatát nem töltöttük fel. **Sem az éles HP/Ryzen hosthoz, sem az élő Legacy Store-hoz, sem MariaDB-hez nem csatlakoztunk.**

## 0. Rövid döntés
**NEM az a megoldás, hogy a v1.5.0 tiltott kapcsolóit bekapcsoljuk.** A termék jelenleg jó offline tesztalap, de az éles Legacy-helyettesítéshez négy igazolás hiányzik: (1) Legacy kétgépes Store-v1 kompatibilitási szerződés a tényleges rekordokon; (2) valós ASA mentésbefejezés + folyamatazonosság + kizárólagos vezérlési tulajdonjog; (3) atomikus többmapos, kétgépes, SQL-es mentési és visszaállítási eljárás; (4) Windows tesztgépen validált frissítés/telepítés, mielőtt prod hozzáférést adunk. A *prémium megoldás* önálló Windows Supervisor/agent + tranzakciós state machine + magyar WPF UI + bizonyítékhoz kötött kezelőszervek + auditnyom, nem a szimulációs kapuk általános feloldása.

**Státuszmagyarázat:** K = funkció kódja létezik; B = WPF/Supervisor hívja; O = működő offline/lokális funkció; S = csak szimuláció; L = éles funkció lezárva; H = bizonyíték hiányzik. A „nem hívja” megállapítás a jelenleg ismert WPF/Supervisor hívási útvonalakra vonatkozik; tesztkódban lehet használat.

## 1. A kódban lévő, de a fő üzemeltetésbe be nem kötött részek

| Prioritás | Modul / metódus | Kódban | Mit bizonyít az audit? | Konkrét bekötés |
|---|---|---|---|---|
| P0 | Core/SafeLifecycle.cs – SafeLifecycle.Stop | K, tesztadapter | A WPF/Supervisor nem hívja; nincs éles implementált Stop/Restart. | LocalSession adapter + OperationCoordinator; SaveWorld bizonyíték/DoExit/process exit kísérleti validálása, egy művelet/gyökér. |
| P0 | Core/SaveWorldValidator.cs – VerifyLiveUnavailable | K, L | A metódus mindig elutasítja az éles bizonyítékot. | Választható, dokumentált ASA/API hook alapú SaveFinished korreláció; stop utáni világállomány-integritás vizsgálata; sikertelen bizonyíték => STOP tiltva. |
| P0 | Core/LocalStoreProbe.cs – Inspect | K, L | Nincs UI/Supervisor hívási pont; Store-séma sem igazolt. | **Olvasó** Legacy-v1 parser/peer/session/lock monitor; eredeti Legacy koordinációhoz soha nem írunk. |
| P0 | Core/LegacyProfileImporter.cs | K, B, H | Csak speciális lokálisan másolt v1 JSON „profiles” exportot fogad; ez önmagában nem azonos a Legacy 1.8.3 saját fájljaival. A draft szándékosan inaktív; nincs valós import/apply. | Valódi fájlok szerződése szerinti read-only dekóder, forrás-staging, diff, explicit tulajdonosi jóváhagyás, továbbra sincs Store-konverzió. |
| P0 | Core/ReleaseCapabilityGate.cs | K, B, L | RealAsaStart/Stop/Restart/Kill, LiveSteamUpdate, LiveSqlRestore és LiveLegacyStoreWrite mindig tiltottak. | **Külön** típusos, igazolt élő bizonyítékok és elfogadási jogosultságok; ne egyetlen boolean kapcsoló. |
| P0 | Core/RecoverySimulation.cs | K, B, S | A Recovery Center szintetikus SaveWorld/journal/exit/start után sem vezérel valós ASA-t. | Read-only watchdogból SafeLifecycle állapotgépre átvezetés CSAK izolált ASA teszt igazolása után, cooldown, maintenance, PID és loader identitás. |
| P1 | Core/SteamCmdTestRunner.cs – RunAsync | K, teszt | A WPF/Supervisor nem hívja, csak FakeSteamCMD-t tesztel. | Külön, valódi SteamCMDUpdateWorker staged cache+install, bounded stdout/timeout, save/stop/backup gate, job lock; installer/Updater külön folyamat. |
| P1 | Core/SteamUpdateDryRun.cs | K, B, S | Közös install preflight + szimulált receipt, de nincs Steam letöltés, telepítés. | App 2430930 build-ID lekérdezés, shared content cache, csak leállított install egyszeri update, pre_update backup és rollback. |
| P1 | Core/AsaApiInspector.cs | K, B, O(read-only) | PE64 EXE/DLL, config, pluginmappa ellenőrzése; nincs release-fetch/telepítő/rollback. | Hivatalos ArkServerApi GitHub Release API, allowlist fájlok és manifest, aláírás/hash, loader típus, elkülönített stop/rollback. |
| P1 | Core/ModStateController.cs – ConfirmAppliedAfterVerifiedRestart | K, L | A metódus szándékosan dob kivételt: az „alkalmazásra vár” flag nem kerül hitelesen törlésre. | A tényleges futó processz indulási argumentumainak, PID/starttime/loader és mods/passivemods hashének összevetése után lehet csak visszaigazolni. |
| P1 | Core/ModUpdatePlanner.cs | K, B, részleges | Lokális mappajelenlét nem igazolja a helyi CurseForge file ID-t; nincs hiteles „installed build” korreláció. | ASA mod-telepítési metaadatok dokumentált forrása + CurseForge file ID + GameVersion/author entitlement; ha nincs, marad unknown. |
| P1 | Core/BackupRetention.cs – Quarantine | K, teszt | Valódi SHA-val ellenőrzött managed backup karanténmozgatás elérhető, de WPF-ben **csak szintetikus RetentionPreview**, nincs operátori éles karantén/restore UI. | Helyi backup katalógus, per-job retention, explicit kiválasztás, „lomtár/karantén”, 2-phase GC és visszavonás. |
| P1 | Core/OfflineSqlArchive.cs | K, B, O(offline) | Exportált SQL fájlokat validál, csak TEMP mappába próbál visszaállítást; nem készít élő adatbázismentést. | Külön MariaDB dump+restore adapter a **valódi** verzióhoz, minimális jogosultságú backup-fiók, teszt DB-be tényleges restore. |
| P1 | Core/OperationalScheduler + RecurringReadOnlyRunner | K, B, részleges | Az ablakfüggetlen Supervisor négy olvasó jobot futtat: Observation/ModCheck/ApiCheck/SteamCheck. A Restart/Wipe/BackupSnapshot/Notification/Maintenance és több művelet nem fut élesben. | Tartós job claim, DST/ütközés, szerepkör, notice ACK és lifecycle kapu; readonly kategóriák önállóan üzemelhetnek. |
| P1 | Core/RconCommandPolicy.cs + FakeRconQueue | K, B, S | Broadcast/ServerChat/SaveWorld/WildDinoWipe előnézet vagy szintetikus „küldés”; élőben csak ListPlayers. | Helyi, minimális jogosultságú RCON command queue, nonce/idempotency és audit; éles destruktív parancsok külön engedéllyel később. |
| P1 | Core/TelemetryHistory.cs | K, B, részleges | CPU/RAM minta és grafikon; játékosszám, RCON RTT, hálózati átlag sokszor null helyett UNKNOWN. | Valódi mért RCON latency/időbélyeg, player list parser vagy engedélyezett API/plugin telemetria, diszk-/WAN-hosszidős megfigyelés. |
| P1 | Core/DirectoryUpdateTransaction.cs | K, rendszerfüggő | WPF/Supervisor nem közvetlenül hívja; főként Updater/integrációs útvonalhoz tartozik. Nem ASA update motor. | Tartani külön: Manager önfrissítés, telepítő-tesztek és publikált verzió manifest; soha nem egyesíteni az ASA állapotgéppel. |
| P2 | Core/WanChangeTracker.cs + ReadOnlyWanProbe | K, B | Recovery Centerből kézi HTTP DNS/IP lekérdezés; nem automatizált WAN-váltás utáni restart. | Szerver-üzembiztos állapot bizonyítása után opt-in stabil IP-változásfigyelés + DNS egyezés + kizárólag az előzőleg futó, saját mapok soros restartja. |
| P2 | Core/LocalContentOperations.InstallLocalPluginFolder | K, nem aktív | Régi, nem tranzakciós API **szándékosan NotSupportedException-t dob**; nincs normál UI-hívó. | A meglévő SafePluginZip és PluginStateManager tranzakciós útvonalát használni; a régi függvényt később teljesen eltávolítani. |
| P2 | WPF UpdateCenter + .github/workflows | K, B, integrációs hiány | A Manager **GitHub Releases** API-t olvas, a v1.5.0 csomag csak Actions-artifact. A workflow kiadási lépése explicit „if: false”. | Verified Preview GitHub Release létrehozása minden sikeres, elfogadott RC commit után; pontos SHA-256, Authenticode és in-app release channel. |

## 2. Ami ténylegesen működik, de NE állítsuk róla, hogy „teljes”

| Terület | Most igazolható | Ami még nincs |
|---|---|---|
| Active/Passive/Disabled | Per-map argumentumok, tartós módváltás, globális helyi map edit, no restart | Valódi futó modváltozat és alkalmazási ACK; módosítás utáni boot-verifikáció. |
| CurseForge | Jóváhagyott kulcs esetén read-only keresés, fájlok és függőségek | Letöltés/telepítés, author distribution opt-in, crossplay/premium entitlement bizonyítása. |
| Pluginok | Fizikailag leállított saját mapon fájlkönyvtár ki-/bekapcsolás + SHA backup, ZIP rollback | Valós runtime load/unload igazolása, függőségek, hivatalos verziók, élő Legacy coexistence és teljes owner lease. |
| JSON/INI | Stopped-state raw/strukturált editing, ETag, BOM/sorvégek, .bak és atomikus csere | Beacon-szintű spawn/loot/engram séma, új kulcsok és teljes config diff/catalog, pending-on-restart bizonyíték. |
| Backup | Offline manifest, izolált csomag, integritás és retenciós motor | Pontos élő világ/profile/tribe + két peer Store + DB egy pontba rendezése, restore bizonyítás. |
| Supervisor | Folyamatazonosítás + CPU/RAM + read-only scheduler | Elismert valós ASA readiness, player count, WAN upload/jitter, fagyás és kétszeres vezérlés kizárása. |
| Legacy import | Szintetikus, explicit v1 JSON másolatok fail-closed dekódolása | Eredeti Legacy-format parser; a mostani **StoreCompatible=false** mindig. |
| RCON | ListPlayers valódi helyi állapot alapján, egyéb parancsok biztonságos előnézete | Write/command queue és SaveWorld ACK külön helyi jog és valódi teszt után. |
| Updater | GitHub Release polling, hash verification, külön updater és recovery | Jelenlegi v1.5.0 nincs v1.5.0 GitHub Release-ként közzétéve, így az automatikus keresés nem látja. |

## 3. Interneten ellenőrzött megoldások – prémium minőségű megvalósítási ajánlások

### 3.1 ASA lifecycle / SaveWorld / Watchdog – P0, döntő
**Források:** [YARK lifecycle](https://getyark.com/docs/lifecycle/), [ARK wiki dedicated setup](https://ark.wiki.gg/wiki/Server_Setup), [HaruHost ASA](https://haruhost.com/gameservers/ark-survival-ascended/), [Cousin manager features](https://asamanager.chezcousin.net/features).

**Megfigyelés:** nyilvános managerek használnak RCON SaveWorld + DoExit + rövid várakozás / backup ellenőrzést, de egy RCON „sikeres” válasz **nem konzisztens világmentési bizonyíték**. A YARK bizonyos folyamatban kifejezetten best-effort pre_stop backupot is dokumentál. Ez gyengébb, mint a mi szigorú 1:1 karaktermegőrzési feltételünk.

**Prémium terv:** külön Windows agent és maponként kizárólagos state machine: registered-owned -> process verified -> notice acknowledged -> correlated save attempt -> completion evidence -> DoExit -> expected exact process exit -> verified world snapshot -> restart -> RCON-ready -> cluster peer ready. *Completion evidence*: dokumentált és ténylegesen tesztelt API/plugin save-finished esemény + operation token, és a sértetlen save snapshot együtt; ha az ASA/API ezt nem biztosítja, marad stop blocked, vagy előzetesen igazolt külön dokumentált, felhasználó által engedélyezett eljárás. Sem timestamp, sem RCON-reply egyedül nem elegendő. A beavatkozás előtt a Windows PID+starttime+exe+args/process tree újramérés kötelező. Fagyásból nem következik Kill alapértelmezésben.

**Elfogadási teszt:** izolált valódi ASA szerver; SaveWorld közben RCON elszakad, plugin leáll, file lock, 2 egymást követő save, PID reuse, API loader más child process, három szimultán Manager indítás, áramszünet. Minden hibánál fail-closed; nem indul második ASA.

### 3.2 LegacyCluster Store 1:1 – P0, nem helyettesíthető általános ARK clusterrel
**A felhasználó eredeti Legacy 1.8.3 ZIP-je alapján**, a specifikációban szereplő lokális metaadatok: cluster-v1.json, cluster-store-v1.json, shared-store-device-v1.json, shared-store-lock-v1.json; közös .legacy-store-v1 koordináció: operation.json, peer fájlok, optional attempted/running/cancelled/archive/mutation.lock; eredeti kétgépes állapot active/done. A peer protokoll frozen/backed/done/cancel/released státuszokat használ, session/configHash/selected/member egyeztetéssel. **NEM módosítható**: jelenlegi cluster ID, aktív Store állapot, session, peer config, lock, a karakter/tribe file-ok és nincs új convertToStore.

**Prémium terv:** (a) kifejezetten **read-only**, schema-v1 validáló mindkét host lokális és az SMB másolatán; (b) canonical JSON/JS szerinti configHash kompatibilitási tesztek a dokumentációval; (c) map-route ownership és két peer session+done+member+root egyeztetés; (d) külön „kétgépes átállási/restore stop-checklist”, de új session soha nem készül automatikusan; (e) Legacy és új Manager közös mapra történő egyidejű aktív kontrollját blokkolni. Általános cluster backup nem elegendő. Semmilyen „Legacy Store olvasható” eredmény ne jelentsen „átvehető” jogosultságot, amíg a valódi tesztmásolatok átjárhatósága nincs bizonyítva.

**Elvégzendő tesztek:** ellenőrzött host- és megosztásmásolat, valós Store állapot 5+5 mapon, hiányzó peer, megtört SMB, elérhetetlen lock, eltérő JS JSON.stringify hash, késői transfer, két különböző session, és karakter/tribe sértetlenség. Mindezt csak izoláltan; a két éles host érintetlen.

### 3.3 SteamCMD és ASA modfrissítés – P1
**Források:** [YARK SteamCMD](https://getyark.com/docs/updates-and-steamcmd/), [YARK source runbook](https://github.com/gabomarin/yark/blob/main/docs/updates-steamcmd.md), [ASA mod/server tutorial](https://github.com/modcommunity/how-to-setup-a-server-in-ark-sa), [ARK server configuration](https://ark.wiki.gg/wiki/Game_user_settings).

**Megerősített:** dedikált ASA Steam app 2430930. A YARK egy közös letöltési/content cache és egyedi install sync mintát ír le; a Saved/world/INI mappát explicit kizárja a syncből, a helyi appmanifest buildID-t a távoli builddel veti össze. Az ASA a CurseForge -mods projekt ID-ket indításkor tölti/frissíti. Az -passivemods külön támogatott és **nem igényli ugyanazt az ID-t a -mods listában**.

**Prémium terv:** download cache -> version manifest -> offline diff -> every owned map in shared installation confirmed stopped -> strict pre_update backup -> staging update with bounded subprocess and exit-code check -> hash health -> data-preserving promote -> exact previous-running set recover; backoff/error rollbacks with durable marker. Any overlap, insufficient disk, failed backup or active process => no update. Distinguish „Game version” and Steam buildID. **Ne implementáljunk külön fizetős mod-downloadert jogosultság megkerülésével**; hagyjuk az ASA saját authorizált induláskori modletöltését működni.

### 3.4 CurseForge prémium adatok / jogosultság – P1
**Források:** [hivatalos CurseForge API-kulcs](https://support.curseforge.com/support/solutions/articles/9000208346-about-the-curseforge-api-and-how-to-apply-for-a-key), [hivatalos author distribution toggle](https://support.curseforge.com/support/solutions/articles/9000207877), [CurseForge Core API](https://docs.curseforge.com/rest-api/).

**Fontos:** harmadik feles API-hozzáféréshez jóváhagyott kulcs kell. Az author megtilthatja projektje fájljainak harmadik feles terjesztését. A fájllista és a letöltési engedély nem ugyanaz. A fizetős mod megvásárlási jogosultságát egy publikus lista NEM igazolja.

**Prémium terv:** részletes mod adatlap (ID, kategória, utolsó frissítés, dokumentált támogatott platformok, fájllista, függőségek, elosztási státusz), lokális fájlidentitás proven/unknown, cached pagination, 403/429 retry policy, explicit „vásárlási jogosultság ismeretlen”. Projekt- és fileID külön adatmező. Ne tegyük a képernyőre hamis Current állapotként puszta mappajelenlétet.

### 3.5 ASA API + loader – P1
**Források:** [ArkServerApi AsaApi release-ek](https://github.com/ArkServerApi/AsaApi/releases), [AsaApiLoader README](https://github.com/ArkServerApi/AsaApiLoader), [YARK ASA API guide](https://getyark.com/docs/ark-server-api/).

**Fontos:** az API community-függőség, nem hivatalos Wildcard/CurseForge mod. Loader útvonal változatok léteznek (AsaApiLoader.exe vagy támogatott Version.dll). A jelenlegi AsaApiInspector csak PE64 alakot + configot + statikus fájlokat ellenőriz, nem kompatibilitást vagy tényleges module load-ot. Az ArkServerApi release notes plugin unload/hook kezelés változásait is tartalmazzák, emiatt a runtime reload nem automatikusan biztonságos minden pluginhoz.

**Prémium terv:** hivatalos GitHub Release metadata -> API binary allowlist + hash/szignó, runtime loader mode kimondása, dependency manifest, stop-required update -> teljes preimage snapshot -> atomi staged csere -> loader process/valós modul betöltés check -> rollback. A telepítőnek a végső támogatott fájlszerkezetet kell használni a megfelelő konkrét ASA API release-ből, nem pusztán az EXE nevéből következtetni.

### 3.6 Backup + Restore + MariaDB – P0/P1
**Források:** [ARK wiki Saved mentés](https://ark.wiki.gg/wiki/Server_Setup), [YARK backup](https://getyark.com/docs/backups/), [MariaDB mariadb-dump](https://mariadb.com/docs/server/clients-and-utilities/backup-restore-and-import-clients/mariadb-dump), [MariaDB mariadb-backup](https://mariadb.com/docs/server/server-usage/backup-and-restore/mariadb-backup/mariadb-backup-overview), [Microsoft VSS](https://learn.microsoft.com/en-us/windows-server/storage/file-server/volume-shadow-copy-service).

**Fontos:** a ShooterGame/Saved tartalmaz világ- és profiladatokat, de az Untamed saját kétgépes Legacy Store + SQL külön mentési koordinációt kíván. A Windows VSS **nem garantál alkalmazáskonzisztenciát ASA vagy MariaDB VSS writer nélkül**. MariaDB --single-transaction csak InnoDB esetén ad konzisztens snapshotot; nem elegendő MyISAM/MEMORY táblákra. A mariadb-backup fizikai online full/incremental backupot nyújt Windowsra is, de --prepare szükséges a visszaállításhoz; fizikailag általában teljes példányra jobb, három kis schema szelektív visszaállítására dokumentált mariadb-dump praktikus.

**Prémium terv:** explicit 2-host backup plan, manifest az összes mapról + SQL schema + Store-coordination read-only snapshot, két peer barriernél csak akkor „complete”, ha mindkét oldalon hash és ownership OK; 3-2-1 tárolás / karantén / külön retention job. Restore: **kizárólag tesztpéldányban** dry-run -> scratch profile/DB -> SHA és karakter/tribe validáció -> felhasználói jóváhagyás -> production maintenance, új pre-restore snapshot, visszaállítási marker, kontroller quorum. Verifikációra használjunk leválasztott MariaDB tesztadatbázist a tényleges motorverzióhoz igazítva; soha ne egy második éles MariaDB installt módosítsunk a build alapján.

### 3.7 RCON / scheduler / watchdog – P1
**Források:** [YARK lifecycle](https://getyark.com/docs/lifecycle/), [Cousin teljes funkciók](https://asamanager.chezcousin.net/ark-survival-ascended-server-manager), [HaruHost ASA](https://haruhost.com/gameservers/ark-survival-ascended/).

**Prémium terv:** (i) műveletspecifikus audit/confirm és rcon-command allowlist; (ii) „at most once” marker a nem idempotens wipe/save/ban parancsokhoz; (iii) szervernapló/RCON hiba nem válthat ki vak Kill-t; (iv) Budapest DST-vel kész UI-independent scheduler, naplózott notice/ACK, missed event, maintenance; (v) CPU/RAM/RCON RTT/játékosszám és readiness külön számsor. Opcionálisan 07:30 Wild Dino Wipe sablonok — csak későbbi felhasználói opt-in; a Custom Rates plugin programját nem szabad automatikusan átírni.

### 3.8 Windows process/exclusive owner + UI bezárás – P0
**Források:** [Microsoft Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects), [named mutex](https://learn.microsoft.com/en-us/windows/win32/sync/using-mutex-objects), [YARK lifecycle](https://getyark.com/docs/lifecycle/).

**Prémium terv:** önálló Windows Supervisor/agent azonosítóval, service/WPF user kontextus és local security ACL, PID+creationTime+executable+commandHash, név szerinti mutex telepítési rootonként, kétgazdás fencing token és tartós journal, port validation és child process reattach. A WPF X-től való tálcára minimalizálás **ne** szabályozza az ASA lifecycle-t. Job Objects használatánál kifejezetten kerülni kell, hogy a WPF-handle lezárása véletlenül lelője a szervert (kill-on-job-close rossz konfiguráció).

### 3.9 Kiadás és frissítő – P1, gyorsan javítható
**Források:** [GitHub Release API](https://docs.github.com/en/rest/releases/releases), [GitHub Actions artifactok lejárnak](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts), [Microsoft Authenticode](https://learn.microsoft.com/powershell/module/microsoft.powershell.security/get-authenticodesignature).

**Bizonyított projektellentmondás:** a wpf/MainWindow.Updater.cs UpdateCenter.CheckAsync a GitHub Release-eket ellenőrzi; a .github/workflows/build-untamed-wpf.yml Release létrehozás „if: false” feltételű. A repó Release-listájában csak korábbi v1.3.1 preview a legfrissebb WPF kiadás, v1.5.0 nincs. A v1.5.0 a 41697040819-es GitHub Actions run artifactjeiben található. Következmény: a saját Updater **nem tudja** megtalálni a v1.5.0-t elérhető Release-ként. Nem a release-ellenőrzést kell megkerülni, hanem a sikeres, jóváhagyott buildet Preview Release assetként publikálni, SHA-256 digesttel és digitális kiadói aláírással, changeloggal; Stable csak explicit, valódi ASA/Legacy acceptance után. Ne tartsunk Release-ként nem ellenőrzött artifactot.

### 3.10 Premium UX, Discord, telefon, licenc – P2
**Források:** [Cousin Manager funkciók](https://asamanager.chezcousin.net/features), [HaruHost ASA](https://haruhost.com/gameservers/ark-survival-ascended/), [YARK dokumentáció](https://getyark.com/docs/), [Tailscale access grants](https://tailscale.com/docs/features/access-control/acls), [Cloudflare Workers WebCrypto](https://developers.cloudflare.com/workers/runtime-apis/web-crypto/), [Discord rate limits](https://github.com/discord/discord-api-docs/blob/main/developers/topics/rate-limits.mdx).

**Prémium UI terv:** egyik oldalon a 10 mapos fleet státusz és a két host egészsége, filter és search minden mod/config/INI/plugin/log listán, tartós műveleti idővonal, térbeli/folyamat-azonosság státusz, REST API felületi kapcsolók *bizonyítékokkal*. Külön per-job diff és előnézet, rollback, progress bar. Prioritás: 1) saját natív WPF; 2) csak olvasó, külön autholt mobilpanel/VPN (nem nyitott WAN RCON), később üzemeltetésre RBAC + approval; 3) Discord-status + rate-limit + szerepkör, értesítés kézbesítési ACK; 4) opcionális UPS figyelés. Licenc: Cloudflare Workers / D1 + Ed25519-aláírt időkorlátos offline token, Windows DPAPI, nincs hard-coded titok, működés leállt internettel is. A licencszerver ne legyen függősége a karaktermentések/Stop biztonsági funkcióinak.

**Szellemi tulajdon:** a YARK forrás GPL-3.0 licencű. Használjuk nyilvános architekturális mintaként és dokumentációként, de ne emeljünk át kódot automatikusan a saját, más licencelésű Managerbe. A Legacy eredeti kódját sem másoljuk át; szerződés alapján tiszta C# implementációt építsünk.

## 4. Konkrét elfogadási kapuk / tesztek

| Kapu | Minősítés |
|---|---|
| A: saját üzemeltetés Legacy mellett | **Csak read-only**. Sem indulás, sem scheduler/watchdog, sem shared Store-írás. A jelenlegi v1.5.0 WPF tesztmódja nem „prod shadow reader” hitelesítés nélkül. |
| B: két host/Store metaadat read-only felismerés | Hiteles v1 parser, 5+5 map, mindkét peer active/done, session/configHash/lock, file unchanged SHA. Ismeretlen => blokkolt. |
| C: mentés minden ismert adatra | Külön HOST-A/HOST-B/SQL/Store manifest, pontos hatókör, konzisztencia, checksum, elérhető restore. |
| D: kontrollált stop/restart | Valós izolált ASA SaveWorld/DoExit + PID identitás + backup + mod/API readiness bizonyítékkal; timeout/failure esetén no kill/no double start. |
| E: Steam/API/mod műveletek | Helyi staging, shared root lock, original Saved config untouched, rollback, official release/hash/API entitlement, játékos notice. |
| F: automatikus watchdog/WAN/wipe | Írásos felhasználói opt-in, Legacy exkluzív leváltása után, max-attempt/cooldown/maintenance/notice; két peer koordináció. |
| G: publikálható Manager | Windows test PC + GitHub Windows Actions; pontos build hash és signed Preview Release; „Stable” csak owner acceptance után. |

## 5. Ajánlott fejlesztési sorrend – max haszon / min kockázat

1. **P0-1 – Legacy read-only adapter** tényleges fájlokra, ownership/fencing és külön „read-only coexisting with Legacy” bizonyítás. **Nem próbálunk Store-t írni.**
2. **P0-2 – SaveWorld/DoExit adapter** izolált tesztszerverrel, valódi időzítési/hiba/megállás jelzések; SafeLifecycle/Recovery/Backup összeillesztése.
3. **P0-3 – Kétgépes védőmentés és izolált restore** MariaDB 10.11-kompatibilis eszközökkel, valódi SQL fixture restore, karakter/profil/tribe diff.
4. **P1-1 – GitHub Preview Release pipeline + updater** (gyors, látható eredmény, nem kell élő ASA).
5. **P1-2 – ASA API release manager + shared SteamCMD staged updater + mod identity**, mindenhol kockázatalapú kapu.
6. **P1-3 – Tartós schedule + RCON és UI retention teljes bekötése**, valós e2e és bizonytalan műveleti state.
7. **P2 – prémium extra**: Beacon-szerű editor, Discord status/role, player history, WAN/jitter, UPS és VPN-alapú mobilpanel.

**Kritikus megjegyzés:** a felhasználó eddig kifejezetten külön Windows tesztgépet és éles gépektől teljes izolációt követelt. Ezt az audit sem módosítja. Egy sikeres FakeAsa CI, mint a v1.5.0, nem jogosít a védelmek automatikus feloldására.

## 6. További ténylegesen hiányzó / nem vizsgált inputok

- Valódi, másolatból kinyert Legacy 1.8.3 cluster-v1.json / store-v1.json / peer-session metaadatok egyező rekordjai titokszűréssel; jelenleg ZIP dokumentáció + régebbi passzív állapotleltár áll rendelkezésre, nem teljes kétoldali tesztcsomag működésre.
- Tesztgépen futó valódi ASA szerver, API loader és lehetőleg csak tesztkarakter/profil; nincs kontrollált valódi SaveWorld-folyamat teszteredmény.
- MariaDB dump külön sandbox tesztpéldányba történt importtal, tábla engine ellenőrzéssel (InnoDB/MyISAM), binlog/jogok/konzisztencia igazolással.
- SteamCMD + CurseForge szolgáltatás tesztkulcsa/author megengedett API scope és fizetős modjogosultság forrás.
- Saját Authenticode code-signing cert (ha valóban aláírt Setup EXE-t szeretnénk). A SHA-256 nem azonos a kiadói aláírással.
- Kétgépes exkluzív controlling handover tervezet, Legacy scheduler/watchdog letiltásának jóváhagyása, külön offline/maintenance teszt.
- Telefonos/Discord admin auth hozzáférési modell és az elérhető feladatkörök. A státuszos read-only kezdet a legbiztonságosabb.

## 7. Nyilvános forráskatalógus (2026.11.03-án ellenőrizve)
1. YARK dokumentáció és open-source runbook: https://getyark.com/docs/ ; https://getyark.com/docs/lifecycle/ ; https://getyark.com/docs/updates-and-steamcmd/ ; https://getyark.com/docs/backups/
2. Cousin Server Manager feature matrix: https://asamanager.chezcousin.net/features
3. HaruHost ASA premium funkciók: https://haruhost.com/gameservers/ark-survival-ascended/
4. ARK hivatalos közösségi dokumentáció: https://ark.wiki.gg/wiki/Server_Setup ; https://ark.wiki.gg/wiki/Game_user_settings
5. ArkServerApi GitHub release / loader: https://github.com/ArkServerApi/AsaApi/releases ; https://github.com/ArkServerApi/AsaApiLoader
6. CurseForge API / author policy: https://docs.curseforge.com/rest-api/ ; https://support.curseforge.com/support/solutions/articles/9000208346-about-the-curseforge-api-and-how-to-apply-for-a-key ; https://support.curseforge.com/support/solutions/articles/9000207877
7. MariaDB logical / physical backup: https://mariadb.com/docs/server/clients-and-utilities/backup-restore-and-import-clients/mariadb-dump ; https://mariadb.com/docs/server/server-usage/backup-and-restore/mariadb-backup/mariadb-backup-overview
8. Microsoft Windows VSS / jobs / mutex / signing: https://learn.microsoft.com/en-us/windows-server/storage/file-server/volume-shadow-copy-service ; https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects ; https://learn.microsoft.com/en-us/windows/win32/sync/using-mutex-objects ; https://learn.microsoft.com/powershell/module/microsoft.powershell.security/get-authenticodesignature
9. GitHub Releases és Actions artifactok: https://docs.github.com/en/rest/releases/releases ; https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts
10. Tailscale és Cloudflare WebCrypto: https://tailscale.com/docs/features/access-control/acls ; https://developers.cloudflare.com/workers/runtime-apis/web-crypto/
11. Modcommunity ASA modlist/start, független forrás: https://github.com/modcommunity/how-to-setup-a-server-in-ark-sa

**Kutatási korlát:** nem szó szerint a teljes internet ellenőrzése történt, hanem a kulcsfontosságú gyártói/hivatalos, licenc- és projektforrásoké, a felhasználó eredeti Legacy-dokumentációjával kiegészítve. Nincs éles ASA vagy kétgépes Legacy-kompatibilitást alátámasztó mérés. A javaslatokat nem szabad kész vagy jóváhagyott production funkcióként kommunikálni.
