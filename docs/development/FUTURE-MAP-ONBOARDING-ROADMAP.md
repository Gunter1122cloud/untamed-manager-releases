# Obsidian Command – jövőbeli ASA mapok, cluster és Store onboarding roadmap

> Cél: amikor egy új ASA map megjelenik, az üzemeltető a helyi Managerben **Új map hozzáadása** felületet használja. A térkép neve nem lehet beégetett feltétel. A Manager soha nem találhat ki Store-jogosultságot vagy a másik hostnak nem küldhet írási parancsot.

## Négy külön igazolási szint

| Fázis | Eredmény | Natív megoldás |
| --- | --- | --- |
| ASA telepítése és egyedi level megadása | Helyi telepítési profil | FreshAsaSteamWizard, DesktopSteamFreshInstallBridge, DesktopInitialMapSetupBridge |
| Élő/történeti map regisztráció | Megfigyelés vagy külön igazolt lifecycle-jog | LocalRunningAsaDiscovery, DesktopRunningAsaRegistrationBridge, DesktopHistoricalStoreImportBridge |
| Cluster metaadat és transfer azonosítás | Cluster-regiszter, nem Store-writer jog | DesktopClusterRegistryBridge, ClusterRegistry, StorePathIdentity |
| Store indítási profil és writer/session | Natív Store bizonyíték szükséges | FreshStoreEnrollmentPolicy csak új, üres, nem történeti clusterben; eredeti Store-v1-hez külön mechanizmus |

A Store-indítási jelző és a cluster-regiszter bejegyzése nem bizonyítja a régi Store-v1 writer/session/backup/karakterprotokoll tagságát. A freshStore.enrollmentApply csak indítási profilt készít elő, NEM hajt végre világkonverziót.

## R0 – közös UI (első PR)

- Szerverek → Új map hozzáadása.
- A SteamCMD varázsló egyedi mapnévvel és egyedi pályakulccsal működik; a tíz ismert map csak javaslat.
- Meglévő ASA-mappa / automatikusan megfigyelt folyamat / történeti kiegészítő map / cluster egy helyről elérhető.
- Az eredeti öt Store-mapon kívüli külön forrás látszik, de hiányzó sharedSession esetén a régi Store-session jogosultsága nem található ki.
- Clusterhez hozzáadás előtt az összes korábbi tag megmarad a kérésben; pontosan 1 hozzáadás / 0 áthelyezés / 0 eltávolítás engedélyezett. A natív preview utáni jóváhagyás az egyetlen írás.
- Új, üres map és nem történeti cluster esetén a meglévő natív FreshStoreEnrollmentPanel elérhető; az adatátalakítást nem végzi el.
- E fázis NEM tanúsít új mapot a régi KovaCluster Store-v1 writer sessionjéhez.

## R1 – univerzális natív discovery/preview/apply

- Egyetlen térkép-onboarding állapot/előnézet/alkalmazás protokoll delegál a már létező védett bridge-ekhez; nincs külső launcher.
- Dinamikus pályakulcs, indítási forma, ASA bináris, WMI PID/startTime, Loader parent, portok, build, modok, Store és cluster; nincs fix 5/10/12 maplimitet jelentő üzleti logika.
- Státuszok: regisztrálható / csak megfigyelhető / külön Store-konverzió bizonyítandó / ütköző vagy bizonytalan. Hibánál magyar, egyetlen javítási teendő.
- Tartós pontos claim, install/cluster lease, owner/session/source hash és generációazonosító, idempotens újraellenőrzés; összeomlás után nincs vak újraküldés.
- A Discord dinamikus státuszban láthatja az új mapot, de gombos vezérlés csak igazolt helyi lifecycle tulajdonjoggal lehetséges.

## R2 – új map az eredeti KovaCluster Store-v1 mellett

- A külön konvertált/később hozzáadott map eredeti GUID, root, map key, saját active Store és backupId, konverziós provenance, cluster+transfer fizikai azonosság és aktuális két host koordinációja külön bizonyítandó.
- Hiányzó sharedSession nem pótolható kitalált értékkel. Az eredeti öt kiválasztott Store writer és peer DONE védelme nem lazulhat.
- Event/Chillhub példa: GUID a6c1f1f6-04d1-46e7-bf2d-e216d6b8c85d, level Asa_N_Chill_Hub, Game UDP 7809, Query UDP 27046, RCON TCP 37015. A Query PID tulajdonosa 2026-11-22-én nem volt igazolva; helyes Windows/ASA UDP ellenőrzést kell kifejleszteni, nem kikerülni.
- Valós peer/transfer pillanatkép, per-map backupId, írási konverzió előtti védett, visszaállítható mentés, tartós journal/rollback, egyetlen Store writer tranzakció.
- Konverzió csak akkor, ha valóban szükséges és előzetesen hitelesített, leállított forrásról külön jóváhagyást kapott. Soha ne írjon át futó világot a gomb automatikusan. Stop csak igazolt SaveWorld completion után.
- Kritikus források: LegacyStoreV1PinnedSource.RequireSupplemental, LegacyStoreV1LiveReadGate.InspectOriginalActive, DesktopHistoricalStoreImportBridge.PrepareSupplemental, RconPortOwnership Query ellenőrzése.
- Fail-closed: sikertelen ellenőrzés nem módosít profilt, Store-t, transferútvonalat, karaktereket vagy futó ASA folyamatot.

## R3 – regresszió, kiadás, tényleges elfogadás

- Windows fixture: jövőbeli egyedi level; original 5 + későbbi hatodik/hetedik/nyolcadik külön Store-rekord; pozitív hiteles konverzió és hamisított provenance negatív.
- N-host dinamikus map és botlista, az eredeti öt map config/ütemezés/world fingerprintje változatlan.
- Negatív: GUID/port/root/INI/Store/UNC/PID ütközés; hiányzó query tulajdon; hiányzó backupId, rossz peer, sérült journal, ismételt gomb, Store-writer jog véletlen megadása.
- Exact-HEAD Windows + Node CI; utána integráció, új main Preview Setup/Portable és SHA256SUMS, majd tulajdonosi fokozatos teszt.
- Tilos a fejlesztéshez éles ASA Stop, Restart, SaveWorld, Wipe vagy crash-szimuláció. Az Event korábbi, csak sikeres SaveWorld utáni leállítási engedélye nem általános felhatalmazás.

## Késznek csak akkor tekinthető

Ha az új map ténylegesen telepíthető, regisztrálható, biztonságosan hozzárendelhető, Store-kompatibilitása külön bizonyított, a Manager + Discord jogosultságai helyesek, és újabb mapnál sem kell kódot írni a pályanévhez. Egyetlen UI-kattintás vagy Store flag önmagában nem bizonyítja ezt.


## N-host new Store transfer admission
New nonhistorical empty-map enrollment accepts remote membership only after every registered remote owner returns a fresh authenticated status with exact local member IDs and the same UNC filesystem directory fingerprint. Persist a cluster/directory-bound local admission pin; an admitted map does not require remote uptime for local lifecycle. No raw paths or credentials in evidence, no remote write commands, no conversion or legacy ACTIVE/DONE rewrite. Old local profiles without the pin must prove transfer agreement when remote members appear. Actual ASA character travel and historical-cluster enrollment remain separate.
