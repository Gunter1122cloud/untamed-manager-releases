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
            Label("Untamed Cluster Vault · Közös munkamenet-ellenőrzés",19,Mint,true),
            Label("A kézi másolatban operation.json és két peer UUID.json, "+
                "helyi másolatokban az azonosító és zárolási metaadatok "+
                "szükségesek. A közös archívum, munkamenet és zárolások "+
                "és transferadatok változatlanok maradnak. NEM élő kompatibilitási tanúsítvány.",12,Soft),
            Label("Közös koordinációs könyvtár helyi MÁSOLATA",12,Aqua),share,
            Btn("Közös koordinációs mappa kijelölése",()=>Pick(share,
                "Kizárólag a közös cluster-koordináció helyi másolata")),
            Label("HP helyi Vault-metaadatok MÁSOLATA",12,Aqua),hp,
            Btn("HP helyi másolat kijelölése",()=>Pick(hp,
                "HP: azonosító/zárolás másolata, nem az éles adatkönyvtár")),
            Label("Ryzen helyi Vault-metaadatok MÁSOLATA",12,Aqua),ryzen,
            Btn("Ryzen helyi másolat kijelölése",()=>Pick(ryzen,
                "Ryzen: azonosító/zárolás másolata, nem az éles adatkönyvtár")),
            Btn("Két peer / session / map és lock ellenőrzése (READ-ONLY)",()=>{
                try
                {
                    var x=KovaStoreV1CopyReader.Inspect(
                        share.Text.Trim(),hp.Text.Trim(),ryzen.Text.Trim());
                    result.Text=x.Report==null?"Hiányzó ellenőrzési jelentés.":FormatVaultReport(x.Report);
                }
                catch(Exception)
                {
                    result.Text="VAULT_INPUT: a másolat hiányos, hibás, változó vagy nem biztonságos. Éles használat tiltva.";
                }
            }),result)));
    }
}

