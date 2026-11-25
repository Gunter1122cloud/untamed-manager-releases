# Discord műveleti események – API v1 és botátadás

## Hatókör és állapot

Ez a fejlesztési ág a natív Manager-futásokat a már meglévő, TLS-pinnelt Control API-n keresztül teszi lekérdezhetővé. A Windows alatt futó éles bot forrása nem része a repositorynak; az `integrations/discord-operation-feed.mjs` átadható modul, nem igazoltan telepített botfrissítés. A teljes lefedettség jelzője szándékosan false. Új botot, HP-fájlmegosztást, szervervezérlési jogosultságot nem igényel.

## Hitelesítés és protokoll

A meglévő Control API 4 bájtos unsigned big-endian hossz + UTF-8 JSON TLS framingjét használd. A kliens az eddigi hitelesített hosttransportot és hostonkénti credentialt/TLS-pint használja. A meglévő guildId, statusChannelId, serviceUserId, sentUtc, requestId, credential és roleIds mezők szükségesek, az actor a serviceUserId a status csatornából. A roleIds nem jogosítja fel a service accountot preview/apply-ra. A transport ne naplózza a teljes envelope-ot vagy titkot.

### events

Request új mezői: kind = events, cursor = null vagy az előző nextCursor változtatás nélkül, limit = 1..100.

Válasz data:
- version: 1
- hostId: helyi Manager identitása
- epoch: UUID vagy null; journalváltás detektálására
- oldestSequence, latestSequence: számszerű határok
- state: ok, notInitialized, cursorInvalid, journalChanged, publicationGap
- gap: true esetén nincs teljes időszak igazolva
- more: további oldal elérhető
- nextCursor: átlátszatlan checkpoint; csak a tartós outbox-commit után menthető
- events: sequence, eventId, operationId, mapIds, opcionális mapNames, operation, origin (scheduler/discord/manager/unknown), actorId (ha igazolt), eventUtc, phase, result, evidence, opcionális scheduledUtc és nativeJobId.

A null cursor a jelenlegi vízjelre baseline-ol; nem küld régi riasztást. Ha nincs journal, empty cursor keletkezik: a következő első esemény ettől még beolvasható. A begin csak külön, tudatos történeti leolvasásra használható; nem az automatikus bot polling alapértéke. Cursor lejárat, epochváltozás, gap esetén nincs automatikus checkpoint-reset. A journal maximum 4096 eseményt és 8 MB-ot tart meg. A gap marker tartós lehet, ezért vizsgálat szükséges; teljes tárolóhiba esetén még a marker mentése is elveszhet. A read route nem hív Invoke-ot, preview/apply-t, RCON-t vagy szervervezérlést.

A scheduler eventUtc aktuális megfigyelési idő, nem az alapfolyamat utólag feltételezett fázisideje. A save-result, stop-result, new-process a natív strukturált bizonyíték első megfigyelése; readiness külön új PID/aktuális Join-jelzés és listener után; wipe-result csak ellenőrzött küldési bizonylat után. A levelezés nem tanúsít fsync-et, játékbeli hatást vagy kliensbelépést.

### scheduleHistory

kind = scheduleHistory, afterRunId = null vagy előző nextRunId, limit = 1..100.
data: version, hostId, origin, historical, state, gap, retentionMayBeTruncated, nextRunId, more, runs[]: runId/mapId/kind/scheduledUtc/claimedUtc/finishedUtc/status/evidence/currentMapName (utóbbi jelenlegi ismert címke, nem történeti név). A natív history legfeljebb 4096 rekord. Az eredeti indulási idő csak akkor jeleníthető meg, ha valóban rögzítették. A claim ideje NEM az ASA új PID ideje. Régi összetett kódból nem generálunk köztes eseményidőpontokat.

### Duplikáció és hiány

A Discord Apply audit requestId-ja ugyanaz az operationId, mint a kapcsolt natív worker eseményei; a nativeJobId külön korreláció. Az eredeti audit claimed, natív visszaigazolás és lezárás ugyanahhoz a futáshoz tartozik. Batch esetén a belső lifecycle worker eseményei el vannak nyomva, a batch külön natív aggregate receiptet publikál. Nem megengedett a régi pollJobs() és az új event-feed párhuzamos adminnapló/riasztás küldése ugyanarra az operationId-ra.

A külön eseménynapló és a meglévő műveleti journal nem egy tranzakció: crash a két write között eseményrést okozhat. A schedulerHistory segítségével terminal reconciliation lehetséges, de más natív journaltípusok teljes helyreállítása még hátravan. Teljes preflight-denial, post-launch readiness, per-target batch fázisok és külső megfigyelési összerendelés további munka. A status capabilities.completeOperationCoverage = false, amíg ez nem teljes.

## Integráció a meglévő egyetlen botba (helyi Codex)

1. Mentsd az aktuális botforrást és a tartós botállapotot; ne változtasd a ticket/XP/rang/szabályzat/LACC modulokat. A Manager vagy HP konfigurációját ne módosítsd, ne indítsd újra.
2. Másold az integrációs modult a bot saját projektjébe, importáld `OperationFeed`-et. Az existing host listából add át az id (Manager hostId), name és aktuális mapNames adatait. A request(host, kind, fields) az eddigi TLS-pinnelt, hostonkénti mutexes adapterre delegáljon és a sikeres data objektumot adja vissza; ne induljon új connection loop a meglévőt megkerülve. A channelFor(id) az ugyanazon Discord kliens ellenőrzött guild/csatornáját adja. A statePath tartós, egyetlen folyamat által írt fájl a bot saját adatkönyvtárában. Az admin/alert/backup/update mezők a meglévő admin-naplo/szerver-riasztasok/mentesek/frissitesek channel ID-jai. A botUserId a saját bot user ID.
3. A status capability/host identity ellenőrzése után a meglévő, terheléskorlátozott polling loopból (például 30 s) hívd a tick()-et; onProblem jelentsen egyértelmű képességhiányt, gaps/uncertain és hibát, redaktált kóddal és deduplikációval. Ne állítson teljes lefedettséget egy régi Managerre. A régi pollJobs továbbra is kezelheti vezérlés és interakció eredményét, **de ne publikáljon külön adminnaplót/riasztást**, ha az adott operationId az eseményfogyasztó útján van.
4. Első indulás baseline, opcionális külön `await feed.backfill(host)` a tényleges megőrzött hajnali futásokhoz. Ez nem küld új kezdési/lezárási riasztásokat. Régi history és live azonos operationId egy adminnaplóba tartozik. Az öt 2026-11-19-i futás tényleges retained historyját olvasd ki; ne gyárts fixture-t éles eseményként.
5. A Node fixture-eket futtasd `node --test integrations/discord-operation-feed.test.mjs`, majd külön helyi tesztben a bot teljes, változatlan jogosultságait és más moduljait ellenőrizd. Csak explicit tulajdonosi engedéllyel, forrásmentés után, megfelelő Manager build rendelkezésre állásakor indítható újra a bot. A jelen fejlesztési feladat nem engedélyez Manager update/restartot, HP konfigurációmódosítást, új ASA restartot vagy wipe-ot.

## Kézbesítés határai

A cursor és outbox egy atomikus helyi fájlcserében mentődik a hálózati send előtt. Saját Discord author + stabil footer marker alapján egyeztetünk. A nonce/enforceNonce csak Discord korlátozott időablakos támogatása; nem globális exactly-once garancia. Timeout után bizonytalan küldést nem ismétlünk vakon: keresés, majd explicit uncertain/review. 429/403/400 egyértelmű elutasításnál késleltetett retry; más hibánál konzervatív egyeztetés. Törölt admin/backup/update logot csak igazolt unknown-message után javítunk, riasztást nem generálunk ismét. Outbox 64 MiB-nál megáll, ellenőrzött archiválási/kompaktálási eljárás szükséges. Állapotfájl törlése vagy teljes lemezhiba esetére nincs pontosan egyszeri garancia. Discord REST siker NEM kliens push garancia.

## További kötelező elfogadás

- A pontos integrált Windows CI és Node CI sikeressége, majd valós botforrás commit és helyi teljes regresszió.
- A megőrzött öt hajnali futás csak olvasási visszaellenőrzése, explicit történeti import, Discord REST GET saját üzenet/marker és csatorna ellenőrzés; időpontok, duplikáció és fázisok bizonyítása.
- A natív folyamatkötés és külső PID-megfigyelés összekapcsolásának, az összes elutasításnak és aszinkron readiness/backup alfajtának teljes lefedettsége.
- Éles teszthez nem használható új Restart vagy DestroyWildDinos. A teljes termék- és éles elfogadást nem helyettesíti egy sikeres fixture.

## P0: beragadt válasz és műveleti állapot

Az új `OperationFeed` külső aszinkron I/O várakozási korlátja alapértelmezetten 10 másodperc. Egy még futó kérés ugyanazon kulcson nem indul újra; késői válasz nem lépteti a cursort. Másik host vagy csatorna ettől függetlenül feldolgozható. A helyi atomikus állapotmentést nem szakítjuk meg. Bizonytalan send után csak saját üzenetmarker-egyeztetés, nem automatikus újraküldés.

A meglévő helyi Node adapterben a `scripts/obsidian_discord_admin.cjs` friss `classifyOperationReply` és `classifyJobReceipt` helperét a preview/apply és job polling ágba kell bekötni. `UM-DC-HOST-BUSY` / `UM-DC-MAP-BUSY` esetén nincs natív dispatch; `UM-DC-REPLY-PENDING` / transport-hiba / natív bizonytalanság esetén ugyanazt az UUID-t kell read-only lekérdezni, nem új Apply-t küldeni. A host mutex várakozásának legyen véges admission ideje, a transport socketnek abszolút határideje; az operation state nem lehet hostszintű tiltóflag. A meglévő jogosultságok, panelek és egyéb botmodulok változatlanok. A tényleges helyi adapter módosítása és ellenőrzése külön szükséges.
