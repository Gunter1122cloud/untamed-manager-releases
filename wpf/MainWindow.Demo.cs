using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    // DEMO actions are in-memory only and never call ClusterService, RCON,
    // SteamCMD, MariaDB, SaveWorld, filesystem or authenticated LAN operations.
    private DemoWorkspace? _demo;
    private Guid? _demoSelectedId;
    private string? _beforeDemoSupervisor;
    private string? _beforeDemoBadge;
    private bool IsDemoMode=>_demo!=null;

    private void DemoMode_Click(object sender,RoutedEventArgs e)
    {
        if(_demo==null)EnterDemoMode();
        else ExitDemoMode();
    }
    private void EnterDemoMode()
    {
        if(_demo!=null)return;
        _demo=new DemoWorkspace();
        _demoSelectedId=_demo.Maps[0].Id;
        _beforeDemoSupervisor=SupervisorStatus.Text;
        _beforeDemoBadge=SupervisorBadge.Text;
        _updateTimer.Stop();
        _hostResourceTimer.Stop();
        DemoToggle.Content="✕ Kilépés DEMO-ból";
        DemoModeIndicator.Visibility=Visibility.Visible;
        ModeFooterText.Text="◆  OFFLINE DEMO · SZIMULÁCIÓ";
        PageEyebrow.Text="OFFLINE DEMO / NINCS VALÓDI MŰVELET";
        SupervisorBadge.Text="DEMO";
        Navigate("dashboard");
    }
    private void ExitDemoMode()
    {
        if(_demo==null)return;
        _demo=null;
        _demoSelectedId=null;
        DemoToggle.Content="▶ DEMO";
        DemoModeIndicator.Visibility=Visibility.Collapsed;
        ModeFooterText.Text="◆  HELYI MANAGER";
        PageEyebrow.Text="UNTAMED KINGDOM  /  MANAGEMENT";
        SupervisorStatus.Text=_beforeDemoSupervisor??"Supervisor: nem ellenőrzött";
        SupervisorBadge.Text=_beforeDemoBadge??"Supervisor: ellenőrzés";
        if(!App.IsDemoStart && !App.IsSmokeTest && !App.IsSupervisionTest)
        {
            _updateTimer.Start();
            _hostResourceTimer.Start();
            _=RefreshLocalResourcesAsync();
        }
        RefreshDashboardMapCards();
        Navigate("dashboard");
    }

    private void RenderDemoDashboard()
    {
        if(_demo is not { } demo)return;
        var art=_maps.ToDictionary(x=>x.Name,StringComparer.OrdinalIgnoreCase);
        ServerCards.ItemsSource=demo.Maps.Select(m=>{
            art.TryGetValue(m.Name,out var image);
            return new MapInfo{
                Id=m.Id,Name=m.Name,Host=m.Host,IsRemote=false,IsDemo=true,
                ModCount=m.Mods.Count(x=>x.Mode!=DemoModMode.Disabled),
                Status=demo.Status(m),CoverBrush=image?.CoverBrush??Panel,
                CoverImage=image?.CoverImage
            };
        }).ToArray();
        HostCards.ItemsSource=demo.Hosts.Select(host=>new HostCardInfo{
            Name=host.Name+" · DEMO",
            Description=demo.Maps.Count(m=>m.Host==host.Name)+" fiktív map · CPU "+
                host.CpuPercent+"% · RAM "+host.UsedRamGiB+"/"+host.TotalRamGiB+" GiB",
            TrustState=host.Online?"SZIMULÁLT ONLINE":"SZIMULÁLT OFFLINE",
            Accent=host.Online?Aqua:Brushes.Orange
        }).ToArray();
        ClusterMapCount.Text=demo.Maps.Count.ToString();
        ClusterHostCount.Text=demo.Hosts.Count.ToString();
        ClusterOnlineCount.Text=demo.Hosts.Count(h=>h.Online).ToString()+
            " DEMO";
        ConfiguredModCount.Text=demo.Maps.Sum(m=>m.Mods.Count(x=>x.Mode!=DemoModMode.Disabled)).ToString();
        DashboardOperationsLine.Text="DEMO · "+
            demo.Maps.Count(m=>m.Running&&demo.FindHost(m.Host).Online)+" szimulált futó map · "+
            demo.Maps.Where(m=>m.Running&&demo.FindHost(m.Host).Online).Sum(m=>m.Players)+
            " mintajátékos · "+demo.Maps.Sum(m=>m.Backups.Count)+
            " mintamentés · RCON csak szimuláció.";
        LocalHostResourceStatus.Text="DEMO · fiktív CPU / RAM: "+
            string.Join("  |  ",demo.Hosts.Select(h=>h.Name+": CPU "+h.CpuPercent+
                "% · RAM "+h.UsedRamGiB+"/"+h.TotalRamGiB+" GiB"));
        ModReadingCoverage.Text="Csak mintamodok, nincs fájlolvasás";
        MapListEmpty.Visibility=Visibility.Collapsed;
        SupervisorStatus.Text="OFFLINE DEMO – szimulált folyamatok, játékosok és hostállapotok; nem élő felügyelet.";
        ClusterStatusText.Text="DEMO · minden művelet csak memória · nincs ASA / Store / SQL / RCON kapcsolat";
    }

    private DemoMap ChosenDemoMap()
    {
        var demo=_demo??throw new InvalidOperationException("DEMO is not active");
        return demo.Maps.FirstOrDefault(m=>m.Id==_demoSelectedId)??demo.Maps[0];
    }

    private void RunDemo(Action operation,TextBlock status,string message,Action? after=null)
    {
        try
        {
            if(_demo==null)throw new InvalidOperationException("DEMO is not active");
            operation();
            status.Text="[DEMO] "+message+" · csak memóriában, valódi művelet nélkül.";
            RenderDemoDashboard();
            after?.Invoke();
        }
        catch(Exception ex){status.Text="[DEMO] Elutasítva: "+ex.Message;}
    }

    private void ShowDemoPage(string page)
    {
        var demo=_demo??throw new InvalidOperationException("DEMO unavailable");
        Header(page switch{
            "servers"=>"Szerverek · OFFLINE DEMO",
            "cluster"=>"Cluster · OFFLINE DEMO",
            "mods"=>"CurseForge Mod Center · OFFLINE DEMO",
            "plugins"=>"ASA API / Pluginok · OFFLINE DEMO",
            "ini"=>"INI Editor · OFFLINE DEMO",
            "rcon"=>"RCON konzol · OFFLINE DEMO",
            "backups"=>"Mentések · OFFLINE DEMO",
            "schedules"=>"Ütemező · OFFLINE DEMO",
            "settings"=>"Beállítások · OFFLINE DEMO",
            "monitoring"=>"Műveleti központ · OFFLINE DEMO",
            "operations"=>"Műveletek · OFFLINE DEMO",
            "recovery"=>"Recovery Center · OFFLINE DEMO",
            _=>"Untamed Manager · OFFLINE DEMO"
        },"Az alábbi adatok és gombok teljes egészében szimuláltak. Nincs ASA-, LAN-, Store- vagy adatbázismódosítás.");
        Add(Box(V(Label("◆  SZIMULÁCIÓ – NEM ÉLES ÁLLAPOT",17,Brushes.Gold,true),
            Label("Három fiktív gép és tíz map; minden adat és művelet kizárólag a memóriában él. A DEMO elhagyásával a módosítások eltűnnek.",12,Soft))));
        switch(page)
        {
            case "servers": DemoServers();break;
            case "operations": ShowOperationsHub();break;
            case "cluster": DemoCluster();break;
            case "mods": DemoSelectedMapPanel("Modlista / CurseForge – kipróbálás",DemoModsPane);break;
            case "plugins": DemoSelectedMapPanel("ASA API / pluginok – kipróbálás",DemoPluginsPane);break;
            case "ini": DemoSelectedMapPanel("Game.ini – kipróbálás",DemoIniPane);break;
            case "rcon": DemoSelectedMapPanel("RCON – mintaválaszok",DemoRconPane);break;
            case "backups": DemoSelectedMapPanel("Mentések – mintakatalógus",DemoBackupsPane);break;
            case "schedules": DemoSchedules();break;
            case "settings": DemoSettings();break;
            case "monitoring": DemoEvents();break;
            case "recovery": DemoRecovery();break;
            default: DemoOther(page);break;
        }
    }

    private void DemoSelectedMapPanel(string title,Func<DemoMap,UIElement> paneFactory)
    {
        var demo=_demo!;
        var chooser=new ComboBox{
            ItemsSource=demo.Maps,DisplayMemberPath="Name",
            SelectedItem=ChosenDemoMap(),MinWidth=200,Margin=new Thickness(0,8,0,12)
        };
        var content=new StackPanel();
        void Display()
        {
            content.Children.Clear();
            if(chooser.SelectedItem is not DemoMap map)return;
            _demoSelectedId=map.Id;
            content.Children.Add(Label(map.Host+" · "+demo.Status(map),12,Aqua));
            content.Children.Add(paneFactory(map));
        }
        chooser.SelectionChanged+=(_,_)=>Display();
        Add(Box(V(Label(title,19,Mint,true),chooser,content)));
        Display();
    }

    private void DemoServers()
    {
        DemoSelectedMapPanel("Szimulált ASA-map kiválasztása",map=>{
            var demo=_demo!;
            var status=Label(demo.Status(map)+" · "+map.Players+" mintajátékos",12,Aqua);
            var operations=new WrapPanel{Margin=new Thickness(0,6,0,12)};
            void Act(Action act,string result)
            {
                RunDemo(act,status,result,()=>status.Text+="\n"+demo.Status(map));
            }
            operations.Children.Add(Btn("▶  Indítás",()=>Act(()=>demo.SetRunning(map.Id,true),"Start")));
            operations.Children.Add(Btn("■  Leállítás",()=>Act(()=>demo.SetRunning(map.Id,false),"Stop")));
            operations.Children.Add(Btn("↻  Restart + SaveWorld",()=>Act(()=>demo.Restart(map.Id),"Restart")));
            operations.Children.Add(Btn("▣  Mentés",()=>Act(()=>demo.Backup(map.Id),"Backup")));
            var tabs=new TabControl{MinHeight=390,Foreground=Brushes.White,Background=Panel};
            void Tab(string name,UIElement content)=>
                tabs.Items.Add(new TabItem{Header=name,Content=new ScrollViewer{
                    Content=content,Padding=new Thickness(12),
                    VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            Tab("Áttekintés",V(Label(map.Name,22,Mint,true),
                Label("Gép: "+map.Host+" · ASA API "+map.ApiVersion,13,Aqua),
                Label("Játékosok (minta): "+map.Players+" · "+demo.Status(map),13,Soft),
                Label("A műveletek nem indítanak vagy állítanak le Windows-folyamatot.",12,Soft)));
            Tab("Modok",DemoModsPane(map));
            Tab("Pluginok / API",DemoPluginsPane(map));
            Tab("INI",DemoIniPane(map));
            Tab("RCON",DemoRconPane(map));
            Tab("Mentések",DemoBackupsPane(map));
            Tab("Naplók",DemoLogsPane(map));
            return V(status,operations,tabs);
        });
    }

    private UIElement DemoModsPane(DemoMap map)
    {
        var demo=_demo!;
        var mods=new ListBox{Height=155,Background=Panel,Foreground=Brushes.White};
        var project=new TextBox{Text="928548",Height=34};
        var mode=new ComboBox{ItemsSource=new[]{"Active","Passive","Disabled","Remove"},
            SelectedIndex=0,MinWidth=180};
        var result=Label("Csak szimulált map-modlista módosítható.",12,Soft);
        void Update()=>mods.ItemsSource=map.Mods.Select(m=>m.Mode+" · "+m.ProjectId).ToArray();
        mods.SelectionChanged+=(_,_)=>{
            if(mods.SelectedItem is not string value)return;
            var pieces=value.Split(" · ",StringSplitOptions.None);
            if(pieces.Length!=2)return;
            project.Text=pieces[1];mode.SelectedItem=pieces[0];
        };
        var buttons=new WrapPanel();
        buttons.Children.Add(Btn("Modmód előnézete",()=>{
            result.Text="[DEMO] "+map.Name+" · "+project.Text+" → "+mode.SelectedItem+
                " (még semmi sem változott).";
        }));
        buttons.Children.Add(Btn("Modmód alkalmazása (DEMO)",()=>{
            RunDemo(()=>{
                DemoModMode? chosen=(string?)mode.SelectedItem switch{
                    "Active"=>DemoModMode.Active,"Passive"=>DemoModMode.Passive,
                    "Disabled"=>DemoModMode.Disabled,"Remove"=>null,
                    _=>throw new InvalidOperationException("Válassz érvényes modállapotot")
                };
                demo.SetMod(map.Id,project.Text.Trim(),chosen);
            },result,"Modlista módosult",Update);
        }));
        void MoveSelected(int direction)
        {
            if(mods.SelectedItem is not string selected)
            { result.Text="[DEMO] Előbb válassz modot a listából.";return; }
            var id=selected.Split(" · ",StringSplitOptions.None).Last();
            RunDemo(()=>{
                if(!demo.MoveMod(map.Id,id,direction))
                    throw new InvalidOperationException("A mod már a sorrend szélén van.");
            },result,"Modok sorrendje módosult",Update);
        }
        buttons.Children.Add(Btn("↑  Feljebb (DEMO)",()=>MoveSelected(-1)));
        buttons.Children.Add(Btn("↓  Lejjebb (DEMO)",()=>MoveSelected(1)));
        Update();
        return V(Label("Modok / betöltési mód",17,Mint,true),
            Label("Aktív, passzív, tiltott és eltávolított módok; csak mintarekordok.",12,Soft),
            project,mode,buttons,mods,result);
    }

    private UIElement DemoPluginsPane(DemoMap map)
    {
        var demo=_demo!;
        var list=new ListBox{Height=155,Foreground=Brushes.White,Background=Panel};
        var status=Label("Fiktív ASA API verzió: "+map.ApiVersion,12,Soft);
        void Update()=>list.ItemsSource=map.Plugins.Select(p=>
            (p.Enabled?"AKTÍV":"TILTVA")+" · "+p.Name).ToArray();
        Update();
        return V(Label("ASA API / pluginok",17,Mint,true),
            Label("A plugin állapotváltása csak a DEMO objektumon történik.",12,Soft),
            list,Btn("Kijelölt plugin átkapcsolása (DEMO)",()=>{
                if(list.SelectedItem is not string selected)
                {status.Text="Válassz ki egy pluginrekordot.";return;}
                string name=selected.Split(" · ",2,StringSplitOptions.None).Last();
                RunDemo(()=>demo.TogglePlugin(map.Id,name),status,
                    name+" szimulált állapota módosult",Update);
            }),status);
    }

    private UIElement DemoIniPane(DemoMap map)
    {
        var demo=_demo!;
        var keys=new ComboBox{ItemsSource=map.Ini.Keys.OrderBy(x=>x).ToArray(),SelectedIndex=0};
        var value=new TextBox{Height=35,Text="2.0"};
        var status=Label("Az INI itt kizárólag memóriabeli mintakonfiguráció.",12,Soft);
        void Selection()
        {
            if(keys.SelectedItem is string key)value.Text=map.Ini[key];
        }
        keys.SelectionChanged+=(_,_)=>Selection();
        Selection();
        return V(Label("INI mezőszerkesztő",17,Mint,true),
            Label("A megadott érték nem kerül sem Game.ini, sem plugin JSON fájlba.",12,Soft),
            keys,value,
            Btn("Változás előnézete",()=>{
                status.Text="[DEMO] "+keys.SelectedItem+" : "+map.Ini[(string)keys.SelectedItem]+" → "+value.Text;
            }),
            Btn("Módosítás mentése (DEMO)",()=>{
                RunDemo(()=>demo.SetIni(map.Id,(string)keys.SelectedItem,value.Text),
                    status,"INI beállítás eltárolva a szimulációban");
            }),status);
    }

    private UIElement DemoRconPane(DemoMap map)
    {
        var demo=_demo!;
        var command=new ComboBox{ItemsSource=new[]{"ListPlayers","GetChat","SaveWorld"},SelectedIndex=0};
        var answer=Label("A válasz fiktív; nincs hálózati RCON-csatlakozás.",12,Soft);
        return V(Label("RCON konzol – offline válaszok",17,Mint,true),command,
            Btn("Parancs szimulálása",()=>{
                try{answer.Text=demo.Rcon(map.Id,(string)command.SelectedItem);
                    RenderDemoDashboard();}
                catch(Exception ex){answer.Text="[DEMO] "+ex.Message;}
            }),answer);
    }

    private UIElement DemoBackupsPane(DemoMap map)
    {
        var demo=_demo!;
        var list=new ListBox{Height=165,Background=Panel,Foreground=Brushes.White};
        var status=Label("Mintakatalógus: nincs mentett állomány.",12,Soft);
        void Refresh()=>list.ItemsSource=map.Backups.ToArray();
        Refresh();
        return V(Label("Backup / Restore – szimuláció",17,Mint,true),
            Label("A mintamentés mod-, plugin-, INI- és állapotpillanatképet tárol a memóriában. A DEMO restore nem ír valódi állományt.",12,Soft),
            list,
            Btn("Mintamentés létrehozása",()=>{
                RunDemo(()=>demo.Backup(map.Id),status,"DEMO mentésrekord létrejött",Refresh);
            }),
            Btn("Kijelölt restore előnézete",()=>{
                status.Text=list.SelectedItem is string selected
                    ?"[DEMO] Restore-terv: "+selected+" · nem történik lemez-, SQL- vagy karakterírás."
                    :"Válassz minta-mentésrekordot.";
            }),
             Btn("Kijelölt pillanatkép visszaállítása (DEMO)",()=>{
                 if(list.SelectedItem is not string selected)
                 { status.Text="[DEMO] Előbb válassz mentést a listából.";return; }
                 RunDemo(()=>demo.Restore(map.Id,selected),status,
                     "Szimulált map visszaállítva (csak memória)",Refresh);
             }),status);
    }
    private UIElement DemoLogsPane(DemoMap map)=>V(
        Label("DEMO naplók",17,Mint,true),
        List(map.Logs.AsEnumerable().Reverse(),220),
        Label("A fenti naplók nem származnak valódi ASA-folyamatból.",12,Soft));

    private void DemoCluster()
    {
        var demo=_demo!;
        Add(Box(V(Label("Három szimulált Windows host – dinamikus cluster",19,Mint,true),
            Label("A gombok csak a mintagép online/offline jelzését módosítják.",12,Soft))));
        foreach(var host in demo.Hosts)
        {
            var state=Label(host.Online?"DEMO ONLINE":"DEMO OFFLINE",13,
                host.Online?Mint:Brushes.Orange);
            Add(Box(V(Label(host.Name,18,Aqua,true),
                Label(demo.Maps.Count(x=>x.Host==host.Name)+" mintamap",12,Soft),
                state,Btn("Online/offline szimulálása",()=>{
                    RunDemo(()=>demo.SetHostOnline(host.Name,!host.Online),state,
                        "Host állapota: "+(!host.Online?"online":"offline"),
                        ()=>state.Text=host.Online?"DEMO ONLINE":"DEMO OFFLINE");
                }))));
        }
        Add(Btn("Szerverek és folyamatok megnyitása",()=>Navigate("servers")));
    }

    private void DemoSchedules()
    {
        var demo=_demo!;
        foreach(var schedule in demo.Schedules.ToArray())
        {
            Add(Box(V(Label(schedule.Key,16,Mint,true),
                Label(schedule.Value?"Bekapcsolva (DEMO)":"Kikapcsolva (DEMO)",12,Soft),
                Btn("Ütemezés állapotának váltása (DEMO)",()=>{
                    demo.ToggleSchedule(schedule.Key);
                    Navigate("schedules");
                }))));
        }
    }
    private void DemoEvents()
    {
        var demo=_demo!;
        Add(Box(V(Label("Műveleti napló / kizárólag fiktív események",18,Mint,true),
            List(demo.Events,350),
            Btn("Napló frissítése",()=>Navigate("monitoring")))));
    }
    private void DemoRecovery()
    {
        var demo=_demo!;
        var map=ChosenDemoMap();
        var status=Label("Nincs valódi állomány- vagy SQL-visszaállítás.",12,Soft);
        Add(Box(V(Label("Recovery Wizard – szimulált helyreállítás",18,Mint,true),
            Label("Kiválasztott map: "+map.Name,12,Aqua),
            Btn("Restart helyreállítási próba (DEMO)",()=>{
                RunDemo(()=>demo.Restart(map.Id),status,
                    "A mintaszerver SaveWorld/Stop/Start folyamata lefutott");
            }),
            Btn("Mentéskatalógus megnyitása",()=>Navigate("backups")),status)));
    }
    private void DemoSettings()
    {
        var demo=_demo!;
        Add(Box(V(Label("DEMO beállítások",19,Mint,true),
            Label("Nem kerül semmi a Windows-profilba vagy a szervermegosztásokba.",12,Soft),
            Label("Szimulált gépek: "+demo.Hosts.Count+
                " · fiktív mapok: "+demo.Maps.Count,12,Aqua),
            Btn("Mintaállapot visszaállítása",()=>{
                _demo=new DemoWorkspace();
                _demoSelectedId=_demo.Maps[0].Id;
                RenderDemoDashboard();
                Navigate("settings");
            }),
            Btn("Kilépés a DEMO-ból",ExitDemoMode))));
    }
    private void DemoOther(string page)
    {
        var headings=new Dictionary<string,string>(StringComparer.Ordinal){
            ["lan"]="LAN párosítás: a DEMO hálózat nélkül fut.",
            ["data"]="Store és SQL: csak mintarekord, nincs adatbáziskapcsolat.",
            ["steam"]="SteamCMD: valós telepítés és frissítés letiltva a DEMO-ban.",
            ["network"]="Hálózat: a DEMO nem végez élő pinget.",
            ["discord"]="Discord: nincs webhook-küldés a DEMO-ban.",
            ["license"]="Frissítések/licenc: a DEMO nem lép kapcsolatba a GitHubbal.",
            ["readiness"]="Minden itt látható státusz szimulált, nem kiadási tanúsítvány.",
            ["extensions"]="A DEMO nem tölt be valódi harmadik féltől származó panelt.",
            ["setup"]="Az igazi mapok regisztrálása nem része a DEMO módnak."
        };
        Add(Box(V(Label(headings.TryGetValue(page,out var label)?label:"Offline DEMO munkatér",
                18,Mint,true),
            Label("Funkcióbemutató: válassz egy működő szimulált kezelőoldalt. Az éles backendek itt nem futnak.",12,Soft),
            Btn("Mapok kezelése (DEMO)",()=>Navigate("servers")),
            Btn("Műveleti napló (DEMO)",()=>Navigate("monitoring")),
            Btn("Beállítások (DEMO)",()=>Navigate("settings")))));
    }

    public void VerifyDemoUiCiSmoke()
    {
        EnterDemoMode();
        if(_demo==null || ServerCards.ItemsSource is not IEnumerable<MapInfo> cards ||
           cards.Count()!=10 || cards.Any(x=>!x.IsDemo) ||
           ClusterHostCount.Text!="3" || DemoModeIndicator.Visibility!=Visibility.Visible)
            throw new InvalidOperationException("Demo dashboard was not isolated or marked");
        foreach(var page in new[]{"servers","cluster","mods","plugins","ini","rcon",
            "backups","schedules","settings","monitoring","recovery","network"})
        {
            Navigate(page);
            if(PageBody.Children.Count==0 || !PageTitle.Text.Contains("DEMO"))
                throw new InvalidOperationException("Missing interactive demo page: "+page);
        }
        var map=ChosenDemoMap();
        _demo.SetRunning(map.Id,false);
        if(map.Running)throw new InvalidOperationException("Demo stop does not update simulated state");
        ExitDemoMode();
        if(_demo!=null || DemoModeIndicator.Visibility!=Visibility.Collapsed)
            throw new InvalidOperationException("Demo state was not discarded on exit");
    }
}
