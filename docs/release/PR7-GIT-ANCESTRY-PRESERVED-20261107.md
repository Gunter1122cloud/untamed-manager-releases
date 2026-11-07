# PR #7 történeti commitok megőrzése az Untamed Manager főágában

**Dátum:** 2026-11-07  
**Forrás PR:** https://github.com/Gunter1122cloud/untamed-manager-releases/pull/7  
**PR #7 utolsó commit:** `be90978aa4bc1acf7556f532504ddb86ca121b30`  
**Konszolidált main előző HEAD:** `89e3685262f0b418e156d0ccd828486f2f35e884`  
**Történeti összevonó commit:** `024ba5792abf37ced4e5e2629a25d6d937fcc53f`

## Miért külön merge?

A #7 utólagos kilenc commitja nem volt a #14-ben összefűzött Git-történet őse, miközben a kódjuk már a közös forrásban létezett. A hét érintett fájl ellenőrzése során hat fájl tartalma azonos volt; a hetedik, a `wpf/MainWindow.Cluster.cs`, már tartalmazott további, újabb LAN- és biztonsági változtatásokat. A #7 ág rámerge-elése régi fájltartalommal visszavonhatott volna újabb javításokat.

Ezért a #7 HEAD **második szülőként** van felvéve az összevonó commitba, amelynek fájlfája **változatlanul az előző, már CI-tesztelt main fájfája**.

- A PR #7 mind a kilenc commitja megmarad az elérhető Git-történetben.
- Az újabb közös kód nem íródik felül.
- Az eredeti Store-v1 zárolás és adatmegőrzés változatlan.
- A kiadott `.NET 10` desktop nem tartalmaz SQL/MariaDB adatbázis-kezelést.
- Az eredeti WPF- és áthozott React/WebView2-felület közötti különbség továbbra is dokumentált.

## Előfeltétel a kiadáshoz

A Windows CI/Playwright/desktop build és natív C# regression ellenőrzés futtatható. A történeti Store-v1 éles indítását és a kétgépes karakterutazást ettől függetlenül csak tényleges ASA szerverrel és játékkliensekkel lehet igazolni. A történeti összevonó commit **nem teszi lehetővé** a Store-v1 karakteradatok vak módosítását vagy a migráció megismétlését.
