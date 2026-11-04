# Elkülönített Windows labor – előkészítés

Ez a meglévő Untamed Manager tesztkörnyezeti eszközkészlete, nem második Manager.
Nem telepít hypervisort, szolgáltatást vagy drivert, és nem indítja újra a hostot.
Az éles ASA/Loader/Manager/Store/SQL nem tesztcél.

## Jelenlegi állapot

- A hordozható QEMU futtatókörnyezet a munkatérben, a repón kívül van.
- A Windows értékelő-ISO letöltése külön `.isolated-lab/images` könyvtárba történik.
- Önálló qcow2 virtuális lemez használható, backing/external-data/encryption nélkül.
- A fájlellenőrzés és a Windows erőforráskorlát valódi CI-gyermekfolyamatokon tesztelt.
- A policy-teszt szintetikus ISO/disk fejlécet használ; azt sosem indítja el.
- Az eszközök önmagukban nem telepítenek Windowst vagy ASA-t. A headless vendéghez
  előkészített, felügyelet nélküli Windows-telepítő és a tesztadatok még szükségesek.
- Nincs valódi ASA SaveComplete, travel vagy SQL restore elfogadási eredmény.

## Védelem

Az `IsolatedLab.psm1` a nyers útvonalnevet és a normalizált helyi elérési utat is
ellenőrzi. UNC, junction/symlink, alternatív adatfolyam, záró pont/space, 8.3-alias
és a laboron kívüli ISO/lemez nem engedett. A qcow2 v3 fejlécből elutasítja a
backing file-t, a titkosítást és az incompatible feature flag-eket, köztük a
külső adatfájlt. Nem nyit fizikai lemezt vagy már létező éles telepítést.

A QEMU paramétereit a policy építi; nincs szabad extra argumentum. Nincs NIC,
hostmegosztás, port forward, QMP/TCP-monitor, soros/parallel hosteszköz vagy
USB/nyerslemez-átadás. A virtuális lemez és az ISO közös 8 MiB/s / 200 IOPS
throttle-csoportban van. A vendég telepítési rebootja a vendégre korlátozódik.

A `WindowsLabJob.cs` kizárólag új saját processzt indít. Felfüggesztve hozza létre,
hozzárendeli az előre korlátozott Windows Job Objecthez, visszaolvassa a korlátokat,
majd engedi futni. CPU hard cap legfeljebb 10%, jobmemória legfeljebb 10 GiB,
idle prioritás, legfeljebb egy processz, nincs örökölt handle vagy breakaway-jog.
Jobbezárás csak a saját gyermeket állítja le; idegen PID-t nem fogad be.

A `Start-IsolatedLab.ps1` ugyanazon laborgyökérre egyszerre egy vendéget enged,
ellenőrzi a QEMU és az ISO megadott SHA-256 lenyomatát, 8 GiB guest-RAMot ad,
24 GiB szabad host-RAM és 192 GiB szabad hostlemez alatt leállítja saját vendégét.
A futási idő alapértéke 10 perc, maximum 60 perc. A hash a fájl azonosságát
ellenőrzi; a hivatalos letöltési forrás igazolása külön előkészítési bizonyíték.

## Ellenőrzés

A workflow `Isolated lab path policy and native resource caps (no guest boot)`
lépése futtatja a `Test-IsolatedLab.ps1`-et a külön GitHub Windows runneren.
Ellenőrzi az útvonalakat, qcow2 feature-öket, junctiont, idézést, valódi natív
CPU/memória jobkorlátot, exit code-ot, idle prioritást és a saját gyermek
megszűnését jobbezárás után. A teszt nem indít VM-et vagy ASA-t.

Hoston a Manager/integrációs tesztcsomagot nem szabad elindítani. A helyi
QEMU `--version` és az üres saját qcow2 létrehozása előkészítési művelet;
ezek nem vendégtelepítési vagy ASA-elfogadási próbák.

Források: [QEMU kapcsolók](https://www.qemu.org/docs/master/system/qemu-manpage.html),
[Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects),
[CPU hard cap](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information).
