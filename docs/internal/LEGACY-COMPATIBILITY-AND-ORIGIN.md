# Belső kompatibilitási és eredetnyilvántartás

Az Untamed Manager önálló C# implementáció. A korábban átadott Kova 1.8.3 forrás
`UNLICENSED` eredetű referencia; kódját és grafikai elemeit nem másoltuk a termékbe.
A történeti auditokat és eredetmegjelölést nem töröljük a saját arculat miatt.
Ez fejlesztői/protokoll-dokumentum, nem felhasználói márkajelzés.

Megőrzött lemezszerződések: `KovaCluster`, `.kova-store-v1`, a történeti ZIP-ek
`Kova/` és `Ryzen-Kova/` prefixei, valamint a korábbi saját offline archívum
`KovaStoreIncluded` JSON-kulcsa. A C# property neve már `VaultStoreIncluded`, de
a JSON-kulcs explicit attribútummal változatlan. A védett régi pluginnevek
felismerési tokenje szintén megmarad. Ezekből nem következik írójog vagy Store-konverzió.

Átnevezések: KovaAuditBridge → VaultAuditBridge; KovaAuditHost → VaultAuditHost;
KovaFixtureAudit → VaultFixtureAudit; KovaProfileImporter → VaultProfileImporter;
KovaStoreV1CopyReader → VaultCompatibilityReader. A hozzájuk tartozó report/model
típusok és WPF-metódusok is semleges nevet kaptak. A hét korábbi audit eredeti
kódhelyhivatkozásai történeti állapotot dokumentálnak; az aktuális nevek a központi
WORK-LEDGER.json nyilvántartásban szerepelnek.

Nincs saját Kova nevű C# típus vagy új metódus. A kötelező protokollértékeket a
VaultProtocol tartalmazza. Felhasználói címkék és hibanaplók Untamed-prezentációt
használnak; a titkok és webhookok nem publikálhatók.
