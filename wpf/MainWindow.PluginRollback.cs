using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms = System.Windows.Forms;
using WinMessageBox = System.Windows.MessageBox;

namespace UntamedManager;
public partial class MainWindow
{
    private void ShowPluginRollbackControls()
    {
        var mapBox=new ComboBox{
            ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",SelectedIndex=0,
            Foreground=Brushes.White,Background=Panel};
        var pluginName=new TextBox{Height=34,Foreground=Brushes.White,Background=Panel,
            ToolTip="Exact installed plugin directory name (Untamed Cluster Vault plugins forbidden)"};
        var previous=new TextBox{Height=34,IsReadOnly=true,
            Foreground=Brushes.White,Background=Panel};
        var status=Label("Válassz ellenőrzött helyi DLL-mentést. Minden Store/SQL művelet tiltott.",12,Soft);
        LocalMap Selected()
        {
            if(mapBox.SelectedItem is not LocalMap map ||
               !_cluster.Config.LocalMaps.Contains(map))
                throw new UnauthorizedAccessException("Only locally owned map may roll back plugins");
            return map;
        }
        string BackupRoot()=>Path.Combine(_cluster.Store.Root,"plugin-backups");
        PluginRollbackPreview Validate()
        {
            var map=Selected();
            return SafePluginZip.PreviewRollback(map,_cluster.Check(map),
                pluginName.Text.Trim(),previous.Text.Trim(),BackupRoot());
        }
        Add(Box(V(
            Label("Plugin DLL-rollback és frissítési előzmény",20,Mint,true),
            Label("Csak korábbi, SHA-256-tal igazolt plugin DLL/PDB fájlok állíthatók vissza. "+
                "A jelenlegi config, licenc, adatbázis és mentés változatlan marad. "+
                "Az összes jelenlegi pluginfájlról külön, teljes védőmásolat készül.",12,Soft),
            mapBox,
            Label("Plugin könyvtár pontos neve",12,Aqua),pluginName,
            Label("Korábbi ellenőrzött plugin-mentés mappája",12,Aqua),previous,
            Btn("Ellenőrzött backup-mappa kiválasztása",()=>{
                using var picker=new Forms.FolderBrowserDialog{
                    Description="Csak a Manager korábbi plugin-backups mentései elfogadottak"};
                if(picker.ShowDialog()==Forms.DialogResult.OK)previous.Text=picker.SelectedPath;
            }),
            Btn("Rollback előnézet – írás nélkül",()=>{
                try
                {
                    var v=Validate();
                    status.Text="Plugin: "+v.PluginName+" · előző binárisok: "+
                        v.PreviousBinaryCount+" · "+
                        (v.Allowed?"Engedélyezhető":"TILTVA")+"\n"+v.Reason;
                }
                catch(Exception ex){status.Text="Előnézet elutasítva: "+ex.Message;}
            }),
            Btn("Ellenőrzött DLL-rollback végrehajtása (CSAK leállított map)",()=>{
                try
                {
                    var preview=Validate();
                    if(!preview.Allowed)
                        throw new InvalidOperationException(preview.Reason);
                    var map=Selected();
                    if(WinMessageBox.Show(map.Name+" / "+preview.PluginName+
                        " – "+preview.PreviousBinaryCount+
                        " korábbi DLL/PDB visszaállítása, előtte teljes védőmásolat.\n"+
                        "A mapnak igazoltan leállítottnak kell lennie. Folytatod?",
                        "Plugin rollback – helyi map",MessageBoxButton.YesNo,
                        MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                    var result=SafePluginZip.Rollback(map,_cluster.Check(map),
                        preview.PluginName,preview.BackupFolder,BackupRoot());
                    status.Text="HELYI DLL-ROLLBACK kész: "+result.PluginName+
                        "\nVisszaállított bináris fájlok: "+result.RestoredBinaryCount+
                        "\nFriss teljes védőmásolat: "+result.PreRollbackBackup;
                }
                catch(Exception ex){status.Text="Rollback elutasítva: "+ex.Message;
                    WriteLog("plugin-rollback",ex);}
            }),status)));
    }
}

