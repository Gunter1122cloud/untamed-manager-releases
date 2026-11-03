using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private readonly WanChangeTracker _wanTracker=new();
    private DateTimeOffset _lastWanProbe=DateTimeOffset.MinValue;

    // Invoked by the isolated CI GUI smoke path, with no server or WAN calls.
    public void VerifyRecoveryCenterCiSmoke()
    {
        Navigate("recovery");
        UpdateLayout();
        if(PageTitle.Text!="Recovery Center – v1.3.7 TEST")
            throw new InvalidOperationException("Recovery Center navigation failed");
        Navigate("dashboard");
    }

    private void ShowRecoveryCenter()
    {
        Header("Recovery Center – v1.3.7 TEST",
            "Watchdog 2.0, szimulált SaveWorld/Restart, WAN/DDNS megfigyelés. Éles szerverműveletek tiltva.");
        Add(Box(V(
            Label("RECOVERY ENGINE: KIZÁRÓLAG SZIMULÁCIÓ",21,Mint,true),
            Label("Sem SaveWorld, sem DoExit, sem Kill, sem szerverindítás nem hajtható végre ezen a felületen. "+
                "A KovaCluster Store és SQL adatok változatlanok maradnak.",13,Soft))));
        var maps=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",
            SelectedIndex=_cluster.Config.LocalMaps.Count>0?0:-1,
            Foreground=Brushes.White,Background=Panel};
        var state=Label("Még nincs helyi map kiválasztva.",12,Soft);
        var events=Label("A helyi Watchdog-események itt jelennek meg.",12,Soft);
        var recovery=Label("Nem történt helyreállítási szimuláció.",12,Soft);
        // The demo identity is not added to any production config or remote Manager.
        var demoMap=Guid.NewGuid();
        LocalMap? SelectedLocal()=>maps.SelectedItem is LocalMap selected &&
            _cluster.Config.LocalMaps.Contains(selected)?selected:null;
        void Refresh()
        {
            var map=SelectedLocal();
            if(map==null)
            {
                state.Text="Nincs regisztrált helyi map. Az offline FakeAsa demó így is elérhető.";
                return;
            }
            var obs=_supervisorSnapshot?.Maps.SingleOrDefault(m=>m.Map==map.Id);
            bool fresh=obs!=null && StatusFreshness.IsFresh(obs.ObservedUtc,DateTimeOffset.UtcNow);
            state.Text=map.Name+" · Maintenance: "+(map.Maintenance?"BE":"KI")+"\n"+
                "Folyamat: "+(fresh?(obs!.Running==true?"fut":obs.Running==false?
                    "Supervisor szerint leállt":"bizonytalan"):"nincs friss, hitelesített Supervisor-mérés")+"\n"+
                "Loader: "+(fresh?obs!.Loader:"nem igazolt")+
                "\nRCON: "+(fresh?obs!.Rcon:"nem igazolt")+
                "\nWatchdog: "+(fresh?obs!.Watchdog:"nincs friss mérés")+
                "\nUtolsó ellenőrzés: "+(fresh?obs!.ObservedUtc.ToString("O"):"ismeretlen")+
                "\nSaveWorld igazolás: NINCS – éles Stop/Restart/Kill tilos";
        }
        maps.SelectionChanged+=(_,_)=>Refresh();
        Add(Box(V(
            Label("Helyi mapok – felügyelet csak olvasásra",18,Mint,true),
            maps,Btn("Állapot újraellenőrzése (Supervisor)",()=>{
                Refresh();
            }),state,
            Btn("Helyreállítási előnézet (művelet nélkül)",()=>{
                Refresh();
                recovery.Text="Előnézet: a mentés befejezését valódi ASA-ból nem tudjuk igazolni. "+
                    "Éles Stop/Restart és Kill→Start: TILTVA. "+
                    "A szimuláció kizárólag FakeAsa-bizonyítékokkal fut.";
            }),
            Btn("Maintenance mód váltása – kizárólag helyi konfiguráció",()=>{
                var map=SelectedLocal();
                if(map==null){recovery.Text="Válassz regisztrált helyi mapot.";return;}
                if(MessageBox.Show("A(z) "+map.Name+" helyi maintenance jelzőjét módosítod. "+
                    "Ez nem állít le és nem indít szervert. Folytatod?",
                    "Helyi maintenance",MessageBoxButton.YesNo,MessageBoxImage.Question)!=
                    MessageBoxResult.Yes)return;
                bool old=map.Maintenance;
                try{map.Maintenance=!old;_cluster.Save();Refresh();
                    recovery.Text=map.Name+": maintenance "+(map.Maintenance?"bekapcsolva":"kikapcsolva");}
                catch(Exception ex){map.Maintenance=old;recovery.Text="Mentés megtagadva: "+ex.Message;}
            }),
            Btn("Watchdog és Recovery eseményelőzmények",()=>{
                var map=SelectedLocal();
                if(map==null){events.Text="A demó maphoz nincs éles felügyeleti esemény.";return;}
                try
                {
                    var journal=_events?.Read(map:map.Id,since:DateTimeOffset.UtcNow.AddDays(-7))
                        .Take(25).ToArray();
                    events.Text=journal==null?"Supervisor eseménynapló nem érhető el.":
                        journal.Length==0?"Nincs helyi esemény.":
                        string.Join("\n",journal.Select(e=>e.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")+
                            " · "+e.Severity+" · "+e.Code));
                }
                catch(Exception ex){events.Text="Eseménynapló nem elérhető: "+ex.Message;}
            }),events)));
        Add(Box(V(
            Label("Safe Restart Engine – offline FakeAsa szimuláció",18,Mint,true),
            Label("Tartós, állapotgépes demó. Valódi szerverfolyamat nem módosul. "+
                "Egy megszakadt műveletet a Manager nem ismétel meg automatikusan.",12,Soft),
            Btn("Szimulált SaveWorld → Stop → Start (NEM ÉLES)",()=>{
                try
                {
                    var id=SelectedLocal()?.Id??demoMap;
                    var now=DateTimeOffset.UtcNow;
                    var fake=new ProcessIdentity{Pid=54321,StartedUtc=now.AddMinutes(-5),
                        ExecutablePath="FakeAsa-test-only.exe",Arguments="--synthetic-map"};
                    string token=Guid.NewGuid().ToString("N");
                    var signals=new[]{
                        new SyntheticSaveSignal(token,SyntheticSaveSignalKind.Started,fake,now.AddMilliseconds(10)),
                        new SyntheticSaveSignal(token,SyntheticSaveSignalKind.Completed,fake,now.AddMilliseconds(50))
                    };
                    var simulatedAt=now.AddSeconds(1);
                    var scenario=new RecoveryScenario(id,true,false,
                        new ProcessCheck(true,"Synthetic running",fake),
                        token,now,simulatedAt,signals,true,true);
                    var operation=RecoverySimulator.Run(
                        new RecoverySimulationJournal(_cluster.Store.Root),scenario);
                    recovery.Text="KIZÁRÓLAG SZIMULÁCIÓ · "+operation.Phase+"\n"+
                        operation.Explanation+"\n"+operation.Attempt;
                }
                catch(Exception ex){recovery.Text="Biztonságos szimuláció blokkolva: "+ex.Message;}
            }),
            Btn("Helyreállítási műveletnapló olvasása",()=>{
                try
                {
                    var id=SelectedLocal()?.Id??demoMap;
                    var steps=new RecoverySimulationJournal(_cluster.Store.Root).Read()
                        .Where(t=>t.Map==id).TakeLast(20);
                    recovery.Text=string.Join("\n",steps.Select(t=>t.Utc.ToLocalTime().ToString("HH:mm:ss")+
                        " · "+t.Phase+" · "+t.Code));
                    if(recovery.Text.Length==0)recovery.Text="Nincs szimulált művelet.";
                }
                catch(Exception ex){recovery.Text="Helyreállítási napló nem olvasható: "+ex.Message;}
            }),recovery)));
        var wan=Label("A WAN-monitor nincs elindítva; sem a router, sem a DDNS beállításai nem változnak.",12,Soft);
        Add(Box(V(
            Label("WAN-IP / untamedkingdom.ddns.net – csak megfigyelés",18,Mint,true),
            Label("A valós ellenőrzés csak gombnyomásra kérdez le két független HTTPS IP-forrást és DNS-t. "+
                "Két, legalább 30 másodpercre lévő egyező mérés szükséges.",12,Soft),
            Btn("Szimulált WAN-IP változás (offline)",()=>{
                var tracker=new WanChangeTracker();
                var start=DateTimeOffset.UtcNow;
                tracker.Observe(new WanProbe(start,"8.8.8.8","8.8.8.8",new[]{"8.8.8.8"}));
                tracker.Observe(new WanProbe(start.AddSeconds(31),"8.8.8.8","8.8.8.8",new[]{"8.8.8.8"}));
                tracker.Observe(new WanProbe(start.AddSeconds(62),"1.1.1.1","1.1.1.1",new[]{"1.1.1.1"}));
                var change=tracker.Observe(new WanProbe(start.AddSeconds(93),"1.1.1.1","1.1.1.1",new[]{"1.1.1.1"}));
                wan.Text="SZIMULÁCIÓ · "+change.State+" · "+
                    (change.ConfirmedChange?"IP-váltás megerősítve":"nincs megerősítve")+
                    "\n"+change.Message+"\nÉles map-újraindítás tiltva.";
            }),
            Btn("Valós IP + DDNS ellenőrzése (csak olvasás)",async ()=>{
                if(DateTimeOffset.UtcNow-_lastWanProbe<TimeSpan.FromMinutes(2))
                {wan.Text="Valós mérés legfeljebb kétpercenként. Nincs automatikus újraindítás.";return;}
                _lastWanProbe=DateTimeOffset.UtcNow;
                wan.Text="HTTPS/DNS megfigyelés folyamatban; nincs router- vagy szervermódosítás...";
                try
                {
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var measurement=await ReadOnlyWanProbe.CaptureAsync(_http,deadline.Token);
                    var result=_wanTracker.Observe(measurement);
                    wan.Text="Mérés: "+result.State+"\nWAN alapvonal: "+
                        (result.Baseline??"még nem igazolt")+" · DNS egyezik: "+
                        (result.DdnsMatches?"igen":"nem igazolt")+"\n"+
                        result.Message+"\nMegerősítés után sincs automatikus restart.";
                }
                catch(Exception ex){wan.Text="WAN megfigyelés sikertelen: "+ex.GetType().Name+
                    ". Szerverművelet nem történt.";}
            }),wan)));
        Refresh();
    }
}
