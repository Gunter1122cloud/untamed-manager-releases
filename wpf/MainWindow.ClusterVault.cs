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
    private static string FormatVaultReport(ClusterVaultArchiveReport result)
    {
        string Flag(bool value)=>value?"IGAZOLT A MÁSOLATBAN":"HIÁNYZIK / ELTÉR";
        return (result.HistoricalConsistent?"TELJES TÖRTÉNETI METADATAEGYEZÉS":
                "RÉSZLEGES VAGY ELLENTMONDÁSOS TÖRTÉNETI MINTA")+
            "\nSHA-256 ellenőrzött fájlok: "+result.VerifiedFiles+
            "\nMapok: "+result.Maps+" · Peerek: "+result.Peers+
            "\nKét host helyi bizonyítéka: "+Flag(result.CompleteTwoHostEvidence)+
            "\nMunkamenet és koordináció: "+Flag(result.SessionConsistent)+
            "\nKonfigurációlenyomatok: "+Flag(result.ConfigHashesVerified)+
            "\n"+string.Join("\n",result.Hosts.Select(h=>
                "Host "+h.Number+": "+h.Maps+" map · helyi metadata: "+Flag(h.LocalEvidence)+
                " · session: "+Flag(h.SessionMatches)+" · hash: "+Flag(h.ConfigHashMatches)+
                " · lock: "+Flag(h.LockMatches)+" · ACTIVE: "+Flag(h.ActiveMaps)))+
            "\n"+string.Join("\n",result.Findings.Select(f=>f.Code+": "+f.Message))+
            "\nÉLES HASZNÁLAT / ÍRÓJOG: TILTVA. Nem élő állapot, nem karaktertranszfer-bizonyíték.";
    }
    private void ShowStoreAliasProof()
    {
        var first=new TextBox{Height=34,Background=Panel,Foreground=Brushes.White};
        var second=new TextBox{Height=34,Background=Panel,Foreground=Brushes.White};
        var status=Label("Két helyi/UNC Store-mappa azonosítására használható; semmit nem ír vagy konvertál.",12,Soft);
        Button? check=null;
        check=Btn("UNC mappaazonosság vizsgálata (csak olvasás)",async()=>{
            if(check!=null)check.IsEnabled=false;
            try
            {
                var decision=await Task.Run(()=>
                    StorePathIdentity.CompareDirectories(first.Text.Trim(),second.Text.Trim()));
                status.Text=decision.Reason+
                    (decision.AliasEvidence?
                        "\\nWindows fájlrendszer-azonosság igazolt. A Store session/hash és writer ownership még külön bizonyítandó.":
                        "\\nKétgépes Store-írás ettől nem engedélyezhető.");
            }
            catch(Exception ex)
            {
                WriteLog("store-path-identity",ex);
                status.Text="Az UNC-azonosság nem igazolható. Store írás tiltva.";
            }
            finally{if(check!=null)check.IsEnabled=true;}
        });
        Add(Box(V(Label("HP / Ryzen · Store UNC aliasazonosság",19,Mint,true),
            Label("A történeti Store-v1 azonosítókat, hash-eket és sessionadatokat nem módosítja.",12,Soft),
            Label("1. ellenőrizendő Store-mappa",12,Aqua),first,
            Label("2. ellenőrizendő Store-mappa",12,Aqua),second,
            check,status)));
    }

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
                status.Text=FormatVaultReport(result);
            }
            catch(Exception)
            {
                status.Text="VAULT_INPUT: az archívum hiányos, hibás, változó vagy nem biztonságos. Éles használat tiltva.";
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

