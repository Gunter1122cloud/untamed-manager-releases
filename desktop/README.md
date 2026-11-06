# Untamed Kingdom — Obsidian React Desktop (migration branch)

This is the **actual new Windows host**, not a website and not a replacement
server backend. It compiles and reuses the existing production `wpf/Core/*.cs`
sources unchanged, just as the existing Supervisor does. The old WPF release,
PR #7/#9/#10 and their configuration/Store formats remain untouched.

## Build (separate Windows test PC only)

```powershell
cd desktop-ui
npm install
npm run build
cd ../desktop
dotnet build -c Release
dotnet run -c Release
```

The `ui/` folder is copied from `desktop-ui/dist` by MSBuild. No runtime
development server is used. WebView2 Evergreen Runtime is required.
The desktop displays **real read-only local manager configuration and observed
process information**, optionally authenticated read-only LAN peer status.
There is no synthetic success state and no fake data.

The only currently accepted bridge methods are
`dashboard.snapshot` and `mods.configured`. The bridge has no write
actions, no RCON execution and no Store/SQL access. Unknown operations fail
closed. New UI sections must be connected incrementally to existing,
permission-checked Core operations, with isolated Windows/ASA acceptance.

No production lifecycle gate is lifted here. Never test this on UK-HOME-01
or UK-HOME-02. Existing WPF and supervisor remain the baseline until the
frontend, all screens and safe operations have been migrated and verified.

## Architecture

`desktop-ui`: React/TypeScript/CSS, compiled to offline `dist/`.

`desktop`: WinForms tray/window + WebView2 virtual host mapping +
strict request-ID read-only bridge, links unchanged `wpf/Core/*.cs`.

`wpf/`: existing protected Manager and Supervisor source, unchanged.

Development stages: complete visual Dashboard, mod catalog and controls,
migrate remaining panels and backend contracts, Windows GUI/Playwright
tests, full offline installer and updater migration, real isolated ASA tests.
