# Untamed Manager – frissítési kiadások

Ez a nyilvános tárhely az **Untamed Manager Windows-telepítőinek** kiadására szolgál. A Manager használata külön Cloudflare-licenchez kötött.

## Első kiadás: v0.5.0

1. A [New release](https://github.com/Gunter1122cloud/untamed-manager-releases/releases/new) oldalon adj meg egy új taget: `v0.5.0`.
2. A kiadás címe: `Untamed Manager v0.5.0`.
3. Csatold a **pontosan** `UntamedManager-0.5.0.exe` nevű telepítőt.
4. Kattints a **Publish release** gombra.
5. Ellenőrizd, hogy ez a fájl letölthető-e:
   https://github.com/Gunter1122cloud/untamed-manager-releases/releases/download/v0.5.0/UntamedManager-0.5.0.exe
6. Ezután a helyi Cloudflare-admin tokennel közzé kell tenni a frissítési jegyzéket a `/v1/admin/releases/publish` végponton; csak ezután jelenhet meg a Manager Frissítés menüjében.

A Release megléte **nem jelenti automatikusan**, hogy a Cloudflare kiadási jegyzék frissült.

## Biztonság

- A GitHub kiadási fájl nyilvánosan letölthető. A licencellenőrzés az alkalmazásban történik.
- **Soha ne tölts fel** licenckulcsot, Cloudflare-admin tokent, `.secrets` könyvtárat, privát aláírókulcsot, ASA-karaktermentést vagy gépauditot.
- A frissítéseket a kliensnek digitális aláírással és SHA-256-tal kell ellenőriznie.
- Az új v0.5 még fejlesztői kiadás: a kétgépes éles ASA/Store integráció nincs kész; csak elkülönített tesztgépen próbáld ki.

## Licenckiszolgáló

`https://untamed-license-hub.untamedkingdom.workers.dev`

**Ez a repository nem az éles szerverek adatait tárolja, hanem csak a programkiadásokat.**
