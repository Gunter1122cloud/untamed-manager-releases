using System;
using System.Collections.Generic;
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
    private void ShowSafeZipPluginsControls()
    {
        Add(Box(V(Label("Biztonságos ZIP-pluginfrissítés",20,Mint,true),
            Label("A csomag kizárólag DLL/PDB fájlokat frissít. Meglévő JSON, adatbázis, licenc és egyéb állomány változatlan marad.",12,Soft),
            Label("Minden korábbi pluginfájl a telepítési mappán KÍVÜL kap SHA-256-tal ellenőrzött védőmásolatot.",12,Soft))));
        var maps=new ListBox{ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",
            SelectionMode=SelectionMode.Multiple,Height=140,Foreground=Brushes.White,
            Background=Panel,BorderBrush=Aqua};
        var path=new TextBox{Height=35,IsReadOnly=true,Foreground=Brushes.White,Background=Panel};
        var result=Label("ZIP nincs kiválasztva.",12,Soft);
        Add(Box(V(Label("Kizárólag helyi, igazoltan leállított mapok",13,Aqua),maps,
            Label("Plugin ZIP fájl (PluginName/PluginName.dll struktúrában)",13,Aqua),path,
            Btn("ZIP fájl kiválasztása",()=>{
                using var picker=new Forms.OpenFileDialog{Title="Biztonságosan ellenőrzendő plugin ZIP",Filter="ZIP csomag (*.zip)|*.zip"};
                if(picker.ShowDialog()==Forms.DialogResult.OK)path.Text=picker.FileName;
            }),
            Btn("ZIP előellenőrzése (írás nélkül)",()=>{
                try
                {
                    var info=SafePluginZip.Inspect(path.Text.Trim());
                    result.Text=$"{info.PluginName}: {info.BinaryCount} DLL, "+
                        $"{info.SkippedNonBinaryCount} egyéb fájl nem kerül felülírásra.";
                }
                catch(Exception ex){result.Text="ZIP elutasítva: "+ex.Message;}
            }),
            Btn("DLL-ek frissítése a kijelölt leállított mapokon",()=>{
                var selected=maps.SelectedItems.OfType<LocalMap>().ToArray();
                if(selected.Length==0){result.Text="Válassz legalább egy helyi mapot.";return;}
                try
                {
                    var info=SafePluginZip.Inspect(path.Text.Trim());
                    if(WinMessageBox.Show($"A(z) {info.PluginName} {info.BinaryCount} DLL-fájlját frissítjük "+
                        $"{selected.Length} helyi mapon. A meglévő configok és licencek megmaradnak. "+
                        "A mapoknak bizonyítottan leállítottnak kell lenniük.\n\nFolytatod?",
                        "Biztonságos pluginfrissítés",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                    var successes=new List<string>();
                    var errors=new List<string>();
                    string protectedRoot=Path.Combine(_cluster.Store.Root,"plugin-backups");
                    foreach(var map in selected)
                    {
                        try
                        {
                            if(!_cluster.Config.LocalMaps.Contains(map))throw new UnauthorizedAccessException("Not a local map");
                            var check=_cluster.Check(map);
                            var installed=SafePluginZip.Install(map,check,path.Text.Trim(),protectedRoot);
                            successes.Add(map.Name+" → "+installed.Target+
                                (installed.PreviousBackup==null?" (új plugin)":" (védőmentés: "+installed.PreviousBackup+")"));
                        }
                        catch(Exception ex){errors.Add(map.Name+": "+ex.Message);WriteLog("plugin-zip",ex);}
                    }
                    result.Text=$"Sikeres: {successes.Count} / Sikertelen: {errors.Count}\n"+
                        string.Join("\n",successes.Concat(errors));
                }
                catch(Exception ex){result.Text="Művelet elutasítva: "+ex.Message;WriteLog("plugin-zip",ex);}
            }),result)));
    }
}
