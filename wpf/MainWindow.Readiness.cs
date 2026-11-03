using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms=System.Windows.Forms;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowReleaseReadiness()
    {
        Header("Képességek / Fejlesztési állapot",
            "Az eddig egyeztetett Manager-funkciók tényleges állapota. "+
            "A Windows CI nem jelenti az éles ASA/karaktermegőrzés bizonyítását.");
        ManagerFeatureMatrix.AssertHonest();
        var all=ManagerFeatureMatrix.Current;
        Add(Box(V(
            Label("UNTAMED KINGDOM · KIADÁSI KÉPESSÉGEK",20,Mint,true),
            Label("Összes nyilvántartott terület: "+all.Length+
                " · Éles környezetben validált: "+
                all.Count(x=>x.ProductionValidated)+
                " · Nem engedélyezett/még hiányzó: "+
                all.Count(x=>x.Status=="BLOCKED"),13,Soft))));
        foreach(var item in all)
        {
            Add(Box(V(
                Label(item.Title+" · "+item.Status,15,Brushes.White,true),
                Label(item.Remaining,12,Soft))));
        }
        Add(Box(Label("A hiányzó funkciókat tesztadatokon lehet fejleszteni. "+
            "Valódi szervervezérlés vagy Store/SQL-írás kizárólag bizonyított "+
            "kompatibilitás, tulajdonosi jóváhagyás és elkülönített elfogadási próba után kapcsolható be.",
            12,Aqua)));
    }

    private void ShowSafeSupportCenter()
    {
        var status=Label("Nem készült diagnosztikai csomag.",12,Soft);
        Add(Box(V(
            Label("Biztonságos diagnosztika / hibajelentés",19,Mint,true),
            Label("A ZIP csak az alkalmazás verzióját, a konfigurált mapok és feladatok "+
                "darabszámát, a funkciók ellenőrzött készültségi állapotát és "+
                "legfeljebb 100 esemény KÓDJÁT tartalmazza. "+
                "Nem másol crash.log-ot, felhasználói fájlokat, jelszót, "+
                "RCON-/Discord-kulcsot, IP-t, teljes útvonalat, játékosnevet vagy "+
                "karakteradatot.",12,Soft),
            Btn("Titokszűrt diagnosztikai ZIP mentése",()=>{
                using var dlg=new Forms.SaveFileDialog{
                    Title="Untamed Kingdom – titokszűrt támogatási csomag",
                    Filter="ZIP fájl (*.zip)|*.zip",
                    FileName="UntamedManager-Support-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip",
                    OverwritePrompt=true
                };
                if(dlg.ShowDialog()!=Forms.DialogResult.OK)return;
                try
                {
                    var events=_events?.Read();
                    var report=SafeSupportBundle.Create(dlg.FileName,
                        _cluster.Config,events,DateTimeOffset.UtcNow);
                    status.Text="Biztonságos ZIP elkészült: "+report.File+
                        "\nSHA-256: "+report.Sha256+
                        "\nFunkciók: "+report.FeatureCount+
                        " · eseménykódok: "+report.EventCount+
                        "\nÉles szerveradat nem került a jelentésbe.";
                }
                catch(Exception ex)
                {
                    status.Text="Diagnosztikai export elutasítva: "+ex.GetType().Name;
                    WriteLog("support-bundle",ex);
                }
            }),status)));
    }
}
