# Untamed Kingdom — Obsidian Command

**Az Untamed Manager fejlesztési és kiadási alkalmazása az Obsidian Command.** Ez a Windowsra telepíthető, helyben futó **React + TypeScript + WebView2 + .NET 10/C#** program, **nem a régi natív WPF Manager**.

## Egyetlen program, egyetlen verzió
**Obsidian Command 2.0.0 Preview** — a program, a React-frontend és a Windows telepítő közös verziója. A GitHub `#build` és `PR #` számok technikai azonosítók, nem új alkalmazások.

**A pontos szabály és a régi verziók magyarázata:** [Egységes verziózás és kiadási szabály](docs/release/OBSIDIAN-ONE-PRODUCT-VERSION-POLICY.md). A régi WPF `1.6.1` és Obsidian `1.4.0` kiadások történeti bejegyzések; a következő Preview frissítési útja a régi Obsidian telepítéseket is megőrzi. A kód és a kiadás egyetlen master feladathoz tartozik: [#12](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/12).

## Kód és kiadás
- `desktop-ui/`: jóváhagyott prémium React Dashboard, Mod Center és egyéb képernyők, helyben csomagolt grafika.
- `desktop/`: helyi Windows/WebView2 host, ellenőrzött, típusos C# műveleti bridge-ek, natív tesztek és Inno Setup.
- `wpf/Core/`: a Managerrel megosztott korábbi C# Core, Store-v1 és folyamatkezelési logika. **Nem** jelenti, hogy a WPF UI az aktuális program.
- `.github/workflows/build-obsidian-desktop.yml`: egyetlen automatikusan publikálandó Windows kiadási út.
- `wpf/`: történeti referencia, közös Store-v1 Core és tesztfájlok. **Nem készül új WPF telepítő vagy WPF release, kézi Core regresszió lehetséges.**

Obsidian kiadások: [GitHub Releases – csak Obsidian Command](https://github.com/Gunter1122cloud/untamed-manager-releases/releases). Csak az **`obsidian-preview`** címkékhez tartozó `Untamed_Obsidian_Desktop_Preview_Setup.exe` / Portable ZIP telepíthető az Obsidian Commandhoz. A `wpf-asa-only` kiadások régi, eltérő alkalmazást jelentenek, és nem kompatibilisek az Obsidian frissítőjével.

## Biztonsági és fejlesztési döntések
A végleges irányról szóló rögzítés: [AGENTS.md](AGENTS.md). A jóváhagyott React-kinézetet, a saját mapok helyi kezelését és az N-host, többclusteres, hitelesített LAN megfigyelést meg kell őrizni. A történeti Legacy 2×5 Store-v1 csak kompatibilitási kivétel.

A program **nem tartalmaz SQL/MariaDB motort vagy adatbázis-kezelőt**; az ASA-pluginok külön MariaDB-szolgáltatása érintetlen. A működő történeti Store-v1 karakter- és transferadatok nem alakíthatók át vagy írhatók felül. Éles, 5+5 mapos karakterutazás sikerét nem bizonyítják a szintetikus Windows CI-tesztek; a telepítést és valódi szervercsatlakozást külön Windows tesztgépen kell elfogadni.

## Kanonikus funkció- és integrációs állapot

A mindenkori implementációs státuszt a [kanonikus funkciómátrix](docs/development/OBSIDIAN-CANONICAL-IMPLEMENTATION-STATUS.md) rögzíti. [PR #58](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/58) már a `main` része: verziózott N-host offline mentés és kizárólag TEMP-sandbox helyreállítás tranzakciós rollbackkel. **Nem élő ASA restore, és nem bizonyítja a karakter/tribe/tribute utazás sértetlenségét.** A hiányzó #53 elfogadás a [master #12](https://github.com/Gunter1122cloud/untamed-manager-releases/issues/12) része.

## Windows tesztbuild
```powershell
cd desktop-ui
npm install --no-audit --no-fund
npm run build
npm run test:ui
cd ../desktop
dotnet publish UntamedDesktop.csproj -c Release -r win-x64 --self-contained true
```

Az alkalmazás használatához Microsoft Edge WebView2 Runtime szükséges, de nincs szükség helyi webszerverre vagy runtime CDN-re. Titkokat, privát licenckulcsokat, mentéseket és éles szerveradatokat tilos a GitHubra feltölteni.
