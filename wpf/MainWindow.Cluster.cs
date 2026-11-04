using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UntamedManager.Core;
using WinMessageBox = System.Windows.MessageBox;

namespace UntamedManager;

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
        if (!App.IsSmokeTest && !App.IsSupervisionTest) _ = BeginClusterNetworkingAsync();
    }
    private async Task BeginClusterNetworkingAsync()
    {
        try { await _cluster.BeginLanAsync(); }
        catch (Exception ex) { WriteLog("LAN-listener",ex); }
        _peerLoop=Task.Run(async ()=>{
            while(!_peerCts.IsCancellationRequested)
            {
                try {
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
        // Preserve the existing visual template, replacing only the data source and status.
        var images=_maps.ToDictionary(x=>x.Name,StringComparer.OrdinalIgnoreCase);
        var active = new List<MapInfo>();
        int knownModAssignments=0, readableModMaps=0;
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
            try { status=_cluster.Check(m).Status; }
            catch(Exception ex){WriteLog("dashboard-process-observation",ex);}
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
        ClusterHostCount.Text=summary.HostCount.ToString();
        ClusterOnlineCount.Text=summary.FreshLanPeers.ToString();
        ConfiguredModCount.Text=summary.ModCountDisplay;
        ModReadingCoverage.Text=summary.ModCoverageDisplay;
        var hosts=new List<HostCardInfo>{
            new() {
                Name=_cluster.Config.LocalHostName,
                Description=summary.LocalMaps+" helyi map · "+App.ProfileName+" profil",
                TrustState="HELYI PROFIL · nincs feltételezett LAN-kapcsolat",
                Accent=Aqua
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
        Header("Cluster · két Manager, két gép",
            "A helyi gépen teljes kezelés; a LAN-peer gép állapota kizárólag olvasható. "+
            "Internet és külső vezérlőszerver nélkül.");
        PageBody.Children.Clear();
        Add(Box(Label("Alapértelmezett kiosztás (nem automatikus import): UK-HOME-01 / HP: Astraeos, Genesis 1, Valguero, Island, Extinction. UK-HOME-02 / Ryzen: Scorched, Lost Colony, Ragnarok, Center, Aberration.",12,Soft)));
        var local = _cluster.LocalStatus();
        Add(Box(V(Label($"◉  HELYI · {local.HostName}",20,Mint,true),
            Label($"{local.Maps.Count} manuálisan regisztrált map · írási jog kizárólag ezen a gépen",12,Soft))));
        foreach(var map in local.Maps)
        {
            var b=Btn($"{(map.Running?"●":"○")}  {map.Name} · {map.Status} · UDP {map.GamePort} · KEZELÉS",()=>{
                _selected=_maps.FirstOrDefault(x=>x.Name==map.Name);
                Navigate("servers");
            });
            Add(Box(b));
        }
        Add(Box(V(Label("◉  TÁVOLI MANAGER",20,Aqua,true),
            Label(_cluster.RemoteSnapshot==null?_cluster.RemoteMessage:
                $"{_cluster.RemoteSnapshot.HostName} · {_cluster.RemoteSnapshot.Maps.Count} map · "+
                $"adatok: {_cluster.RemoteSnapshot.ObservedUtc.LocalDateTime:HH:mm:ss} · "+(_cluster.RemoteIsFresh?"FRISS":"ELAVULT / OFFLINE"),12,Soft))));
        if(_cluster.RemoteSnapshot is { } remote)
        {
            foreach(var m in remote.Maps)
                Add(Box(V(Label($"{(_cluster.RemoteIsFresh && m.Running?"●":"○")}  {m.Name}",16,Brushes.White,true),
                    Label($"{(_cluster.RemoteIsFresh?m.Status:"ELAVULT MÉRÉS · "+m.Status)} · UDP {m.GamePort} · CSAK MEGFIGYELÉS",12,Aqua))));
        }
        Add(Btn("Peer állapot frissítése",async()=>{
            await _cluster.PollPeerAsync();
            RefreshDashboardMapCards();
            if(_page=="cluster") ShowClusterPage();
        }));
    }
    private void ShowLocalServerPage()
    {
        Header("Szerverek · csak ez a Windows gép",
            "A távoli szerverek itt nem kezelhetők. A felvett mapok a helyi profilban tárolódnak; "+
            "nincs automatikus mapkeresés vagy importálás.");
        Add(Box(V(Label($"Gép: {_cluster.Config.LocalHostName} · Profil: {App.ProfileName}",18,Mint,true),
            Label($"{_cluster.Config.LocalMaps.Count} helyi map · A Manager bezárása/frissítése soha nem állítja le a futó szervereket.",12,Soft),
            Btn("+ Map manuális hozzáadása",()=>ShowMapEditor(null)))));
        if(_cluster.Config.LocalMaps.Count==0)
        {
            Add(Box(Label("Nincs helyi map hozzáadva. Az első mapot a fenti gombbal regisztrálhatod.",15,Soft)));
            return;
        }
        var layout=new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(245)});
        layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        var list=new ListBox { ItemsSource=_cluster.Config.LocalMaps, DisplayMemberPath="Name",
            Foreground=Brushes.White, Background=Panel, BorderBrush=Aqua,
            FontSize=15,Padding=new Thickness(8),MinHeight=425,Margin=new Thickness(0,0,15,0)};
        var details=new StackPanel();
        Grid.SetColumn(details,1);
        layout.Children.Add(list);layout.Children.Add(details);
        list.SelectionChanged+=(_,_)=>{
            details.Children.Clear();
            if(list.SelectedItem is LocalMap map) DisplayLocalMapDetails(details,map);
        };
        list.SelectedItem=_cluster.Config.LocalMaps.FirstOrDefault(x=>x.Id==_selected?.Id)
                         ?? _cluster.Config.LocalMaps.First();
        Add(layout);
    }
    private void DisplayLocalMapDetails(StackPanel parent,LocalMap map)
    {
        var result=_cluster.Check(map);
        parent.Children.Add(Label(map.Name,25,Brushes.White,true));
        parent.Children.Add(Label(result.Status,14,result.Running?Mint:Soft));
        var controls=new StackPanel{Orientation=Orientation.Horizontal};
        controls.Children.Add(Btn("Beállítások szerkesztése",()=>ShowMapEditor(map)));
        controls.Children.Add(Btn("Eltávolítás a Managerből",()=>{
            if(WinMessageBox.Show($"Eltávolítod ezt a mapot a listából? A játékfájlok és a futó szerver változatlan marad.\n{map.Name}",
                "Map eltávolítása",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            { _cluster.RemoveMap(map.Id);RefreshDashboardMapCards();ShowLocalServerPage(); }
        }));
        parent.Children.Add(controls);
        var tabs=new TabControl { Margin=new Thickness(0,14,0,0),MinHeight=380,
            Background=Panel,Foreground=Brushes.White,BorderBrush=Aqua };
        void Tab(string title,UIElement body)
        {
            tabs.Items.Add(new TabItem{Header=title,Content=new ScrollViewer{
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=body,
                Padding=new Thickness(12)}});
        }
        var startStop=new StackPanel{Orientation=Orientation.Horizontal};
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
        parent.Children.Add(tabs);
    }
    private UIElement MapModPane(LocalMap map)
    {
        var list=new ListBox{Height=225,Background=Panel,Foreground=Brushes.White};
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
        var list=new ListBox{Height=215,Background=Panel,Foreground=Brushes.White};
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
        var output=new TextBox{Height=290,IsReadOnly=true,TextWrapping=TextWrapping.NoWrap,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            FontFamily=new FontFamily("Consolas")};
        return V(Label("Helyi naplónézet · csak olvasás",16,Mint,true),
            Label(map.LogFilePath,12,Soft),
            Btn("Napló újratöltése",()=>{
                try{
                    if(!IsUnderRoot(map.LogFilePath,map.InstallationPath))throw new InvalidOperationException("Érvénytelen naplóútvonal");
                    using var fs=new FileStream(map.LogFilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                    fs.Seek(-Math.Min(100_000,fs.Length),SeekOrigin.End);
                    using var sr=new StreamReader(fs,Encoding.UTF8);
                    output.Text=sr.ReadToEnd();
                }catch(Exception e){output.Text=e.Message;}
            }),output);
    }
    private void ShowMapEditor(LocalMap? existing)
    {
        var map=existing ?? new LocalMap();
        var dialog=new Window {
            Title=existing==null?"Map hozzáadása":"Map szerkesztése",
            Owner=this,Width=720,Height=790,MinWidth=620,MinHeight=620,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Background=new SolidColorBrush(Color.FromRgb(8,28,29)),Foreground=Brushes.White
        };
        var form=new StackPanel{Margin=new Thickness(23)};
        var fields=new Dictionary<string,TextBox>();
        TextBox Field(string caption,string initial)
        {
            form.Children.Add(Label(caption,13,Aqua,true));
            var t=new TextBox{Text=initial,Margin=new Thickness(0,0,0,11)};
            fields[caption]=t;form.Children.Add(t);return t;
        }
        form.Children.Add(Label("Kizárólag erre a Windows gépre felvett map",22,Mint,true));
        var names=new ComboBox{ItemsSource=KnownMaps,IsEditable=true,Text=map.Name,
            Foreground=Brushes.White,Background=Panel,Margin=new Thickness(0,0,0,12)};
        form.Children.Add(Label("Map neve – az ismert mapok javaslatok, új ASA map neve is megadható",13,Aqua,true));form.Children.Add(names);
        Field("Map telepítési gyökérkönyvtára",map.InstallationPath);
        Field("Szerver EXE teljes elérési útja",map.ExecutablePath);
        Field("Indítási paraméterek (pontos egyezés)",map.Arguments);
        Field("API-loader EXE (opcionális)",map.ApiLoaderPath);
        Field("API-loader argumentumok",map.ApiLoaderArguments);
        var apiStart=new CheckBox{
            Content="Indítás ASA API-val (maponként külön; kikapcsolva normál ArkAscendedServer.exe)",
            IsChecked=map.StartWithAsaApi,Foreground=Brushes.White,
            Margin=new Thickness(0,0,0,9)};
        form.Children.Add(apiStart);
        form.Children.Add(Label("Bekapcsolva a hivatalos AsaApiLoader.exe indul, pontosan a szerver eredeti "+
            "paramétereivel. A teljes helyi API-telepítés és a leállított állapot kötelező. "+
            "Éles ASA indítás továbbra is tiltva.",12,Soft));
        var maintenance=new CheckBox{Content="Maintenance – helyi indítás tiltva",IsChecked=map.Maintenance,Foreground=Brushes.White};
        form.Children.Add(maintenance);
        var monitorRcon=new CheckBox{Content="Hitelesített RCON-monitoring (ListPlayers)",IsChecked=map.MonitorRcon,Foreground=Brushes.White};
        form.Children.Add(monitorRcon);
        Field("Watchdog ellenőrzés másodpercben (5–3600)",map.WatchdogIntervalSeconds.ToString());
        Field("Game UDP port",map.GamePort==0?"":map.GamePort.ToString());
        Field("Query UDP port",map.QueryPort==0?"":map.QueryPort.ToString());
        Field("RCON TCP port",map.RconPort==0?"":map.RconPort.ToString());
        Field("RCON host (helyi IP)",map.RconHost);
        Field("Helyi naplófájl",map.LogFilePath);
        form.Children.Add(Label("RCON jelszó (Windows DPAPI-val titkosítva)",13,Aqua,true));
        var password=new PasswordBox{Margin=new Thickness(0,0,0,13)};
        form.Children.Add(password);
        var actions=new StackPanel{Orientation=Orientation.Horizontal};
        actions.Children.Add(Btn("Mentés",()=>{
            try{
                var name=LocalMapNameRules.Normalize(names.Text);
                var root=Path.GetFullPath(fields["Map telepítési gyökérkönyvtára"].Text.Trim());
                var exe=Path.GetFullPath(fields["Szerver EXE teljes elérési útja"].Text.Trim());
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
                var target=existing??new LocalMap();
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
                if(existing==null)_cluster.AddMap(target);
                else _cluster.Save();
                RefreshDashboardMapCards();dialog.DialogResult=true;
                ShowLocalServerPage();
            }catch(Exception e){Notice("Nem sikerült menteni: "+e.Message);}
        }));
        actions.Children.Add(Btn("Mégse",()=>dialog.DialogResult=false));
        form.Children.Add(actions);
        dialog.Content=new ScrollViewer{Content=form,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        dialog.ShowDialog();
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

