# Untamed Kingdom — Obsidian Command

**Az Untamed Manager fejlesztési és kiadási alkalmazása az Obsidian Command.** Ez a Windowsra telepíthető, helyben futó **React + TypeScript + WebView2 + .NET 10/C#** program, **nem a régi natív WPF Manager**.

## Kód és kiadás
- `desktop-ui/`: jóváhagyott prémium React Dashboard, Mod Center és egyéb képernyők, helyben csomagolt grafika.
- `desktop/`: helyi Windows/WebView2 host, ellenőrzött, típusos C# műveleti bridge-ek, natív tesztek és Inno Setup.
- `wpf/Core/`: a Managerrel megosztott korábbi C# Core, Store-v1 és folyamatkezelési logika. **Nem** jelenti, hogy a WPF UI az aktuális program.
- `.github/workflows/build-obsidian-desktop.yml`: egyetlen automatikusan publikálandó Windows kiadási út.
- `wpf/`: történeti UI, külön futtatható kompatibilitási/regressziós referencia. **Nem készül új automatikus WPF release.**

Aktuális kiadások: [Obsidian Command GitHub Releases](https://github.com/Gunter1122cloud/untamed-manager-releases/releases). Csak az **`obsidian-preview`** címkékhez tartozó `Untamed_Obsidian_Desktop_Preview_Setup.exe` / Portable ZIP telepíthető az Obsidian Commandhoz. A `wpf-asa-only` kiadások régi, eltérő alkalmazást jelentenek, és nem kompatibilisek az Obsidian frissítőjével.

## Biztonsági és fejlesztési döntések
A végleges irányról szóló rögzítés: [AGENTS.md](AGENTS.md). A jóváhagyott React-kinézetet, a saját mapok helyi kezelését és a kétgépes, hitelesített LAN megfigyelést meg kell őrizni.

A program **nem tartalmaz SQL/MariaDB motort vagy adatbázis-kezelőt**; az ASA-pluginok külön MariaDB-szolgáltatása érintetlen. A működő történeti Store-v1 karakter- és transferadatok nem alakíthatók át vagy írhatók felül. Éles, 5+5 mapos karakterutazás sikerét nem bizonyítják a szintetikus Windows CI-tesztek; a telepítést és valódi szervercsatlakozást külön Windows tesztgépen kell elfogadni.

## Windows tesztbuild
```powershell
cd desktop-ui
npm ci
npm run build
npm run test:ui
cd ../desktop
dotnet publish UntamedDesktop.csproj -c Release -r win-x64 --self-contained true
```

Az alkalmazás használatához Microsoft Edge WebView2 Runtime szükséges, de nincs szükség helyi webszerverre vagy runtime CDN-re. Titkokat, privát licenckulcsokat, mentéseket és éles szerveradatokat tilos a GitHubra feltölteni.
