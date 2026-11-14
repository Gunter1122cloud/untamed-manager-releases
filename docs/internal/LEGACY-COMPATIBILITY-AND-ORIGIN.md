# Belső kompatibilitási és forráseredet-nyilvántartás

Az Obsidian Command önálló Windows ASA Manager. A korábbi Manager 1.8.3-as forráscsomagját a felhasználó a fejlesztő kifejezett engedélyével biztosította felhasználásra. Ez az engedélyről tett felhasználói nyilatkozat, nem a nyilvános tárolóban közzétett licencdokumentum.

## Tényleges kompatibilitási szerződések

A korábbi program által a lemezre írt **eredeti** clusterazonosító, Store-v1 koordinációs mappa, helyi profilkönyvtár, történeti mentési útvonalak és JSON-kulcsok nem változtathatók meg terméknév-csere kedvéért. A C# típusnevek, belső mezőnevek, tesztek és a React–C# üzenetprotokoll összefüggnek; nem szabad globális szövegcserével átírni őket.

Az eredeti kompatibilitási értékek forrása a meglévő `VaultProtocol` és a kapcsolódó Core-implementáció. A korábbi forrás átadásának engedélye nem jelent automatikus jogot az éles Store-állapot átírására. Az éles ASA-karakterfájlok, 0 bájtos profilok, megosztott transferadatok és eredeti gépprofilok az izolált fejlesztés során változatlanok maradnak.

## Terméknév és nyilvános megjelenés

A megjelenített terméknév **Obsidian Command**. Az alkalmazás neve, telepítője, README-je és kiadási leírásai nem a korábbi projekt márkanevét viselik. A kompatibilitási célú, belső kódazonosítók és lemezformátumok megtartása nem márkajelzés és nem külön alkalmazás telepítése.

Az átvett forrásból csak igazoltan integrált funkció nevezhető működőnek; a kétgépes, élő karakterutazás önmagában nem igazolható egy Windows CI-futtatással.
