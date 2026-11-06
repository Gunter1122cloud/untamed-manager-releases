# P0–P3 konkrét megvalósítási backlog

Dátum: 2026-11-04. Ez tervezési átadás, nem éles műveleti engedély.
Bázis: PR #3, `3b8b448fb906a02d45615a466ef2900ca40200f0`.
Minden végrehajtás először harmadik Windows tesztgépen történik. A most futó UK-HOME-02 és a távoli HP nem tesztcél.

## E. A húsz legsürgősebb javítás, sorrendben

A sorrend függőségi sorrend. Nem minden P1 funkció vár minden P0 termékfunkcióra, de éles íróművelet nem kerülheti meg saját mentési/ownership kapuit.
A `új` jelölés valóban hiányzó adaptert jelent, nem már létező metódust.

| Sorrend / ID | P | Konkrét feladat | Meglévő módosítási hely / új adapter | Függőség | Ellenőrizhető elfogadási feltétel |
| --- | --- | --- | --- | --- | --- |
| 1 / T01 | P0 | Kizárólagos helyi műveleti tulajdonjog és fail-closed kapu | Core/ClusterBackend.cs::LaunchLocal; Core/ReleaseCapabilityGate.cs::Check; új OperationCoordinator | – | Két vezérlő azonos mapon nem kaphat egyszerre írójogot; idegen/ismeretlen folyamatnál nincs művelet. |
| 2 / T02 | P0 | Valódi ASA/Loader identitás, preflight és readiness | Core/WindowsProcessFamily.cs::InspectFamily; Core/ProcessFamily.cs; Core/AsaInstallationInventory.cs::Inspect; új LiveAsaSession | T01 | PID-újrahasznosítás, megváltozó PPID/EXE/argumentum, portütközés és csak process-start külön teszt; RUNNING csak korrelált API/ASA/RCON/join bizonyítékkal. |
| 3 / T03 | P0 | Hiteles SaveComplete adapter | Core/SaveWorldValidator.cs::VerifyLiveUnavailable; új AsaSaveCompletionEvidence | T02 | Valódi izolált ASA-n késleltetett, megszakadó és párhuzamos mentésnél a pontos folyamat/művelet befejezése igazolható; puszta válasz/időbélyeg nem fogadható el. |
| 4 / T04 | P0 | Valódi SaveWorld → DoExit → ExitConfirmed → Restart | Core/SafeLifecycle.cs::Stop; Core/IsolatedFakeAsaSession.cs mint interfészreferencia; új LiveAsaSession | T01–T03 | Nincs új indítás igazolt kilépés előtt; timeoutnál NEEDS_OPERATOR, nem automatikus Kill; UI bezárása nem állítja le az ASA-t. |
| 5 / T05 | P0 | Store-v1 kétoldali referencia és kompatibilis ownership | Core/ClusterVaultArchiveInspector.cs::InspectFiles; Core/KovaStoreV1CopyReader.cs::Inspect; Core/StoreV1Fingerprint.cs::Compute; új StoreOwnershipCoordinator | T01,T02 | Mindkét host négy helyi rekordja és közös session/hash/selected/backup/marker igazolt; régi/új vezérlő nem írhat együtt; nincs új session/konverzió/locktörlés. |
| 6 / T06 | P0 | Karakter/tribe/transfer konzisztenciahatár | új ClusterTransferConsistency; Core/OfflineClusterBackup.cs::Verify | T03–T05 | Izolált 5+5 mintacluster oda-vissza utazás, peer-kiesés és megszakítás után minden tesztkarakter/tribe egyszer és helyesen visszanyerhető. |
| 7 / T07 | P0 | Koordinált kétgépes clustermentés és karbantartási folyamat | Core/LocalBackupWorkflow.cs::Create; Core/OfflineClusterBackup.cs::Build; új ClusterMaintenanceWorkflow | T03–T06 | Egy közös batch azonosít világ/SaveGames/plugin/Store/transfer/SQL részeket; hiányzó résznél nincs érvényes restore point; minden védett komponens leltározott. |
| 8 / T08 | P0 | MariaDB verzióeltérés tisztázása, valódi dump és elkülönített restore | Core/OfflineSqlArchive.cs::ImportFixture/SimulateRestore; új MariaDbDumpAdapter | T07 | A futó service-bináris 10.11.19.0 és a tervezett 11.4 eltérése tisztázott; valódi engine/verzió csak engedélyezett read-only kapcsolattal igazolt. Három teszt-DB dumpja visszatölthető; motorok/rutinok/konzisztenciahatár dokumentált, nincs automatikus upgrade vagy éles restore. |
| 9 / T09 | P0 | Teljes restore-terv, staging és visszagörgetés | Core/SyntheticRestore.cs::Restore/Recover; Core/SafePluginZip.cs::Rollback; új ClusterRestoreCoordinator | T06–T08 | Minden fájlcsere előtti/utáni hibaponton megmarad az eredeti; félbeszakadás blokkolja az indítást; rendszeres izolált visszaállítási próba. |
| 10 / T10 | P0 | Crash/restart műveleti napló és állapotidővonal | Core/RecoverySimulation.cs; Core/EventJournal.cs; Core/UpdateRecovery.cs; új DurableOperationJournal | T01–T09 | Áramszünet/manager-crash után nincs vak replay vagy új konverzió; minden lépés és bizonyíték időrendben látható; eredménytelen lépés operátori állapot. |
| 11 / T11 | P1 | SteamCMD valódi staged telepítés/frissítés | Core/SteamUpdateDryRun.cs::Plan/Simulate; Core/SteamCmdTestRunner.cs::RunAsync; új SteamUpdateWorker | T04,T07,T09 | App/build ID ellenőrzés, egyszeri shared-install frissítés, hibás letöltés és rollback izolált ASA-val; futó map nem frissül. |
| 12 / T12 | P1 | ASA API/plugin frissítés teljes adatvédelemmel | Core/AsaApiReleaseCatalog.cs::FetchLatestAsync; Core/AsaApiInspector.cs::Inspect; Core/SafePluginZip.cs::Install/Rollback; Core/DirectoryUpdateTransaction.cs | T04,T07,T09 | Loader/proxy ütközés, függőség és runtime betöltés külön állapot; JSON/DB/licenc megmarad; leállított tesztmapon megszakítási és rollback próba. |
| 13 / T13 | P1 | CurseForge telepítési bizonyíték és jogosultság | Core/CurseForgeCatalog.cs::SearchAsaAsync/GetModFilesAsync; Core/ModUpdatePlanner.cs::InspectCandidate/Assess | T11,T12 | Projekt-ID nem helyettesíti a telepített file ID-t; crossplay/prémium/distribution/függőség ellenőrzött; ismeretlennél nincs automatikus telepítés. |
| 14 / T14 | P1 | Modállapot runtime ACK, sorrend és tömeges szerkesztés | Core/ModStateController.cs::ApplyLocalBatch/ConfirmAppliedAfterVerifiedRestart; MainWindow.ModState.cs::ShowModStateEditor; MainWindow.Bulk.cs | T02,T04,T13 | Passzív mod egyszer szerepel a -mods elején és -passivemods-ban; tényleges boot/modlista igazolja a pending flag törlését; hibás batch visszaáll. |
| 15 / T15 | P1 | Jogosultságolt RCON adminparancs-sor | Core/RconProtocol.cs::Execute; Core/RconCommandPolicy.cs::Preview; Core/RconCommandPolicy.cs::FakeRconQueue | T01,T03,T04 | Auth, multipart/request ID/timeout/duplikált kérés tesztelt; módosító parancs nem ismétlődik automatikusan; SaveWorld ACK nem mentési bizonyíték. |
| 16 / T16 | P1 | Valódi ütemezett végrehajtás és történeti hibák tisztázása | Core/OperationalScheduler.cs::Next; Core/PersistentScheduler.cs::RunDue; Core/RecurringReadOnlyRunner.cs; Supervisor/Worker.cs | T04,T07,T15 | Budapest DST mindkét váltása, kimaradt futás, tartós egyszeri claim, notice-ACK és karbantartási kivétel tesztelt; régi sync-error nem téves aktuális riasztás. |
| 17 / T17 | P1 | Watchdog háromhibás diagnosztika és riasztás | Core/WatchdogDiagnostics.cs::Observe; Core/SupervisorPolicy.cs::Observe; Supervisor/Worker.cs; Core/DiscordStatusPublisher.cs::SendAsync | T01,T02,T15,T16 | Legalább három hibából időablak/frissesség alapján egy deduplikált riasztás; kézi stop/maintenance nem restartol; automatikus backup/recovery csak későbbi P0 bizonyítékra. |
| 18 / T18 | P1 | INI/JSON teljes szerkesztési és konfliktusút | Core/SafeConfigEditor.cs::Read/Preview/Save/SetIniValue; MainWindow.ConfigEditor.cs::ShowSafeTextEditor | T01,T07 | Game.ini és GameUserSettings.ini külön strukturált nézet, JSON ismételt kulcs/séma, BOM/CRLF/komment/duplikált kulcs megőrzés; staging utáni külső változás sem veszhet el. |
| 19 / T19 | P1 | Modkattintási/fókuszhiba izolált GUI-regressziója | MainWindow.ModState.cs::ShowModStateEditor/Refresh; MainWindow.Bulk.cs::ShowModOrderingControls; MainWindow.xaml.cs::Btn/Navigate; App.xaml | T14 | 100 váltott A/B modművelet és listaújrarajzolás után második elem kattintható; focus/hit-test/modal overlay/mouse capture ellenőrzött, minimalizálás nélkül. Éles Kován nem reprodukáljuk. |
| 20 / T20 | P1 | Manager frissítés és csomagok hitelessége | Core/UpdateCenter.cs::DownloadAndVerifyAsync/CheckAuthenticodePublisher; Core/UpdateRecovery.cs; Updater/Program.cs; installer/UntamedManager.iss | T01,T10 | Pontos csatorna/verzió/aláírás, megszakadt telepítés visszaáll; Manager/Supervisor kompatibilis; futó teszt-ASA túléli; harmadik tesztgépes Setup/Portable elfogadás. |

## További saját és Kova-alapú fejlesztések

| ID | P | Feladat | Pontos kapcsolódó kódhely | Függőség | Elfogadás |
| --- | --- | --- | --- | --- | --- |
| T21 | P2 | Discord bot/riasztások és választható adminjogok | Core/DiscordStatusPublisher.cs::Compose/SendAsync; MainWindow.Discord.cs; új DiscordBotAdapter | T15,T17 | Opt-in, rang/role, lejáró megerősítés, rate-limit és egyszeri művelet; token sem UI-exportra, sem naplóba nem kerül. |
| T22 | P2 | Clusterchat, Discord-chat és közösségi kiegészítők | új ClusterChatRelay; Core/EventJournal.cs; MainWindow.Discord.cs | T01,T21 | Üzenet-ID/epoch/TTL/bodykorlát és Unicode; régi üzenetek nem zúdulnak vissza; személyes azonosító nem diagnosztikai export. |
| T23 | P2 | Opcionális mobilos állapot/admin felület | Core/StatusCipher.cs::Encrypt/Decrypt; Core/ClusterBackend.cs; új RemoteReadOnlyGateway | T01,T15,T21 | Első körben read-only, VPN/TLS/RBAC és replayvédelem; távoli peer státuszüzenet soha nem vezérel helyi mapot. |
| T24 | P2 | WAN/DDNS és alapból kikapcsolt IP-restart opció | Core/WanChangeTracker.cs::Observe; Core/WanChangeTracker.cs::ReadOnlyWanProbe.CaptureAsync; MainWindow.Network.cs | T04,T16,T17 | Kézi WAN-kezelés marad alapérték; csak ismételten igazolt változás és külön opt-in után soros, mentésbiztos restart tesztgépen. |
| T25 | P2 | Saját Cloudflare licenc és offline türelem | MainWindow.Updater.cs::ShowUpdateCenter; MainWindow.xaml.cs licenc/health maradványnézet; új LicenseClient | T20 | Aktiválás/aláírt offline ticket/grace/óra-visszaállítás tesztelt; hálózat/licenchiba nem állítja le a futó ASA-t. |
| T26 | P2 | Eseményközpont és egységes titokszűrés | Core/SafeSupportBundle.cs; Core/EventJournal.cs; MainWindow.ModState.cs argumentum-előnézet; App.xaml.cs hibakezelés | T10,T17 | Whitelist export és UI/log redakció közös szabályokból; szintetikus jelszó/token/endpoint/karakteradat egyetlen publikálható kimenetben sem található. |
| T27 | P3 | Választható UPS-események | új UpsReadOnlyProvider; Core/WatchdogDiagnostics.cs | T03,T04,T07 | Első körben csak státusz; battery/eseménykimaradás nem okoz vak Killt; leállítás kizárólag igazolt P0 útvonalon, külön opt-innel. |
| T28 | P3 | Kapacitás, terhelésfüggő indítási sor és trendek | Core/TelemetryHistory.cs::Observe/Read; Supervisor/Worker.cs; új StartCapacityPolicy | T02,T04,T07 | Külön HP/Ryzen RAM/CPU/lemezkeret; mintavétel korlátos és opt-in; ismeretlen adat nem nulla; indítási sor erőforráshiánynál vár. |
| T29 | P2 | Mély PvE/spawn/engram/loot és konfigurációs sablonok | Core/SafeConfigEditor.cs; új AsaConfigurationSchema; MainWindow.ConfigEditor.cs | T18 | Verziózott séma és előnézet, ismeretlen kulcs megőrzés, maponkénti eltérések, biztonságos visszavonható sablonalkalmazás. |
| T30 | P2 | Natív/offline WPF UX, ikon, tálca és funkcióőszinteség | App.xaml.cs; MainWindow.xaml.cs::Navigate; MainWindow.xaml; Core/SafeSupportBundle.cs::ManagerFeatureMatrix | T19,T20 | X tálcára visz, egyszeri példány, billentyűzet/navigáció/DPI teszt; történeti dashboard-szám nem élő adatként jelenik meg; offline helyi megfigyelés működik. |
| T31 | P3 | Kova további, nem ASA funkcióinak tudatos határa | Minecraft-moduloknak nincs C# ASA-megfelelőjük; új modul csak külön termékdöntésre | – | Minecraft-kezelés nem kerül automatikusan az ASA-roadmapbe; opcionális scope és fenntartási költség dokumentált. |
| T32 | P2 | Mentéskatalógus, retenció és karantén GUI | Core/BackupRetention.cs::Preview/Quarantine; Core/LocalBackupCatalog.cs; MainWindow.OperationalCalendar.cs::ShowRetentionPreviewControls | T07,T09 | Másik task, kézi és védőmentés nem törlődik automatikusan; karantén visszavonható; minden restore point tényleges próbaeredményt mutat. |
| T33 | P3 | Választható Manager UI-bővítési pont | új IManagerPanelExtension; MainWindow.xaml.cs::Navigate | T01,T26,T30 | Aláírt/allowlist bővítmény, szűk adat- és műveletjogok; bővítményhiba nem blokkol lifecycle-t; nem ASA plugin hot-reload. |

## F. Külön izolált valódi ASA/karaktertesztet igénylő elfogadások

| Próba | Előkészítés | Negatív esetek | Kötelező bizonyíték |
|---|---|---|---|
| Process/Loader/readiness | Külön telepített mintavilág és tesztprofil | PID reuse, régi loader, gyerekcsere, RCON késés, startup crash | EXE/start/PPID/args és ugyanazon munkamenet API/ASA/RCON/join eseményei |
| SaveComplete | Tesztkarakter/tribe és ellenőrzött save adapter | RCON megszakad, hook hiányzik, két párhuzamos SaveWorld, lemezírás késik | Pontos save művelet és process korreláció, fájl- és alkalmazásszintű utóellenőrzés |
| Stop/Restart | Igazolt save és ownership | DoExit-válasz nélkül kilépés, timeout, idegen process, kézi stop | ExitConfirmed előtti új spawn kizárt; timeout NEEDS_OPERATOR, automata Kill nélkül |
| Kétgépes Store/transfer | Két elkülönített host/profil és tesztmegosztás, nem éles UNC | Hiányzó peer, félbeszakadt travel, eltérő hash/session, lock, writer verseny | Ugyanaz a tesztkarakter/tribe egyszer, sértetlenül tér vissza; nincs új konverzió |
| Backup/SQL/restore | Tíz szintetikus/test map, három külön teszt-DB | Megszakadó dump/copy, elégtelen hely, hiányzó manifest, részleges host | Egy konzisztenciahatár, teljes összetevőleltár, visszaállítható batch és rollback |
| Steam/API/plugin/mod frissítés | Staged teszttelepítés és teljes védőmentés | Sérült ZIP/DLL, hibás függőség/premium/crossplay, data migration | Változatlan config/adat/licenc, tényleges betöltött verzió és működő rollback |
| Scheduler/watchdog | Tesztprocessz és mesterséges óra/hálózathiba | DST, három RCON-hiba, stale/future idő, restartvihar, maintenance | Tartós egyszeri claim, egy riasztás/incidens, nincs tiltott save/kill/replay |
| Setup/Updater/GUI | Harmadik asztali tesztgép | Frissítés közbeni crash, DPI, két kattintás/listafrissítés, X/tray | Futó teszt-ASA tovább él; UI kattintható marad; régi verzió és adat megőrizhető |

A tesztadatok és eredeti éles karakterek nem keverhetők. Az éles gépről kapott állapot-pillanatkép nem teljes restore-fixture.

## Hiányzó bemenetek / feloldási feltételek

1. HP mind a négy helyi Store-v1 rekordjának konzisztens privát másolata és az ugyanahhoz tartozó közös koordináció. Az UK-HOME-01-re most nem kapcsolódtunk.
2. Igazolt read-only MariaDB-fiók/sémaexport, jogosultsági szerződéssel. Meglévő pluginjelszót nem emeltünk ki és nem használtunk új SQL-kapcsolathoz.
3. Kova renderer aktuális menüleírása vagy biztonságosan megfigyelhető felület. A 44 helyi modul nem tartalmazza a teljes távoli fő-UI-t.
4. Reprodukciós, nem éles modlistát használó GUI-fixture a kattintási hibához. Felhasználói hibajelzés önmagában nem gyökérok.
5. A masterben felsorolt további korábbi dokumentumok önálló bemenetei: teljes felületterv, kutatás/adatigény, végleges terv és teljes 61-műveletes korábbi lefedési dokumentum. A jelenlegi katalógus és repóbeli összefoglalók nem helyettesítik az el nem küldött eredetiket.

## Az audit végrehajtási korlátja

Nincs kódfrissítés, migration, Store-session létrehozás, locktörlés, SQL-módosítás, RCON-küldés vagy saját Manager-futtatás. Ez az ág kizárólag szanitizált dokumentációt tartalmaz.
