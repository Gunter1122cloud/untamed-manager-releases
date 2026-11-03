using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;
public partial class MainWindow
{
    private void ShowModStateEditor()
    {
        var mapBox=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,
            DisplayMemberPath="Name",SelectedIndex=0,Background=Panel,Foreground=Brushes.White};
        var modId=new TextBox{Height=34,Text="928548",Background=Panel,Foreground=Brushes.White};
        var mode=new ComboBox{ItemsSource=new[]{"ACTIVE","PASSIVE","DISABLED","REMOVE"},
            SelectedIndex=0,Background=Panel,Foreground=Brushes.White};
        var order=new TextBox{Text="",Height=30,Background=Panel,Foreground=Brushes.White};
        var list=new ListBox{Height=180,Background=Panel,Foreground=Brushes.White};
        var status=Label("A módosítás kizárólag a helyi indítási konfigurációt érinti; nincs restart.",12,Soft);
        LocalMap Selected()
        {
            if(mapBox.SelectedItem is not LocalMap map ||
               !_cluster.Config.LocalMaps.Contains(map))
                throw new UnauthorizedAccessException("Csak regisztrált helyi map szerkeszthető");
            return map;
        }
        ModEdit EditFor(LocalMap map)
        {
            ModMode? selected=(string?)mode.SelectedItem switch{
                "ACTIVE"=>ModMode.Active,"PASSIVE"=>ModMode.Passive,
                "DISABLED"=>ModMode.Disabled,"REMOVE"=>null,
                _=>throw new InvalidDataException("Ismeretlen modállapot")
            };
            int? position=null;
            if(order.Text.Trim().Length>0)
            {
                if(!int.TryParse(order.Text,out var index)||index<1)
                    throw new InvalidDataException("A sorrend 1-től indul");
                position=index-1;
            }
            return new(map.Id,modId.Text.Trim(),selected,position);
        }
        void Refresh()
        {
            try
            {
                var map=Selected();
                var state=ModStateController.Read(map);
                list.ItemsSource=state.Active.Select((id,i)=>"ACTIVE   "+(i+1)+".  "+id)
                    .Concat(state.Passive.Select((id,i)=>"PASSIVE  "+(i+1)+".  "+id))
                    .Concat(state.Disabled.Select((id,i)=>"DISABLED "+(i+1)+".  "+id))
                    .ToArray();
                status.Text=map.Name+" · ACTIVE: "+state.Active.Count+
                    " · PASSIVE: "+state.Passive.Count+
                    " · DISABLED: "+state.Disabled.Count+
                    "\nÚjraindítás után érvényesítendő: "+
                    (state.Pending?"IGEN – jelenleg csak konfiguráció":"nincs jelzett változás");
            }
            catch(Exception ex){status.Text="Modállapot nem olvasható: "+ex.Message;}
        }
        mapBox.SelectionChanged+=(_,_)=>Refresh();
        Add(Box(V(Label("v1.5 · ACTIVE / PASSIVE / DISABLED Mod Editor",20,Mint,true),
            Label("Az ASA -mods= és -passivemods= listái külön kezelve. DISABLED csak a "+
                "Manager helyi profiljában szerepel. Ismeretlen indítási kapcsolók változatlanok.",12,Soft),
            mapBox,modId,mode,Label("Opcionális pozíció: 1, 2, 3... (üres = lista vége)",12,Aqua),
            order,
            Btn("Modállapot előnézete – írás nélkül",()=>{
                try
                {
                    var map=Selected();var preview=ModStateController.Preview(map,EditFor(map));
                    status.Text="ELŐNÉZET · "+map.Name+
                        "\nEredeti argumentumok: "+preview.PreviousArguments+
                        "\nÚj argumentumok: "+preview.NewArguments+
                        "\nDISABLED: "+string.Join(",",preview.Updated.Disabled)+
                        "\nVáltozás: "+(preview.Changed?"IGEN":"NINCS")+
                        "\nSzerverindítás és RCON: NEM TÖRTÉNT.";
                }
                catch(Exception ex){status.Text="Előnézet elutasítva: "+ex.Message;}
            }),
            Btn("Kiválasztott helyi map modlistájának mentése",()=>{
                try
                {
                    var map=Selected();var preview=ModStateController.Preview(map,EditFor(map));
                    if(System.Windows.MessageBox.Show(
                        map.Name+" indítási modparaméterei módosulnak; szerver nem indul újra.\n"+
                        preview.NewArguments,
                        "Modlista mentése",System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question)!=
                        System.Windows.MessageBoxResult.Yes)return;
                    ModStateController.ApplyLocalBatch(_cluster,new[]{EditFor(map)});
                    Refresh();
                }
                catch(Exception ex){status.Text="Mentés elutasítva: "+ex.Message;}
            }),
            Btn("Kijelölt ID beállítása az összes helyi mapon",()=>{
                try
                {
                    var maps=_cluster.Config.LocalMaps.ToArray();
                    if(maps.Length==0)throw new InvalidOperationException("Nincs helyi map");
                    var edits=maps.Select(EditFor).ToArray();
                    foreach(var edit in edits)
                    {
                        var map=maps.Single(m=>m.Id==edit.Map);
                        ModStateController.Preview(map,edit);
                    }
                    if(System.Windows.MessageBox.Show(maps.Length+
                        " helyi map modlistáját módosítod. Másik hostot ez nem érint.\n"+
                        "Automatikus restart NINCS.","Globális modmódosítás",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning)!=
                        System.Windows.MessageBoxResult.Yes)return;
                    ModStateController.ApplyLocalBatch(_cluster,edits);
                    Refresh();
                }
                catch(Exception ex){status.Text="Globális művelet elutasítva: "+ex.Message;}
            }),
            Btn("Aktuális modlista újraolvasása",Refresh),list,status)));
        Refresh();
    }
}
