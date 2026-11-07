using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms = System.Windows.Forms;
using WinMessageBox = System.Windows.MessageBox;

namespace UntamedManager;
public partial class MainWindow
{
    private UIElement MapBackupPane(LocalMap map)
    {
        var world=new CheckBox{Content="Világ, karakterek, tribe, SavedArks/SaveGames",IsChecked=true,Foreground=Brushes.White};
        var ini=new CheckBox{Content="Game.ini / GameUserSettings.ini",IsChecked=true,Foreground=Brushes.White};
        var plugins=new CheckBox{Content="Helyi pluginfájlok, konfigurációk és tartós adatok",IsChecked=false,Foreground=Brushes.White};
        string defaultRoot=Path.Combine(_cluster.Store.Root,"local-backups",map.Id.ToString("N"));
        var folder=new TextBox{Text=defaultRoot,Height=35,Foreground=Brushes.White,Background=Panel};
        var status=Label("Nincs elkészült mentés. Éles ASA/Store-v1 restore és Cluster Vault-írás letiltva.",12,Soft);
        int counted=0;
        Button? create=null;
        create=Btn("Ellenőrzött fájlmentés létrehozása",async ()=>
        {
            try
            {
                if(!_cluster.Config.LocalMaps.Contains(map))throw new UnauthorizedAccessException("Not an owned local map");
                var state=_cluster.Check(map);
                if(!state.ConfirmedStopped)throw new InvalidOperationException("A map leállított állapota nem igazolt");
                LocalBackupScope chosen=LocalBackupScope.None;
                if(world.IsChecked==true)chosen|=LocalBackupScope.WorldAndProfiles;
                if(ini.IsChecked==true)chosen|=LocalBackupScope.Ini;
                if(plugins.IsChecked==true)chosen|=LocalBackupScope.Plugins;
                if(chosen==LocalBackupScope.None)throw new InvalidOperationException("Legalább egy adatkört válassz ki");
                string output=Path.Combine(folder.Text.Trim(),
                    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);
                if(WinMessageBox.Show("Ez LEÁLLÍTOTT mapról készülő, SHA-256-tal ellenőrzött fájlmásolat.\n"+
                    "Nem minősül bizonyítottan konzisztens ASA/Store mentésnek.\n\n"+output+
                    "\n\nFolytatod?","Helyi mentés",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                status.Text="Mentés folyamatban...";counted=0;if(create!=null)create.IsEnabled=false;
                var result=await Task.Run(()=>LocalBackupWorkflow.Create(map,state,output,chosen,_=>{
                    int n=Interlocked.Increment(ref counted);
                    if(n%25==0)Dispatcher.BeginInvoke(()=>status.Text=$"Mentés folyamatban: {n} fájl...");
                }));
                status.Text=$"ELLENŐRZÖTT FÁJLMÁSOLAT: {result.Manifest.Files.Count} fájl.\n"+
                    result.PackageDirectory+"\nASA konzisztencia nem bizonyított; restore tiltva.";
            }
            catch(Exception ex){status.Text="Mentés sikertelen: "+ex.Message;WriteLog("backup",ex);}
            finally{if(create!=null)create.IsEnabled=true;}
        });
        var verify=Btn("Meglévő mentés SHA-256 ellenőrzése",()=>
        {
            try
            {
                using var pick=new Forms.FolderBrowserDialog{Description="Válassz egy korábban létrehozott Untamed mentéscsomagot"};
                if(pick.ShowDialog()!=Forms.DialogResult.OK)return;
                var manifest=TestBackupPackage.Verify(pick.SelectedPath,map.Id);
                status.Text=$"Fájlszintű épség ellenőrizve: {manifest.Files.Count} fájl · {manifest.CreatedUtc:O}\n"+
                    manifest.Scope+"\nA visszaállítás továbbra is letiltva.";
            }
            catch(Exception ex){status.Text="Mentés nem ellenőrizhető: "+ex.Message;WriteLog("backup-verify",ex);}
        });
        var history=Btn("Mentési előzmények és SHA-256 állapot ellenőrzése",async ()=>{
            try
            {
                if(!_cluster.Config.LocalMaps.Contains(map))
                    throw new UnauthorizedAccessException("Not a local map");
                string root=folder.Text.Trim();
                status.Text="Mentési előzmények fájlonkénti ellenőrzése folyamatban...";
                var packages=await Task.Run(()=>LocalBackupCatalog.Inspect(root,map.Id,15));
                status.Text=packages.Count==0?"Nem található mentés ezen a helyi útvonalon.":
                    string.Join("\n",packages.Select(p=>
                        $"{(p.Valid?"ÉP":"HIBÁS")} · {Path.GetFileName(p.PackageDirectory)} · "+
                        $"{p.FileCount} fájl · {p.Status}"))+
                    "\nA helyreállítás továbbra is tiltva.";
            }
            catch(Exception ex)
            {
                status.Text="Mentésleltár nem ellenőrizhető: "+ex.Message;
                WriteLog("backup-history",ex);
            }
        });
        return V(
            Label("Csak helyileg regisztrált, igazoltan leállított mapról készíthető offline fájlmásolat.",12,Aqua),
            Label("A külső adatbázis, a közös Cluster Vault és a kétgépes transzfer nem része ennek a helyi mapmentésnek.",12,Soft),
            world,ini,plugins,
            Label("Helyi célkönyvtár (nem lehet a map gyökerén belül)",12,Aqua),folder,
            Btn("Célmappa kiválasztása",()=>{
                using var pick=new Forms.FolderBrowserDialog{Description="Válassz a map gyökerétől különálló helyi mentési mappát"};
                if(pick.ShowDialog()==Forms.DialogResult.OK)folder.Text=pick.SelectedPath;
            }),
            create,verify,history,status,BackupRetentionPane(map));
    }
}
