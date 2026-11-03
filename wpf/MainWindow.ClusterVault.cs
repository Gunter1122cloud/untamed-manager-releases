using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms=System.Windows.Forms;

namespace UntamedManager;

// Untamed-owned presentation. The archive adapter intentionally understands
// the established Store-v1 disk format without changing it or adopting its
// external implementation.
public partial class MainWindow
{
    private void ShowClusterVaultArchiveCenter()
    {
        var zip=new TextBox{Height=34,IsReadOnly=true,
            Background=Panel,Foreground=Brushes.White};
        var status=Label("Válassz egy már kimásolt, helyi Cluster Vault ZIP-et. "+
            "A program a csomagot közvetlenül olvassa, nem bontja ki és nem módosítja.",12,Soft);
        Button? inspect=null;
        inspect=Btn("Untamed Cluster Vault · teljes ZIP-integritás ellenőrzése",async ()=>{
            if(inspect!=null)inspect.IsEnabled=false;
            try
            {
                string input=zip.Text.Trim();
                status.Text="Vault-archívum és a mentett cluster-kapcsolatok ellenőrzése...";
                var result=await Task.Run(()=>ClusterVaultArchiveInspector.Inspect(input));
                string issues=result.Warnings.Length==0?"Nincs figyelmeztetés.":
                    string.Join("\n",result.Warnings.Select(x=>"• "+x));
                status.Text=(result.SessionConsistent&&result.ConfigHashesVerified&&
                        result.LocalHostMapRecordsActive?
                        "ELLENŐRZÖTT TÖRTÉNETI CLUSTER VAULT ARCHÍVUM":
                        "VAULT ARCHÍVUM ELLENŐRZÉSE: KORLÁTOZÁS / ELTÉRÉS")+
                    "\nFájlok SHA-256: "+result.VerifiedFiles+
                    "\nMapok: "+result.Maps+" · Kapcsolódó hostok: "+result.Peers+
                    "\nConfig hash: "+(result.ConfigHashesVerified?"EGYEZIK":"ELTÉR")+
                    "\nMentett helyi mapállapot: "+(result.LocalHostMapRecordsActive?"ACTIVE":"ELTÉR")+
                    "\nMapok: "+string.Join(", ",result.MapNames)+
                    "\n"+issues+
                    "\nNEM ÉLŐ ÁLLAPOT. Nem igazolja a másik gép helyi adatait, "+
                    "a karaktertranszfert vagy a működő Store-átvételt.";
            }
            catch(Exception ex)
            {
                status.Text="Cluster Vault archívum visszautasítva: "+ex.Message;
                WriteLog("untamed-vault-archive-inspect",ex);
            }
            finally{if(inspect!=null)inspect.IsEnabled=true;}
        });
        Add(Box(V(
            Label("UNTAMED KINGDOM · CLUSTER VAULT",20,Mint,true),
            Label("Saját központi karakter- és clusteradatok ellenőrzője. "+
                "A meglévő kompatibilis Store-v1 ZIP-eket változatlanul fogadja. "+
                "A tesztgép csak olvas, az éles szerverekhez és az SQL-hez nem kapcsolódik.",12,Soft),
            Label("Korábban exportált Vault-metadata ZIP",12,Aqua),zip,
            Btn("Vault ZIP kiválasztása",()=>{
                using var picker=new Forms.OpenFileDialog{
                    Title="Untamed Cluster Vault – helyi archívum kiválasztása",
                    Filter="ZIP-archívum (*.zip)|*.zip",CheckFileExists=true};
                if(picker.ShowDialog()==Forms.DialogResult.OK)zip.Text=picker.FileName;
            }),
            inspect,status)));
    }
}
