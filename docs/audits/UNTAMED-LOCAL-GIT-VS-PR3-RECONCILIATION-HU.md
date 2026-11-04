# Helyi forráspéldány és GitHub PR #3 egyeztetése

2026-11-04. Kizárólag olvasó összevetés; nincs törlés, reset, clean, automatikus add vagy forrásfelülírás.

## Melyik a tényleges projekt?

A munkamappa alatti `untamed-manager-dev/` a korábbi körben GitHub-kapcsolaton keresztül letöltött szöveges forráspéldány.
**Sem a munkamappa gyökerében, sem ebben a könyvtárban nincs .git.** Ez nem Git-checkout; ezért itt a git status/diff nem tud commitolatlan állapotot megállapítani.
Git parancssori eszköz korábban nem volt elérhető. Nem hoztunk létre új Git-adatbázist és nem rendeztünk át könyvtárakat.

A vizsgált munkamappában egyetlen ilyen projektmappa van; `untamed-manager-dev/untamed-manager-dev` nested másolat nincs.
Ez a megállapítás a munkamappára vonatkozik, nem a teljes számítógép minden könyvtárára.
Az éles Kova telepítése és éles szervermappák ettől különállók; nem forrás-worktree-k.

## Pontos számok a dokumentáció hozzáadása előtti alapállapoton

| Mérőszám | Eredmény | Jelentés |
|---|---|---|
| Helyi szöveges forrásfájl | 139 | Nem 139 funkcionális változtatás |
| Helyi bin/obj buildtermék | 24 | Az előző körben készültek; most nem futtattunk buildet/tesztet |
| Helyi összes fájl ebben a forráspéldányban | 163 | 139 +24; nem git diff stat |
| GitHub PR #3 HEAD teljes fájlfája | 162 | Teljes repo, preview/asset állományokkal |
| Helyi–HEAD nyers Git-blob egyezés | 14 | Pontosan a PR #3-ban módosult fájlok |
| Eltérés kizárólag fájlvégi CR/LF/üres sor miatt | 125 | A letöltött példány végén más újsorszám; tartalmi kódeltérés nem talált |
| Helyi funkcionálisan eltérő szöveges fájl | 0 | A vizsgált 139 fájl körében |
| Helyi, repóban nem szereplő forrásfájl | 0 | bin/obj és munkamappán kívüli helyi evidence nem tartozik ide |
| Csak GitHubon lévő preview/asset | 23 | 22 régi preview PNG/base64 és egy reference-atlas.jpg |
| Nested duplikált projekt | 0 | Munkamappán belül |

A Git-blob SHA-1 a Git által használt `blob <bytehossz>\0<adat>` szerkezetből készült, titkos adattartalom exportja nélkül.
A 125 eltérésnél csak sorvég-normalizálás és fájlvégi újsorvariáció volt megengedett az összevetésben; belső whitespace/kód nem lett eldobva.
Semmit nem normalizáltunk vissza vagy írtunk felül a forrásban.

## A „141 helyi módosított fájl” és a 14 PR-fájl közötti különbség

A 141-es korábbi felületi számlálóhoz nem áll rendelkezésre az eredeti útvonallista és Git-bázis, ezért eredetét nem állítjuk bizonyítottnak.
A helyi fájllétrehozás/szöveges letöltés számlálója nem azonos egy távoli PR bázishoz viszonyított diffjével.
Az ellenőrizhető tény: **PR #3 három commitban 14 fájlt változtat a PR #1 HEAD-jéhez képest**, és ezek a helyi példányban byte-egyezők.
Nincs 127 elveszett funkcionális javításra bizonyíték. A teljes helyi másolat létrehozása nem jelent ugyanennyi új megvalósítást.

## GitHub-ágak és diffek

- main: `df7da47221369274c170d0930bb06f6e9e317c1f`.
- PR #1: `feature/store-lifecycle-evidence-parity-20261103`, HEAD `62dea92ce52e8270e57833fc976356ce199026d5`; nyitott Draft; 19 változott fájl; teljes patch átnézve.
- PR #3: `codex/p0-store-v1-parity`, HEAD `3b8b448fb906a02d45615a466ef2900ca40200f0`; cél PR #1 fejlesztési ága; nyitott Draft; teljes 14-fájlos patch átnézve.
- Legutóbbi igazolt alkalmazásforrás: PR #3 HEAD. Korábbi [Windows CI](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/41795392001) ezen a forráson sikeres, nem az új dokumentációs commit tesztje.
- Új auditág: `codex/read-only-kova-gap-audit`, PR #3 HEAD-ről, célága `codex/p0-store-v1-parity`. Csak dokumentációt tartalmaz.

## Mi került PR #3-ba?

| Fájl | Helyi–HEAD állapot |
| --- | --- |
| .github/workflows/build-untamed-wpf.yml | EXACT |
| wpf/Core/ClusterVaultArchiveInspector.cs | EXACT |
| wpf/Core/HistoricalLaunchProfileReader.cs | EXACT |
| wpf/Core/KovaStoreV1CopyReader.cs | EXACT |
| wpf/Core/StoreV1Fingerprint.cs | EXACT |
| wpf/Core/UntamedManagerArchiveInspector.cs | EXACT |
| wpf/Core/VaultReadSnapshot.cs | EXACT |
| wpf/MainWindow.ClusterVault.cs | EXACT |
| wpf/MainWindow.KovaStoreV1.cs | EXACT |
| wpf/P0-STORE-V1-AUDIT-2026-11-04-HU.md | EXACT |
| wpf/Testing/Integration/ClusterVaultTests.cs | EXACT |
| wpf/Testing/Integration/ProfessionalOperationsTests.cs | EXACT |
| wpf/Testing/StoreV1/Program.cs | EXACT |
| wpf/Testing/StoreV1/StoreV1.csproj | EXACT |

## Mi maradt kizárólag helyben?

A hordozható SDK és csomagja a `.dev-tools/` könyvtárban; a korábban keletkezett bin/obj; az eredeti négy privát ZIP;
valamint a `development-evidence/` auditsegédjei, privát integrity baseline-ja és képkivágatai. Ezek egyike sincs az audit commit whitelistjén.
A publikus auditba csak a kifejezetten megnevezett, redaktált dokumentumok és összesítő JSON kerül.
A 23 hiányzó preview/asset miatt a helyi példányt teljes offline WPF-checkoutnak sem nevezzük; az előző teljes build a GitHub runner teljes checkoutjából készült.
