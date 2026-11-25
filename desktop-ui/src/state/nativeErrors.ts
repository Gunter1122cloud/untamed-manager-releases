// Stable native codes remain available for support; never expose raw paths
// or credentials in the friendly message. Unknown codes stay diagnosable.
const messages: Record<string,string> = {
  "UM-MOD-REPAIR-STOP-REQUIRED": "A map fut, vagy a leállított ASA és Loader állapot nem igazolt. A modfájlok változatlanok.",
  "UM-MOD-REPAIR-SHARED-INSTALL": "A telepítést másik map is használja; a mod cache nem különíthető el biztonságosan.",
  "UM-MOD-REPAIR-CHANGED": "A mod cache a jóváhagyás óta megváltozott. Ellenőrizd újra a mappákat.",
  "UM-MOD-REPAIR-NOT-OWNED-OR-ENABLED": "A projekt nincs az adott map aktív vagy passzív modlistáján, vagy a map nem kezelhető helyben.",
  "UM-MOD-REPAIR-ROLLBACK-UNCERTAIN": "A modkarantén részleges lehet. Az eredeti fájlok megmaradnak; ellenőrizd a karantént, ne ismételd meg automatikusan.",
  "UM-MOVE-NOT-ENOUGH-SPACE": "A célmeghajtón nincs elegendő szabad tárhely a másoláshoz és a biztonsági tartalékhoz.",
  "UM-MOVE-TARGET-EXISTS": "A célkönyvtár már létezik. Válassz új, üresen létrehozható útvonalat.",
  "UM-MOVE-STOP-REQUIRED": "A map vagy az ASA API Loader nincs igazoltan leállítva.",
  "UM-MOVE-ONLY-STANDALONE-LOCAL": "Ez a költöztetési útvonal csak különálló, helyi, nem Store és nem cluster maphoz használható.",
  "UM-MOVE-PATH-OVERLAP": "A cél vagy forrás átfed egy másik telepítéssel, a Manager profiljával vagy a cluster könyvtárával.",
  "UM-MOVE-SOURCE-CHANGED": "A forrás fájllistája megváltozott. Az eredeti telepítés megmaradt.",
  "UM-MOVE-HASH-MISMATCH": "A forrás és a másolat SHA-256 ellenőrzése eltért. A profil nem állítható át a nem igazolt másolatra.",
  "UM-MOVE-PROFILE-CHANGED": "A helyi map konfigurációja megváltozott. Ellenőrizd a profilt és az esetleges másolatot.",
  "UM-INI-DRAFT-APPROVAL": "Új, érvényes INI hash-előnézet és a piszkozat mentésének jóváhagyása szükséges.",
  "UM-INI-ETAG-MISMATCH": "A lemezen lévő INI közben megváltozott. A frissebb fájl nem írható felül a régi előnézettel.",
  "UM-INI-DRAFT-SHARED-INSTALL": "A telepítés nem külön maphoz tartozik; függő INI nem készíthető hozzá biztonságosan.",
  "UM-INITIAL-MAP-MOD-ID": "Érvényes számból álló CurseForge projektazonosító szükséges.",
  "UM-INITIAL-MAP-MOD-CATEGORY": "A CurseForge nem igazolt elérhető ASA Maps projektet ehhez az azonosítóhoz.",
  "UM-CURSEFORGE-NO-KEY": "A hivatalos CurseForge API ellenőrzéséhez állítsd be a kulcsot a Beállításokban.",
  "UM-MAP-MOD-MUST-BE-ACTIVE": "A kiválasztott map packnak aktív modként kell szerepelnie.",
  "UM-MAP-MOD-CANNOT-DISABLE": "A maphoz kötött CurseForge pack nem tiltható le és nem állítható passzívra.",
  "UM-MAP-MOD-REMASTER-FLAG": "A remaster indítási kapcsoló és a kiválasztott map pack nem egyezik.",
  "UM-IMPORT-SAVE-LAYOUT-UNVERIFIED": "A mentési mappa nem ellenőrizhető biztonságosan. Az eredeti telepítés változatlan maradt.",
  "UM-IMPORT-DIRECTORY-MISSING": "A megadott helyi telepítési mappa nem található.",
  "UM-ASA-INSTALL-EMPTY": "A mappa üres. Indíts új SteamCMD-telepítést.",
  "UM-ASA-INSTALL-INCOMPLETE": "Az ASA telepítése hiányos. Ellenőrizd a fájlokat a SteamCMD felületén.",
  "UM-ASA-INSTALL-FOREIGN": "A mappa nem igazolt ASA-telepítés. Válassz másik mappát.",
  "UM-ASA-INSTALL-INACCESSIBLE": "A Manager nem fér hozzá a telepítéshez. Ellenőrizd a Windows-jogosultságokat.",
  "UM-ASA-INSTALL-UNKNOWN": "A telepítés állapota nem ellenőrizhető. Ellenőrizd a meghajtót és próbáld újra a lekérdezést.",
  "UM-ASA-EXE-INVALID": "Az ASA futtatható fájl helyén nem érvényes helyi fájl található.",
  "UM-ASA-EXE-EMPTY": "Az ASA futtatható fájl üres. Ellenőrizd a telepítést a SteamCMD felületén.",
  "UM-ASA-EXE-MISSING": "Az ASA futtatható fájlja hiányzik.",
  "UM-NATIVE-UNAVAILABLE": "A natív Manager-kapcsolat nem érhető el.",
  "UM-NATIVE-TIMEOUT-OPERATION-STATUS-UNKNOWN": "A natív válasz nem érkezett meg időben. A művelet eredménye nem igazolt; csak állapotot ellenőrizz.",
  "UM-READ-UNAVAILABLE": "Az aktuális állapot nem olvasható.",
};
export function describeNativeError(code: string, fallback = "A natív művelet eredménye nem igazolt."): string {
  const safeCode=/^UM-[A-Z0-9-]{1,120}$/.test(code) ? code : "UM-UNKNOWN";
  return (Object.hasOwn(messages,safeCode)?messages[safeCode]:fallback)+" ("+safeCode+")";
}
