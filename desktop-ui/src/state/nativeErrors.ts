// Stable native codes remain available for support; never expose raw paths
// or credentials in the friendly message. Unknown codes stay diagnosable.
const messages: Record<string,string> = {
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
