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
    private void ShowHistoricalTwoHostKovaAudit()
    {
        var hp=new TextBox{Height=34,IsReadOnly=true,Foreground=Brushes.White,Background=Panel};
        var ryzen=new TextBox{Height=34,IsReadOnly=true,Foreground=Brushes.White,Background=Panel};
        var result=Label("Két különálló, már másolt, titokszűrt cluster-store.json jelentés szükséges.",12,Soft);
        void Select(TextBox target,string host)
        {
            using var dialog=new Forms.OpenFileDialog{
                Title="Korábbi "+host+" titokszűrt cluster-store.json másolata",
                Filter="JSON-fájl (*.json)|*.json",CheckFileExists=true};
            if(dialog.ShowDialog()==Forms.DialogResult.OK)target.Text=dialog.FileName;
        }
        Add(Box(V(
            Label("Kova kétgépes történeti audit · HP + Ryzen",20,Mint,true),
            Label("A 2026.11.02-i eredeti HP- és Ryzen-audit különböző "+
                "JSON-sémáit ismeri. Kizárólag kimásolt JSON-fájlokat olvas. "+
                "Nem ellenőrzi a MOST futó Kova peer-eket, nem csatlakozik SMB-hez "+
                "és nem ad engedélyt karakter/Store átvételre.",12,Soft),
            Label("HP / UK-HOME-01 cluster-store.json (másolat)",12,Aqua),hp,
            Btn("HP riport kiválasztása",()=>Select(hp,"HP")),
            Label("Ryzen / UK-HOME-02 cluster-store.json (másolat)",12,Aqua),ryzen,
            Btn("Ryzen riport kiválasztása",()=>Select(ryzen,"Ryzen")),
            Btn("5+5 map, peer, session, lock és SHA-256 ellenőrzése",()=>{
                try
                {
                    var first=KovaAuditBridge.InspectCopy(hp.Text,KovaAuditHost.Hp);
                    var second=KovaAuditBridge.InspectCopy(ryzen.Text,KovaAuditHost.Ryzen);
                    var pair=KovaAuditBridge.Evaluate(first,second);
                    result.Text=(pair.HistoricalSnapshotConsistent?
                        "TÖRTÉNETI AUDIT EGYEZIK":"TÖRTÉNETI AUDIT ELUTASÍTVA")+
                        "\nHP: "+first.MapNames.Length+" map, "+
                        "Ryzen: "+second.MapNames.Length+
                        " map\nHP SHA-256: "+first.SourceHash+
                        "\nRyzen SHA-256: "+second.SourceHash+
                        "\nMapok: "+string.Join(", ",pair.MapNames)+
                        "\n"+(pair.Issues.Length>0?
                            string.Join("\n",pair.Issues)+"\n":"")+
                        "NEM élő állapot és NEM Kova Store 1:1 kompatibilitási igazolás.";
                }
                catch(Exception ex)
                {
                    result.Text="Kova-audit elutasítva: "+ex.Message;
                    WriteLog("kova-readonly-audit",ex);
                }
            }),result)));
    }
}
