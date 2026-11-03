using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms=System.Windows.Forms;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowUntamedManagerArchiveAudit()
    {
        var source=new TextBox{Height=34,IsReadOnly=true,
            Foreground=Brushes.White,Background=Panel};
        var result=Label("Nincs kiválasztott archívum. A vizsgálat semmit nem importál "+
            "az aktuális Manager-profilba.",12,Soft);
        Button? inspect=null;
        inspect=Btn("A teljes Manager-archívum ellenőrzése (csak olvasás)",async ()=>{
            if(inspect!=null)inspect.IsEnabled=false;
            try
            {
                result.Text="Fájlok, munkamenetek, folyamatnapló és "+
                    "ütemezési állapotok ellenőrzése...";
                string selected=source.Text.Trim();
                var audit=await Task.Run(()=>
                    UntamedManagerArchiveInspector.Inspect(selected));
                result.Text="UNTAMED MANAGER · MIGRÁCIÓS ELLENŐRZÉS"+
                    "\nFájlok SHA-256: "+audit.VerifiedFiles+
                    "\nHelyi mapok: "+audit.LocalMaps+
                    " · teljes cluster: "+audit.SharedMaps+
                    " · partnerek: "+audit.StorePeers+
                    "\nMunkamenet: "+(audit.SharedSessionConsistent?"EGYEZIK":"ELTÉR")+
                    " · konfigurációhash: "+
                    (audit.PeerConfigHashesVerified?"EGYEZIK":"ELTÉR")+
                    "\nIdőzített műveletek: "+audit.ScheduledTasks+
                    " · legutóbb kihagyott: "+audit.SkippedTaskResults+
                    " · szinkronhibás profilok: "+audit.ScheduleSyncErrors+
                    "\nKorábbi automatikus indítás: "+audit.AutoStartProfiles+
                    " · watchdog: "+audit.WatchdogProfiles+
                    " · API indulási állapot: "+audit.PendingLoaderStates+
                    "\nKülön backup-beállítások: "+audit.BackupPolicyFiles+
                    "\nRégi Discord-konfiguráció: "+
                    (audit.DiscordDisabled?"INAKTÍV":"BEKAPCSOLVA A MÁSOLATBAN")+
                    " · távoli vezérlési szabályok: "+
                    (audit.DiscordControlsConfigured?"TALÁLHATÓK":"NINCSENEK")+
                    "\nMapok: "+string.Join(", ",audit.MapNames)+
                    "\n"+string.Join("\n",audit.Findings.Select(x=>"• "+x))+
                    "\nForrás SHA-256: "+audit.SourceSha256+
                    "\nIMPORTÁLÁS / START / RESTORE: TILTVA. "+
                    "A mentett állapot történeti, nem bizonyít jelenlegi üzemkészséget.";
            }
            catch(Exception ex)
            {
                result.Text="Az archívum nem fogadható el: "+
                    ex.GetType().Name+
                    ". Nem történt mentés, felülírás vagy profilimport.";
                // Avoid writing exception text: it may include a confidential
                // local archive path or other user-controlled fields.
            }
            finally{if(inspect!=null)inspect.IsEnabled=true;}
        });
        Add(Box(V(
            Label("UNTAMED MANAGER · MIGRÁCIÓS VAULT",20,Mint,true),
            Label("A korábbi Windows szerverkezelőből készített konfigurációs ZIP "+
                "ellenőrzése. A ZIP-et nem bontjuk ki, nem futtatunk LevelDB-t, "+
                "nem vesszük át automatikusan a parancsokat, Discord-titkokat, "+
                "folyamatazonosítókat vagy régi ütemezéseket. "+
                "A munkamenet- és karakterbiztonság külön bizonyítandó.",12,Soft),
            Label("Korábban kimásolt Manager-metadata archívum",12,Aqua),
            source,
            Btn("Manager-archívum kiválasztása",()=>{
                using var dialog=new Forms.OpenFileDialog{
                    Filter="ZIP-archívum (*.zip)|*.zip",CheckFileExists=true,
                    Title="Untamed Manager – történeti konfigurációmásolat"};
                if(dialog.ShowDialog()==Forms.DialogResult.OK)
                    source.Text=dialog.FileName;
            }),
            inspect,result)));
    }
}
