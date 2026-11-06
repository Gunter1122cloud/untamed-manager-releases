using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowLocalPluginInventoryControls()
    {
        var maps=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,
            DisplayMemberPath="Name",SelectedItem=_cluster.Config.LocalMaps.FirstOrDefault(m=>m.Id==_preferredPluginMapId)??_cluster.Config.LocalMaps.FirstOrDefault(),
            Background=Panel,Foreground=Brushes.White};
        var entries=new ListBox{Height=230,Background=Panel,Foreground=Brushes.White,
            BorderBrush=Aqua};
        var status=Label("Ez a nézet csak helyi DLL-eket vizsgál; nem olvas adatbázist vagy Untamed Cluster Vault-t.",12,Soft);
        Add(Box(V(
            Label("Helyi ASA API pluginleltár",20,Mint,true),
            Label("DLL jelenlét, fájlverzió és SHA-256; a betöltött plugin állapota ebből nem állapítható meg.",12,Soft),
            maps,
            Btn("Helyi pluginleltár frissítése (csak olvasás)",()=>{
                try
                {
                    if(maps.SelectedItem is not LocalMap map || !_cluster.Config.LocalMaps.Contains(map))
                        throw new UnauthorizedAccessException("Válassz egy regisztrált helyi mapot");
                    var plugins=LocalPluginInventory.Scan(map);
                    entries.ItemsSource=plugins.Select(p=>p.Protected
                        ? $"{p.PluginName} – védett, részletes vizsgálat kihagyva"
                        : $"{p.PluginName} – {p.Binaries.Count} gyökér-DLL"+
                          string.Concat(p.Binaries.Select(d=>
                              $"\n  {d.Name} · {d.Version} · SHA-256 {d.Sha256}"))).ToArray();
                    status.Text=$"{plugins.Count} helyi pluginmappa; futás közbeni betöltés nincs igazolva.";
                }
                catch(Exception e){status.Text="Leltározás sikertelen: "+e.Message;WriteLog("plugin-inventory",e);}
            }),
            entries,status)));
    }
}

