# Untamed Kingdom Manager — WPF v1.0.1 Preview

## Állapot
A Windows 10/11 x64 alkalmazás a `.github/workflows/build-untamed-wpf.yml` GitHub Actions workflow-val, Windows alatt fordul. A build önálló `UntamedManager.exe` fájlt és GitHub Release-t készít. Ez **tesztelőzetes**, nem teljes Kova-helyettesítő.

## Elkészült
- Natív WPF-ablak, testreszabott navigáció, állapotkártyák, tíz képes helyettesítő térképkártya, lassú fejléc-fényanimáció
- HP/Ryzen 5–5 map alapadatok a korábbi auditból
- Menük: Áttekintés, Szerverek, Modok, Pluginok, INI, Mentések, RCON, Ütemezések, Licenc/Frissítés, Beállítások
- Windows tálcaikon, X → elrejtés, egypéldányos védelem és hibafájlok a `%LOCALAPPDATA%\UntamedManager\WPF` mappában
- Biztonságos helyi INI-vizsgálat és backup, offline statuszok, Cloudflare `/health` ellenőrzés

## Ami nincs kész
- A jelenlegi mod- és plugin-képernyők válogatott tételeket mutatnak, **nem az összes eredeti mod/pluginrekord teljes importját**
- A licencaktiválás és automatikus frissítéstelepítés még **nincs átvezetve** az új WPF-alkalmazásba
- Élő hostagent, telepítés/indítás/leállítás, SteamCMD, CurseForge API, valódi RCON, scheduler, Kova Store 1:1 kompatibilitás még nem bizonyított vagy nincs megvalósítva
- A panoráma és mapgrafika jelenleg natív WPF-vektoros helyettesítő; a végleges képes arculati elemek még nem kerültek be
- A sikeres build **nem helyettesíti** a Windows alatti GUI, hosszú futási és üzemi teszteket

## Biztonság
- A harmadik, külön Windows tesztgépen használd.
- Ne telepítsd a UK-HOME-01 vagy UK-HOME-02 éles gépre.
- A program nem kér és nem tartalmaz titkos API-kulcsot.
- A GitHub repository **nyilvános**, így a forráskódja nyilvánosan olvasható.
