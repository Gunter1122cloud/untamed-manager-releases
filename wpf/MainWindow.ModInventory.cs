using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowLocalModAssignmentInventory()
    {
        var list=new ListBox{Height=245,Foreground=Brushes.White,Background=Panel,BorderBrush=Aqua};
        var common=Label("Az összes helyi map közös modjai még nincsenek beolvasva.",12,Soft);
        Add(Box(V(
            Label("Helyi mapok modkiosztása – konfigurációs leltár",20,Mint,true),
            Label("A regisztrált mapok -mods= indítási listái; ez nem igazolja a tényleges modtelepítést vagy betöltést.",12,Soft),
            Btn("Valós helyi modlista beolvasása (csak olvasás)",()=>{
                try
                {
                    var report=LocalModAssignments.Inspect(_cluster.Config.LocalMaps);
                    list.ItemsSource=report.Maps.Select(m=>m.Error==null
                        ? m.Name+" · "+m.OrderedIds.Count+" mod (sorrend): "+
                          (m.OrderedIds.Count==0?"nincs":string.Join(", ",m.OrderedIds))
                        : m.Name+" · HIBÁS indítási modlista: "+m.Error).ToArray();
                    common.Text=report.Complete
                        ? report.Maps.Count+" helyi map · "+report.UniqueIdCount+
                          " különböző ID · minden mapon: "+
                          (report.CommonIds.Count==0?"nincs":string.Join(", ",report.CommonIds))+
                          "\nCsak egy helyi mapon: "+
                          (report.SingleMapIds.Count==0?"nincs":string.Join(", ",report.SingleMapIds))
                        : "Legalább egy map modlistája hibás. A közös és kizárólagos modokat "+
                          "nem jelölöm megbízhatónak, amíg az indítási argumentumok nem egyértelműek.";
                }
                catch(Exception ex)
                {
                    common.Text="Modleltár nem olvasható: "+ex.Message;
                    WriteLog("local-mod-inventory",ex);
                }
            }),list,common)));
    }
}
