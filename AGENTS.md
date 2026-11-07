# Untamed Manager — immutable development direction (2026-11-07)

**CURRENT SHIPPING PRODUCT: Obsidian Command = `desktop-ui/` (React + TypeScript + offline CSS/assets) + `desktop/` (Windows WinForms/WebView2 shell and C#/.NET 10 native bridges).**

The user expressly abandoned the former native WPF UI. Do **not** resume native WPF UI work, present a WPF Setup as the recommended/current Manager, or publish any new WPF release. The old `wpf/` project is preserved for historic implementation reference and shared `wpf/Core/*.cs`, `wpf/Updater/`, and isolated regression fixtures ONLY. WPF workflows, if retained, are opt-in/manual compatibility tests.

## Product invariants
- The accepted visuals are `desktop-ui/src/PremiumDashboard.tsx`, `PremiumModBoard.tsx`, `approved-dashboard-fidelity.css`, `mod-fidelity.css` and associated premium CSS and bundled artwork. Preserve the compact four-KPI Dashboard, ten-map server table left, host/events right, map gallery optional, and premium two-column Mod Center.
- All visible buttons must use the typed, allowlisted native `desktop/*Bridge.cs` routes. No demo simulation presented as real. Distinguish configured/requested state, observed runtime state and independently verified effects.
- Exactly two independent Windows Managers, five locally owned maps per machine. Peer status may be authenticated read-only. No free-form remote start/stop writes or SQL/database inside the shipping Manager. External plugins' MariaDB remains a separate service.
- **Preserve original Store-v1 and character-travel data byte-for-byte.** No automatic `-converttostore`, conversion/rebuild, lock/session deletion, or zero-byte arkprofile cleanup. Keep historic map protection until independent, verified live integration tests are passed.
- No production ASA-server or shared Store/transfer files are used as developer test fixtures. Run CI on isolated Windows test files and do not equate its green state with live character-travel certification.
- One supported release channel and updater identity: `v1.4.0-obsidian-preview.<run>`, native desktop `UntamedDesktop.exe`, `Untamed_Obsidian_Desktop_Preview_Setup.exe`. The installed release tag must be preserved in the Setup/Portable, hashed, and checked independently of WPF releases.
- Do not reintroduce the abandoned SQL/MariaDB GUI, historic three-SQL archive/restore, or expose secrets in GitHub releases or the React UI.

## Next work
Audit and implement changes **only in `desktop/`, `desktop-ui/`, shared `wpf/Core/`, and the Obsidian CI/updater where unavoidable**. Old WPF GUI is not a parallel delivery target. Document any remaining real-ASA blockers instead of asserting they passed synthetic tests.
