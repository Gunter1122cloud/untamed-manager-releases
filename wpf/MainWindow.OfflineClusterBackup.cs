using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms=System.Windows.Forms;
using WinMessageBox=System.Windows.MessageBox;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowOfflineClusterBackupWizard()
    {
        TextBox NewBox()=>new(){Height=34,IsReadOnly=true,
            Foreground=Brushes.White,Background=Panel};
        var hp=NewBox();var ryzen=NewBox();var sql=NewBox();var target=NewBox();
        var status=Label("Válaszd ki az ELŐZETESEN MÁSOLT 5+5 map és 3 SQL "+
            "mentéscsomagot a külön Windows tesztgépen.",12,Soft);
        void Pick(TextBox field,string caption)
        {
            using var dialog=new Forms.FolderBrowserDialog{Description=caption};
            if(dialog.ShowDialog()==Forms.DialogResult.OK)field.Text=dialog.SelectedPath;
        }
        Button? build=null;
        build=Btn("5+5 map és 3 SQL OFFLINE archívum összeállítása",async ()=>{
            if(build!=null)build.IsEnabled=false;
            try
            {
                var p1=hp.Text.Trim();var p2=ryzen.Text.Trim();
                var p3=sql.Text.Trim();var dest=Path.Combine(target.Text.Trim(),
                    "Untamed-Offline-Cluster-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+
                    Guid.NewGuid().ToString("N")[..8]);
                if(WinMessageBox.Show(
                    "Ez 13 korábban elkészített, kizárólag HELYI mentéscsomagból "+
                    "valós SHA-256 ellenőrzött fájlmásolatot épít.\n"+
                    "ÉLES ASA-MENTÉST, SQL-DUMPOT ÉS KOVA STORE-MENTÉST NEM VÉGEZ. "+
                    "A kétgépes konzisztenciát NEM bizonyítja.\n\n"+
                    "Cél: "+dest+"\n\nFolytatod?",
                    "Offline 10-map archívum",MessageBoxButton.YesNo,
                    MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                status.Text="Offline csomagolás folyamatban; források nem módosulnak...";
                var result=await Task.Run(()=>OfflineClusterBackup.BuildFromFolders(
                    p1,p2,p3,dest));
                status.Text="OFFLINE FÁJLINTEGRITÁS ARCHÍVUM KÉSZ\n"+
                    "Map-csomagok: "+result.Items.Count(x=>x.Host!="SQL")+
                    " · SQL-exportok: "+result.Items.Count(x=>x.Host=="SQL")+
                    "\nCsomag: "+dest+"\n"+result.Disclaimer;
            }
            catch(Exception ex)
            {
                status.Text="UM-OFFLINE-BACKUP: a másolás nem fejeződött be."+
                    "\nFélbemaradt .incomplete könyvtárat kézzel kell megvizsgálni.";
                WriteLog("offline-cluster-archive",ex);
            }
            finally{if(build!=null)build.IsEnabled=true;}
        });
        Add(Box(V(
            Label("v1.6 · Kétgépes OFFLINE archívum / 10 map + 3 SQL",20,Mint,true),
            Label("Másolt backup-csomagok: HP mappában 5 mapcsomag, Ryzen mappában 5; "+
                "SQL mappában arkshop / arkpermissions / lethalquests almappák. "+
                "Minden mappának korábban elkészített Untamed manifest.json + data + complete "+
                "állományt kell tartalmaznia. Az eredeti hostokkal nem kommunikál.",12,Soft),
            Label("HP – öt map offline mentésmappája",12,Aqua),hp,
            Btn("HP mappa kijelölése",()=>Pick(hp,"HP offline 5 backup-mappa")),
            Label("Ryzen – öt map offline mentésmappája",12,Aqua),ryzen,
            Btn("Ryzen mappa kijelölése",()=>Pick(ryzen,"Ryzen offline 5 backup-mappa")),
            Label("SQL – három offline export csomag",12,Aqua),sql,
            Btn("SQL mappa kijelölése",()=>Pick(sql,"arkshop / arkpermissions / lethalquests csomagok")),
            Label("Célkönyvtár a külön tesztgépen",12,Aqua),target,
            Btn("Célkönyvtár kijelölése",()=>Pick(target,"Üres hely külön a forrásoktól")),
            build,
            Btn("Meglévő OFFLINE clusterarchívum ellenőrzése",async ()=>{
                using var dialog=new Forms.FolderBrowserDialog{
                    Description="Korábban létrehozott Untamed-Offline-Cluster mappa"};
                if(dialog.ShowDialog()!=Forms.DialogResult.OK)return;
                try
                {
                    var result=await Task.Run(()=>OfflineClusterBackup.Verify(dialog.SelectedPath));
                    status.Text="ARCHÍVUM SHA-256 ÉP: "+result.Items.Length+
                        " csomag, batch "+result.Batch+
                        "\n"+result.Disclaimer;
                }
                catch(Exception ex){status.Text="Archívum nem ellenőrizhető: "+
                    UntamedPresentation.Redact(ex.Message);}
            }),
            Btn("Félbeszakadt 37-elemes backupnapló ellenőrzése",async ()=>{
                using var picker=new Forms.FolderBrowserDialog{
                    Description="Válaszd ki a félbeszakadt .incomplete offline batch mappát"};
                if(picker.ShowDialog()!=Forms.DialogResult.OK)return;
                string selected=picker.SelectedPath;
                string original=selected.EndsWith(".incomplete",
                    StringComparison.OrdinalIgnoreCase)?
                    selected[..^".incomplete".Length]:selected;
                try
                {
                    var progress=await Task.Run(()=>
                        ClusterBatchArchive.InspectInterrupted(original));
                    status.Text=(progress.NeedsOperator?
                        "T07 BACKUP – OPERÁTORI VIZSGÁLAT SZÜKSÉGES":
                        "Nincs befejezetlen másolás")+
                        "\nEllenőrzött fájlok: "+progress.VerifiedFiles+
                        "\nBatch: "+(progress.BatchId?.ToString("N")??"nem igazolt")+
                        "\n"+progress.Reason+
                        "\nAutomatikus újraindítás vagy törlés: NINCS.";
                }
                catch(Exception ex)
                {
                    WriteLog("offline-batch-progress",ex);
                    status.Text="UM-BACKUP-JOURNAL: a másolás naplója nem ellenőrizhető. "+
                        "Automatikus folytatás nincs.";
                }
            }),
            Btn("10 map + 3 SQL visszaállítási PRÓBA – kizárólag izolált TEMP",async ()=>{
                using var dialog=new Forms.FolderBrowserDialog{
                    Description="Válassz hitelesített offline clusterarchívumot (13 csomag)"};
                if(dialog.ShowDialog()!=Forms.DialogResult.OK)return;
                string archive=dialog.SelectedPath;
                string destination=Path.Combine(Path.GetTempPath(),
                    "Untamed-Isolated-Restore-"+Guid.NewGuid().ToString("N"));
                if(WinMessageBox.Show(
                    "A 10 map játékfájljait és 3 SQL-exportját egy ÚJ, elkülönített "+
                    "TEMP könyvtárba másolja. Nem állít vissza valódi ASA/Store/SQL "+
                    "szolgáltatást és nem írja felül a meglévő fájlokat.\n"+
                    "Cél: "+destination+"\n\nFolytatod?",
                    "Izolált offline restore-próba",MessageBoxButton.YesNo,
                    MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                status.Text="Izolált restore fájlmásolás és SHA-256 ellenőrzés...";
                try
                {
                    var result=await Task.Run(()=>
                        ClusterRestoreCoordinator.MaterializeToLab(archive,destination));
                    status.Text="IZOLÁLT RESTORE-FÁJLOK ELKÉSZÜLTEK\n"+
                        "Mapok: "+result.Maps.Length+" · SQL-exportok: "+result.Sql.Length+
                        "\nKönyvtár: "+destination+
                        "\nÉLES ASA/SQL/STORE VISSZAÁLLÍTÁS NEM TÖRTÉNT.";
                }
                catch(Exception ex)
                {
                    WriteLog("isolated-cluster-restore",ex);
                    status.Text="UM-RESTORE-LAB: visszaállítási próba elutasítva. "+
                        "Az esetleges .incomplete mappa külön operátori vizsgálatot igényel.";
                }
            }),status)));
    }
}
