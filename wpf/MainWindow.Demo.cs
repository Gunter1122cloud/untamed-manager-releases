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
    private readonly Dictionary<Guid,List<DemoMod[]>> _demoModTimeline=new();
    private readonly Dictionary<Guid,int> _demoModCursor=new();
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
        _demoModTimeline.Clear();_demoModCursor.Clear();
        _demoSelectedId=_demo.Maps[0].Id;
        _beforeDemoSupervisor=SupervisorStatus.Text;
        _beforeDemoBadge=SupervisorBadge.Text;
        _updateTimer.Stop();
        _hostResourceTimer.Stop();
        DemoToggle.Content="✕ Kilépés";
        DemoModeIndicator.Visibility=Visibility.Collapsed;
        ModeFooterText.Text="◆  OFFLINE DEMO · SZIMULÁCIÓ";
        PageEyebrow.Text="OFFLINE DEMO / NINCS VALÓDI MŰVELET";
        SupervisorBadge.Text="DEMO";
        Navigate("dashboard");
    }
    private void ExitDemoMode()
    {
        if(_demo==null)return;
        _demo=null;
        _demoModTimeline.Clear();_demoModCursor.Clear();
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
                PlayerCount=m.Running&&demo.FindHost(m.Host).Online?m.Players:0,
                Status=demo.Status(m),CoverBrush=image?.CoverBrush??Panel,
                CoverImage=image?.CoverImage
            };
        }).ToArray();
        HostCards.ItemsSource=demo.Hosts.Select(host=>new HostCardInfo{
            Name=host.Name+" · DEMO",
            Description=demo.Maps.Count(m=>m.Host==host.Name)+" fiktív map · CPU "+
                host.CpuPercent+"% · RAM "+host.UsedRamGiB+"/"+host.TotalRamGiB+" GiB",
            TrustState=host.Online?"SZIMULÁLT ONLINE":"SZIMULÁLT OFFLINE",
            Accent=host.Online?Aqua:Brushes.Orange,
            CpuPercent=host.Online?host.CpuPercent:null,
            RamPercent=host.Online && host.TotalRamGiB>0
                ?100d*host.UsedRamGiB/host.TotalRamGiB:null
        }).ToArray();
        ClusterMapCount.Text=demo.Maps.Count.ToString();
        RunningServerKpi.Text=demo.Maps.Count(m=>m.Running&&demo.FindHost(m.Host).Online).ToString()+" DEMO";
        PlayersKpi.Text=demo.Maps.Where(m=>m.Running&&demo.FindHost(m.Host).Online)
            .Sum(m=>m.Players).ToString()+" DEMO";
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
            "mods"=>"Mod Center · OFFLINE DEMO",
            "plugins"=>"ASA API / Pluginok · OFFLINE DEMO",
            "ini"=>"INI Editor · OFFLINE DEMO",
            "rcon"=>"RCON konzol · OFFLINE DEMO",
            "backups"=>"Mentések · OFFLINE DEMO",
            "schedules"=>"Ütemező · OFFLINE DEMO",
            "settings"=>"Beállítások · OFFLINE DEMO",
            "monitoring"=>"Műveleti központ · OFFLINE DEMO",
            "operations"=>"Műveletek · OFFLINE DEMO",
            "logs"=>"Naplóközpont · OFFLINE DEMO",
            "recovery"=>"Recovery Center · OFFLINE DEMO",
            _=>"Untamed Manager · OFFLINE DEMO"
        },"Az alábbi adatok és gombok teljes egészében szimuláltak. Nincs ASA-, LAN-, Store- vagy adatbázismódosítás.");
        // The page heading, top mode badge and footer identify simulation.
        // Avoid a second oversized warning card that pushes core controls
        // below the fold at 900px height or elevated Windows DPI.
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
            case "logs": DemoSelectedMapPanel("Naplóközpont · mintanaplók",DemoLogsPane);break;
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
        var titleBar=new WrapPanel {Margin=new Thickness(0,1,0,9)};
        titleBar.Children.Add(Label(title,17,Mint,true));
        titleBar.Children.Add(chooser);
        Add(V(titleBar,content));
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
            var tabs=new TabControl{Foreground=Brushes.White,Background=Panel};
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
        // Exactly the same native input controls and visual tokens as the local
        // editor. Only the operation executor is different: DemoWorkspace memory.
        var demo=_demo!;
        if(!_demoModTimeline.TryGetValue(map.Id,out var timeline))
        {
            timeline=new List<DemoMod[]> {map.Mods.ToArray()};
            _demoModTimeline[map.Id]=timeline;
            _demoModCursor[map.Id]=0;
        }
        int cursor=_demoModCursor[map.Id];
        var staged=timeline[cursor].ToList();
        var list=new ListView {
            MinHeight=260,MaxHeight=510,
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("PremiumModRow")
        };
        var search=new TextBox {Width=180,Height=36,Margin=new Thickness(0,0,9,7)};
        var filter=new ComboBox {ItemsSource=new[]{"Összes","Aktív","Passzív","Letiltva"},
            SelectedIndex=0,Width=130,Margin=new Thickness(0,0,9,7)};
        var project=new TextBox {Width=140,Height=36,Margin=new Thickness(0,0,9,7),
            ToolTip="DEMO: modprojekt azonosító"};
        var mode=new ComboBox {ItemsSource=new[]{"Aktív","Passzív","Letiltva","Eltávolítás"},
            SelectedIndex=0,MinWidth=150,Margin=new Thickness(0,0,0,9)};
        var selected=Label("Válassz modot",18,Mint,true);
        var details=Label("DEMO: szimulált modállapot, nem éles telepítés.",12,Soft);
        var summary=Label("",12,Soft);
        var result=Label("DEMO · Nincs függőben lévő módosítás.",12,Soft);
        var preview=Label("",12,Soft);
        PremiumModRow? chosen=null;
        Button? undoButton=null,redoButton=null,applyButton=null;
        void Checkpoint()
        {
            if(timeline[cursor].SequenceEqual(staged))return;
            if(cursor<timeline.Count-1)timeline.RemoveRange(cursor+1,timeline.Count-cursor-1);
            timeline.Add(staged.ToArray());
            cursor=timeline.Count-1;
            _demoModCursor[map.Id]=cursor;
        }
        void ResetHistory()
        {
            staged=map.Mods.ToList();
            timeline.Clear();timeline.Add(staged.ToArray());
            cursor=0;_demoModCursor[map.Id]=0;
        }
        string ModeName(DemoModMode value)=>value switch{
            DemoModMode.Active=>"Aktív",DemoModMode.Passive=>"Passzív",
            DemoModMode.Disabled=>"Letiltva",_=>"Ismeretlen"
        };
        DemoModMode? ParseMode()=>((string?)mode.SelectedItem) switch {
            "Aktív"=>DemoModMode.Active,"Passzív"=>DemoModMode.Passive,
            "Letiltva"=>DemoModMode.Disabled,"Eltávolítás"=>(DemoModMode?)null,
            _=>throw new InvalidOperationException("Nincs kiválasztott DEMO modmód")
        };
        void ShowSelection()
        {
            chosen=list.SelectedItem as PremiumModRow;
            selected.Text=chosen==null?"Válassz modot":chosen.DisplayName;
            details.Text=chosen==null?"Jelölj ki egy modot az állapotválasztáshoz.":
                chosen.State+" · DEMO konfiguráció. Minden változás csak memóriában marad.";
            if(chosen!=null)mode.SelectedItem=chosen.State;
        }
        void Render()
        {
            string oldId=chosen?.Id??"";
            var all=staged.Select((m,i)=>new PremiumModRow(m.ProjectId,ModeName(m.Mode),i+1,
                map.Mods.SequenceEqual(staged)?"DEMO mintamod":"Függőben lévő szimulált módosítás") {
                    DisplayTitle=m.ProjectId switch {
                        "928548"=>"Shiny! Dinos · DEMO",
                        "942024"=>"Dino Depot · DEMO",
                        "950914"=>"Awesome Teleporters · DEMO",
                        _=>"Modprojekt "+m.ProjectId+" · DEMO"
                    },
                    ToggleCommand=new PremiumUiCommand(()=>Attempt(()=>Stage(m.ProjectId,
                        m.Mode==DemoModMode.Active?DemoModMode.Disabled:DemoModMode.Active)))
                }).ToArray();
            string term=search.Text.Trim();
            string selectedFilter=filter.SelectedItem as string??"Összes";
            var visible=all.Where(x=>
                (term.Length==0||x.Id.Contains(term,StringComparison.OrdinalIgnoreCase)||
                 x.DisplayName.Contains(term,StringComparison.OrdinalIgnoreCase)||
                 x.State.Contains(term,StringComparison.OrdinalIgnoreCase)) &&
                (selectedFilter=="Összes"||x.State==selectedFilter)).ToArray();
            list.ItemsSource=visible;
            list.SelectedItem=visible.FirstOrDefault(x=>x.Id==oldId)
                ??visible.FirstOrDefault();
            summary.Text=map.Name+" · "+all.Length+" mintamod · "+visible.Length+" megjelenítve";
            result.Text=map.Mods.SequenceEqual(staged)?
                "[DEMO] Nincs függőben lévő változás.":
                "[DEMO] "+cursor+" előkészített lépés – még csak a képernyőn látható.";
            if(undoButton!=null)undoButton.IsEnabled=cursor>0;
            if(redoButton!=null)redoButton.IsEnabled=cursor<timeline.Count-1;
            if(applyButton!=null)applyButton.IsEnabled=!map.Mods.SequenceEqual(staged);
            ShowSelection();
        }
        void Stage(string id,DemoModMode? target)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(id??"",
                @"\A[0-9]{5,12}\z"))
                throw new InvalidOperationException("5–12 számjegyű modazonosító szükséges.");
            if(target.HasValue&&staged.Any(m=>m.ProjectId==id&&m.Mode==target.Value))
            {
                preview.Text="[DEMO] Ez a mod már a kiválasztott állapotban van.";
                return;
            }
            staged.RemoveAll(m=>m.ProjectId==id);
            if(target.HasValue)staged.Add(new DemoMod(id,target.Value));
            Checkpoint();
            preview.Text="[DEMO] Előnézet mentés nélkül. Eltűnik a DEMO elhagyásakor.";
            Render();
        }
        void Move(int delta)
        {
            if(chosen==null)throw new InvalidOperationException("Előbb válassz modot.");
            var at=staged.FindIndex(x=>x.ProjectId==chosen.Id);
            var to=at+delta;
            if(at<0||to<0||to>=staged.Count)return;
            (staged[at],staged[to])=(staged[to],staged[at]);
            Checkpoint();
            Render();
        }
        void Attempt(Action action)
        {
            try {action();}
            catch(Exception ex){result.Text="[DEMO] Elutasítva: "+UntamedPresentation.Redact(ex.Message);}
        }
        list.SelectionChanged+=(_,_)=>ShowSelection();
        search.TextChanged+=(_,_)=>Attempt(Render);
        filter.SelectionChanged+=(_,_)=>Attempt(Render);

        var leftToolbar=new WrapPanel {Margin=new Thickness(0,0,0,7)};
        leftToolbar.Children.Add(search);
        leftToolbar.Children.Add(filter);
        leftToolbar.Children.Add(project);
        leftToolbar.Children.Add(Btn("＋ Mod hozzáadása",()=>Attempt(()=>
            Stage(project.Text.Trim(),DemoModMode.Active))));
        var left=V(Label("MOD CENTER  /  KONFIGURÁLT MODOK",16,Mint,true),
            Label("DEMO mintamodok · sorrend · állapot · jóváhagyott szimulált mentés",12,Soft),
            summary,list);
        var right=V(Label("Mod részletei",17,Mint,true),selected,details,
            Label("Csak DEMO · nincs valós fájl vagy szerverkapcsolat",11,Soft),
            Label("Állapot",12,Aqua),mode,
            Btn("Állapot módosítása",()=>Attempt(()=>{
                if(chosen==null)throw new InvalidOperationException("Válassz modot.");
                Stage(chosen.Id,ParseMode());
            })),
            Btn("↑  Előrébb a sorrendben",()=>Attempt(()=>Move(-1))),
            Btn("↓  Hátrébb a sorrendben",()=>Attempt(()=>Move(1))));
        var grid=new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(7,GridUnitType.Star),MinWidth=340});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(3,GridUnitType.Star),MinWidth=225});
        var leftBox=Box(left);var rightBox=Box(right);
        Grid.SetColumn(rightBox,1);
        grid.Children.Add(leftBox);grid.Children.Add(rightBox);

        var actions=new WrapPanel {Margin=new Thickness(0,8,0,7)};
        actions.Children.Add(Btn("Változások előnézete",()=>{
            preview.Text="[DEMO] Előtte: "+map.Mods.Count+
                " mod · Utána: "+staged.Count+" mod\n"+
                string.Join(" · ",staged.Select(m=>m.ProjectId+" ("+ModeName(m.Mode)+")"))+
                "\nAz ASA, Store és SQL nincs csatlakoztatva.";
        }));
        undoButton=Btn("↶  Visszavonás (DEMO)",()=>Attempt(()=>{
            if(cursor==0)throw new InvalidOperationException("Nincs visszavonható lépés.");
            cursor--;_demoModCursor[map.Id]=cursor;
            staged=timeline[cursor].ToList();Render();
        }));
        redoButton=Btn("↷  Újra (DEMO)",()=>Attempt(()=>{
            if(cursor>=timeline.Count-1)throw new InvalidOperationException("Nincs újraalkalmazható lépés.");
            cursor++;_demoModCursor[map.Id]=cursor;
            staged=timeline[cursor].ToList();Render();
        }));
        actions.Children.Add(undoButton);
        actions.Children.Add(redoButton);
        actions.Children.Add(Btn("Módosítások elvetése",()=>{
            ResetHistory();preview.Text="[DEMO] Módosítások elvetve.";Render();
        }));
        var apply=Btn("Módosítások alkalmazása (DEMO)",()=>RunDemo(()=>{
            demo.ApplyModList(map.Id,staged.ToArray());
            ResetHistory();
        },result,"Modlista alkalmazva a DEMO memóriában",()=>{
            preview.Text="[DEMO] Alkalmazva – az éles konfiguráció érintetlen.";
            Render();
        }));
        apply.Style=(Style)System.Windows.Application.Current.FindResource("PrimaryAction");
        applyButton=apply;
        actions.Children.Add(apply);
        Render();
        return V(Box(leftToolbar),grid,Box(V(Label("Függőben lévő változások",16,Mint,true),
            result,actions,preview)));
    }

    private UIElement DemoPluginsPane(DemoMap map)
    {
        var demo=_demo!;
        var search=new TextBox{Width=205,Height=36,
            ToolTip="DEMO plugin keresése név alapján"};
        var filter=new ComboBox{ItemsSource=new[]{"Összes","Engedélyezett","Letiltott"},
            SelectedIndex=0,MinWidth=135,Margin=new Thickness(8,0,0,0)};
        var list=new ListView{
            MinHeight=260,MaxHeight=510,
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("PremiumPluginRow")
        };
        var summary=Label("",12,Soft);
        var title=Label("Válassz mintaplugint",18,Mint,true);
        var detail=Label("A DEMO csak memóriában módosít adatokat. Nincs pluginfájl vagy ASA-folyamat.",12,Soft);
        var status=Label("DEMO · Fiktív ASA API-verzió: "+map.ApiVersion,12,Soft);
        PremiumPluginRow? chosen=null;
        Button enable=null!,disable=null!;
        void Render()
        {
            string selected=chosen?.Name??(list.SelectedItem as PremiumPluginRow)?.Name??"";
            string term=search.Text.Trim();
            string state=filter.SelectedItem as string??"Összes";
            var all=map.Plugins.Select(p=>new PremiumPluginRow(
                p.Name,p.Enabled?"Engedélyezett":"Letiltott",
                "DEMO · csak szimulált pluginállapot",p.Enabled) {
                    ToggleCommand=new PremiumUiCommand(()=>{
                        list.SelectedItem=list.Items.OfType<PremiumPluginRow>()
                            .FirstOrDefault(x=>x.Name==p.Name);
                        Toggle(!p.Enabled);
                    })
                }).ToArray();
            var visible=all.Where(x=>
                (term.Length==0||x.Name.Contains(term,StringComparison.OrdinalIgnoreCase)) &&
                (state=="Összes"||(state=="Engedélyezett"&&x.Enabled)||
                 (state=="Letiltott"&&!x.Enabled))).ToArray();
            list.ItemsSource=visible;
            list.SelectedItem=visible.FirstOrDefault(x=>x.Name==selected)
                ??visible.FirstOrDefault();
            summary.Text=map.Name+" · "+all.Length+" mintaplugin · "+
                all.Count(x=>x.Enabled)+" engedélyezett · "+
                all.Count(x=>!x.Enabled)+" letiltott";
            ShowSelection();
        }
        void ShowSelection()
        {
            chosen=list.SelectedItem as PremiumPluginRow;
            title.Text=chosen?.Name??"Válassz mintaplugint";
            detail.Text=chosen==null?
                "Válassz plugint a DEMO állapotváltáshoz.":
                chosen.State+" · kizárólag a szimulált adat változik.\n"+
                "DLL, licenc, SQL, valódi ASA API és fájlrendszer NEM érintett.";
            enable.IsEnabled=chosen!=null&&!chosen.Enabled;
            disable.IsEnabled=chosen!=null&&chosen.Enabled;
        }
        void Toggle(bool enabling)
        {
            if(chosen==null){status.Text="DEMO · Előbb válassz plugint.";return;}
            var name=chosen.Name;
            var current=map.Plugins.FirstOrDefault(p=>p.Name==name);
            if(current==null){status.Text="DEMO · A mintaplugin már nem található.";return;}
            if(current.Enabled==enabling){Render();return;}
            RunDemo(()=>demo.TogglePlugin(map.Id,name),status,
                name+(enabling?" bekapcsolva (DEMO)":" kikapcsolva (DEMO)"),Render);
        }
        enable=Btn("Bekapcsolás (DEMO)",()=>Toggle(true));
        disable=Btn("Kikapcsolás (DEMO)",()=>Toggle(false));
        list.SelectionChanged+=(_,_)=>ShowSelection();
        search.TextChanged+=(_,_)=>Render();
        filter.SelectionChanged+=(_,_)=>Render();
        var toolbar=new WrapPanel{Margin=new Thickness(0,4,0,10)};
        toolbar.Children.Add(search);
        toolbar.Children.Add(filter);
        var left=Box(V(Label("PLUGIN CENTER  /  ASA API",16,Mint,true),
            Label("DEMO mintapluginok · a kapcsolók kizárólag memóriát módosítanak",12,Soft),
            summary,list));
        var commands=new WrapPanel{Margin=new Thickness(0,3,0,8)};
        commands.Children.Add(enable);
        commands.Children.Add(disable);
        var right=Box(V(Label("Plugin részletei",18,Mint,true),
            title,detail,commands,
            Label("DEMO-ban nincs tényleges DLL- vagy fájlművelet.",11,Soft)));
        var layout=new Grid {Margin=new Thickness(0,0,0,5)};
        layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(7,GridUnitType.Star),MinWidth=340});
        layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(3,GridUnitType.Star),MinWidth=225});
        Grid.SetColumn(right,1);
        layout.Children.Add(left);layout.Children.Add(right);
        Render();
        return V(Box(toolbar),layout,Box(V(Label("Visszajelzés",14,Mint,true),status)));
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
        var list=new ListBox{MinHeight=155,MaxHeight=300,Background=Panel,Foreground=Mint};
        var status=Label("Mintakatalógus: nincs mentett állomány.",12,Soft);
        void Refresh()=>list.ItemsSource=map.Backups.ToArray();
        Refresh();
        return V(Label("BACKUP CENTER  /  DEMO",17,Mint,true),
            Label("A mintamentés kizárólag memóriabeli mod-, plugin-, INI- és állapotpillanatkép. Nem ír valódi állományt, Store-t vagy SQL-t.",12,Soft),
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
