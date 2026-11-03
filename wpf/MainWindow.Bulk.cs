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
    private void ShowBulkPluginsControls() => ShowSafeZipPluginsControls();

}
