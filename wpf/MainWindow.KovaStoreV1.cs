using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms=System.Windows.Forms;

namespace UntamedManager;
public partial class MainWindow
{
    private void ShowKovaStoreV1CopyReader()
    {
        TextBox Field()=>new(){Height=34,IsReadOnly=true,
            Foreground=Brushes.White,Background=Panel};
        var share=Field();var hp=Field();var ryzen=Field();
        var result=Label("Három helyi, MÁSOLT mappát válassz. Élő SMB-re nem kapcsolódunk.",12,Soft);
        void Pick(TextBox target,string description)
        {
            using var dialog=new Forms.FolderBrowserDialog{Description=description};
            if(dialog.ShowDialog()==Forms.DialogResult.OK)target.Text=dialog.SelectedPath;
        }
        Add(Box(V(
            Label("v1.6 · Valódi Kova Store-v1 koordinációs formátum – csak olvasás",19,Mint,true),
            Label("A kézi másolatban operation.json és két peer UUID.json, "+
                "helyi másolatokban shared-store-device-v1.json és "+
                "shared-store-lock-v1.json szükséges. A Kova forrása, Store, lock "+
                "és transferadatok változatlanok maradnak. NEM élő kompatibilitási tanúsítvány.",12,Soft),
            Label("Közös koordinációs könyvtár helyi MÁSOLATA",12,Aqua),share,
            Btn("Közös koordinációs mappa kijelölése",()=>Pick(share,
                "Kizárólag a közös .kova-store-v1 mappa offline másolata")),
            Label("HP helyi Kova-metaadatok MÁSOLATA",12,Aqua),hp,
            Btn("HP helyi másolat kijelölése",()=>Pick(hp,
                "HP: device/lock fájlok másolata, nem az éles Kova adatkönyvtár")),
            Label("Ryzen helyi Kova-metaadatok MÁSOLATA",12,Aqua),ryzen,
            Btn("Ryzen helyi másolat kijelölése",()=>Pick(ryzen,
                "Ryzen: device/lock fájlok másolata, nem az éles Kova adatkönyvtár")),
            Btn("Két peer / session / map és lock ellenőrzése (READ-ONLY)",()=>{
                try
                {
                    var x=KovaStoreV1CopyReader.Inspect(
                        share.Text.Trim(),hp.Text.Trim(),ryzen.Text.Trim());
                    result.Text=(x.HistoricalConsistent?
                        "KIMÁSOLT STORE-V1 METAADATAI EGYEZNEK":
                        "KOORDINÁCIÓS ELLENTMONDÁS – ÁTVÉTEL TILTVA")+
                        "\nSession: "+x.Session+
                        "\nMapok: "+string.Join(", ",x.MapNames)+
                        "\nPeer azonosítók: "+string.Join(" / ",x.DeviceIds)+
                        "\n"+string.Join("\n",x.Problems)+
                        "\n"+x.Warning+
                        "\nÉles ASA Start/Stop, migráció, Store-írás továbbra is TILTVA.";
                }
                catch(Exception ex)
                {
                    result.Text="Store-v1 olvasó elutasítva: "+ex.Message;
                    WriteLog("kova-raw-store-v1",ex);
                }
            }),result)));
    }
}
