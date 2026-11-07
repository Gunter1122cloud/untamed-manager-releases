using System;
using System.IO;
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
    // Offline-only: existing SHA-verified 5+5 stopped-map backup packages and
    // the operator's SEPARATE COPY of the original Store/transfer directory.
    // No SQL, no Kova session creation, no server stop/start, no UNC writer.
    private void ShowOfflineClusterBackupWizard()
    {
        TextBox Folder()=>new(){Height=34,IsReadOnly=true,
            Foreground=Brushes.White,Background=Panel};
        var hp=Folder();
        var ryzen=Folder();
        var transfer=Folder();
        var output=Folder();
        var status=Label("Válaszd ki az előzetesen elkészített, HELYI másolatokat: "+
            "öt HP és öt Ryzen mentéscsomag, valamint a közös Store/transfer fájlok másolata.",12,Soft);
        void Pick(TextBox target,string description)
        {
            using var box=new Forms.FolderBrowserDialog{Description=description};
            if(box.ShowDialog()==Forms.DialogResult.OK)target.Text=box.SelectedPath;
        }
        Button? create=null;
        create=Btn("SQL-mentes, 10-map offline ASA archívum készítése",async ()=>{
            if(create!=null)create.IsEnabled=false;
            try
            {
                string hpCopy=hp.Text.Trim(),ryzenCopy=ryzen.Text.Trim(),
                    transferCopy=transfer.Text.Trim(),parent=output.Text.Trim();
                if(!Directory.Exists(hpCopy)||!Directory.Exists(ryzenCopy)||
                   !Directory.Exists(transferCopy)||!Directory.Exists(parent))
                    throw new InvalidDataException("Mind a négy létező, elkülönített mappa szükséges");
                string destination=Path.Combine(parent,"Untamed-ASA-Offline-"+
                    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+
                    Guid.NewGuid().ToString("N")[..8]);
                if(WinMessageBox.Show(
                    "Ez a művelet csak ELŐRE elkészített offline helyi másolatokból "+
                    "állít össze új SHA-256-tal ellenőrzött archívumot. "+
                    "A közös Store/transfer mappa eredetije nem módosul. "+
                    "Nem készít adatbázis-mentést, és nem igazol élő kétgépes "+
                    "karakterutazási vagy pillanatkép-konzisztenciát.\n\n"+
                    "Cél: "+destination+"\n\nJóváhagyod?",
                    "Untamed ASA / Store offline archívum",MessageBoxButton.YesNo,
                    MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                status.Text="10 map és Store/transfer fájlintegritás ellenőrzése...";
                var result=await Task.Run(()=>
                    OfflineAsaClusterArchive.BuildFromFolders(
                        hpCopy,ryzenCopy,transferCopy,destination));
                status.Text="OFFLINE SHA-256 ARCHÍVUM KÉSZ\nMapok: "+result.Maps.Length+
                    " (HP: 5, Ryzen: 5) · Transferállományok: "+result.Transfer.Length+
                    "\nCsomag: "+destination+
                    "\nÉLŐ STORE-V1 KONZISZTENCIA NINCS IGAZOLVA; éles restore letiltva.";
            }
            catch(Exception ex)
            {
                WriteLog("asa-offline-cluster-backup",ex);
                status.Text="A csomag nem tekinthető elkészültnek. "+
                    "A félbemaradt .incomplete mappát kézzel ellenőrizd.";
            }
            finally{if(create!=null)create.IsEnabled=true;}
        });
        Add(Box(V(
            Label("5+5 mapos ASA / Store offline archívum – SQL nélkül",20,Mint,true),
            Label("Öt HP + öt Ryzen, korábban ellenőrzött Untamed map-mentéscsomag. "+
                "A transfer könyvtár kizárólag az eredeti Store-v1 állományok "+
                "külön elkészített másolata lehet, az eredeti Store-v1 műveleti metaadatokkal. "+
                "A Manager nem hajt végre új Store-konverziót.",12,Soft),
            Label("HP: öt offline map mentéscsomag szülőkönyvtára",12,Aqua),hp,
            Btn("HP másolatmappa",()=>Pick(hp,"Öt leállított HP map külön másolt mentéscsomagjai")),
            Label("Ryzen: öt offline map mentéscsomag szülőkönyvtára",12,Aqua),ryzen,
            Btn("Ryzen másolatmappa",()=>Pick(ryzen,"Öt leállított Ryzen map külön másolt mentéscsomagjai")),
            Label("Store/transfer: eredeti közös mappa ELŐRE elkészített helyi másolata",12,Aqua),transfer,
            Btn("Transfer másolatmappa",()=>Pick(transfer,"Eredeti Store/transfer könyvtár helyi MÁSOLATA")),
            Label("Új archívum szülőmappája (különálló helyi lemez)",12,Aqua),output,
            Btn("Célmappa kijelölése",()=>Pick(output,"Új SQL-mentes archívum szülőmappája")),
            create,
            Btn("Meglévő ASA offline archívum SHA-256 ellenőrzése",async ()=>{
                using var box=new Forms.FolderBrowserDialog{
                    Description="Korábban elkészített Untamed-ASA-Offline archívum"};
                if(box.ShowDialog()!=Forms.DialogResult.OK)return;
                try
                {
                    status.Text="A teljes archívum fájlonkénti SHA-256 ellenőrzése...";
                    var report=await Task.Run(()=>
                        OfflineAsaClusterArchive.Verify(box.SelectedPath));
                    status.Text="ARCHÍVUM ÉP · "+report.Maps.Length+" map · "+
                        report.Transfer.Length+" transferfájl · "+report.Batch+
                        "\nÉles karakterutazás nem igazolt.";
                }
                catch(Exception ex)
                {
                    WriteLog("asa-offline-cluster-verify",ex);
                    status.Text="Archívum ellenőrzése sikertelen; nem használható visszaállításhoz.";
                }
            }),
            Btn("10 map + transfer próba-visszaállítás – kizárólag TEMP labor",async ()=>{
                using var box=new Forms.FolderBrowserDialog{
                    Description="Válassz már elkészült SQL-mentes ASA archívumot"};
                if(box.ShowDialog()!=Forms.DialogResult.OK)return;
                string archive=box.SelectedPath;
                string destination=Path.Combine(Path.GetTempPath(),
                    "Untamed-AsaRestore-Lab-"+Guid.NewGuid().ToString("N"));
                if(WinMessageBox.Show(
                    "Az archívumot egy véletlenszerű, ÚJ Windows TEMP könyvtárba "+
                    "ellenőrzötten kibontja. Nem ír vissza ASA vagy Store/transfer "+
                    "szerverfájlt, és nem importál adatbázist.\n\n"+
                    "Cél: "+destination+"\n\nJóváhagyod?",
                    "Csak elkülönített próba-visszaállítás",
                    MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                status.Text="Próba-visszaállítás és világfájl SHA-256 ellenőrzés...";
                try
                {
                    var result=await Task.Run(()=>
                        OfflineAsaRestoreLab.MaterializeToLab(archive,destination));
                    status.Text="PRÓBA-VISSZAÁLLÍTÁS FÁJLINTEGRITÁSA ÉP · "+
                        result.SourceArchive.Maps.Length+" map · "+
                        result.MapFiles.Length+" világfájl"+
                        "\nLabor: "+destination+
                        "\nJátékbeli működés és éles Store-v1 írás NEM bizonyított.";
                }
                catch(Exception ex)
                {
                    WriteLog("asa-offline-restore-lab",ex);
                    status.Text="Izolált restore sikertelen; .incomplete mappa operátori ellenőrzést igényel.";
                }
            }),status)));
    }
}
