using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowAsaInstallationPreflightControls()
    {
        var entries=new ListBox{Height=225,Background=Panel,Foreground=Brushes.White,BorderBrush=Aqua};
        var status=Label("A helyi ASA-telepítések csoportosítása csak olvasás; frissítés tiltva.",12,Soft);
        Add(Box(V(
            Label("ASA telepítések és Steam buildazonosítók",20,Mint,true),
            Label("Közös telepítési mappák felismerése, meglévő appmanifest vizsgálata, "+
                "folyamatállapot ellenőrzése. Ez nem indít SteamCMD-t.",12,Soft),
            Btn("Helyi telepítések ellenőrzése (csak olvasás)",()=>{
                try
                {
                    var installations=AsaInstallationInventory.Inspect(
                        _cluster.Config.LocalMaps,map=>_cluster.Check(map));
                    entries.ItemsSource=installations.Select(i=>
                        $"{string.Join(", ",i.LocalMaps)}\n{i.InstallationRoot}\n"+
                        $"ASA EXE: {(i.ServerExecutablePresent?"van":"hiányzik")}; "+
                        $"Steam build: {i.InstalledSteamBuild??"ismeretlen"}\n"+
                        $"Frissítés TILTVA: {i.Reason}").ToArray();
                    status.Text=$"{installations.Count} különálló helyi ASA telepítés, "+
                        $"{_cluster.Config.LocalMaps.Count} helyi map. Éles frissítés és újraindítás letiltva.";
                }
                catch(Exception e)
                {
                    status.Text="Telepítési előellenőrzés elutasítva: "+e.Message;
                    WriteLog("asa-install-preflight",e);
                }
            }),entries,status)));
    }
}
