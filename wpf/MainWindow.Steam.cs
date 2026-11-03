using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms = System.Windows.Forms;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowSteamCmdPreflightControls()
    {
        var path=new TextBox { Height=38,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua,IsReadOnly=true };
        var status=Label("Nincs ellenőrzött SteamCMD. Ellenőrzés csak olvasással; telepítés és frissítés tiltva.",12,Soft);
        Add(Box(V(
            Label("SteamCMD előellenőrzés (offline)",20,Mint,true),
            Label("Helyi steamcmd.exe: elérhetőség, fájlméret, utolsó módosítás és SHA-256. "+
                  "A programot nem futtatjuk és ASA-szervert nem frissítünk.",12,Soft),
            path,
            Btn("steamcmd.exe kiválasztása",()=>{
                using var picker=new Forms.OpenFileDialog {
                    Title="SteamCMD helyi végrehajtható fájlja",
                    Filter="SteamCMD (steamcmd.exe)|steamcmd.exe"
                };
                if(picker.ShowDialog()==Forms.DialogResult.OK)path.Text=picker.FileName;
            }),
            Btn("SteamCMD fájl ellenőrzése (csak olvasás)",()=>{
                try
                {
                    var result=SteamCmdPreflight.Inspect(path.Text.Trim());
                    status.Text=$"HELYI FÁJL ELLENŐRIZVE: {result.ExecutablePath}\n"+
                        $"Méret: {result.LengthBytes} bájt; módosítva (UTC): {result.LastModifiedUtc:O}\n"+
                        $"SHA-256: {result.Sha256}\n"+
                        "Az ASA telepítési verziója és a SteamCMD működése nincs ellenőrizve.";
                }
                catch(Exception e)
                {
                    status.Text="SteamCMD előellenőrzés elutasítva: "+e.Message;
                    WriteLog("steamcmd-preflight",e);
                }
            }),status)));
    }
}
