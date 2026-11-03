using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowMultiMapBackupOverview()
    {
        var entries=new ListBox{
            Height=235,Foreground=Brushes.White,Background=Panel,BorderBrush=Aqua
        };
        var status=Label("Az összes helyi map mentései még nincsenek ellenőrizve.",12,Soft);
        Button? button=null;
        button=Btn("Összes helyi map mentéseinek SHA-256 ellenőrzése",async ()=>{
            if(button!=null)button.IsEnabled=false;
            status.Text="Mentési csomagok újraellenőrzése folyamatban...";
            try
            {
                var reports=await Task.Run(()=>LocalBackupOverview.Inspect(
                    _cluster.Store.Root,_cluster.Config.LocalMaps.ToArray(),10));
                entries.ItemsSource=reports.Select(x=>
                    x.MapName+" · ép: "+x.ValidPackages+" · sérült / hiányos: "+
                    x.InvalidPackages+" · ellenőrzött méret: "+
                    (x.VerifiedBytes/1048576d).ToString("F1")+" MiB"+
                    (x.LatestVerifiedUtc.HasValue
                        ?" · legutóbbi (UTC): "+x.LatestVerifiedUtc.Value.ToString("yyyy-MM-dd HH:mm")
                        :" · nincs ellenőrzött mentés")).ToArray();
                status.Text="Helyi mapok: "+reports.Count+" · SHA-256 szerint ép csomagok: "+
                    reports.Sum(x=>x.ValidPackages)+" · hibás/hiányos: "+
                    reports.Sum(x=>x.InvalidPackages)+
                    ". A SHA-256 nem igazolja a futó ASA mentés konzisztenciáját. Restore tiltva.";
            }
            catch(Exception ex)
            {
                status.Text="Mentésleltár ellenőrzése nem sikerült: "+ex.Message;
                WriteLog("multi-map-backup",ex);
            }
            finally{if(button!=null)button.IsEnabled=true;}
        });
        Add(Box(V(
            Label("Helyi mapok mentési áttekintője – csak olvasás",20,Mint,true),
            Label("Alapértelmezett helyi mentési mappák. Egyedi külső útvonalon tárolt csomagokat ez a nézet nem vizsgál.",12,Soft),
            button,entries,status)));
    }
}
