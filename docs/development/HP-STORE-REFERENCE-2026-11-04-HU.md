# HP Store-v1 másolat – új történeti referencia

A felhasználó 2026-11-04-én átadta a HP eredeti négyfájlos, 2026-10-26 jelölésű
helyi Store-metaadatmásolatát. A négy eredeti JSON és a ZIP privát marad, a repóba
nem kerül. A munkamappában lévő másolatokat csak olvasó ZIP/JSON-összevetés vizsgálta;
a bennük szereplő útvonalakat nem nyitotta meg, hálózati kapcsolatot nem létesített.

## Megállapítás

- Mind a négy helyi rekord megvan; a device-azonosító a korábbi koordináció egyik peerje.
- A munkamenet, a done állapot, az öt saját map kiválasztása, az active rekordok,
  a mapgyökerek és a mentési hivatkozások egyeznek. Összesen tíz különböző map.
- A HP helyi konfigurációja és helyi lockja egymással egyezik; a helyi lock
  konfigurációs hash-e a saját beágyazott konfigurációjára érvényes.
- A HP helyi `path` mező UNC-gépneve eltér a közös koordináció HP-peer rekordjának
  `path` mezőjétől. A share és az alútvonal szövege egyezik; az aliasok azonos
  megosztásra mutatását nem ellenőriztük. Emiatt a helyi és a peer hash eltér.
- A többi vizsgált konfigurációs mező (`id`, `mode`, `backupPath`, `members`) egyezik.
- A másik host helyi és peer-konfigurációja, hash-e és rekordjai egyeznek.
- A korábbi koordinációs archívum manifest-hash-ei érvényesek; a kapott másolatokban
  nincs running/cancelled/mutation jelző. Mindkét eredeti ZIP SHA-256 értéke változatlan.

Ez nem bizonyít éles hibát vagy adatvesztést. A különböző UNC-gépnevek lehetnek
aliasok, de a byte-/hash-egyezést nem helyettesítjük feltételezett névfeloldással.
Nincs automatikus javítás, átnevezés, sessionváltás, locktörlés vagy protokollkonverzió.

## Implementáció és bizonyítékhatár

Az új `InspectPairedCopies` a manifestes koordinációs ZIP és a külön négyfájlos
helyi ZIP tartalmát memóriában társítja. Nem csomagolja újra az eredetiket. A négy
helyi fájl allowlistes; hiányzó/extra/ismételt/csonka/linked/oversize bejegyzés
elutasítva. A meglévő közös Store-validator ellenőrzi a peer/session/hash/map/lock
összefüggéseket. A WPF ugyanitt biztosít két ZIP-et választó ellenőrző műveletet.

Az új CI-esetek saját szintetikus adatok: helyes teljes pár, sértetlen források,
hiány/extra/traversal/case-duplicate/symlink, ismételt device, más session,
önmagában helyes, de eltérő UNC-konfiguráció, túlméretes helyi metadata.
A privát referencia helyi vizsgálata független PowerShell-összevetés volt;
az eredeti privát ZIP-eket a C# program ezen a hoston nem futtatta.

A korábbi „HP négy helyi rekordja hiányzik” bemeneti hiány megszűnt, de az azonos
konfigurációjú kétoldali referencia és a valódi writer ownership még nem igazolt.
A pontosítás további privát, azonos időponthoz tartozó másolattal vagy elkülönített
tesztben igazolt alias-/protokollviselkedéssel történhet. Élő HP-hozzáférés nincs.
A valódi karakter/tribe travel, SaveComplete és koordinált SQL restore tesztjei
továbbra is szükségesek; a végleges kiadás nincs kész.
