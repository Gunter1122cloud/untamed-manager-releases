# Untamed Kingdom — egyetlen Manager, egyetlen verziózási szabály

**Kanonikus alkalmazás:** **Obsidian Command** (React + TypeScript a `desktop-ui/` alatt, Windows/WebView2/C# .NET 10 a `desktop/` alatt). Az Untamed Manager név történeti/ernyőnév. A natív WPF UI *nem* második kiadandó program.

## Amit a felhasználó lát

- **Programverzió:** **2.0.0 Preview** (mindaddig Preview, amíg a tényleges Store-v1 A→B→A, restore és 10-map élő elfogadás nincs bizonyítva).
- **Build:** GitHub Actions száma (például `#1480`), **nem önálló szoftververzió**. Csak hibabejelentéshez vagy pontos újratelepítéshez szükséges.
- **Pull request:** fejlesztési módosítás sorszáma; nem alkalmazásverzió.
- **GitHub release tag:** `v2.0.0-obsidian-preview.<github-run-number>`. A régi, már telepített Obsidian 1.4 Preview frissítője e formátumot felismeri. Egy tag mindig egy megváltoztathatatlan CI-vel tesztelt buildet jelöl.
- **Kiadási csatorna:** kizárólag Obsidian Preview, amíg a stabil éles elfogadás hiányzik. Sem WPF, sem SQL, sem mobil nem jelenhet meg Obsidian-frissítésként.
- **Telepítő kompatibilitása:** `Untamed_Obsidian_Desktop_Preview_Setup.exe`; Portable: `Untamed_Obsidian_Desktop_Preview_Portable.zip`; futtatható program: `UntamedDesktop.exe`. A jelenlegi AppId és telepítési könyvtár szándékosan változatlan, hogy az 1.4-es Obsidian telepítés frissíthető maradjon.

## Egyetlen forrás, több formátum

**Csak `desktop/Version.props` `ObsidianProductVersion` értékét módosítjuk verzióváltáskor.** A `desktop/UntamedDesktop.csproj` ebből veszi az alkalmazás, assembly és fájlverziót. A Windows build ellenőrzi, hogy a React `desktop-ui/package.json` technikai verziója egyezik-e; az Inno Setup a build során ugyanebből kapja a verziót. A setup/portable `installed-release.txt` a tényleges GitHub run sorszámával együtt íródik. A Release csak sikeres Windows CI és SHA-256 ellenőrzés után publikálható.

A React npm-csomag önmagában nem egy második termék. Ha később `2.0.1` vagy `2.1.0` lesz, ugyanebben a fájlban történik a tudatos verzióemelés, és a CI megakadályozza az eltérő .NET/React verziók együttes kiadását.

## Történeti számozások

| Korábbi jelölés | Mit jelent? | Most |
| --- | --- | --- |
| `1.3.x`, `1.6.1` és `wpf-asa-only` | Régi natív WPF Manager | **Archív**, nem fejlesztjük/telepítjük Obsidian helyett |
| `1.4.0-obsidian-preview.*` | Korábbi React/WebView2 tesztkiadások | **Frissíthető** a következő Obsidian Preview-ra |
| `0.1.0` a React package-ben | Régi belső frontend-csomagszám | Felváltotta a közös `2.0.0` |
| `#27` vagy `#12` | PR / master feladat | Nem verzió |
| `#1478` | CI build / konkrét régi kiadás | Nem verzió |

A régi GitHub kiadások megmaradnak **történeti rekordnak**, mert törlésük rontaná a régi csomagok visszakereshetőségét és ellenőrizhetőségét. Egyik sem jelenhet meg a Manager új támogatott kiadásaként. Régi WPF Setup és WPF release az új kiadási folyamatban **nem készül**; kézi, csak olvasó Store-v1 regressziós teszt megtartható.

## Funkciók nem változnak pusztán a verziószám miatt

A `2.0.0 Preview` *nem* azt jelenti, hogy már bizonyított az éles migráció. Továbbra is nyitott: eredeti Store-v1 kétgépes writer/session/lock hűség, HP → Ryzen → HP survivor/item/dino/tribe utazás, teljes élő clustermentés és visszaállítás, SaveComplete/játékbeli ellenőrzés, teljes ütemező/watchdog, mod/plugin runtime ellenőrzés és végső UX. Kötelező referenciaparitás igen, saját éles tanúsítás nem állítható.

**Egyetlen master fejlesztési nyilvántartás:** [GitHub #12](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/12). A későbbi Discord/mobil projektek külön [#8](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/8) backlogok. Minden kódfunkciót Obsidianban, nem a régi WPF GUI-ban készítünk.

**Biztonság:** nincs Managerbe épített SQL/MariaDB, nincs Store-konverzió vagy nulla bájtos profil „javítás”, és nincsenek élő UK-HOME adatok fejlesztői tesztfixture-ként.
