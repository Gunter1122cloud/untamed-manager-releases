# Implementációs állapotjelentés – nem végleges átadás

Mind a hét audit teljes tartalma feldolgozva; 61 művelet, 44 referenciamodul,
42 saját ötlet, 33 backlog-feladat és 41 funkciócsoport szerepel a beágyazott
WORK-LEDGER.json nyilvántartásban. A hét új technikai javítás külön FIX01–FIX07 sor.

## Tényleges kódváltozások

- Gépszintű named semaphore alapú, awaiten át is használható műveleti lease.
  Installáció alapján azonosít, nem profil/map ID alapján. A crash-journal nem
  törlődik PID/TTL alapján. Mutáció után bizonytalan eredménynél NeedsOperator.
- Bekötés: helyi launch, modbatch, plugin toggle/install/rollback, konfigurációmentés,
  fájlszintű backup. A korábbi külső Manager ezt a lease-t nem ismeri: átadási
  ownership és valódi Store writer koordináció ettől még nem elfogadott.
- ConfigStore optimista compare-and-swap és rövid gépszintű mentési mutex;
  elavult példány nem írja felül némán egy másik példány frissebb konfigurációját.
- SafeLifecycle: határidő akkor is érvényes, ha az adapter nem figyeli a tokent;
  Running és ConfirmedStopped ellentmondása nem vezet restarthoz; kilépés közben
  EXE/argumentum/start/PID azonosság ellenőrzése. Nincs új Kill útvonal.
- MariaDB 10.11.19 baseline, dump/restore eszközsorozat és három DB halmaz policy.
  A bináris verzió önmagában nem igazolt SQL-verzió. A policy nem dump/restore adapter.
- Semleges saját wrappernevek, változatlan régi Store- és archívumprotokoll.
  Felhasználói szövegek/logok márka- és titokszűrése, modargumentum-előnézet redakció.
- Stabil modlista-gyűjtemény és kiválasztott ID megőrzése; sorválasztás bekötve
  az ID/mód szerkesztőbe. Célzott WPF route/focus/hit-test regresszió.
- A teljes munkaleltár és a műveleti journal olvasó állapotnézete a meglévő WPF-ben.

## Ellenőrzés

Az új OperationSafetyTests valódi külön Windows folyamatokkal vizsgálja a lease-t,
a processzösszeomlás utáni visszautasítást, a konfigurációs CAS-t és a metadata policy-t.
A lifecycle szélső esetek szintetikus sessionök; ezek nem valódi ASA SaveComplete.
A WPF smoke 100 váltott routed click/fókusz/hit-test ciklust ellenőriz szintetikus modlistán.
A korábbi teljes Windows CI, Store/Manager ZIP, FakeAsa/FakeSteamCMD, installer
és updater tesztkészlet is követelmény. Futási eredményt csak az aktuális CI adhat.

Az első szakasz igazolt commitja `016a43b556afc5d29781513fadf049c0dee8b383`.
A [teljes Windows CI 41808264246](https://github.com/Gunter1122cloud/untamed-manager-releases/actions/runs/41808264246)
sikeres; [Draft PR #6](https://github.com/Gunter1122cloud/untamed-manager-releases/pull/6).
Ez saját Windows-kód és szintetikus lifecycle ellenőrzése, nem valódi ASA-elfogadás.

## Következő változások és külön ellenőrzésük

- A kötegelt lease megszerzésének hibája után minden korábban megszerzett lease
  felszabadítása megkísérelt. A journal hibája nem rejti el az eredeti hibát;
  a megmaradt pending journal továbbra is megakadályozza a vak ismétlést.
- A JSON-szerkesztő objektumonként elutasítja az ismételt kulcsokat, a tömbökbe
  ágyazott objektumokban és az escape-elt kulcsneveknél is. Ismeretlen config-kind
  nem kerülheti meg az ellenőrzést. A különböző objektumok azonos nevű kulcsa,
  a JSON kis-/nagybetű-megkülönböztetése és az eredeti megjegyzések megmaradnak.
- Új negatív teszt: journalírási hiba után is minden OS-foglalás elengedve;
  a régi pending állapot visszaállítása után új művelet megtagadva.
- Új JSON-tesztek: nested/escaped duplikáció, érvénytelen kind, BOM/CRLF/komment
  és védőmentés megőrzése valódi ideiglenes Windows-fájlokon.
- `tools/lab`: önálló qcow2/ISO és helyi útvonalak ellenőrzése; backing file,
  external data, junction, UNC, alternatív adatfolyam, pont/space alias tiltása.
  Nincs vendég-NIC, megosztás vagy port forward. Közös 8 MiB/s és 200 IOPS
  QEMU-throttle a virtuális lemezen és az ISO-n.
- A natív laborhelper felfüggesztett saját gyermeket hoz létre; csak ellenőrzött
  Windows Job Object-hozzárendelés után engedi futni. 10% CPU hard cap,
  legfeljebb 10 GiB jobmemória, idle prioritás, egyetlen processz és kill-on-close.
  Meglévő folyamathoz nem kapcsolódik. A korlátokat indítás előtt visszaolvassa.
  Az indító 24 GiB szabad host-RAM és 192 GiB szabad lemez alatt, valamint a
  futási határidőnél kizárólag a saját laborprocesszét állítja le.

Ezek új regresszióinak eredménye külön CI-futásban igazolandó. A laborhelper
CI-próbája saját rövid Windows-gyermekfolyamatot indít, VM-et és ASA-t nem.
Az első szakasz TESTED nyilvántartása az első igazolt commitra vonatkozik;
nem minősíti késznek a teljes T01–T33 elfogadási feltételeit.

## Helyi izolált labor

A felhasználó engedélyezte a munkamappán belüli elkülönített labort, de az élő
szerverek, cluster és adatbázis érintése, valamint a gép újraindítása TILOS.
Olvasó felmérés: aktív hypervisor nem látható, Hyper-V/VirtualBox/VMware/QEMU runtime
nem elérhető a vizsgált helyeken; Windows Sandbox bináris sincs. Az optional-feature
lekérdezésből nem állítunk telepítési állapotot, mert nem adott használható választ.
A .isolated-lab könyvtárak és izolációs terv elkészültek; ez nem működő VM vagy ASA-teszt.
Azóta a hivatalos Windows QEMU-csomag SHA-512 ellenőrzése és telepítés nélküli
kibontása megtörtént; a `--version` sikeresen QEMU 11.1.0-t jelzett. MSI/NSIS
telepítő, Windows-szolgáltatás vagy rendszerfunkció nem indult. A vendégkép még hiányzik.
Tervezett határ: 8 GiB vendég-RAM, 2 vCPU, 10% host CPU hard cap, külön virtuális lemez,
nincs hostmappa-megosztás/LAN-hozzáférés. Új rendszerfunkció vagy újraindítás nincs engedélyezve.
A valódi izolált teszt előtt önálló vendég Windows-kép, működő virtualizáció/emuláció,
ASA teszttelepítés és a HP helyi Store-referenciája szükséges. Jelenleg nincs valós
SaveComplete/transfer/SQL restore bizonyíték, ezért a teljes végleges kiadás NINCS KÉSZ.

A végleges funkciók elfogadását nem helyettesítjük gate-átállítással vagy a
tesztkörnyezet sikeresnek nevezésével. A nyilvántartásban minden további T21–T33
feladat is megmarad; nem tekintjük őket ezzel a szakaszszal teljesítettnek.

Technikai források: [QEMU kapcsolók](https://www.qemu.org/docs/master/system/qemu-manpage.html),
[Windows CPU hard cap](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information),
[Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
