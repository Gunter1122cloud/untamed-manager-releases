# Obsidian Command — tényleges Windows Manager

Ez a jelenlegi, felhasználó által kiválasztott asztali alkalmazás: **offline React/TypeScript UI a `desktop-ui/` alatt, helyi WinForms/WebView2 shell, és .NET 10 C# native bridge-ek**. A korábbi WPF Manager UI-t **nem** fejlesztjük tovább.

## Fordítás (elkülönített Windows tesztgépen)
```powershell
cd desktop-ui
npm ci
npm run build
npm run test:ui
cd ../desktop
dotnet publish UntamedDesktop.csproj -c Release -r win-x64 --self-contained true
```

A React `dist/` bekerül a desktop `ui/` mappájába, és a WebView2 kizárólag a helyben csomagolt virtuális hostot tölti be. Nincs külön webes admin UI vagy CDN-függőség. Az új alkalmazás helyi WinForms héjat használ; a backend a megosztott `../wpf/Core/*.cs` logikára épül, **a natív WPF ablakokra nem**.

## Tényleges funkciók és korlátok
- Dashboard, két host áttekintése, helyi mapbeállítások, modlista/CurseForge, pluginleltár és védett JSON-szerkesztés.
- Helyi, új és bizonyítottan tiszta ASA telepítések SteamCMD telepítése, kétfázisú indítása/leállítása/újraindítása; RCON olvasás és jóváhagyott admin parancsok.
- Natív INI szerkesztés, mapmentések, SQL-mentes 5+5 mapos **előre elkészített offline másolatok** SHA-256 archívuma, kizárólag TEMP-laboros próba-visszaállítás.
- Modlista többmapos helyi másolása és átvitele fájlban a két Manager között. Távoli Manager csak hitelesített megfigyelésre jogosult.
- Frissítési központ: **csak Obsidian kiadások**, külön SHA-256-ellenőrzött Setup, külső visszaállításképes updater, egyértelmű kiadási azonosítóval.

A korábbi dokumentációban szereplő „only `dashboard.snapshot` and `mods.configured`” állítás elavult. A kiterjesztett, engedélyezett RPC-felület a `desktop/ObsidianWindow.cs` és `desktop-ui/src/bridge.ts` fájlokban ellenőrizhető. A runtime-hatások és élő Store-v1 utazás nem tekinthetők hitelesítettnek pusztán szintetikus tesztektől.

**Nincs a desktopban MariaDB/SQL kezelő.** A történeti Store-v1 map indításának tiltása továbbra is él, nem állítható, hogy a karakterutazás éles próbája megvolt. A két UK-HOME éles gép nincs CI tesztnek kitéve.

## Elfogadott UI
`desktop-ui/src/PremiumDashboard.tsx`, `PremiumModBoard.tsx`, `approved-dashboard-fidelity.css`, `mod-fidelity.css`. A fontos vizuális arányok: négy kompakt KPI; szervertábla balra, host/események jobbra; prémium kéthasábos Mod Center; a képes mapgaléria opcionális.
