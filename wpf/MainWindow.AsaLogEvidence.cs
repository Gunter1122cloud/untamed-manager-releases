using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms=System.Windows.Forms;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowHistoricalLogEvidencePanel()
    {
        var asa=new TextBox{Height=32,IsReadOnly=true,
            Foreground=Brushes.White,Background=Panel};
        var api=new TextBox{Height=32,IsReadOnly=true,
            Foreground=Brushes.White,Background=Panel};
        var status=Label("Válassz korábban kimásolt TXT/LOG fájlokat. Nincs "+
            "szerver-, RCON-, hálózati vagy Store-művelet.",12,Soft);
        string Pick(string title)
        {
            using var dialog=new Forms.OpenFileDialog{
                Title=title,Filter="Naplómásolat (*.txt;*.log)|*.txt;*.log",
                CheckFileExists=true};
            return dialog.ShowDialog()==Forms.DialogResult.OK?dialog.FileName:"";
        }
        Button? run=null;
        run=Btn("Történeti ASA / API indulási bizonyíték ellenőrzése",async ()=>{
            if(run!=null)run.IsEnabled=false;
            try
            {
                var result=await Task.Run(()=>AsaHistoricalLogReader.InspectFiles(
                    asa.Text,api.Text));
                status.Text="TÖRTÉNETI NAPLÓELLENŐRZÉS (CSAK OLVASÁS)"+
                    "\nASA naplónyitás: "+(result.LogOpened?"IGEN":"NINCS")+
                    "\nASA sikeres indulás: "+(result.ServerStarted?"IGEN":"NINCS")+
                    "\nCsatlakozás hirdetése: "+(result.JoinAdvertised?"IGEN":"NINCS")+
                    "\nIndulás → hirdetés: "+(result.StartedToAdvertising is { } duration?
                        duration.TotalSeconds.ToString("F1")+" s":"NEM IGAZOLT")+
                    "\nAPI betöltve: "+(result.ApiLoaded?"IGEN":"NINCS")+
                    "\nAPI hooks: "+(result.ApiHooksInitialized?"IGEN":"NINCS")+
                    "\nAPI és ASA azonos időablak: "+(result.ApiSameStartWindow?"IGEN":"NEM IGAZOLT")+
                    "\nAPI plugin naplósorok: "+result.ApiPluginLines+
                    "\nSzabályos leállásra utaló naplósorrend: "+
                        (result.ShutdownSequenceConfirmed?"IGEN":"NEM IGAZOLT")+
                    "\nÉLŐ FOLYAMAT / RCON READY: NEM IGAZOLT"+
                    "\nSAVEWORLD BEFEJEZÉS: NEM IGAZOLT"+
                    "\nEz nem engedélyez Start / Stop / Restart / Kill műveletet.";
            }
            catch(Exception e)
            {
                // Never include a copied log path or raw log text in the UI error.
                status.Text="Naplóellenőrzés visszautasítva: "+
                    e.GetType().Name+". Nincs szerverművelet.";
            }
            finally{if(run!=null)run.IsEnabled=true;}
        });
        Add(Box(V(
            Label("ASA / ArkApi · történeti naplóelemző",18,Mint,true),
            Label("Eltérő indítási munkameneteket nem lehet összekapcsolni. "+
                "RCON-válasz nem SaveComplete; a naplóelemzés sosem indít szervert.",12,Soft),
            Label("ASA-naplómásolat",12,Aqua),asa,
            Btn("ASA TXT/LOG kiválasztása",()=>asa.Text=Pick("ASA naplómásolat")),
            Label("Opcionális ArkApi-naplómásolat",12,Aqua),api,
            Btn("ArkApi TXT/LOG kiválasztása",()=>api.Text=Pick("ArkApi naplómásolat")),
            run,status)));
    }
}
