using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using WinMessageBox=System.Windows.MessageBox;

namespace UntamedManager;
public partial class MainWindow
{
    private void ShowSafeTextEditor(ConfigKind kind)
    {
        var maps=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",
            SelectedIndex=0,Foreground=Brushes.White,Background=Panel};
        var path=Label("Nincs megnyitott, ellenőrzött helyi konfiguráció.",12,Soft);
        var editor=new TextBox{Height=300,AcceptsReturn=true,AcceptsTab=true,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            FontFamily=new FontFamily("Consolas"),FontSize=12,
            Foreground=Brushes.White,Background=Panel,IsEnabled=false};
        var status=Label("Mentés előtt kötelező hash/állapotellenőrzés és .bak védőmásolat.",12,Soft);
        var section=new TextBox{Height=32,Text="ServerSettings",Background=Panel,Foreground=Brushes.White};
        var key=new TextBox{Height=32,Text="XPMultiplier",Background=Panel,Foreground=Brushes.White};
        var value=new TextBox{Height=32,Text="2.0",Background=Panel,Foreground=Brushes.White};
        var occurrence=new TextBox{Height=32,Text="0",Background=Panel,Foreground=Brushes.White};
        ConfigSnapshot? snapshot=null;
        LocalMap CurrentMap()
        {
            if(maps.SelectedItem is not LocalMap m || !_cluster.Config.LocalMaps.Contains(m))
                throw new UnauthorizedAccessException("Csak regisztrált helyi map szerkeszthető");
            return m;
        }
        maps.SelectionChanged+=(_,_)=>{snapshot=null;editor.Text="";editor.IsEnabled=false;
            path.Text="Map megváltozott; fájlt újra kell nyitni.";};
        Add(Box(V(Label(kind==ConfigKind.GameIni?"INI Editor 2.0":"Plugin JSON Editor",
                20,Mint,true),
            Label("Kódolás/BOM, sorvégek, kommentek és ismeretlen mezők megőrzése. "+
                "Kizárólag igazoltan leállított map fájljait módosítja; nem olvas Untamed Cluster Vault-t.",12,Soft),
            maps,
            Btn(kind==ConfigKind.GameIni?"Helyi Game.ini / GameUserSettings.ini megnyitása":
                "Helyi plugin JSON megnyitása",()=>{
                try
                {
                    var map=CurrentMap();
                    var picker=new Microsoft.Win32.OpenFileDialog{CheckFileExists=true,
                        Filter=kind==ConfigKind.GameIni?
                        "ASA INI|Game.ini;GameUserSettings.ini":"Plugin JSON|*.json"};
                    if(picker.ShowDialog(this)!=true)return;
                    snapshot=SafeConfigEditor.Read(picker.FileName,map.InstallationPath,
                        kind,_cluster.Check(map),map.Maintenance);
                    editor.Text=snapshot.Text;editor.IsEnabled=true;
                    path.Text=snapshot.Path;
                    status.Text="SHA-256 "+snapshot.Sha256+"\nKódolás: "+snapshot.EncodingId+
                        " · BOM: "+snapshot.HasBom+" · fájlméret: "+snapshot.Bytes;
                }
                catch(Exception ex){snapshot=null;editor.IsEnabled=false;
                    status.Text="Fájl megnyitása megtagadva: "+ex.Message;}
            }),path,editor,
            Btn("Változások előnézete és szintaxisellenőrzés",()=>{
                try
                {
                    if(snapshot==null)throw new InvalidOperationException("Nyiss meg egy fájlt");
                    var preview=SafeConfigEditor.Preview(snapshot,editor.Text);
                    status.Text="Előnézet: "+(preview.Changed?"módosult":"változatlan")+
                        "\nSorok: "+preview.OldLines+" → "+preview.NewLines+
                        "\nEredeti hash: "+preview.OldSha256+
                        "\nÚj hash: "+preview.NewSha256+
                        "\n"+preview.Description;
                }
                catch(Exception ex){status.Text="Hibás konfiguráció: "+ex.Message;}
            }),
            Btn("Ellenőrzött mentés (.bak + ETag + atomikus csere)",()=>{
                try
                {
                    if(snapshot==null)throw new InvalidOperationException("Nincs megnyitott fájl");
                    var map=CurrentMap();
                    SafeConfigEditor.Preview(snapshot,editor.Text);
                    if(WinMessageBox.Show("A helyi "+Path.GetFileName(snapshot.Path)+
                        " fájlt írod át a(z) "+map.Name+
                        " leállított mapon. Mentés előtt védőmásolat készül. Folytatod?",
                        "Konfiguráció mentése",MessageBoxButton.YesNo,
                        MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                    string backup=SafeConfigEditor.Save(snapshot,editor.Text,
                        map.InstallationPath,_cluster.Check(map),map.Maintenance);
                    snapshot=SafeConfigEditor.Read(snapshot.Path,map.InstallationPath,
                        kind,_cluster.Check(map),map.Maintenance);
                    status.Text=backup.Length==0?"Nem volt változás.":
                        "Biztonságos mentés elkészült. Védőmásolat: "+backup+
                        "\nÚj hash: "+snapshot.Sha256+
                        "\nHa restart szükséges, azt külön kell jóváhagyni.";
                }
                catch(Exception ex){status.Text="Mentés megtagadva: "+ex.Message;}
            }),status)));
        if(kind==ConfigKind.GameIni)
        {
            Add(Box(V(Label("Strukturált INI-mező szerkesztése",18,Mint,true),
                Label("Szekció, kulcs, érték és előfordulás (0 = első). "+
                    "Duplikált kulcsokat és a többi sort változatlanul hagyja.",12,Soft),
                section,key,value,occurrence,
                Btn("Kiválasztott kulcs módosítása a szerkesztőben",()=>{
                    try
                    {
                        if(snapshot==null)throw new InvalidOperationException("Nincs INI fájl");
                        if(!int.TryParse(occurrence.Text,out int occurrenceIndex))
                            throw new InvalidDataException("Érvénytelen előfordulás");
                        editor.Text=SafeConfigEditor.SetIniValue(
                            snapshot with{Text=editor.Text},section.Text,key.Text,value.Text,
                            occurrenceIndex);
                        status.Text="Mező módosítva az előnézetben; még nincs fájlba mentve.";
                    }
                    catch(Exception ex){status.Text="Mező szerkesztése elutasítva: "+ex.Message;}
                }))));
        }
    }
}

