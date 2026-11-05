using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UntamedManager.Core;
using WinMessageBox = System.Windows.MessageBox;

namespace UntamedManager;

internal sealed record PremiumLocalServerRow(LocalMap Map,string Name,string State,string Detail)
{
    public ImageSource? CoverImage { get; init; }
}

public partial class MainWindow
{
    private readonly ClusterService _cluster = new(App.ProfileName,App.ProfileRootOverride);
    private readonly CancellationTokenSource _peerCts = new();
    private Task? _peerLoop;
    private static readonly string[] KnownMaps = {
        "Astraeos","Genesis Part 1","Valguero","The Island","Extinction",
        "Scorched Earth","Lost Colony","Ragnarok","The Center","Aberration"
    };
    private void StartClusterUi()
    {
        // No automatic map discovery or import. Only local maps explicitly saved by the user.
        RefreshDashboardMapCards();
        if (!App.IsSmokeTest && !App.IsSupervisionTest && !App.IsDemoStart) _ = BeginClusterNetworkingAsync();
    }
    private async Task BeginClusterNetworkingAsync()
    {
        try { await _cluster.BeginLanAsync(); }
        catch (Exception ex) { WriteLog("LAN-listener",ex); }
        _peerLoop=Task.Run(async ()=>{
            while(!_peerCts.IsCancellationRequested)
            {
                try {
                    if(_demo!=null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(12),_peerCts.Token);
                        continue;
                    }
                    bool wasFresh=_cluster.RemoteIsFresh;
                    await _cluster.PollPeerAsync(_peerCts.Token);
                    if(wasFresh!=_cluster.RemoteIsFresh)try{_events?.Append(_cluster.Config.LocalHostName,null,_cluster.RemoteIsFresh?"Information":"Warning",_cluster.RemoteIsFresh?"lan-peer-connected":"lan-peer-stale");}catch{}
                    await Dispatcher.InvokeAsync(()=>{
                        RefreshDashboardMapCards();
                        if(_page=="cluster") ShowClusterPage();
                    });
                    await Task.Delay(TimeSpan.FromSeconds(12),_peerCts.Token);
                } catch(OperationCanceledException) { break; }
                catch(Exception ex) {
                    WriteLog("LAN-poll",ex);
                    try{await Task.Delay(TimeSpan.FromSeconds(12),_peerCts.Token);}catch(OperationCanceledException){break;}
                }
            }
        },_peerCts.Token);
    }
    private void EndClusterUi()
    {
        _peerCts.Cancel();
        _ = _cluster.DisposeAsync().AsTask();
    }
    private void RefreshDashboardMapCards()
    {
        if(_demo!=null)return; // keep production observations off the DEMO dashboard
        // Preserve the existing visual template, replacing only the data source and status.
        var images=_maps.ToDictionary(x=>x.Name,StringComparer.OrdinalIgnoreCase);
        var active = new List<MapInfo>();
        int knownModAssignments=0, readableModMaps=0;
        int runningProcesses=0, stoppedProcesses=0, unverifiedProcesses=0;
        foreach(var m in _cluster.Config.LocalMaps)
        {
            images.TryGetValue(m.Name,out var art);
            int? configuredMods=null;
            string status="Állapot ismeretlen";
            try
            {
                var mods=ModStateController.Read(m);
                configuredMods=mods.Active.Count+mods.Passive.Count;
                knownModAssignments+=configuredMods.Value;
                readableModMaps++;
            }
            catch(Exception ex){WriteLog("dashboard-mod-observation",ex);}
            try
            {
                var check=_cluster.Check(m);
                status=check.Status;
                if(check.Running && check.Identity!=null)runningProcesses++;
                else if(check.ConfirmedStopped)stoppedProcesses++;
                else unverifiedProcesses++;
            }
            catch(Exception ex)
            {
                unverifiedProcesses++;
                WriteLog("dashboard-process-observation",ex);
            }
            active.Add(new MapInfo {
                Id=m.Id,Name=m.Name,Host=_cluster.Config.LocalHostName,
                ModCount=configuredMods,GamePort=m.GamePort>0?m.GamePort:null,
                QueryPort=m.QueryPort>0?m.QueryPort:null,RconPort=m.RconPort>0?m.RconPort:null,
                Status=status,
                CoverImage=art?.CoverImage, CoverBrush=art?.CoverBrush ?? Panel,
                IsRemote=false
            });
        }
        if(_cluster.RemoteSnapshot!=null)
        {
            foreach(var m in _cluster.RemoteSnapshot.Maps)
            {
                images.TryGetValue(m.Name,out var art);
                active.Add(new MapInfo {
                    Name=m.Name,Host=_cluster.RemoteSnapshot.HostName,
                    ModCount=null,GamePort=m.GamePort>0?m.GamePort:null,
                    Status=_cluster.RemoteIsFresh?m.Status:"ELAVULT / NEM HITELESÍTHETŐ",
                    CoverImage=art?.CoverImage,
                    CoverBrush=art?.CoverBrush ?? Panel,IsRemote=true,IsFresh=_cluster.RemoteIsFresh
                });
            }
        }
        var remote=_cluster.RemoteSnapshot;
        var summary=new DashboardSummary(_cluster.Config.LocalMaps.Count,
            remote?.Maps.Count,knownModAssignments,readableModMaps,
            remote!=null && _cluster.RemoteIsFresh);
        ServerCards.ItemsSource=active;
        MapListEmpty.Visibility=active.Count==0?Visibility.Visible:Visibility.Collapsed;
        ClusterMapCount.Text=summary.MapCount.ToString();
        RunningServerKpi.Text=runningProcesses.ToString();
        PlayersKpi.Text="—"; // no trusted live player count is available here
        ClusterHostCount.Text=summary.HostCount.ToString();
        ClusterOnlineCount.Text=summary.FreshLanPeers.ToString();
        DashboardOperationsLine.Text="Helyi folyamatok: "+runningProcesses+" igazolt futó · "+
            stoppedProcesses+" igazolt leállított · "+unverifiedProcesses+" bizonytalan. "+
            "Játékosok/RCON: nincs hitelesített aktuális adat · Mentések: ellenőrizd a Mentések oldalon.";
        ConfiguredModCount.Text=summary.ModCountDisplay;
        ModReadingCoverage.Text=summary.ModCoverageDisplay;
        var hosts=new List<HostCardInfo>{
            new() {
                Name=_cluster.Config.LocalHostName,
                Description=summary.LocalMaps+" helyi map · "+App.ProfileName+" profil",
                TrustState="HELYI PROFIL · nincs feltételezett LAN-kapcsolat",
                Accent=Aqua,
                CpuPercent=_latestLocalResource?.CpuPercent,
                RamPercent=_latestLocalResource?.UsedMemoryKiB is ulong used &&
                           _latestLocalResource.TotalMemoryKiB is ulong total && total>0
                           ?Math.Clamp(used*100d/total,0,100):null
            }
        };
        if(remote!=null)hosts.Add(new HostCardInfo {
            Name=remote.HostName,
            Description=remote.Maps.Count+" utoljára megfigyelt távoli map",
            TrustState=_cluster.RemoteIsFresh
                ?"HITelesített LAN · friss, csak olvasás"
                :"ELAVULT LAN-ADAT · nincs élő állapotbizonyíték",
            Accent=_cluster.RemoteIsFresh?Mint:Brushes.Orange
        });
        HostCards.ItemsSource=hosts;
        if(ClusterStatusText!=null) ClusterStatusText.Text=
            $"{_cluster.Config.LocalHostName}: {_cluster.Config.LocalMaps.Count} helyi map · "+
            (_cluster.RemoteSnapshot==null?_cluster.RemoteMessage:
             $"{_cluster.RemoteSnapshot.HostName}: {_cluster.RemoteSnapshot.Maps.Count} távoli map · "+(_cluster.RemoteIsFresh?"friss, csak olvasás":"ELAVULT / OFFLINE"));
    }
    private async void DashboardRefresh_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button button || !button.IsEnabled)return;
        if(_demo!=null){RenderDemoDashboard();return;}
        button.IsEnabled=false;
        try
        {
            // Read-only: no remote lifecycle, Store, SQL, plugin or configuration mutation.
            await _cluster.PollPeerAsync();
        }
        catch(Exception ex)
        {
            WriteLog("dashboard-refresh",ex);
        }
        finally
        {
            RefreshDashboardMapCards();
            button.IsEnabled=true;
        }
    }

    public void VerifyDashboardUiCiSmoke()
    {
        RefreshDashboardMapCards();
        var remote=_cluster.RemoteSnapshot;
        var registered=_cluster.Config.LocalMaps.Count+(remote?.Maps.Count??0);
        if(ServerCards.ItemsSource is not IEnumerable<MapInfo> cards ||
           cards.Count()!=registered ||
           ClusterMapCount.Text!=registered.ToString() ||
           (MapListEmpty.Visibility==Visibility.Visible)!=(registered==0))
            throw new InvalidOperationException("Dashboard map cards do not reflect the registered map inventory");
        int knownHosts=remote==null?1:2;
        if(HostCards.ItemsSource is not IEnumerable<HostCardInfo> hosts ||
           hosts.Count()!=knownHosts || ClusterHostCount.Text!=knownHosts.ToString() ||
           ClusterOnlineCount.Text!=(_cluster.RemoteIsFresh && remote!=null?"1":"0"))
            throw new InvalidOperationException("Dashboard host and LAN counters imply unobserved hosts");
    }

    private void ShowClusterPage()
    {
        Header("Cluster","Helyi mapok kezelése és a párosított LAN-gazdagép megfigyelése. A távoli konfiguráció nem szerkeszthető.");
        var local=_cluster.LocalStatus();
        var hosts=new Grid { Margin=new Thickness(0,0,0,10) };
        hosts.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
        hosts.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
        var localList=new StackPanel { Margin=new Thickness(0,8,0,0) };
        if(local.Maps.Count==0)
            localList.Children.Add(Label("Nincs regisztrált helyi map. A Szerverek oldalon tudsz hozzáadni.",12,Soft));
        foreach(var map in local.Maps)
        {
            var entry=Btn(map.Name+"  ·  "+map.Status+
                "  · Steam "+(map.SteamBuildId??"ismeretlen")+
                "  · "+map.SteamUpdateState,()=>{
                _selected=_maps.FirstOrDefault(x=>x.Name==map.Name);
                Navigate("servers");
            });
            entry.HorizontalAlignment=HorizontalAlignment.Stretch;
            entry.HorizontalContentAlignment=HorizontalAlignment.Left;
            entry.ToolTip="Helyi szerver – részletek megnyitása";
            localList.Children.Add(entry);
        }
        var localInfo=V(Label(local.HostName+" · HELYI",18,Mint,true),
            Label(local.Maps.Count+" regisztrált map · ezen a gépen szerkeszthető",12,Soft),
            localList);
        var left=Box(localInfo);
        Grid.SetColumn(left,0);
        hosts.Children.Add(left);

        var remoteList=new StackPanel { Margin=new Thickness(0,8,0,0) };
        var remote=_cluster.RemoteSnapshot;
        string remoteTitle=remote?.HostName??"LAN-peer nincs csatlakoztatva";
        string remoteStatus=remote==null ? _cluster.RemoteMessage :
            remote.Maps.Count+" ismert távoli map · "+
            ( _cluster.RemoteIsFresh ? "friss, hitelesített mérés" : "elavult / nem hitelesíthető mérés" );
        if(remote?.Maps is { } peerMaps)
            foreach(var map in peerMaps)
            {
                remoteList.Children.Add(Label(map.Name+"  ·  "+
                    (_cluster.RemoteIsFresh?map.Status:"ELAVULT · "+map.Status)+
                    "\nSteam Build: "+
                    (_cluster.RemoteIsFresh?(map.SteamBuildId??"ismeretlen"):"ELAVULT / NEM HITELES")+
                    " · "+(_cluster.RemoteIsFresh?map.SteamUpdateState:"NEM IGAZOLT"),
                    12,_cluster.RemoteIsFresh?Aqua:Soft));
            }
        else remoteList.Children.Add(Label(
            "A távoli szerverek adatai csak hitelesített peer-kapcsolatból származhatnak.",12,Soft));
        var remoteInfo=V(Label(remoteTitle+" · CSAK OLVASÁS",18,Aqua,true),
            Label(remoteStatus,12,Soft),remoteList);
        var right=Box(remoteInfo);
        Grid.SetColumn(right,1);
        hosts.Children.Add(right);
        Add(hosts);

        var actions=new WrapPanel { Margin=new Thickness(0,4,0,0) };
        actions.Children.Add(Btn("Peer állapot frissítése",async()=>{
            await _cluster.PollPeerAsync();
            RefreshDashboardMapCards();
            if(_page=="cluster")ShowClusterPage();
        }));
        actions.Children.Add(Btn("Helyi szerverek",()=>Navigate("servers")));
        actions.Children.Add(Btn("LAN beállítások",()=>Navigate("lan")));
        Add(Box(V(Label("Cluster kapcsolat",16,Mint,true),
            Label("A LAN-peer soha nem kap helyi íróműveletet ezen a nézeten.",12,Soft),
            actions)));
    }

    private void ShowLocalServerPage()
    {
        Header("Szerverek","Helyi ASA mapok · aktuálisan megfigyelt folyamatazonosság és maponkénti kezelés.");
        var rows=new List<PremiumLocalServerRow>();
        foreach(var map in _cluster.Config.LocalMaps)
        {
            string state;
            try
            {
                var check=_cluster.Check(map);
                state=check.Running&&check.Identity!=null?
                    "Igazolt futó":check.ConfirmedStopped?"Leállítva":"Nem igazolt";
            }
            catch(Exception ex)
            {
                WriteLog("server-list-observation",ex);
                state="Állapot ismeretlen";
            }
            var mapArtwork=_maps.FirstOrDefault(x=>
                x.Name.Equals(map.Name,StringComparison.OrdinalIgnoreCase))?.CoverImage;
            rows.Add(new PremiumLocalServerRow(map,map.Name,state,
                "Helyi konfiguráció · "+_cluster.Config.LocalHostName){CoverImage=mapArtwork});
        }
        var count=Label(rows.Count+" helyi map · "+_cluster.Config.LocalHostName+
            " · "+App.ProfileName+" profil",12,Soft);
        var toolbar=new WrapPanel{Margin=new Thickness(0,2,0,4)};
        toolbar.Children.Add(Btn("+ Map hozzáadása",()=>ShowMapEditor(null)));
        toolbar.Children.Add(Btn("Cluster nézet",()=>Navigate("cluster")));
        var header=Box(V(Label("Helyi szerverek",19,Mint,true),
            count,
            Label("A távoli mapok itt csak megfigyelhetők a Cluster nézetben. Az ismeretlen állapot nem számít leállítottnak.",12,Soft),
            toolbar));
        Add(header);
        if(rows.Count==0)
        {
            Add(Box(V(Label("Még nincs regisztrált map",17,Mint,true),
                Label("Az első mapot manuálisan add hozzá. A program nem importál vagy indít automatikusan szervereket.",12,Soft))));
            return;
        }
        var search=new TextBox {Width=208,Height=36,
            Tag="Map keresése",
            ToolTip="Keresés a regisztrált mapok nevére"};
        var list=new ListBox {
            MinHeight=165,MaxHeight=480,Margin=new Thickness(0,8,0,0),
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("PremiumServerRow")
        };
        NestedWheelScroll.SetChainToParent(list,true);
        var sidebar=Box(V(Label("Maplista",17,Mint,true),search,list));
        var details=new StackPanel();
        var detailSurface=Box(details);
        var layout=new Grid {Margin=new Thickness(0,1,0,8)};
        layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(265)});
        layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        Grid.SetColumn(sidebar,0);Grid.SetColumn(detailSurface,1);
        layout.Children.Add(sidebar);layout.Children.Add(detailSurface);
        BindResponsiveServerDetails(layout,sidebar,detailSurface);
        void Filter()
        {
            string id=(list.SelectedItem as PremiumLocalServerRow)?.Map.Id.ToString("N")
                ??_selected?.Id.ToString("N")??"";
            var term=search.Text.Trim();
            var visible=rows.Where(x=>term.Length==0||
                x.Name.Contains(term,StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource=visible;
            list.SelectedItem=visible.FirstOrDefault(x=>x.Map.Id.ToString("N")==id)
                ??visible.FirstOrDefault();
        }
        list.SelectionChanged+=(_,_)=>{
            details.Children.Clear();
            if(list.SelectedItem is PremiumLocalServerRow selected)
            {
                _selected=_maps.FirstOrDefault(x=>x.Name.Equals(selected.Name,StringComparison.OrdinalIgnoreCase))
                    ??new MapInfo{Id=selected.Map.Id,Name=selected.Name};
                DisplayLocalMapDetails(details,selected.Map);
            }
            else details.Children.Add(Label("A maplista szűrője nem adott találatot.",13,Soft));
        };
        search.TextChanged+=(_,_)=>Filter();
        Add(layout);
        Filter();
    }
    private void DisplayLocalMapDetails(StackPanel parent,LocalMap map)
    {
        var result=_cluster.Check(map);
        // Reuse the dashboard's original map artwork on the LIVE server-detail page.
        // No invented status or fake process telemetry is drawn over this image.
        var mapArtwork=_maps.FirstOrDefault(x=>
            x.Name.Equals(map.Name,StringComparison.OrdinalIgnoreCase))?.CoverImage;
        var cover=new Grid{Height=168,ClipToBounds=true};
        if(mapArtwork is not null)
            cover.Children.Add(new Image{Source=mapArtwork,Stretch=Stretch.UniformToFill,
                Opacity=0.81,IsHitTestVisible=false});
        var shade=new LinearGradientBrush{
            StartPoint=new Point(0,0.5),EndPoint=new Point(1,0.5)
        };
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(242,10,25,28),0));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(175,12,35,36),0.62));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(112,13,28,31),1));
        cover.Children.Add(new System.Windows.Shapes.Rectangle{
            Fill=shade,IsHitTestVisible=false});
        var heading=new StackPanel{Margin=new Thickness(21,17,21,17),
            VerticalAlignment=VerticalAlignment.Center};
        heading.Children.Add(Label("ASA  /  LOCAL MAP COMMAND",11,Aqua,true));
        heading.Children.Add(Label(map.Name,27,Mint,true));
        heading.Children.Add(Label(_cluster.Config.LocalHostName+"  •  "+
            result.Status,12,result.Running?Aqua:Soft));
        cover.Children.Add(heading);
        parent.Children.Add(new Border{
            Background=Panel,BorderBrush=Aqua,BorderThickness=new Thickness(1),
            CornerRadius=new CornerRadius(12),ClipToBounds=true,
            Child=cover,Margin=new Thickness(0,0,12,17)
        });
        // Identity, ports and configuration are distinct from live process/API readiness.
        var indicators=new WrapPanel {Margin=new Thickness(0,3,0,12)};
        Border Metric(string caption,string value)
        {
            return new Border {
                Background=new SolidColorBrush(Color.FromRgb(36,47,53)),
                BorderBrush=new SolidColorBrush(Color.FromRgb(53,64,72)),
                BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(7),
                Padding=new Thickness(11,8,11,8),Margin=new Thickness(0,0,9,7),
                Child=V(Label(caption,10,Soft,true),Label(value,14,Mint,true))
            };
        }
        indicators.Children.Add(Metric("GAME / UDP",map.GamePort.ToString()));
        indicators.Children.Add(Metric("QUERY / UDP",map.QueryPort.ToString()));
        indicators.Children.Add(Metric("RCON / TCP (KONFIG.)",map.RconPort.ToString()));
        indicators.Children.Add(Metric("ASA API / KONFIG.",map.StartWithAsaApi?"BE":"KI"));
        parent.Children.Add(indicators);
        // Wrap long actions instead of clipping them on 1366px / high-DPI desktops.
        var controls=new WrapPanel{Margin=new Thickness(0,0,0,10)};
        controls.Children.Add(Btn("Beállítások szerkesztése",()=>ShowMapEditor(map)));
        controls.Children.Add(Btn("Eltávolítás a Managerből",()=>{
            if(WinMessageBox.Show($"Eltávolítod ezt a mapot a listából? A játékfájlok és a futó szerver változatlan marad.\n{map.Name}",
                "Map eltávolítása",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            { _cluster.RemoveMap(map.Id);RefreshDashboardMapCards();ShowLocalServerPage(); }
        }));
        var quick=new WrapPanel{Margin=new Thickness(0,1,0,9)};
        quick.Children.Add(Btn("Mod Center",()=>{
            _preferredModMapId=map.Id;Navigate("mods");
        }));
        quick.Children.Add(Btn("Plugin Center",()=>{
            _preferredPluginMapId=map.Id;Navigate("plugins");
        }));
        quick.Children.Add(Btn("Mentések",()=>{
            _preferredBackupMapId=map.Id;
            Navigate("backups");
        }));
        parent.Children.Add(quick);
        parent.Children.Add(controls);
        var tabs=new TabControl { Margin=new Thickness(0,8,0,0),
            Background=Panel,Foreground=Brushes.White,BorderBrush=Aqua };
        void Tab(string title,UIElement body)
        {
            var viewer=new ScrollViewer {
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=body,
                Padding=new Thickness(12)
            };
            NestedWheelScroll.SetChainToParent(viewer,true);
            tabs.Items.Add(new TabItem{Header=title,Content=viewer});
        }
        var startStop=new WrapPanel{Margin=new Thickness(0,4,0,8)};
        var readiness=Label("ASA / Loader / RCON Ready: még nincs ellenőrizve. API és csatlakozás külön igazolandó.",12,Soft);
        startStop.Children.Add(Btn("Indítás (csak FakeAsa teszt)",()=>{
            try{
                ReleaseCapabilityGate.DemandFakeAsaStart(_cluster,map);
                if(WinMessageBox.Show($"Elindítod az elkülönített FakeAsa tesztfolyamatot?\n{map.Name}",
                    "FakeAsa teszt indítása",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                // Check once more immediately before launch; ClusterService also rechecks
                // exact process identity while holding the launch mutex.
                ReleaseCapabilityGate.DemandFakeAsaStart(_cluster,map);
                _cluster.LaunchLocal(map.Id);
                Notice("Csak FakeAsa tesztfolyamat indult. ASA éles indítás nincs engedélyezve.");
                ShowLocalServerPage();
            } catch(Exception e){WriteLog("launch-preview",e);Notice("Indítás tiltva: "+e.Message);}
        }));
        startStop.Children.Add(Btn("Státusz ellenőrzése",()=>{
            var x=_cluster.Check(map);
            Notice(x.Status);
        }));
        bool readinessBusy=false;
        startStop.Children.Add(Btn("ASA Ready vizsgálat",async()=>{
            if(readinessBusy)return;
            readinessBusy=true;
            readiness.Text="ASA/Loader folyamat és helyi RCON ellenőrzés...";
            try
            {
                var result=await AsaReadyProbe.ProbeAsync(_cluster,map.Id);
                readiness.Text="ASA Ready: "+result.Stage+" · "+result.Explanation+
                    " · API initialized: "+(result.ApiInitialized?"IGEN":"NEM IGAZOLT")+
                    " · JOIN: "+(result.JoinAdvertised?"IGEN":"NEM IGAZOLT");
            }
            catch(Exception ex)
            {
                WriteLog("asa-readiness",ex);
                readiness.Text="ASA Ready vizsgálat sikertelen. Hitelesítési adat nem jeleníthető meg.";
            }
            finally{readinessBusy=false;}
        }));
        startStop.Children.Add(Btn("Folyamatazonosság rögzítése",()=>{
            try
            {
                var observed=_cluster.Check(map);
                bool changed=_cluster.RecordObservedIdentity(map.Id,observed);
                Notice(changed
                    ? "Ellenőrzött folyamatazonosság rögzítve a helyi profilban."
                    : "A folyamatazonosság változatlan; nem történt konfigurációírás.");
            }
            catch(Exception ex)
            {
                WriteLog("process-identity-capture",ex);
                Notice("Az azonosító rögzítése elutasítva. Ellenőrizd a folyamatot és a műveleti naplót.");
            }
        }));
        var stopUnavailable=Btn("Stop / Restart – mentésigazolás hiányzik",()=>{});
        stopUnavailable.IsEnabled=false;
        stopUnavailable.ToolTip="Az ASA mentés befejezését igazoló adapter még nincs validálva. RCON-válasz önmagában nem elég.";
        startStop.Children.Add(stopUnavailable);
        Tab("Áttekintés",V(
            Label("Végrehajtható fájl: "+map.ExecutablePath,12,Soft),
            Label("Indítási argumentumok: "+map.Arguments,12,Soft),
            Label("Indítás ASA API-val: "+(map.StartWithAsaApi?"BE (előfeltételek ellenőrzendők)":"KI"),12,Aqua),
            Label($"Game {map.GamePort} / Query {map.QueryPort} / RCON {map.RconPort}",12,Aqua),
            Label(result.Running?"Futó szerver visszacsatlakoztatva":result.ConfirmedStopped?
                "Leállítva; automatikus indítás nincs":"Ismeretlen folyamatállapot; indítás tiltva",13,result.Running?Mint:Soft),
            startStop,readiness));
        Tab("Modok / CurseForge",MapModPane(map));
        Tab("Pluginok / ASA API",MapPluginPane(map));
        Tab("INI",MapIniPane(map));
        Tab("RCON / Konzol",MapRconPane(map));
        Tab("Mentések",MapBackupPane(map));
        Tab("Naplók",MapLogPane(map));
        Tab("Steam / Frissítések",SteamMapVersionPane(map));
        parent.Children.Add(tabs);
    }
    private UIElement MapModPane(LocalMap map)
    {
        var list=new ListBox{MinHeight=90,MaxHeight=260,Background=Panel,Foreground=Brushes.White};
        var status=Label("Helyi modparaméterek ellenőrzése...",12,Soft);
        void Refresh()
        {
            try
            {
                var set=ModStateController.Read(map);
                var rows=set.Active.Select((id,i)=>"ACTIVE  "+(i+1)+".  "+id)
                    .Concat(set.Passive.Select((id,i)=>"PASSIVE  "+(i+1)+".  "+id))
                    .Concat(set.Disabled.Select((id,i)=>"DISABLED  "+(i+1)+".  "+id))
                    .ToArray();
                RefreshModList(list,rows.Length==0?new[]{"Nincs konfigurált mod ezen a mapon."}:rows);
                status.Text=map.Name+" · "+set.Active.Count+" aktív · "+
                    set.Passive.Count+" passzív · "+set.Disabled.Count+" letiltott"+
                    (set.Pending?" · INDÍTÁSKOR ÉRVÉNYESÜLŐ VÁLTOZÁS":"");
            }
            catch(Exception ex)
            {
                WriteLog("map-mod-list",ex);
                list.ItemsSource=Array.Empty<string>();
                status.Text="A modlista nem olvasható. Ellenőrizd a helyi indítási paramétereket.";
            }
        }
        var view=V(
            Label("Helyi map · valódi -mods és -passivemods paraméterek",17,Mint,true),
            Label("Ez nem történeti auditlista. Csak a kiválasztott helyi map konfigurációja látszik; nincs automatikus restart.",12,Soft),
            Btn("Modlista újraolvasása",Refresh),
            Btn("Mod Center – ennek a mapnak a szerkesztése",()=>{
                _preferredModMapId=map.Id;
                Navigate("mods");
            }),list,status);
        Refresh();return view;
    }

    private UIElement MapPluginPane(LocalMap map)
    {
        var list=new ListBox{MinHeight=90,MaxHeight=260,Background=Panel,Foreground=Brushes.White};
        var status=Label("Helyi ASA API és pluginok ellenőrzése...",12,Soft);
        void Refresh()
        {
            try
            {
                var observed=_cluster.Check(map);
                var api=AsaApiInspector.Inspect(map.InstallationPath,observed);
                var items=PluginStateManager.Inventory(map);
                list.ItemsSource=items.Count==0
                    ?new[]{"Nincs megfigyelhető pluginmappa ezen a telepítésen."}
                    :items.Select(x=>(x.Enabled?"AKTÍV  ":"LETILTVA  ")+x.Name+
                        " · lemezállapot, futásidejű betöltés nem igazolt").ToArray();
                status.Text="ASA API fájlok: "+(api.Complete?"ellenőrizhetők":"hiányosak")+
                    " · loader beállítás: "+(map.StartWithAsaApi?"BE":"KI")+
                    " · "+items.Count+" helyi plugin könyvtár";
            }
            catch(Exception ex)
            {
                WriteLog("map-plugin-list",ex);
                list.ItemsSource=Array.Empty<string>();
                status.Text="A helyi ASA API/pluginleltár nem olvasható; a szerver futása nem következtethető a fájlokból.";
            }
        }
        var view=V(
            Label("Helyi map · ASA API és pluginleltár",17,Mint,true),
            Label("A lista tényleges telepítési könyvtárakból készül. A DLL jelenléte nem igazolja az ASA API runtime-betöltését.",12,Soft),
            Btn("Indítás ASA API-val – beállítás szerkesztése",()=>ShowMapEditor(map)),
            Btn("API és pluginleltár újraolvasása",Refresh),
            Btn("Plugin Center – ennek a mapnak a kezelése",()=>{
                _preferredPluginMapId=map.Id;
                Navigate("plugins");
            }),list,status);
        Refresh();return view;
    }

    private UIElement MapIniPane(LocalMap map)
    {
        var choice=new ComboBox {
            ItemsSource=new[]{"Game.ini","GameUserSettings.ini"},SelectedIndex=0,
            Margin=new Thickness(0,6,0,9),Foreground=Brushes.White,Background=Panel
        };
        var editor=new TextBox {
            Height=260,AcceptsReturn=true,AcceptsTab=true,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            FontFamily=new FontFamily("Consolas"),TextWrapping=TextWrapping.NoWrap,
            Foreground=Brushes.White,Background=Panel,IsEnabled=false
        };
        var status=Label("Az INI szerkesztéshez igazoltan leállított helyi map kell. "+
            "Kötelező az eredeti fájl hash-ellenőrzése és védőmentése.",12,Soft);
        ConfigSnapshot? baseline=null;
        void Reset()
        {
            baseline=null;editor.Text="";editor.IsEnabled=false;
            status.Text="Új fájl kiválasztva: nyisd meg a biztonságos szerkesztőben.";
        }
        choice.SelectionChanged+=(_,_)=>Reset();
        void EnsureLocal()
        {
            if(!_cluster.Config.LocalMaps.Contains(map))
                throw new UnauthorizedAccessException("Only the owned local map can be edited");
        }
        string SelectedFile()
        {
            if(choice.SelectedItem is not string name ||
               name is not ("Game.ini" or "GameUserSettings.ini"))
                throw new InvalidDataException("Válassz támogatott helyi INI fájlt");
            return Path.Combine(map.InstallationPath??"",
                "ShooterGame","Saved","Config","WindowsServer",name);
        }
        var load=Btn("INI biztonságos megnyitása",()=>{
            try
            {
                EnsureLocal();
                baseline=SafeConfigEditor.Read(SelectedFile(),map.InstallationPath,
                    ConfigKind.GameIni,_cluster.Check(map),map.Maintenance);
                editor.Text=baseline.Text;editor.IsEnabled=true;
                status.Text="Fájl: "+Path.GetFileName(baseline.Path)+
                    " · SHA-256: "+baseline.Sha256+
                    " · Kódolás: "+baseline.EncodingId+
                    " · BOM: "+baseline.HasBom+
                    "\nCsak a szerkesztőben módosult; lemezen még változatlan.";
            }
            catch(Exception ex)
            {
                WriteLog("map-ini-read",ex);
                baseline=null;editor.Text="";editor.IsEnabled=false;
                status.Text="INI megnyitás megtagadva: "+ex.Message;
            }
        });
        var preview=Btn("INI változásainak ellenőrzése",()=>{
            try
            {
                EnsureLocal();
                if(baseline==null)throw new InvalidOperationException("Előbb nyisd meg a helyi INI fájlt");
                var check=SafeConfigEditor.Preview(baseline,editor.Text);
                status.Text=(check.Changed?"MÓDOSULT":"VÁLTOZATLAN")+
                    " · "+check.OldLines+" → "+check.NewLines+" sor"+
                    "\nEredeti SHA-256: "+check.OldSha256+
                    "\nTervezett SHA-256: "+check.NewSha256+
                    "\nA fájl még nincs mentve.";
            }
            catch(Exception ex){status.Text="INI előnézet sikertelen: "+ex.Message;}
        });
        var save=Btn("Ellenőrzött INI mentése és .bak védőmásolat",()=>{
            try
            {
                EnsureLocal();
                if(baseline==null || !string.Equals(
                    baseline.Path,Path.GetFullPath(SelectedFile()),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Újra meg kell nyitnod a kiválasztott INI fájlt");
                var check=SafeConfigEditor.Preview(baseline,editor.Text);
                if(!check.Changed){status.Text="Az INI változatlan; mentés nem szükséges.";return;}
                if(WinMessageBox.Show(map.Name+" / "+Path.GetFileName(baseline.Path)+
                    "\nA fájl csak igazoltan leállított mapon módosítható. "+
                    "Ellenőrzött védőmásolat készül, a restart külön művelet. Folytatod?",
                    "INI mentése",MessageBoxButton.YesNo,
                    MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                string backup=SafeConfigEditor.Save(baseline,editor.Text,
                    map.InstallationPath,_cluster.Check(map),map.Maintenance,
                    ()=>_cluster.Check(map));
                baseline=SafeConfigEditor.Read(baseline.Path,map.InstallationPath,
                    ConfigKind.GameIni,_cluster.Check(map),map.Maintenance);
                editor.Text=baseline.Text;
                status.Text="INI ellenőrzötten mentve. Védőmásolat: "+backup+
                    "\nÚj SHA-256: "+baseline.Sha256+
                    "\nNem történt automatikus szerver-újraindítás.";
            }
            catch(Exception ex)
            {
                WriteLog("map-ini-save",ex);
                status.Text="INI mentés megtagadva: "+ex.Message;
            }
        });
        return V(Label("Helyi map INI · SHA-256 / ETag / atomikus csere / .bak",16,Mint,true),
            Label("A megszakított vagy kívülről módosított fájlt a rendszer nem írja felül. "+
                "A Manager a karakter- és Store-fájlokat nem szerkeszti.",12,Soft),
            choice,load,preview,save,status,editor);
    }
    private static bool IsUnderRoot(string path,string root)
    {
        if(string.IsNullOrWhiteSpace(root))return false;
        string parent=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string full=Path.GetFullPath(path);
        return full.StartsWith(parent,StringComparison.OrdinalIgnoreCase) &&
            !full.Contains(VaultProtocol.ClusterId,StringComparison.OrdinalIgnoreCase);
    }
    private UIElement MapRconPane(LocalMap map)
    {
        var feedback=Label("A kapcsolat manuálisan indítható. RCON csak a helyi maphoz.",12,Soft);
        var command=new TextBox{Text="ListPlayers",Margin=new Thickness(0,8,0,10),Height=38};
        return V(Label($"RCON: {map.RconHost}:{map.RconPort}",16,Aqua,true),
            command,Btn("Parancs küldése",async()=>{
                try{
                    var password=SecureStore.Unprotect(map.RconPasswordProtected);
                    if(string.IsNullOrEmpty(password))throw new InvalidOperationException("RCON jelszó nincs megadva");
                    if(!_cluster.Config.LocalMaps.Contains(map))
                        throw new UnauthorizedAccessException("Only local Manager map allowed");
                    if(command.Text.Trim()!="ListPlayers")
                        throw new InvalidOperationException("Release Candidate: only ListPlayers is allowed; lifecycle/administrative RCON disabled");
                    var state=_cluster.Check(map);
                    if(!ReleaseCapabilityGate.Check(ManagerOperation.LocalReadOnlyRcon,
                        true,map.Maintenance,false,state.Running).Allowed)
                        throw new InvalidOperationException("Verified running local map required");
                    feedback.Text=await RconProtocol.Execute(map.RconHost,map.RconPort,password,"ListPlayers");
                }catch(Exception ex){feedback.Text="RCON hiba: "+ex.Message;WriteLog("rcon",ex);}
            }),feedback);
    }
    private UIElement MapLogPane(LocalMap map)
    {
        var output=new TextBox{Height=260,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            FontFamily=new FontFamily("Consolas"),
            Foreground=Brushes.White,Background=Panel};
        var message=Label("Csak a map saját helyi naplófájljának utolsó, korlátozott részét olvassuk.",12,Soft);
        void Refresh()
        {
            try
            {
                if(!_cluster.Config.LocalMaps.Contains(map))
                    throw new UnauthorizedAccessException("Csak saját helyi map naplója");
                var result=LocalLogCenter.Read(map);
                output.Text=string.Join("\n",result.Lines.Select(x=>x.Content));
                message.Text=result.FileName+" · "+result.Lines.Count+
                    " titokszűrt sor · "+(result.Truncated?"csak naplóvég":"teljes fájlrész");
            }
            catch(Exception ex)
            {
                WriteLog("map-log-read",ex);
                output.Text="";
                message.Text="A helyi napló nem nyitható meg: "+ex.GetType().Name;
            }
        }
        return V(Label("ASA napló · csak olvasás",16,Mint,true),
            message,
            Btn("Napló végének betöltése",Refresh),
            Btn("Teljes Naplóközpont megnyitása",()=>{
                _preferredLogMapId=map.Id;
                Navigate("logs");
            }),
            output);
    }
    /// <summary>
    /// Fully live WPF map editor; this is not a demo or independent profile writer.
    /// Keep the original API-launch, root-path and port validations in Save.
    /// Fixed footer + per-step ScrollViewer support 1366x768 and 150% DPI.
    /// </summary>
    private Window? _editorScreenshotWindow;

    // Screenshot-only hook: opens the real, unmodified WPF editor with empty
    // local test inputs. No configuration writes or server operations.
    internal Window PrepareMapEditorScreenshot()
    {
        if (!App.IsSmokeTest)
            throw new InvalidOperationException("Editor screenshots require the isolated smoke mode");
        ShowMapEditor(null,true);
        return _editorScreenshotWindow ??
            throw new InvalidOperationException("Map editor did not open");
    }

    private void ShowMapEditor(LocalMap? existing,bool screenshotOnly=false)
    {
        var map=existing ?? new LocalMap();
        var desktop=SystemParameters.WorkArea;
        var dialog=new Window {
            Title=existing==null?"Új ASA szerver hozzáadása":"ASA szerver szerkesztése",
            Owner=this,Width=Math.Min(880,Math.Max(530,desktop.Width-28)),
            Height=Math.Min(735,Math.Max(420,desktop.Height-28)),
            MinWidth=530,MinHeight=420,ResizeMode=ResizeMode.CanResize,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Background=new SolidColorBrush(Color.FromRgb(12,20,24)),Foreground=Brushes.White
        };
        var fields=new Dictionary<string,TextBox>();
        var steps=new List<StackPanel>();
        StackPanel MakeStep(string title,string description)
        {
            var section=new StackPanel{Margin=new Thickness(22,19,22,22)};
            section.Children.Add(Label(title,23,Mint,true));
            section.Children.Add(Label(description,13,Soft));
            section.Children.Add(new Border{Background=Aqua,Height=2,Opacity=0.45,Margin=new Thickness(0,7,0,18)});
            steps.Add(section);
            return section;
        }
        void BrowseFolder(TextBox input)
        {
            using var picker=new System.Windows.Forms.FolderBrowserDialog{
                Description="Válaszd ki a helyi ASA telepítési gyökérkönyvtárát",
                ShowNewFolderButton=false
            };
            if(Directory.Exists(input.Text))picker.SelectedPath=input.Text;
            if(picker.ShowDialog()==System.Windows.Forms.DialogResult.OK)
                input.Text=picker.SelectedPath;
        }
        void BrowseFile(TextBox input,string title,string filter)
        {
            var picker=new Microsoft.Win32.OpenFileDialog{Title=title,Filter=filter,CheckFileExists=true};
            var folder=Path.GetDirectoryName(input.Text);
            if(!string.IsNullOrWhiteSpace(folder)&&Directory.Exists(folder))
                picker.InitialDirectory=folder;
            if(picker.ShowDialog(dialog)==true)input.Text=picker.FileName;
        }
        TextBox Field(StackPanel step,string caption,string initial,string browse="")
        {
            step.Children.Add(Label(caption,13,Aqua,true));
            var t=new TextBox{Text=initial,MinHeight=38,
                Margin=new Thickness(0,0,browse.Length==0?0:12,12)};
            fields[caption]=t;
            if(browse.Length==0)
                step.Children.Add(t);
            else
            {
                var line=new DockPanel{LastChildFill=true};
                var button=Btn("Tallózás…",()=>{
                    if(browse=="folder")BrowseFolder(t);
                    else BrowseFile(t,caption,browse=="log"?
                        "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*":
                        "Executable (*.exe)|*.exe|All files (*.*)|*.*");
                });
                button.MinWidth=112;button.MinHeight=38;
                DockPanel.SetDock(button,Dock.Right);
                line.Children.Add(button);
                line.Children.Add(t);
                step.Children.Add(line);
            }
            return t;
        }

        var location=MakeStep("01  /  Térkép és telepítés",
            "Csak ezen a Windows gépen található ASA telepítés adható hozzá. Automatikus import vagy szerverindítás nincs.");
        var names=new ComboBox{ItemsSource=KnownMaps,IsEditable=true,Text=map.Name,
            Margin=new Thickness(0,0,0,18),MinHeight=39};
        location.Children.Add(Label("ASA map neve",13,Aqua,true));
        location.Children.Add(names);
        var installMode=new ComboBox {
            ItemsSource=new[]{"Meglévő ASA-szerver felvétele (adatmegőrző)",
                "Új ASA-szerver automatikus telepítése SteamCMD-vel"},
            SelectedIndex=0,IsEnabled=existing==null,
            Margin=new Thickness(0,7,0,14),MinHeight=38
        };
        location.Children.Add(Label("Telepítési mód",13,Aqua,true));
        location.Children.Add(installMode);
        bool Fresh()=>existing==null && installMode.SelectedIndex==1;
        var defaultFreshRoot=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UntamedManager","ASA-Servers",map.Id.ToString("N"));
        var installRoot=Field(location,"Map telepítési gyökérkönyvtára",
            existing==null?defaultFreshRoot:map.InstallationPath,"folder");
        var serverExe=Field(location,"Szerver EXE teljes elérési útja",map.ExecutablePath,"exe");
        var steamCmdExe=Field(location,"SteamCMD EXE (új telepítés esetén)",
            string.IsNullOrWhiteSpace(_cluster.Config.SteamCmdExecutable)?
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UntamedManager","SteamCMD","steamcmd.exe"):
                _cluster.Config.SteamCmdExecutable,"exe");
        var downloadSteam=new CheckBox{
            Content="Ha hiányzik, hivatalos Valve SteamCMD ZIP letöltése (HTTPS)",
            Margin=new Thickness(0,1,0,12),IsChecked=false
        };
        location.Children.Add(downloadSteam);
        var retryFresh=new CheckBox{
            Content="Korábbi, kizárólag ezen maphoz tartozó félbeszakadt telepítés kézi újrapróbálása",
            Margin=new Thickness(0,1,0,12),IsChecked=false
        };
        location.Children.Add(retryFresh);
        var sessionBox=Field(location,"Új szerver nyilvános neve",
            "Untamed Kingdom - "+(map.Name.Length==0?"The Island":map.Name));
        var installStatus=Label("SteamCMD nincs elindítva; az új mapnak külön, üres könyvtár szükséges.",12,Soft);
        location.Children.Add(installStatus);
        void ChangeMode()
        {
            bool fresh=Fresh();
            if(fresh && string.IsNullOrWhiteSpace(installRoot.Text))
                installRoot.Text=defaultFreshRoot;
            steamCmdExe.Visibility=fresh?Visibility.Visible:Visibility.Collapsed;
            downloadSteam.Visibility=fresh?Visibility.Visible:Visibility.Collapsed;
            retryFresh.Visibility=fresh?Visibility.Visible:Visibility.Collapsed;
            sessionBox.Visibility=fresh?Visibility.Visible:Visibility.Collapsed;
            serverExe.IsReadOnly=fresh;
            serverExe.ToolTip=fresh?"Telepítés után a SteamCMD által létrehozott EXE automatikusan kitöltődik.":
                "Létező szerver EXE kijelölése";
            installStatus.Text=fresh?
                "Új, külön ASA gyökér: a map kiválasztása után 2430930 települ, adatokat nem importálunk.":
                "Meglévő szerver: nincs letöltés, a világok és INI-k érintetlenek.";
        }
        installMode.SelectionChanged+=(_,_)=>ChangeMode();
        ChangeMode();
        var locateResult=Label("Válaszd ki a telepítési mappát az ASA-fájlok felismeréséhez.",12,Soft);
        var detect=Btn("ASA szerver EXE + API loader felismerése",()=>{
            try
            {
                // Exact local folder probe only. No SteamCMD, file writes,
                // server start, disk traversal or remote Manager operations.
                var probe=LocalAsaInstallLocator.Probe(installRoot.Text);
                if(!probe.HasServerExecutable)
                {
                    locateResult.Text="Az ArkAscendedServer.exe nem található a szabványos "+
                        "ShooterGame\\Binaries\\Win64 mappában. Válassz másik gyökérmappát, "+
                        "vagy add meg az EXE teljes helyi útvonalát.";
                    return;
                }
                serverExe.Text=probe.ServerExecutable!;
                if(probe.ApiLoaderExecutable!=null &&
                   fields.TryGetValue("API-loader EXE (opcionális)",out var loaderBox) &&
                   string.IsNullOrWhiteSpace(loaderBox.Text))
                    loaderBox.Text=probe.ApiLoaderExecutable;
                locateResult.Text="Helyi ASA EXE megtalálva. "+
                    (probe.HasApiLoader?"API loader is található.":"API loader nem található.")+
                    " Az indítási mód és az argumentumok változatlanok.";
            }
            catch(Exception ex)
            {
                locateResult.Text="Az ASA-fájlok nem ellenőrizhetők: "+
                    UntamedPresentation.Display(ex.Message);
            }
        });
        detect.ToolTip="A kijelölt helyi gyökérkönyvtárból csak a szabványos EXE-útvonalakat ellenőrzi.";
        location.Children.Add(detect);
        location.Children.Add(locateResult);

        var launch=MakeStep("02  /  Indítás és portok",
            "Új telepítéskor a valódi ASA mapazonosítóval generálunk indítást. Meglévő profilnál a paraméterek megmaradnak.");
        Field(launch,"Indítási paraméterek (pontos egyezés)",map.Arguments);
        Field(launch,"Game UDP port",map.GamePort==0?"":map.GamePort.ToString());
        Field(launch,"Query UDP port",map.QueryPort==0?"":map.QueryPort.ToString());
        Field(launch,"RCON TCP port",map.RconPort==0?"":map.RconPort.ToString());

        var api=MakeStep("03  /  ASA API loader",
            "Normál és loaderes indítás külön kezelve. Az API-mód váltása leállított szervert igényel.");
        Field(api,"API-loader EXE (opcionális)",map.ApiLoaderPath,"exe");
        Field(api,"API-loader argumentumok",map.ApiLoaderArguments);
        var apiStart=new CheckBox {
            Content="Indítás ASA API-val (alapesetben ArkAscendedServer.exe)",
            IsChecked=map.StartWithAsaApi,Margin=new Thickness(0,8,0,14)
        };
        api.Children.Add(apiStart);
        api.Children.Add(Label("API módban a loader az eredeti szerverargumentumokat kapja. "+
            "Csak helyileg telepített loaderrel használható. Az éles ASA indítás biztonsági korlátja változatlan.",
            12,Soft));

        var monitoring=MakeStep("04  /  RCON és felügyelet",
            "A jelszó Windows DPAPI-val védett, üresen hagyva a meglévő érték változatlan.");
        var maintenance=new CheckBox {Content="Maintenance – helyi indítás tiltva",
            IsChecked=map.Maintenance,Margin=new Thickness(0,0,0,11)};
        var monitorRcon=new CheckBox {Content="Hitelesített RCON-monitoring (ListPlayers)",
            IsChecked=map.MonitorRcon,Margin=new Thickness(0,0,0,14)};
        monitoring.Children.Add(maintenance);
        monitoring.Children.Add(monitorRcon);
        Field(monitoring,"Watchdog ellenőrzés másodpercben (5–3600)",
            map.WatchdogIntervalSeconds.ToString());
        Field(monitoring,"RCON host (helyi IP)",map.RconHost);
        Field(monitoring,"Helyi naplófájl",map.LogFilePath,"log");
        monitoring.Children.Add(Label("RCON jelszó (Windows DPAPI-val titkosítva)",13,Aqua,true));
        var password=new PasswordBox{MinHeight=37,Margin=new Thickness(0,0,0,13)};
        monitoring.Children.Add(password);

        var review=MakeStep("05  /  Ellenőrzés és mentés",
            "A Mentés teljes útvonal-, port- és ASA API-ellenőrzést végez. Nem indít vagy állít le szervert.");
        var summary=Label("",13,Mint);
        var validationHints=Label("",12,Soft);
        review.Children.Add(new Border{Background=Panel,BorderBrush=Aqua,
            BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),
            Padding=new Thickness(17),Child=summary});
        review.Children.Add(validationHints);
        review.Children.Add(Label("A konfiguráció kizárólag az Untamed Manager helyi profiljába kerül. "+
            "A meglévő karakterek, Store-v1, SQL és mentések érintetlenek.",12,Soft));

        var wizardGrid=new Grid();
        wizardGrid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        wizardGrid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        wizardGrid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var header=new StackPanel{Margin=new Thickness(22,15,22,14)};
        header.Children.Add(Label("UNTAMED KINGDOM  /  MAP SETUP",12,Aqua,true));
        var currentStep=Label("",13,Soft);
        header.Children.Add(currentStep);
        var headerPanel=new Border{Background=new SolidColorBrush(Color.FromRgb(19,42,44)),
            BorderBrush=new SolidColorBrush(Color.FromRgb(54,100,87)),
            BorderThickness=new Thickness(0,0,0,1),Child=header};
        Grid.SetRow(headerPanel,0);wizardGrid.Children.Add(headerPanel);
        var body=new StackPanel();
        foreach(var step in steps)body.Children.Add(step);
        var scroll=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        Grid.SetRow(scroll,1);wizardGrid.Children.Add(scroll);
        var errorMessage=Label("",12,Brushes.OrangeRed);
        var footer=new Grid{Margin=new Thickness(18,13,18,8)};
        footer.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        footer.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        errorMessage.MaxWidth=330;errorMessage.VerticalAlignment=VerticalAlignment.Center;
        footer.Children.Add(errorMessage);
        var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};
        int stepIndex=0;
        Action<int>? showStep=null;
        var previous=Btn("← Előző",()=>showStep?.Invoke(stepIndex-1));
        var next=Btn("Következő →",()=>showStep?.Invoke(stepIndex+1));
        Button saveButton=null!;
        saveButton=Btn("Mentés",async()=>{
                    saveButton.IsEnabled=false;
                    try{
                        var name=LocalMapNameRules.Normalize(names.Text);
                        var root=Path.GetFullPath(fields["Map telepítési gyökérkönyvtára"].Text.Trim());
                        var exeText=fields["Szerver EXE teljes elérési útja"].Text.Trim();
                        if(Fresh())
                        {
                            // Check local ports BEFORE starting a multi-GB download.
                            if(!int.TryParse(fields["Game UDP port"].Text,out int preGame) ||
                               !int.TryParse(fields["Query UDP port"].Text,out int preQuery) ||
                               !int.TryParse(fields["RCON TCP port"].Text,out int preRcon) ||
                               preGame is <1 or >65535 || preQuery is <1 or >65535 ||
                               preRcon is <1 or >65535)
                                throw new InvalidDataException("Telepítés előtt érvényes portok szükségesek.");
                            LocalMapPortPolicy.RequireNoConflicts(new LocalMap{
                                Id=map.Id,Name=name,GamePort=preGame,QueryPort=preQuery,RconPort=preRcon},
                                _cluster.Config.LocalMaps);
                            if(apiStart.IsChecked==true)
                                throw new InvalidDataException(
                                    "Új telepítésnél az ASA API loader külön, ellenőrzött lépésben engedélyezhető.");
                            var args=AsaSteamMapCatalog.LaunchArguments(
                                name,sessionBox.Text.Trim(),preGame,preQuery);
                            var request=new SteamFreshInstallRequest(
                                map.Id,name,sessionBox.Text.Trim(),root,
                                steamCmdExe.Text.Trim(),
                                downloadSteam.IsChecked==true,retryFresh.IsChecked==true);
                            var progress=new Progress<SteamInstallProgress>(info=>{
                                installStatus.Text=info.Stage+" · "+info.Detail;
                                errorMessage.Text="SteamCMD: "+info.Detail;
                            });
                            var receipt=await SteamFreshInstall.InstallAsync(
                                request,_cluster.Config.LocalMaps.ToArray(),progress);
                            exeText=receipt.ServerExecutable;
                            serverExe.Text=exeText;
                            fields["Indítási paraméterek (pontos egyezés)"].Text=args;
                            installStatus.Text="Telepítés sikeres. Steam Build ID: "+
                                (receipt.BuildId??"ismeretlen")+"; ASA szerver még NEM indult el.";
                        }
                        var exe=Path.GetFullPath(exeText);
                        if(!Directory.Exists(root)||!File.Exists(exe)||!IsUnderRoot(exe,root))
                            throw new InvalidDataException("Az EXE-nek a megadott helyi telepítési könyvtárban kell lennie");
                        if(!int.TryParse(fields["Game UDP port"].Text,out var game)||game is < 1 or > 65535)
                            throw new InvalidDataException("Érvénytelen Game port");
                        if(!int.TryParse(fields["Query UDP port"].Text,out var query)||query is < 1 or > 65535)
                            throw new InvalidDataException("Érvénytelen Query port");
                        if(!int.TryParse(fields["RCON TCP port"].Text,out var rcon)||rcon is < 1 or > 65535)
                            throw new InvalidDataException("Érvénytelen RCON port");
                        string loader=fields["API-loader EXE (opcionális)"].Text.Trim();
                        bool enableApi=apiStart.IsChecked==true;
                        string serverArguments=fields["Indítási paraméterek (pontos egyezés)"].Text.Trim();
                        string loaderArguments=fields["API-loader argumentumok"].Text.Trim();
                        if(enableApi && loader.Length==0)
                            loader=Path.Combine(root,"ShooterGame","Binaries","Win64","AsaApiLoader.exe");
                        if(loader.Length>0 && (!File.Exists(loader)||!IsUnderRoot(loader,root)))
                            throw new InvalidDataException("A loader EXE-nek a helyi mapgyökérben kell lennie");
                        if(existing!=null && existing.StartWithAsaApi!=enableApi &&
                           !_cluster.Check(existing).ConfirmedStopped)
                            throw new InvalidOperationException("Az API-indítási módot csak igazoltan leállított mapon változtathatod meg");
                        if(enableApi)
                        {
                            if(loaderArguments.Length>0 && loaderArguments!=serverArguments)
                                throw new InvalidDataException("API módban a loader paraméterei nem térhetnek el a szerver paramétereitől");
                            // Validate before mutating the registered map.
                            _=AsaApiLaunchPolicy.Resolve(new LocalMap{
                                InstallationPath=root,ExecutablePath=exe,Arguments=serverArguments,
                                ApiLoaderPath=loader,ApiLoaderArguments=serverArguments,
                                StartWithAsaApi=true
                            });
                            loaderArguments=serverArguments;
                        }
                        if(!int.TryParse(fields["Watchdog ellenőrzés másodpercben (5–3600)"].Text,out var interval)||interval<5||interval>3600)throw new InvalidDataException("Érvénytelen watchdog-időköz");
                        // Stage a full candidate without mutating the registered map.
                        // This preserves all process and Store-v1 identity fields.
                        var target=existing==null?map:
                            JsonSerializer.Deserialize<LocalMap>(
                                JsonSerializer.Serialize(existing)) ??
                            throw new InvalidDataException("Map profile cannot be staged");
                        target.Name=name;target.InstallationPath=root;target.ExecutablePath=exe;
                        target.Arguments=serverArguments;
                        target.ApiLoaderPath=loader.Length==0?"":Path.GetFullPath(loader);
                        target.ApiLoaderArguments=loaderArguments;
                        target.StartWithAsaApi=enableApi;
                        target.Maintenance=maintenance.IsChecked==true;
                        target.MonitorRcon=monitorRcon.IsChecked==true;
                        target.WatchdogIntervalSeconds=interval;
                        target.GamePort=game;target.QueryPort=query;target.RconPort=rcon;
                        target.RconHost=fields["RCON host (helyi IP)"].Text.Trim();
                        target.LogFilePath=fields["Helyi naplófájl"].Text.Trim();
                        if(password.Password.Length>0)target.RconPasswordProtected=SecureStore.Protect(password.Password);
                        // UDP Game+Query collisions and TCP RCON collisions are
                        // checked against local, registered maps only; never
                        // treat a remote LAN snapshot as a locally owned map.
                        LocalMapPortPolicy.RequireNoConflicts(target,_cluster.Config.LocalMaps);
                        if(existing==null)_cluster.AddMap(target);
                        else _cluster.ReplaceMap(existing,target);
                        RefreshDashboardMapCards();dialog.DialogResult=true;
                        ShowLocalServerPage();
                    }catch(Exception e){
                        WriteLog("steam-map-wizard",e);
                        errorMessage.Text="Nem menthető: "+UntamedPresentation.Display(e.Message);
                        saveButton.IsEnabled=true;
                    }
                });
        saveButton.Style=(Style)System.Windows.Application.Current.Resources["PrimaryAction"];
        saveButton.MinWidth=112;
        var cancel=Btn("Mégse",()=>dialog.DialogResult=false);
        buttons.Children.Add(previous);
        buttons.Children.Add(next);
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancel);
        Grid.SetColumn(buttons,1);footer.Children.Add(buttons);
        var footerPanel=new Border{Background=new SolidColorBrush(Color.FromRgb(18,33,36)),
            BorderBrush=new SolidColorBrush(Color.FromRgb(52,79,76)),
            BorderThickness=new Thickness(0,1,0,0),Child=footer};
        Grid.SetRow(footerPanel,2);wizardGrid.Children.Add(footerPanel);
        showStep=index=>{
            if(index<0||index>=steps.Count)return;
            stepIndex=index;
            for(int i=0;i<steps.Count;i++)
                steps[i].Visibility=i==index?Visibility.Visible:Visibility.Collapsed;
            currentStep.Text=$"Lépés {index+1} / {steps.Count}  •  "+
                (existing==null?"Új helyi szerver":"Meglévő konfiguráció");
            previous.IsEnabled=index>0;
            next.Visibility=index<steps.Count-1?Visibility.Visible:Visibility.Collapsed;
            saveButton.Visibility=index==steps.Count-1?Visibility.Visible:Visibility.Collapsed;
            saveButton.Content=Fresh()?"SteamCMD telepítés + map hozzáadása":"Mentés";
            errorMessage.Text="";
            if(index==steps.Count-1)
            {
                summary.Text="Map: "+names.Text+
                    "\nTelepítés: "+fields["Map telepítési gyökérkönyvtára"].Text+
                    "\nEXE: "+fields["Szerver EXE teljes elérési útja"].Text+
                    "\nGame / Query / RCON port: "+
                    fields["Game UDP port"].Text+" / "+fields["Query UDP port"].Text+" / "+
                    fields["RCON TCP port"].Text+
                    "\nASA API: "+(apiStart.IsChecked==true?"bekapcsolva":"kikapcsolva")+
                    "\nMaintenance: "+(maintenance.IsChecked==true?"bekapcsolva":"kikapcsolva");
                // All these checks are read-only. Save repeats every check
                // and may still fail if files or a peer configuration change.
                var problems=new List<string>();
                try{LocalMapNameRules.Normalize(names.Text);}
                catch(Exception){problems.Add("Érvénytelen vagy üres mapnév.");}
                string chosenRoot=installRoot.Text.Trim();
                string chosenExe=serverExe.Text.Trim();
                if(!Fresh() && (chosenRoot.Length==0 || !Directory.Exists(chosenRoot)))
                    problems.Add("A meglévő ASA gyökérkönyvtár nem létezik.");
                if(!Fresh() && (chosenExe.Length==0 || !File.Exists(chosenExe)))
                    problems.Add("A meglévő szerver EXE nem található.");
                else if(!Fresh() && chosenRoot.Length>0 && Directory.Exists(chosenRoot))
                {
                    try{
                        if(!IsUnderRoot(Path.GetFullPath(chosenExe),Path.GetFullPath(chosenRoot)))
                            problems.Add("A szerver EXE nincs a megadott telepítési gyökérben.");
                    }catch(Exception){problems.Add("Az EXE útvonala nem ellenőrizhető.");}
                }
                bool goodGame=int.TryParse(fields["Game UDP port"].Text,out int gp) && gp>0 && gp<=65535;
                bool goodQuery=int.TryParse(fields["Query UDP port"].Text,out int qp) && qp>0 && qp<=65535;
                bool goodRcon=int.TryParse(fields["RCON TCP port"].Text,out int rp) && rp>0 && rp<=65535;
                if(!goodGame || !goodQuery || !goodRcon)
                    problems.Add("Game, Query és RCON: 1–65535 közötti port szükséges.");
                else
                {
                    var previewMap=new LocalMap{Id=map.Id,Name=names.Text,
                        GamePort=gp,QueryPort=qp,RconPort=rp};
                    problems.AddRange(LocalMapPortPolicy.Conflicts(previewMap,
                        _cluster.Config.LocalMaps));
                }
                if(!int.TryParse(fields["Watchdog ellenőrzés másodpercben (5–3600)"].Text,
                    out int seconds) || seconds<5 || seconds>3600)
                    problems.Add("Watchdog: 5–3600 másodperc közötti egész szám szükséges.");
                var loaderPath=fields["API-loader EXE (opcionális)"].Text.Trim();
                if(Fresh())
                {
                    try
                    {
                        _=AsaSteamMapCatalog.LevelFor(names.Text);
                        _=AsaSteamMapCatalog.LaunchArguments(names.Text,
                            sessionBox.Text.Trim(),gp,qp);
                        if(chosenRoot.Length==0 || !Path.IsPathFullyQualified(chosenRoot))
                            problems.Add("Új ASA gyökér: teljes helyi elérési út szükséges.");
                        else if(Directory.Exists(chosenRoot) &&
                                Directory.EnumerateFileSystemEntries(chosenRoot).Any() &&
                                retryFresh.IsChecked!=true)
                            problems.Add("Új szerver könyvtára nem üres; importáld meglévőként, vagy ellenőrizd a félbeszakadt telepítést.");
                        if(steamCmdExe.Text.Length==0)
                            problems.Add("SteamCMD teljes elérési útja szükséges.");
                        else if(!File.Exists(steamCmdExe.Text) && downloadSteam.IsChecked!=true)
                            problems.Add("SteamCMD hiányzik; pipáld be a hivatalos letöltést vagy válassz meglévőt.");
                        if(apiStart.IsChecked==true)
                            problems.Add("Friss telepítésnél az ASA API loader csak utólag, ellenőrzés után engedélyezhető.");
                    }
                    catch(Exception){problems.Add("Ismeretlen mapazonosító vagy hibás indítási adatok.");}
                }
                if(apiStart.IsChecked==true && !Fresh())
                {
                    if(loaderPath.Length==0 && chosenRoot.Length>0)
                        loaderPath=Path.Combine(chosenRoot,
                            "ShooterGame","Binaries","Win64","AsaApiLoader.exe");
                    if(loaderPath.Length==0 || !File.Exists(loaderPath))
                        problems.Add("Bekapcsolt ASA API indításhoz loader EXE szükséges.");
                }
                if(problems.Count==0)
                {
                    validationHints.Foreground=Aqua;
                    validationHints.Text="✓ A helyi fájlok, portok és az alapbeállítások előzetes ellenőrzése sikeres. "+
                        "Mentéskor újra ellenőrzünk. ASA-indítás nem történik.";
                }
                else
                {
                    validationHints.Foreground=Brushes.OrangeRed;
                    validationHints.Text="Mentés előtt javítandó:\n• "+string.Join("\n• ",problems);
                }
                saveButton.IsEnabled=problems.Count==0;
            }
            scroll.ScrollToTop();
        };
        dialog.Content=wizardGrid;
        showStep(0);
        if (screenshotOnly)
        {
            _editorScreenshotWindow=dialog;
            dialog.Show();
        }
        else dialog.ShowDialog();
    }
    private void ShowLanSettings()
    {
        Header("LAN Manager-beállítások","Hitelesített, egyirányú státuszlekérdezés. Nincs távoli parancsvégrehajtás vagy fájlhozzáférés.");
        var host=new TextBox{Text=_cluster.Config.LocalHostName};
        var listen=new TextBox{Text=_cluster.Config.ListenPort.ToString()};
        var remote=new TextBox{Text=_cluster.Config.PeerAddress};
        var remotePort=new TextBox{Text=_cluster.Config.PeerPort.ToString()};
        var key=new PasswordBox();
        Add(Box(V(Label("Saját gép neve",13,Aqua),host,
            Label("LAN figyelőport (0 = kikapcsolva)",13,Aqua),listen,
            Label("Peer helyi IP / hostnév",13,Aqua),remote,
            Label("Peer TCP port",13,Aqua),remotePort,
            Label("Közös párosítási titok · min. 24 karakter · mindkét gépen azonos",13,Aqua),
            key,
            Btn("LAN beállítások mentése",()=>{
                try{
                    if(!int.TryParse(listen.Text,out int port)||port!=0&&port is < 1024 or >65535)
                        throw new InvalidDataException("Érvénytelen listen port");
                    if(!int.TryParse(remotePort.Text,out int peer)||peer is < 0 or >65535)
                        throw new InvalidDataException("Érvénytelen peer port");
                    _cluster.UpdateSettings(config=>{
                        config.LocalHostName=host.Text.Trim();
                        config.ListenPort=port; config.PeerAddress=remote.Text.Trim();
                        config.PeerPort=peer;
                        if(key.Password.Length>0)
                        {
                            if(key.Password.Length<24)throw new InvalidDataException("A párosítási kulcs túl rövid");
                            config.PairingKeyProtected=SecureStore.Protect(key.Password);
                        }
                    });
                    RefreshDashboardMapCards();
                    Notice("Beállítások mentve. A LAN-figyelő változtatása Manager-újraindítás után érvényes.");
                }catch(Exception e){Notice(e.Message);}
            }),
            Btn("LAN peer lekérdezése most",async()=>{
                await _cluster.PollPeerAsync();RefreshDashboardMapCards();
                Notice(_cluster.RemoteMessage);
            }))));
    }
}

