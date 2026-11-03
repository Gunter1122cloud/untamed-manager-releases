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
    private void ShowBulkModsControls()
    {
        Add(Box(V(Label("Több helyi map modbeállításai",20,Mint,true),
            Label("A módosítás az indítási -mods= listába kerül, és csak a következő szerverindításnál lép életbe.",12,Soft))));
        var selected=new ListBox {
            ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",SelectionMode=SelectionMode.Multiple,
            Height=150,Foreground=Brushes.White,Background=Panel,BorderBrush=Aqua
        };
        var ids=new TextBox{Height=38,Text="912815,928548"};
        var info=Label("Válaszd ki a helyi mapokat, majd add meg a CurseForge azonosítókat.",12,Soft);
        Add(Box(V(Label("Helyi mapok (Ctrl/Shift: több kijelölés)",13,Aqua,true),selected,
            Label("CurseForge mod ID-k, vesszővel elválasztva",13,Aqua,true),ids,
            Btn("Modok hozzáadása a kijelölt helyi mapokhoz",()=>Apply(true)),
            Btn("Modok eltávolítása a kijelölt helyi mapokból",()=>Apply(false)),info)));
        ShowModOrderingControls();
        void Apply(bool add)
        {
            var maps=selected.SelectedItems.OfType<LocalMap>().ToList();
            if(maps.Count==0){info.Text="Nem jelöltél ki helyi mapot.";return;}
            string[] requested=ids.Text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            var originals=maps.Select(x=>(map:x,args:x.Arguments)).ToArray();
            try {
                var proposed=maps.Select(m=>(map:m,arguments:LocalContentOperations.ModifyMods(m.Arguments,requested,add))).ToArray();
                var message=string.Join("\n",proposed.Select(x=>$"{x.map.Name}: {x.arguments}"));
                if(WinMessageBox.Show($"A módosítás a következő szerverindításkor érvényesül:\n{message}\n\nMented?",
                   "Modbeállítások",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                foreach(var change in proposed)change.map.Arguments=change.arguments;
                _cluster.Save();
                info.Text=$"{proposed.Length} helyi map indítási modlistája mentve. Futó szervert nem indítottunk újra.";
            }catch(Exception e){
                foreach(var old in originals)old.map.Arguments=old.args;
                info.Text="Művelet megszakítva: "+e.Message;
                WriteLog("bulk-mods",e);
            }
        }
    }
    private void ShowModOrderingControls()
    {
        var maps=new ListBox {
            ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",
            SelectionMode=SelectionMode.Single,Height=125,Foreground=Brushes.White,
            Background=Panel,BorderBrush=Aqua
        };
        var order=new TextBox { Height=38,Background=Panel,Foreground=Brushes.White,
            BorderBrush=Aqua,ToolTip="Pontosan a meglévő mod ID-k, új sorrendben" };
        var status=Label("Csak a modok sorrendje változhat; hozzáadás és eltávolítás itt tiltott.",12,Soft);
        Add(Box(V(
            Label("Modok betöltési sorrendje – helyi map",20,Mint,true),
            Label("Az indítási -mods= lista olvasása és biztonságos átrendezése; futó szervert nem módosítunk.",12,Soft),
            maps,
            Btn("Jelenlegi sorrend beolvasása",()=>{
                try
                {
                    if(maps.SelectedItem is not LocalMap map || !_cluster.Config.LocalMaps.Contains(map))
                        throw new InvalidOperationException("Válassz egy helyi mapot");
                    order.Text=string.Join(",",LocalContentOperations.ReadMods(map.Arguments));
                    status.Text=map.Name+": a jelenlegi mod ID-k sorrendje betöltve.";
                }
                catch(Exception e){status.Text="Beolvasás sikertelen: "+e.Message;WriteLog("mod-order-read",e);}
            }),
            Label("Mod ID-k kívánt sorrendben, vesszővel elválasztva",13,Aqua),
            order,
            Btn("Sorrend előnézete és mentése",()=>{
                if(maps.SelectedItem is not LocalMap map || !_cluster.Config.LocalMaps.Contains(map))
                { status.Text="Válassz egy helyi mapot.";return; }
                var original=map.Arguments;
                try
                {
                    var requested=order.Text.Split(',',StringSplitOptions.TrimEntries);
                    var proposed=LocalContentOperations.ReorderMods(original,requested);
                    if(WinMessageBox.Show(
                        $"A(z) {map.Name} modbetöltési sorrendje a KÖVETKEZŐ indításkor változik:\n\n"+
                        proposed+"\n\nA többi indítási paraméter és a modlista tartalma változatlan. Mented?",
                        "Modok sorrendje",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                    map.Arguments=proposed;
                    _cluster.Save();
                    status.Text=map.Name+": a modok sorrendje mentve. Nincs automatikus újraindítás.";
                }
                catch(Exception e)
                {
                    map.Arguments=original;
                    status.Text="Sorrendmódosítás elutasítva: "+e.Message;
                    WriteLog("mod-order",e);
                }
            }),
            status)));
    }
    private void ShowBulkPluginsControls() => ShowSafeZipPluginsControls();

}
