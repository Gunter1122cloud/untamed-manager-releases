# YARK → Obsidian Command: teljes projektleltár és funkcionális paritás-audit (2026-11-25)

## Rögzített források és hatókör

- YARK: https://github.com/gabomarin/yark/tree/a9b5e0be72b3bd3618a917caf207bbd80c3451ef (GPL-3.0). Teljes Git tree: 1460 fájl, 144 backend TypeScript, 611 renderer TypeScript/TSX és 207 tests/ TypeScript/TSX. A teljes fájlfát és a fő domain-határokat felmértük; a releváns dokumentáció (18 dokumentum) és a fő lifecycle/Steam/RCON/backup/API/mod/INI/cluster/log/metrics/update források célzott vizsgálata történt. Ez funkcionális architektúra-audit, nem mind az 1460 fájl soronkénti security review, és nem futtatott upstream teszt.
- Obsidian: https://github.com/Gunter1122cloud/untamed-manager-releases/tree/b8ff92ad962eea62b9afd4ec6132c4a3b8aa3991 (#192 draft branch). Teljes Git tree: 647 fájl. Célzott összevetés a shipping desktop/native bridge/UI/shared core réteggel; legacy WPF UI nem fejlesztési cél.
- #189–#192 jelenleg draft, nem main. Nem release-ready megállapítás; e dokumentum nem módosít működési kódot és nem érinti éles szervereket.
- YARK referenciateszt ≠ Obsidian live teszt. YARK saját dokumentációja a real-host próbát külön kezeli a CI mockoktól. A referencia működési cél ettől függetlenül kötelező: **REFERENCIÁVAL IGAZOLT – NÁLUNK IS KÖTELEZŐEN MŰKÖDNIE KELL**. A tényleges helyi implementáció/teszt állapot külön mező.
- Licenc: saját C# adaptáció elsődleges; tényleges GPL kód átvétel esetén az érintett terjesztés GPLv3 kötelezettségeit (licenc, copyright, megfelelő forrás, notices) teljesíteni kell. A külön wiki-adatok/licencek és YARK proxy/branding nem másolhatók automatikusan. Nem vállalunk hallgatólagos licencváltást.
- Kötelező termékinvariáns: WebView2/React + .NET 10, két vagy több önálló host, helyi ownership, JSON/fájlos állapot (nincs SQL), Store-v1/UNC transfer adatok byte preservation, nem új approval workflow, nem production tesztadat.

## Fejlesztési lista

P0 = a napi szerverüzemeltetés akadályait megszüntető javítás; P1 = fontos funkcióparitás; P2 = üzemeltetési/UX/teljesítmény. A forrásfájlok YARK esetében src/backend/ alatt értendők, ha nincs külön prefix; Obsidian esetében desktop/, desktop-ui/ vagy wpf/Core/. Az alábbi sorrend javasolt, nem kész funkciók listája.

### P0-01 — Egyetlen helyi lifecycle állapotgép
- YARK: infra/process/process-start.ts; process-ready-wait.ts; process-graceful-stop.ts; orchestration/instance-lock-manager.ts.
- Obsidian: DesktopFreshAsa*Bridge, DesktopHistorical*Start/Stop/RestartBridge, UntamedOperationCoordinator.
- Helyi állapot: Részleges, több párhuzamos út.
- Kötelező fejlesztés: Közös natív Start/Stop/Restart/SaveWorld parancsmotor; a régi Store és új map adapter csak a tulajdon- és launch-azonosságot adja. Újraindítás előtt SaveWorld, DoExit, folyamatkilépés, mentés és egyetlen Start; semmi dupla zárolás vagy rutinszerű emberi jóváhagyás.
- Elfogadás: Minden UI/Discord/ütemező ugyanazon motoron; API és natív, siker/hiba, megszakítás, két map párhuzamosan, shared install kizárás.

### P0-02 — Fázisalapú automatikus recovery
- YARK: orchestration/critical-job-recovery.ts; domains/updates/update-critical-jobs.ts; domains/backups/backup-critical-jobs.ts.
- Obsidian: DesktopRestartReconciler, AsaManualRestartJournal, UntamedOperationCoordinator, LegacyStoreV1SaveEvidenceJournal.
- Helyi állapot: PR190 részleges.
- Kötelező fejlesztés: Tartós run ID, cél, PID+start epoch, fázis, előző bizonyíték; előküldési hibát lezárni, befejezettet adoptálni, bizonyítottan folytathatót újraindítani, kétes tényleges mutációt maphoz kötni. A hibás map ne tiltsa az egész hostot. Eredeti napló archiválása.
- Elfogadás: Manager crash minden fázisban; nincs duplikált ASA, wipe, restore, vagy idegen PID leállítás; jó mapok tovább működnek.

### P0-03 — RCON közös session és reconnect
- YARK: infra/rcon/rcon-client.ts; rcon-session-manager.ts; domains/instances/instance-rcon.ts.
- Obsidian: RconProtocol, DesktopRconAdminBridge, DesktopHistoricalRconAdminBridge, DesktopRconReadOnlyBridge.
- Helyi állapot: PR192 részleges, one-shot.
- Kötelező fejlesztés: Maponként egy soros autentikált session, session generation, új process epoch érvényteleníti, újrakapcsolódás 2–30 s max 10 kísérlet; read-only próba új kapcsolatból; timeout után socket zárás; azonosított válasz, üres ACK, UTF-8 és több frame. Mutáció nem ismételhető vakon.
- Elfogadás: Széttöredezett csomag, auth fail, üres ACK, stale válasz, reconnect, restart, ListPlayers polling és admin együtt; helyi listener tulajdon ellenőrzés.

### P0-04 — SaveWorld és graceful stop
- YARK: infra/process/process-graceful-stop.ts; domains/instances/instance-stop.ts; docs/server-lifecycle.md.
- Obsidian: LegacyStoreV1GracefulStop, AsaGracefulStop, AsaWorldSaveEvidence, DesktopRconReadOnlyBridge.
- Helyi állapot: PR192 dupla lease javítás; mentési bizonyíték részleges.
- Kötelező fejlesztés: SaveWorld exact parancs, külön dispatch/ACK/stabil .ark állapot; ne várjunk kizárólag nem létező SaveComplete logra. DoExit után valódi process exit, log close csak jel, saját PID ellenőrzés. Stop kérés starting alatt is lekezelhető. YARK force-kill fallback nem kerül át automatikusan.
- Elfogadás: Nincs log marker, mentés késik, kapcsolat megszakad, Loader megmarad, PID cserélődik; tiszta stop; mentési adatvédelem.

### P0-05 — Restart utáni dinowipe
- YARK: domains/maintenance/maintenance-restart-runtime.ts; tests/maintenance-restart-runtime.test.ts.
- Obsidian: DesktopScheduledAsaOperationsSupervisor, DesktopFreshAsaRestartBridge, DesktopRollingSteamSupervisor.
- Helyi állapot: PR192 ütemezett út, még nincs manuális/rolling és save-first.
- Kötelező fejlesztés: Közös post-restart hook (konfigurált opt-in), új ASA PID, 60 s RCON probe, legalább 20 s settle, opcionális SaveWorld, egyszeri DestroyWildDinos, külön wipe eredmény. Manuális, batch, ütemezett és Steam restart ugyanazt a hookot használja.
- Elfogadás: API Loader és natív, wipe off/on, save-first, késői RCON, cancellation, restart siker/wipe hiba; nincs dupla wipe.

### P0-06 — SteamCMD tartós letöltési sor
- YARK: domains/updates/update-perform.ts; update-queue.ts; update-critical-jobs.ts.
- Obsidian: SteamSerialMaintenanceQueue, DesktopRollingSteamSupervisor, DesktopStoppedSteamUpdateBridge.
- Helyi állapot: PR191 idle checkpoint és retry; dispatched recovery hiányos.
- Kötelező fejlesztés: Map/install szintű durable fázis: preflight, backup, download, validate, files-applied, restart, ready, rollback. Fázisspecifikus retry 3 próbálkozás, backoff; restart manager után felismerés. Cancel csak biztonságos checkpointnál, külön sikertelen map eredmény.
- Elfogadás: Megszakítás minden fázisban, hálózat, disk full, külső SteamCMD, nincs két update egy hoston; többi map működik.

### P0-07 — SteamCMD cache és verzió
- YARK: domains/updates/steamcmd-content-cache.ts; steamcmd-run.ts; domains/instances/official-ark-probe.ts; shared/server/server-update-status.ts.
- Obsidian: SteamSharedPayloadCache, AsaSteamBuildCatalog, SteamStoppedMapUpdater, DesktopRollingSteamSupervisor.
- Helyi állapot: PR191 részleges.
- Kötelező fejlesztés: Hostonként közös depot/content, rolling és batch közös verified payload; public build compare, local >= public current; force refresh, TTL és last-known-good, cache cleanup. SHA256 allowlist, maponként Valve validate, nincs Saved/API/mod felülírás. Cache fallback csak ismert pre-mutation feltételnél.
- Elfogadás: Öt map ugyanazon build, 1 letöltési forrás, sérült cache, eltérő build, kevés hely, config/plugin hash változatlan.

### P0-08 — Steam rollback és valódi haladás
- YARK: domains/updates/update-perform.ts; steamcmd-disk-progress.ts; steamcmd-control.ts.
- Obsidian: SteamStoppedMapUpdater, SteamFreshInstall, RollingSteamSettings.
- Helyi állapot: Hiányos.
- Kötelező fejlesztés: Pre-update world backup stabil ID, biztonságos rollback; külön world/config és binary rollback, régi bináris csak ténylegesen megőrzött csomagból. Steam console/log tail, appmanifest bytes, hitelesített progress; pause/cancel/retry állapotok, nem hamis százalék.
- Elfogadás: Hálózati hiba, partial download, copy fail, rollback fail, cancel; újraindulás csak ellenőrzött binárissal.

### P0-09 — Reattach és crash recovery
- YARK: infra/process/left-running-reattach.ts; windows-process-identity.ts; domains/instances/auto-start.ts; domains/crash-recovery/crash-recovery-plan.ts; crash-recovery-service.ts.
- Obsidian: DesktopAutomaticAsaDiscoverySupervisor, DesktopRunningAsaAdoptionBridge, DesktopWatchdogRecovery.
- Helyi állapot: Meglévő részleges.
- Kötelező fejlesztés: Manager startkor reattach az autostart előtt; PID+epoch+path+args+loader; missing/stale/inaccessible külön; inaccessible nem újraindítás. Exit ok, intentional stop, maintenance, cooldown/backoff, stabil futás után retry reset. Ne egyetlen szűk fatal code-lista legyen a crash sole trigger.
- Elfogadás: Manager restart futó 5 map mellett, valódi crash, user close, stop, PID reuse, inaccessible WMI, crash-loop, API child crash.

### P1-10 — Map telepítés/import/clone
- YARK: domains/instances/import-existing-install.ts; install-health.ts; instance-clone.ts; clone-install-copy.ts; move-install-service.ts.
- Obsidian: DesktopSteamFreshInstallBridge, DesktopRunningAsaRegistrationBridge, DesktopInitialMapSetupBridge, LocalAsaInstallLocator.
- Helyi állapot: Meglévő, bővítendő.
- Kötelező fejlesztés: Nested root feloldás, EXE/manifest/INI map token, saved folder, mod és runtime port javaslat. Incomplete/ready/stale/imported állapotok. Clone config-only vagy teljes, move csak stopped, port ütközés előellenőrzés. Dinamikus új map és mod-map ID; ne hardcode 5+5.
- Elfogadás: Új Event/custom map, nem teljes install, import futás közben, harmadik host, azonos gyökér elutasítás.

### P1-11 — Backup csomag és reconcile
- YARK: domains/backups/world-snapshot.ts; backup-create-pipeline.ts; backup-reconcile.ts; backup-package.ts.
- Obsidian: LocalBackupWorkflow, DesktopWorldBackupBridge, LocalBackupCatalog.
- Helyi állapot: Meglévő offline hash; live workflow bővítendő.
- Kötelező fejlesztés: Világ/profile/tribe/INI/plugin scope; map-token folder feloldás; stable copy, ZIP/manifest és SHA256, félbemaradt csomag felismerés, disk inventory reconcile, export/import. Live backup csak SaveWorld/stabil snapshot alapján; 0 KB embedded store adatot nem szűrni vakon.
- Elfogadás: Félbeszakadt backup, eltérő map folder, profil/tribe/0KB preserve, hiányzó archive, restore digest.

### P1-12 — Backup ütemezés/retention/player snapshot
- YARK: domains/backups/backup-schedule-runtime.ts; backup-retention.ts; player-session-watcher.ts.
- Obsidian: DesktopScheduledStoppedBackupSupervisor, ManagedBackupRetention, DesktopWorldBackupBridge.
- Helyi állapot: Stopped-only schedule, további live és player mentés kell.
- Kötelező fejlesztés: Időszakos live world backup, keep-last/age/quota/pinned, hiányzó archive nem eszik slotot, per-player join/leave snapshot opcionális. Csak Manager saját archive törölhető, shared transfer soha.
- Elfogadás: Retention pin, disk pressure, no players, player profile késik, maponként hiba izoláció.

### P1-13 — Restore és cluster mentés
- YARK: domains/backups/backup-restore-apply.ts; backup-critical-jobs.ts; docs/backups.md.
- Obsidian: MultiHostCoordinatedRestore, DesktopMultiHostClusterBackupBridge, DesktopOfflineAsaRestoreBridge.
- Helyi állapot: Meglévő offline kétgépes erősebb alap; live acceptance nem igazolt.
- Kötelező fejlesztés: Pre-restore safeguard, manifest verification, staging, atomic replace/rollback, fázisazonos recovery. Két host coordinated restore és shared transfer csak külön saját adapter; YARK egygépes restore nem helyettesíti.
- Elfogadás: Test-owned restore, SHA256, no overwrite unrelated data, A→B→A survivor/item/dino transfer validation külön.

### P1-14 — ASA API és pluginok
- YARK: domains/asa-api/asa-api-cache.ts; asa-api-install.ts; asa-api-plugin-install.ts; asa-api-inject.ts.
- Obsidian: DesktopAsaApiCoreBridge, DesktopPluginZipBridge, AsaApiOwnedInstaller, AsaApiLaunchPolicy.
- Helyi állapot: Meglévő loader és cache.
- Kötelező fejlesztés: Release/version-aware cache, staging és partial cleanup, loader/version.dll mode szétválasztás, plugin zip gyökér felismerés, stop-gated install/remove/rollback, per-plugin hibajelzés. Meglévő Loader és plugin kompatibilitás elsőbbséget élvez.
- Elfogadás: ASA API-val/nélküle indítás, cache reuse, plugin update/rollback, rossz zip, DLL lock, megőrzött JSON.

### P1-15 — Mod Center
- YARK: domains/mods/mods-service.ts; asa-mod-local-files.ts; docs/mods.md.
- Obsidian: DesktopSharedModInventory, DesktopCurseForgeDiscoveryBridge, DesktopHistoricalLiveModSync, DesktopModProfileBridge.
- Helyi állapot: Meglévő, parity bővítés.
- Kötelező fejlesztés: Enabled/disabled/passive, ordered IDs, mod-map ID külön, dependency és platform compatibility, missing local files redownload, metadata batch/cache, staged next launch; sync több mapra, egyéni load order. Saját CF kulcs/proxy, YARK szolgáltatását nem terhelni.
- Elfogadás: Mod ID/order tartós, passive flag, disabled no spawn, CF 429, dependency, custom cosmetic incompatible, start utáni valós mod állapot.

### P1-16 — INI szemantikus editor
- YARK: domains/config/ini-service.ts; ini-compose.ts; shared/ini/ini-semantic-ops.ts; docs/ini-metadata.md.
- Obsidian: DesktopIniBridge, SafeConfigEditor.
- Helyi állapot: Meglévő text/fingerprint/rebase.
- Kötelező fejlesztés: Szemantikus section/key/occurrence patch, raw/visual egyezés, pending edit running alatt, flush stop/start előtt, conflict merge, Manager owned kulcsok. Megjegyzés/encoding/unknown keys megőrzés; saját forrású catalog licenc szerint.
- Elfogadás: Külső INI edit, duplicate keys, engram/loot összetett, port drift, pending restart, változatlan unrelated rows.

### P1-17 — Konfig másolás / sablon
- YARK: domains/config/config-transfer-service.ts; cluster-ini-template-apply-service.ts.
- Obsidian: LocalProfileTransfer, DesktopModCopyBridge, DesktopIniBridge.
- Helyi állapot: Részleges.
- Kötelező fejlesztés: Multi-target kiválasztás, merge/replace, preview diff, fingerprint és backup; identity/paths/ports/saves/cluster membership/password alapból nem másolt. Maponkénti eredmény; helyi tulajdonos ír.
- Elfogadás: Külön mapok külön portok, eltérő INI, egy target hiba, rollback.

### P1-18 — Cluster health és portok
- YARK: domains/cluster/compliance.ts; infra/process/host-port-probe.ts; docs/clusters.md.
- Obsidian: DesktopClusterRegistryBridge, AsaLocalPortPreflight, ClusterTransferConsistency.
- Helyi állapot: Obsidian N-host előny; YARK compliance UX.
- Kötelező fejlesztés: Közös ClusterID/dir, mod divergence warning, port conflict error, permission/readability, link/UNC státusz. Ne keverjük össze config compliance-t és valós travel bizonyítékot. Session-only port override csak explicit kérésre és runtime auth követéssel.
- Elfogadás: 3 host / 12+ map, port clash, eltérő mods, peer down, local operations not frozen.

### P1-19 — RCON játékoskezelés
- YARK: domains/instances/instance-rcon.ts; admin-list.ts; ban-list.ts; docs/rcon.md.
- Obsidian: DesktopBanManager, DesktopRconAdminBridge, HistoricalAdminRconPanel.
- Helyi állapot: Részleges.
- Kötelező fejlesztés: KickPlayer/BanPlayer/Unban exact verb, EOS id, admin whitelist, BanListURL warning; session history per map, unknown players nem 0, ListPlayers egyetlen poll queue. Ne bypassolt lifecycle raw DoExit legyen.
- Elfogadás: Unicode, EOS ID, üres lista, ban/unban, remote ban URL, restart után session history.

### P2-20 — Logok és hibadiagnosztika
- YARK: domains/logs/logs-service.ts; logs/log-retention-scheduler.ts; docs/logs.md.
- Obsidian: LocalLogCenter, DesktopOperationsCenterBridge, EventJournal.
- Helyi állapot: Meglévő, bővítendő.
- Kötelező fejlesztés: Mit/Miért/Hol/Mit tegyen, phase/runID/PID/build, safe redacted excerpt, export, retention; ASA runtime logs read-only. Ne NeedsOperator általános üzenet, hanem tényleges ok.
- Elfogadás: Hibás Steam, RCON auth, mod fail, API loader, no disk, redaction, log retention.

### P2-21 — Dashboard és erőforrás
- YARK: domains/instances/process-metrics-sampler.ts; infra/process/windows-process-sample.ts; docs/component-structure.md.
- Obsidian: PremiumDashboard, LocalHostResourceProbe, DesktopRecurringReadOnlySupervisor.
- Helyi állapot: Meglévő.
- Kötelező fejlesztés: CPU/RAM/uptime/player live és stale/unknown; metrics csak ahol kell, diff push, bounded polling, generation gate, külön host szín. Meglévő premium design megtartása.
- Elfogadás: 10+ map, UI responsiveness, tab change, peer outage, no fake zero.

### P2-22 — Desktop shell és frissítő
- YARK: src/main/app-update-service.ts; quit-gate.ts; app-tray.ts; docs/settings.md.
- Obsidian: DesktopObsidianUpdateInstallBridge, DirectoryUpdateTransaction, DesktopStartupMode.
- Helyi állapot: Meglévő.
- Kötelező fejlesztés: Leave-running / tray / Windows autostart, quit gate aktív tranzakció alatt, manager update után reattach; immutable Setup/Portable SHA256 és Authenticode megtartása, old build rollback.
- Elfogadás: Update futó ASA mellett, interrupted installer, wrong SHA, reattach, no forced server restart.

### P2-23 — Discord és értesítés
- YARK: src/main/discord-webhook-service.ts; docs/settings.md.
- Obsidian: DesktopDiscordControlApi, DesktopDiscordNotifications, DesktopDiscordBotRuntime.
- Helyi állapot: Obsidian bővebb, nem lecserélendő.
- Kötelező fejlesztés: YARK event filter és rate-limit tanulság; egységes native job esemény, owner/admin azonos jog, map/Event kártya, státusz read-only, idempotent gomb/értesítés.
- Elfogadás: Bot restart, duplikált gomb, jogosultság, külön map státusz, failed wipe ≠ failed restart.

### P2-24 — Teszt- és integrációs stratégia
- YARK: docs/e2e-validation.md; .github/workflows/ci.yml; .github/workflows/real-host-validation.yml.
- Obsidian: desktop/Tests, wpf/Testing, desktop-ui/tests, Windows CI.
- Helyi állapot: Meglévő izolált tesztek; még hiányzó acceptance.
- Kötelező fejlesztés: Source-backed acceptance matrix, test ownership, per-route tests, actual disposable Windows ASA test, full runtime release evidence. Nem új általános approval gate: a funkció normál futása automatikus.
- Elfogadás: Windows CI + célzott test-owned ASA: API/vanilla start, save/stop/restart/wipe, Steam, restore, travel; a konkrét merge SHA kiadása.

## Mit nem másolunk változtatás nélkül

YARK SQLite repository, Electron shell, egygépes process-lock, világmentést nem ellenőrző optimista ACK, force-kill fallback, cache copy amely csak Saved-et zárja ki (API/plugin felülírás lehetséges), közösségi proxy/branding, illetve a forrás által is ambiguity esetén alkalmazott blokkolás. Obsidian megtartja a natív tulajdonellenőrzést, védett mentést, hitelesített LAN/Discord és N-host Store migrációt. A cél nem a hibák elrejtése vagy minden tiltás mechanikus törlése, hanem automatikus, állapottól függő helyreállítás és a hibás map izolációja.

## Fejlesztési sorrend és kiadási szerződés

1. #189–#192 exact HEAD CI és integráció ellenőrzése, P0-01–05 közös lifecycle/recovery/RCON, meglévő patch regression javítása.
2. P0-06–09 Steam/reattach/watchdog közös durable queue és phase recovery; rutin operator gate megszüntetése; per-map isolation.
3. P1-10–13 map onboarding és backup/restore; P1-14–19 ASA API/mod/INI/cluster/RCON.
4. P2-20–24 diagnosztika, UI, Discord és release acceptance. Független PR-ek ahol lehet, a meglévő stack függőségeit explicit rögzítve. Minden PR: konkrét source pointer, actual files changed, synthetic regression, exact HEAD Windows CI, merge SHA release; real ASA test evidence külön. Normál funkció nem marad állandó manuális approval mögött. Kétes tényleges mutációt nem replayelünk, de nem állhat meg emiatt az összes map.

## Elsődleges upstream dokumentumok

- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/server-lifecycle.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/rcon.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/maintenance.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/updates-steamcmd.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/critical-job-recovery.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/backups.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/asa-api.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/mods.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/ini-metadata.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/config-transfer.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/clusters.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/logs.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/settings.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/e2e-validation.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/ipc-validation.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/credential-threat-model.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/launch-options-catalog.md
- https://github.com/gabomarin/yark/blob/a9b5e0be72b3bd3618a917caf207bbd80c3451ef/docs/release-downloads.md
