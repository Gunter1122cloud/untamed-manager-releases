using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms = System.Windows.Forms;

namespace UntamedManager;

// The isolated MariaDB dump and empty-target restore live in the Backup page.
// Full cluster SQL/ASA/Store recovery remains deliberately uncertified.
public partial class MainWindow
{
    public void PrepareScreenshotPage(string page)
    {
        // Only rendered application pages, never a fabricated design preview.
        if(!new[]{"dashboard","servers","mods","plugins","cluster","backups","settings","ini"}
            .Contains(page,StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(page),"Unknown screenshot page");
        Navigate(page);
        UpdateLayout();
        if(page!="dashboard" && string.IsNullOrWhiteSpace(PageTitle.Text))
            throw new InvalidOperationException("Page did not render a title: "+page);
    }

    public void VerifyCompleteManagerCiSmoke()
    {
        if(VersionBadge.Text!="WPF "+UiVersion)
            throw new InvalidOperationException("Stale WPF version badge or assembly mismatch");
        foreach(string page in new[]{"setup","data","steam","settings","readiness","extensions"})
        {
            Navigate(page);
            UpdateLayout();
            if(string.IsNullOrWhiteSpace(PageTitle.Text))
                throw new InvalidOperationException("Empty Release Candidate page: "+page);
        }
        VerifySignedPanelsCiSmoke();
        VerifyDashboardUiCiSmoke();
        Navigate("dashboard");
    }

    private void ShowSetupWizard()
    {
        Header("Kezdeti beállítás / Tesztgép","Géphez kötött map-regisztráció. Nem importálunk automatikusan meglévő karakter- vagy SQL-adatokat.");
        Add(Box(V(
            Label("1. Jelenlegi Manager profil",20,Mint,true),
            Label("Gép: "+_cluster.Config.LocalHostName+" · Profil: "+App.ProfileName+
                " · Azonosító: "+_cluster.Config.LocalHostId,12,Soft),
            Label("Ezen a tesztgépen "+_cluster.Config.LocalMaps.Count+
                " map van helyileg regisztrálva. A távoli Manager adatai nem írhatók.",12,Aqua),
            Btn("Helyi map hozzáadása a beállítóval",()=>ShowMapEditor(null)))));
        Add(Box(V(
            Label("2. Kétgépes kiosztás – tájékoztatás",19,Mint,true),
            Label("UK-HOME-01 / HP: Astraeos, Genesis Part 1, Valguero, The Island, Extinction.",12,Soft),
            Label("UK-HOME-02 / Ryzen: Scorched Earth, Lost Colony, Ragnarok, The Center, Aberration.",12,Soft),
            Label("Ez nem automatikus import; az éles gépekhez nem kapcsolódunk.",12,Soft))));
        Add(Box(V(
            Label("3. Üzemeltetés előfeltételei",19,Mint,true),
            Label("Éles ASA SaveWorld-igazolás és kétgépes Store-kompatibilitás még nem bizonyított. "+
                "Valódi MariaDB-mentés és új, üres célra restore a Mentések oldalon elérhető; "+
                "teljes clusterrestore, éles ASA Stop/Restart/Kill és SteamCMD-frissítés még nem engedélyezett.",12,Soft),
            Btn("Recovery Center megnyitása",()=>Navigate("recovery")),
            Btn("Mentésellenőrzés megnyitása",()=>Navigate("backups")),
            Btn("Cluster Vault / SQL központ",()=>Navigate("data")))));
        ShowVaultProfilePreviewControls();
    }

    private void ShowDataSafetyCenter()
    {
        Header("Untamed Cluster Vault és SQL",
            "Offline Vault/SQL archívumok; valódi MariaDB-mentés és üres célra restore a Mentések oldalon.");
        Add(Label("MariaDB referencia: "+MariaDbCompatibility.DeploymentBaseline+
            ". A 11.4-es ág nem automatikus frissítési cél; dump/restore előtt a szerver és az eszközök verzióját külön igazolni kell.",12,Aqua));
        Add(Box(V(
            Label("Adatbiztonsági kapuk – Release Candidate",19,Mint,true),
            Label("Valódi MariaDB-dump és ellenőrzött új, üres célra történő SQL-restore: a Mentések oldalon. "+
                "Meglévő SQL-adatbázis felülírása, teljes clusterrestore, Cluster Vault-konverzió "+
                "és karakter/tribe módosítás továbbra sem engedélyezett. "+
                "Az SQL-csomag épsége önmagában nem bizonyít karaktertranszfer-kompatibilitást.",12,Soft),
            Btn("Valódi SQL-mentés és üres célra visszatöltés",()=>Navigate("backups")))));
        var database=new ComboBox{ItemsSource=OfflineSqlArchive.SupportedDatabases,
            SelectedIndex=0,Foreground=Brushes.White,Background=Panel};
        var selected=new TextBox{Height=34,IsReadOnly=true,
            Foreground=Brushes.White,Background=Panel};
        var sqlStatus=Label("Még nincs kiválasztott offline .sql fájl.",12,Soft);
        string? recent=null;
        Add(Box(V(
            Label("MariaDB offline SQL csomagolás – SHA-256",19,Mint,true),
            Label("Válassz egy már exportált arkshop.sql, arkpermissions.sql vagy lethalquests.sql fájlt. "+
                "Nem futtatunk SQL parancsot vagy mysqldump-ot. A csomag helyileg, külön mappába kerül.",12,Soft),
            database,selected,
            Btn("Offline SQL fájl kiválasztása",()=>{
                using var picker=new Forms.OpenFileDialog{
                    Title="Offline SQL tesztfájl kiválasztása",
                    Filter="SQL dump (*.sql)|*.sql",CheckFileExists=true};
                if(picker.ShowDialog()==Forms.DialogResult.OK)selected.Text=picker.FileName;
            }),
            Btn("SHA-256 ellenőrzött offline csomag készítése",()=>{
                try
                {
                    var result=OfflineSqlArchive.ImportFixture(_cluster.Store.Root,
                        selected.Text.Trim(),(string)database.SelectedItem,
                        Guid.Parse(_cluster.Config.LocalHostId));
                    recent=result.Package;
                    sqlStatus.Text=result.Database+": "+result.Bytes+" bájt; SHA-256 "+
                        result.Sha256+"\n"+result.Scope+"\nHelyi mentés: "+result.Package;
                }
                catch(Exception ex){sqlStatus.Text="SQL csomag megtagadva: "+UntamedPresentation.Redact(ex.Message);
                    WriteLog("offline-sql",ex);}
            }),
            Btn("Csomag integritásának újraellenőrzése",()=>{
                try
                {
                    if(recent==null)throw new InvalidOperationException("Nincs mentett csomag");
                    var item=OfflineSqlArchive.Verify(recent,(string)database.SelectedItem,
                        Guid.Parse(_cluster.Config.LocalHostId));
                    sqlStatus.Text="SHA-256 OK: "+item.Sha256+
                        "\nEz csak offline csomag. A valódi MariaDB-műveletek a Mentések oldalon találhatók.";
                }
                catch(Exception ex){sqlStatus.Text="Csomag ellenőrzése sikertelen: "+UntamedPresentation.Redact(ex.Message);}
            }),
            Btn("SQL visszaállítás szimulálása (csak TEMP mappába)",()=>{
                try
                {
                    if(recent==null)throw new InvalidOperationException("Nincs ellenőrzött csomag");
                    string path=OfflineSqlArchive.SimulateRestore(recent,
                        (string)database.SelectedItem,Guid.Parse(_cluster.Config.LocalHostId));
                    sqlStatus.Text="KIZÁRÓLAG OFFLINE SZIMULÁCIÓ: SHA-256 szerint ép fájl:\n"+
                        path+"\nValódi MariaDB import NEM történt.";
                }
                catch(Exception ex){sqlStatus.Text="Restore-szimuláció elutasítva: "+UntamedPresentation.Redact(ex.Message);}
            }),sqlStatus)));
        ShowClusterVaultArchiveCenter();
        ShowStoreAliasProof();
        ShowUntamedManagerArchiveAudit();
        ShowHistoricalVaultAudit();
        ShowVaultSessionCopyReader();
        ShowOfflineClusterBackupWizard();
    }

    private void ShowSteamUpdateCenter()
    {
        Header("SteamCMD Update Center – tesztgép",
            "Előnézet és elkülönített TEMP teszttelepítés. Éles ASA frissítés továbbra is tiltva.");
        Add(Box(V(Label("SteamCMD Update Center – v"+UiVersion,20,Mint,true),
            Label("Éles ASA frissítés és automatikus map-restart nem engedélyezett "+
                "igazolt SaveWorld/Stop és valódi ASA kompatibilitási teszt nélkül.",12,Soft))));
        var path=new TextBox{IsReadOnly=true,Height=34,Background=Panel,Foreground=Brushes.White};
        var size=new TextBox{Text="1024",Height=34,Background=Panel,Foreground=Brushes.White};
        var map=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",
            Foreground=Brushes.White,Background=Panel,SelectedIndex=0};
        var status=Label("Nincs SteamCMD update terv.",12,Soft);
        Add(Box(V(Label("Helyi telepítések / frissítési előnézet",19,Mint,true),
            Label("Méretbecslés MiB-ben; a frissítéshez biztonsági tartalékot követelünk.",12,Soft),
            path,
            Btn("steamcmd.exe kijelölése (csak ellenőrzés)",()=>{
                using var picker=new Forms.OpenFileDialog{
                    Filter="SteamCMD|steamcmd.exe",CheckFileExists=true};
                if(picker.ShowDialog()==Forms.DialogResult.OK)path.Text=picker.FileName;
            }),size,map,
            Btn("Megosztott telepítési gyökér ellenőrzése (írás nélkül)",()=>{
                try
                {
                    if(map.SelectedItem is not LocalMap selected ||
                       !_cluster.Config.LocalMaps.Contains(selected))
                        throw new InvalidOperationException("Local map required");
                    if(!long.TryParse(size.Text,out long mb) || mb<1 || mb>500000)
                        throw new InvalidDataException("Invalid update size estimate");
                    var plan=SteamUpdateDryRun.Plan(path.Text.Trim(),
                        selected.InstallationPath,_cluster.Config.LocalMaps.ToArray(),
                        local=>_cluster.Check(local),checked(mb*1024*1024));
                    status.Text="Telepítés: "+plan.InstallRoot+"\nMapok: "+
                        string.Join(", ",plan.Maps)+"\nTelepített build: "+
                        (plan.InstalledBuild??"ismeretlen")+"\nSzabad: "+
                        plan.AvailableBytes/1048576+" MiB\nSzimuláció: "+
                        (plan.DryRunAllowed?"előfeltételek teljesülnek":"BLOKKOLVA")+
                        "\n"+plan.Reason;
                }
                catch(Exception ex){status.Text="Frissítési előnézet elutasítva: "+ex.Message;}
            }),
            Btn("Valódi SteamCMD futtatása – csak izolált TEMP telepítés",async ()=>{
                if(map.SelectedItem is not LocalMap selected ||
                   !_cluster.Config.LocalMaps.Contains(selected))
                {status.Text="Válassz helyi, regisztrált tesztmapot.";return;}
                if(!long.TryParse(size.Text,out long mb) ||mb is <1 or >500000)
                {status.Text="Adj meg érvényes MiB-frissítési méretbecslést.";return;}
                string executable=path.Text.Trim();
                string installation=selected.InstallationPath;
                var selectedMaps=_cluster.Config.LocalMaps.Where(m=>string.Equals(
                    m.InstallationPath,installation,StringComparison.OrdinalIgnoreCase)).ToArray();
                string profile=Path.Combine(Path.GetTempPath(),
                    "Untamed-SteamLab-"+Guid.NewGuid().ToString("N"),"Manager");
                if(MessageBox.Show(
                    "Ez NEM szimuláció: a valódi steamcmd.exe elindulhat és "+
                    "letölthet vagy módosíthat fájlokat, kizárólag egy külön "+
                    "TEMP/Untamed-SteamLab-* telepítésben.\n"+
                    "A két éles szervergép, az éles ASA és a Store nem érinthető.\n"+
                    "Telepítés: "+installation+"\n"+
                    "Érintett helyi mapok: "+selectedMaps.Length+
                    "\nElindítod a tesztfrissítést?",
                    "Valódi SteamCMD – csak elkülönített tesztgép",
                    MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                status.Text="SteamCMD tesztművelet: helyi folyamat- és mentési ellenőrzések...";
                try
                {
                    Directory.CreateDirectory(profile);
                    var receipt=await SteamCmdIsolatedUpdater.RunAsync(
                        executable,installation,profile,selectedMaps,
                        local=>_cluster.Check(local),checked(mb*1024*1024),
                        TimeSpan.FromMinutes(45));
                    status.Text="STEAMCMD TESZTFUTTATÁS VÉGET ÉRT"+
                        "\nSteamCMD-válasz: "+(receipt.SteamAcknowledged?"elfogadva":"nem igazolt")+
                        "\nMentési és pluginfájlok: "+
                        (receipt.ProtectedFilesPreserved?"SHA-256 szerint változatlanok":"HIBA")+
                        "\nBuild: "+(receipt.BuildBefore??"ismeretlen")+" → "+
                        (receipt.BuildAfter??"ismeretlen")+
                        "\nNapló: "+receipt.Operation+
                        "\nÉles ASA-frissítés nem történt.";
                }
                catch(Exception ex)
                {
                    WriteLog("steam-lab-update",ex);
                    status.Text="UM-STEAM-LAB: a frissítés nem igazolható. "+
                        "Sikertelen módosítás után a napló operátori vizsgálatot igényel; "+
                        "a Manager nem indítja újra a műveletet.";
                }
            }),
            Btn("Teljesen offline SteamCMD-update szimuláció",()=>{
                string? fixture=null;
                try
                {
                    fixture=Path.Combine(Path.GetTempPath(),
                        "Untamed-SteamCMD-Demo-"+Guid.NewGuid().ToString("N"));
                    string install=Path.Combine(fixture,"ASA-install");
                    Directory.CreateDirectory(Path.Combine(install,
                        "ShooterGame","Binaries","Win64"));
                    Directory.CreateDirectory(Path.Combine(install,"steamapps"));
                    File.WriteAllText(Path.Combine(fixture,"steamcmd.exe"),
                        "Synthetic fixture: NEVER execute");
                    File.WriteAllText(Path.Combine(install,
                        "ShooterGame","Binaries","Win64","ArkAscendedServer.exe"),
                        "Not a real ASA binary");
                    File.WriteAllText(Path.Combine(install,"steamapps","appmanifest_2430930.acf"),
                        "\"buildid\" \"12345\"");
                    var sample=new LocalMap{Name="OFFLINE Test Install",
                        InstallationPath=install};
                    var plan=SteamUpdateDryRun.Plan(Path.Combine(fixture,"steamcmd.exe"),
                        install,new[]{sample},
                        _=>new ProcessCheck(false,"synthetic confirmed stopped",null,true),
                        1024*1024);
                    var receipt=SteamUpdateDryRun.Simulate(plan,_cluster.Store.Root);
                    status.Text="OFFLINE SIMULÁCIÓ: "+receipt.State+
                        "\n"+receipt.Description+"\nNaplóazonosító: "+receipt.Id+
                        "\nSem SteamCMD, sem ASA nem indult el.";
                }
                catch(Exception ex){status.Text="SteamCMD szimuláció sikertelen: "+ex.Message;}
                finally{if(fixture!=null)try{Directory.Delete(fixture,true);}catch{}}
            }),status)));
    }

    private void ShowSafeProfileTransfer()
    {
        var result=Label("Nincs export vagy import.",12,Soft);
        var path=new TextBox{Height=34,Foreground=Brushes.White,Background=Panel,
            IsReadOnly=true};
        Add(Box(V(
            Label("Manager profil átvitele – titkok nélkül",19,Mint,true),
            Label("Csak a saját gép pontos mapazonosítóira érvényes Watchdog és RCON-monitoring "+
                "preferenciákat mentjük. Nem exportálunk jelszót, API-kulcsot, "+
                "Cluster Vault adatot, SQL-t vagy szerverindítási paramétert.",12,Soft),
            path,
            Btn("Biztonságos beállítás-export",()=>{
                using var save=new Forms.SaveFileDialog{
                    Filter="Manager beállítások (*.json)|*.json",
                    FileName="untamed-preferences-"+DateTime.Now.ToString("yyyyMMdd")+".json"};
                if(save.ShowDialog()!=Forms.DialogResult.OK)return;
                try{LocalProfileTransfer.ExportToFile(_cluster.Config,save.FileName);
                    result.Text="Export elkészült: "+save.FileName;}
                catch(Exception ex){result.Text="Export elutasítva: "+ex.Message;}
            }),
            Btn("Beállítás-import fájl kiválasztása",()=>{
                using var open=new Forms.OpenFileDialog{
                    Filter="Manager beállítások (*.json)|*.json",CheckFileExists=true};
                if(open.ShowDialog()==Forms.DialogResult.OK)path.Text=open.FileName;
            }),
            Btn("Import előnézete (nem módosít)",()=>{
                try
                {
                    var file=new FileInfo(path.Text);
                    if(file.Length>200000)throw new InvalidDataException("Config too large");
                    var preview=LocalProfileTransfer.Preview(_cluster.Config,
                        File.ReadAllText(file.FullName));
                    result.Text=(preview.Allowed?"Ellenőrizhető":"TILTVA")+" · "+
                        preview.MatchedMaps+" map · "+preview.Reason;
                }
                catch(Exception ex){result.Text="Előnézet sikertelen: "+ex.Message;}
            }),
            Btn("Import jóváhagyása (csak helyi preferenciák)",()=>{
                try
                {
                    var file=new FileInfo(path.Text);
                    if(file.Length>200000)throw new InvalidDataException("Config too large");
                    string json=File.ReadAllText(file.FullName);
                    var preview=LocalProfileTransfer.Preview(_cluster.Config,json);
                    if(!preview.Allowed)throw new InvalidOperationException(preview.Reason);
                    if(MessageBox.Show("Csak a Watchdog ellenőrzési időköz és a ListPlayers "+
                        "monitoring változik. A meglévő konfigurációról a Manager ment "+
                        "előző verziót. Folytatod?","Csak helyi preferenciák",
                        MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                    LocalProfileTransfer.Apply(_cluster.Config,json);
                    _cluster.Save();
                    result.Text="Import sikeres: "+preview.MatchedMaps+
                        " helyi map preferenciái, Store/SQL/indítás változatlan.";
                }
                catch(Exception ex){result.Text="Import elutasítva: "+ex.Message;}
            }),result)));
    }
}

