using System;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowNetworkDiagnostics()
    {
        Header("Hálózati diagnosztika · ping és jitter",
            "Kézzel indítható ICMP-mérés; nem módosít routert, DDNS-t, "+
            "portátirányítást vagy játékszervert.");
        var router=new TextBox{Text="192.168.1.254",Height=34,
            Foreground=Brushes.White,Background=Panel};
        var external=new TextBox{Text="1.1.1.1",Height=34,
            Foreground=Brushes.White,Background=Panel};
        var status=Label("Még nem mértünk. A két gépes clusterutazás közbeni "+
            "pingcsúcsok oka ebből az egy mérésből nem következtethető ki.",12,Soft);
        Button? button=null;
        button=Btn("Router és külső IP mérése (12+12 ping)",async ()=>{
            if(button!=null)button.IsEnabled=false;
            try
            {
                string first=router.Text.Trim(),second=external.Text.Trim();
                if(!NetworkLatencyDiagnostics.ValidTarget(first)||
                   !NetworkLatencyDiagnostics.ValidTarget(second) ||first==second)
                    throw new InvalidOperationException(
                        "Két eltérő, konkrét IPv4-cím szükséges");
                status.Text="Csak olvasó hálózati mérés folyamatban...";
                var inside=await NetworkLatencyDiagnostics.MeasureAsync(first,12);
                var outside=await NetworkLatencyDiagnostics.MeasureAsync(second,12);
                string Format(LatencyReport s)
                {
                    string Ms(double? n)=>n.HasValue?n.Value.ToString("F1")+" ms":"ismeretlen";
                    return s.Destination+": "+
                        s.Responses+"/"+s.Samples+" válasz, veszteség "+
                        s.LossPercent.ToString("F1")+"%, medián "+Ms(s.MedianMs)+
                        ", P95 "+Ms(s.P95Ms)+", csúcs "+Ms(s.MaxMs)+
                        ", jitter "+Ms(s.MeanAbsoluteJitterMs);
                }
                status.Text="Helyi út: "+Format(inside)+
                    "\nKülső út: "+Format(outside)+
                    "\nAz ICMP-re adott válasz nem bizonyítja az ASA-UDP, "+
                    "RCON vagy cluster-Store szolgáltatás elérhetőségét. "+
                    "Nincs automatikus restart vagy IP-beállítás.";
            }
            catch(Exception ex)
            {
                status.Text="Hálózati mérés nem sikerült: "+
                    ex.GetType().Name+
                    ". A megadott célpont vagy az ICMP-jogosultság ellenőrizendő.";
            }
            finally{if(button!=null)button.IsEnabled=true;}
        });
        Add(Box(V(
            Label("UNTAMED KINGDOM · HÁLÓZATI MÉRŐ",20,Mint,true),
            Label("Az első IP alapértelmezetten a korábbi Telekom-router "+
                "címe. A külön tesztgépen csak akkor várható válasz, ha "+
                "ugyanazon a helyi hálózaton van. A második cím egy külső "+
                "ICMP-cél. Minden mérés kizárólag gombnyomásra indul.",12,Soft),
            Label("Router IPv4 (LAN)",12,Aqua),router,
            Label("Külső ellenőrző IPv4",12,Aqua),external,
            button,status)));
    }
}
