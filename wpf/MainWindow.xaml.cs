using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.ComponentModel;
using UntamedManager.Core;
using Forms = System.Windows.Forms;
using WinMessageBox = System.Windows.MessageBox;

namespace UntamedManager;

public sealed class MapInfo
{
    public Guid Id { get; init; }
    public bool IsRemote { get; init; }
    public bool IsDemo { get; init; }
    public bool IsFresh { get; init; }
    public string SourceBadge => IsDemo ? "DEMO" : !IsRemote ? "HELYI" : IsFresh ? "LAN · FRISS" : "LAN · ELAVULT";
    public Brush BadgeForeground => IsDemo ? Brushes.Gold : IsRemote && !IsFresh ? Brushes.Orange : Brushes.Aquamarine;
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int? ModCount { get; init; }
    public int? GamePort { get; init; }
    public int? QueryPort { get; init; }
    public int? RconPort { get; init; }
    public string Status { get; init; } = "Állapot ismeretlen";
    public string Caption => $"{Host} · "+(ModCount.HasValue?$"{ModCount} konfigurált mod":"modszám ismeretlen")+
        " · "+(GamePort.HasValue&&GamePort>0?$"UDP {GamePort}":"port ismeretlen")+" · "+Status;
    public Brush CoverBrush { get; init; } = Brushes.DarkSlateGray;
    public ImageSource? CoverImage { get; init; }
}

public sealed class HostCardInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string TrustState { get; init; } = "";
    public Brush Accent { get; init; } = Brushes.Aquamarine;
}

public partial class MainWindow : Window
{
    private static string UiVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private readonly ObservableCollection<MapInfo> _maps = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Forms.NotifyIcon _tray = new();
    private bool _exit;
    private MapInfo? _selected;
    private Guid? _preferredModMapId;
    private Guid? _preferredPluginMapId;
    private string _page = "dashboard";
    private bool _animationsEnabled = true;
    private string SettingsFile => Path.Combine(App.LogDir,"visual-settings.json");
    private readonly string[] _modNames = {
        "S-Dino Variants (912815)", "Shiny! Dinos (928548)",
        "Utilities Plus (928621)", "Cliffans Backpacks (930170)",
        "Arkitect Structures Remastered (931874)", "Visual Storage (935528)",
        "Cybers Structures QoL+ (940975)", "Dino Depot (942024)",
        "Awesome Teleporters (950914)", "Runic Wyverns (974884)",
        "CKF (1149214)", "Super Spyglass Plus", "Fear Ascended",
        "Dynamic Survivors", "Hair Style", "Immersive Taming",
        "Xyphias' Hatzegopteryx", "Moro's Tylosaurus"
    };
    private readonly string[] _plugins = {
        "ASA API / AsaApiLoader", "ArkShop", "ArkShopUI", "Permissions 1.1",
        "Advanced Messages Ascended", "Rio Anti-PvE-Griefing",
        "CousinCustomRates 1.5", "ImplantFix",
        "Unicode RCON ASA", "Lethal Quests Ascended"
    };
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(17,45,44));
    private static readonly Brush Mint = new SolidColorBrush(Color.FromRgb(166,248,115));
    private static readonly Brush Aqua = new SolidColorBrush(Color.FromRgb(108,225,203));
    private static readonly Brush Soft = new SolidColorBrush(Color.FromRgb(159,190,183));

    public MainWindow()
    {
        InitializeComponent();
        VersionBadge.Text="WPF "+UiVersion;
        InitializeAutomaticUpdates();
        InitializeSupervision();
        InitializeLocalResourceTelemetry();
        // Only artwork mapping is static; actual map/port/host/mod data comes from
        // registered local profiles and authenticated, fresh LAN peer snapshots.
        var rows = new (string name, string a, string b)[] {
            ("Astraeos","#244A57","#4B8C72"),
            ("Genesis Part 1","#2B255E","#7053A9"),
            ("The Island","#28606B","#4F9274"),
            ("Extinction","#4A464C","#7C7D62"),
            ("Valguero","#165579","#559AB7"),
            ("Ragnarok","#554046","#A16A43"),
            ("Lost Colony","#4C2858","#8A5A96"),
            ("Scorched Earth","#6A4826","#B47D3D"),
            ("The Center","#245E58","#4D9B72"),
            ("Aberration","#2F295E","#8B4FA2")
        };
        var referenceAtlas = new BitmapImage(new Uri("pack://application:,,,/Assets/reference-atlas.jpg",UriKind.Absolute));
        referenceAtlas.Freeze();
        var brandCrop = new CroppedBitmap(referenceAtlas,new Int32Rect(1000,0,200,200));
        brandCrop.Freeze();
        BrandArtwork.Source = brandCrop;
        var footerCrop = new CroppedBitmap(referenceAtlas,new Int32Rect(1000,260,200,140));
        footerCrop.Freeze();
        FooterArtwork.Source = footerCrop;
        // Reference painting only: true buttons, titles, counters and badges remain live WPF controls.
        var heroCrop = new CroppedBitmap(referenceAtlas,new Int32Rect(180,38,820,160));
        heroCrop.Freeze();
        HeroImage.Source = heroCrop;
        int mapIndex = 0;
        foreach (var (name,a,b) in rows)
        {
            var gradient = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(a),
                (Color)ColorConverter.ConvertFromString(b), 20);
            gradient.Freeze();
            var photo = new CroppedBitmap(referenceAtlas,new Int32Rect((mapIndex % 5)*200,260+(mapIndex/5)*104,200,96));
            photo.Freeze();
            mapIndex++;
            _maps.Add(new MapInfo { Name=name, CoverBrush=gradient, CoverImage=photo });
        }
        ServerCards.ItemsSource = _maps;
        try {
            if(File.Exists(SettingsFile)) {
                using var doc=JsonDocument.Parse(File.ReadAllText(SettingsFile));
                if(doc.RootElement.TryGetProperty("animations",out var a) && a.ValueKind==JsonValueKind.False) _animationsEnabled=false;
            }
        } catch (Exception e) { WriteLog("settings",e); }
        UpdateNavSelection("dashboard");
        if (!App.IsSmokeTest) SetupTray();
        StartClusterUi();
        if(App.IsDemoStart)EnterDemoMode();
    }

    private void SetupTray()
    {
        try
        {
            var exe = Environment.ProcessPath;
            _tray.Icon = exe is not null ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : System.Drawing.SystemIcons.Application;
            _tray.Text = "Untamed Manager";
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Megnyitás",null,(_,_)=>Dispatcher.Invoke(Restore));
            menu.Items.Add("Frissítések",null,(_,_)=>Dispatcher.Invoke(()=>{Restore();Navigate("license");}));
            menu.Items.Add("Kilépés",null,(_,_)=>Dispatcher.Invoke(Exit));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_,_)=>Dispatcher.Invoke(Restore);
            _tray.Visible = true;
        }
        catch(Exception ex) { WriteLog("tray",ex); }
    }
    private void Restore() { Show(); WindowState=WindowState.Normal; Activate(); }
    private void Exit()
    {
        _exit=true;
        EndClusterUi();
        _tray.Visible=false;_tray.Dispose();_http.Dispose();
        Close();System.Windows.Application.Current.Shutdown();
    }
    private void Window_Closing(object? sender,CancelEventArgs e)
    {
        if (!_exit) {e.Cancel=true;Hide();}
    }
    private void Window_Loaded(object sender,RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => {
            Dashboard.ScrollToTop();
            Dashboard.ScrollToVerticalOffset(0);
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if(_animationsEnabled) try { AnimateBackground(); } catch(Exception ex) { WriteLog("animation",ex); }
        // Dismiss only after the actual WPF window has loaded and rendered.
        Dispatcher.BeginInvoke(new Action(() => {
            if(System.Windows.Application.Current is App app)app.NotifyMainUiReady(this);
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    private void AnimateBackground()
    {
        if (HeroImage.RenderTransform is ScaleTransform imageScale)
        {
            var zoom = new DoubleAnimation(1.015,1.07,TimeSpan.FromSeconds(30))
            { AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever };
            imageScale.BeginAnimation(ScaleTransform.ScaleXProperty,zoom);
            imageScale.BeginAnimation(ScaleTransform.ScaleYProperty,zoom);
        }
        var glow = FindName("AmbientGlow") as FrameworkElement;
        if (glow is not null)
        {
            var anim = new DoubleAnimation(0.48,0.83,TimeSpan.FromSeconds(9))
              { AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever };
            glow.BeginAnimation(OpacityProperty,anim);
        }
    }

    private static void WriteLog(string area,Exception ex)
    {
        try
        {
            Directory.CreateDirectory(App.LogDir);
            File.AppendAllText(Path.Combine(App.LogDir,"operations.log"),
                UntamedPresentation.Redact($"{DateTimeOffset.Now:O} [{area}] {ex}\n"));
        }
        catch { }
    }
    private static void Notice(string message) =>
        WinMessageBox.Show(UntamedPresentation.Redact(message),"Untamed Manager",MessageBoxButton.OK,MessageBoxImage.Information);

    private void Navigate_Click(object sender,RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page) Navigate(page);
    }
    private void Map_Click(object sender,RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MapInfo map)
        {
            if(_demo!=null)
            {
                _demoSelectedId=map.Id;
                Navigate("servers");
                return;
            }
            _selected = map;
            Navigate(map.IsRemote ? "cluster" : "servers");
        }
    }
    private void UpdateNavSelection(string page)
    {
        foreach(var element in NavigationMenu.Children)
        {
            if (element is not Button button || button.Tag is not string tag) continue;
            NavSelection.SetIsCurrent(button,tag==page);
        }
    }
    private void SetAnimations(bool enabled)
    {
        _animationsEnabled=enabled;
        try
        {
            Directory.CreateDirectory(App.LogDir);
            File.WriteAllText(SettingsFile,JsonSerializer.Serialize(new {animations=enabled}));
            if(enabled) AnimateBackground();
            else
            {
                if(HeroImage.RenderTransform is ScaleTransform x)
                {
                    x.BeginAnimation(ScaleTransform.ScaleXProperty,null);
                    x.BeginAnimation(ScaleTransform.ScaleYProperty,null);
                    x.ScaleX=1.03;x.ScaleY=1.03;
                }
                AmbientGlow.BeginAnimation(UIElement.OpacityProperty,null);
                AmbientGlow.Opacity=.12;
            }
        } catch(Exception ex) { WriteLog("visual-settings",ex); }
    }
    private void Navigate(string page)
    {
        _page=page;
        UpdateNavSelection(page);
        Dashboard.Visibility=page=="dashboard" ? Visibility.Visible : Visibility.Collapsed;
        OtherPage.Visibility=page=="dashboard" ? Visibility.Collapsed : Visibility.Visible;
        if (page=="dashboard")
        {
            if(_demo!=null)RenderDemoDashboard();
            Dashboard.ScrollToTop();
            return;
        }
        PageBody.Children.Clear();
        OtherPage.ScrollToTop();
        if(_demo!=null){ShowDemoPage(page);return;}
        switch(page)
        {
            case "servers": ShowLocalServerPage(); break;
            case "cluster": ShowClusterPage(); break;
            case "lan": ShowLanSettings(); break;
            case "mods": Mods(); break;
            case "plugins": Plugins(); break;
            case "ini": Ini(); break;
            case "backups": Backups(); break;
            case "data": ShowDataSafetyCenter(); break;
            case "steam": ShowSteamUpdateCenter(); break;
            case "rcon": Rcon(); break;
            case "schedules": ShowBackgroundSchedules(); break;
            case "monitoring": ShowMonitoring(); break;
            case "network": ShowNetworkDiagnostics(); break;
            case "discord": ShowDiscordStatusPage(); break;
            case "recovery": ShowRecoveryCenter(); break;
            case "license": ShowUpdateCenter(); break;
            case "readiness": ShowReleaseReadiness(); break;
            case "extensions": ShowManagerExtensions(); break;
            case "settings": Settings(); break;
            case "setup": ShowSetupWizard(); break;
        }
    }
    private void Header(string title,string description)
    {
        PageTitle.Text=title;PageDescription.Text=description;
    }
    private static TextBlock Label(string text,int size=14,Brush? color=null,bool bold=false) =>
       new() { Text=UntamedPresentation.Display(text),Foreground=color??Brushes.White,FontSize=size,
           TextWrapping=TextWrapping.Wrap,FontWeight=bold?FontWeights.Bold:FontWeights.Normal,
           Margin=new Thickness(0,0,0,10) };
    private static Border Box(UIElement child) =>
       new() { Background=Panel,BorderBrush=new SolidColorBrush(Color.FromRgb(55,97,85)),
           BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),
           Padding=new Thickness(18),Margin=new Thickness(0,0,0,12),Child=child };
    private static StackPanel V(params UIElement[] elements)
    {
        var p=new StackPanel();
        foreach(var e in elements)p.Children.Add(e);
        return p;
    }
    private static Button Btn(string text, Action action)
    {
        var b=new Button { Content=UntamedPresentation.Display(text),Style=(Style)System.Windows.Application.Current.Resources["ActionButton"] };
        b.Click+=(_,_)=>action();return b;
    }
    private void Add(UIElement e) => PageBody.Children.Add(e);
    private static ListBox List(IEnumerable<string> entries,int height=310) =>
       new(){ ItemsSource=entries.ToArray(),Height=height,Foreground=Brushes.White,
             Background=Panel,BorderThickness=new Thickness(0),FontSize=13,
             Padding=new Thickness(8)};

    private void Servers()
    {
        Header("Szerverek","A 10 térkép és az auditált portok. Nincs élő agentkapcsolat; az online státuszt nem szimuláljuk.");
        var grid=new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(245)});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        var list=new ListBox { ItemsSource=_maps,DisplayMemberPath="Name",Foreground=Brushes.White,
            Background=Panel,BorderBrush=Aqua,MinHeight=450,Margin=new Thickness(0,0,16,0),
            FontSize=15,Padding=new Thickness(8)};
        var detail=new StackPanel();
        Grid.SetColumn(detail,1);grid.Children.Add(list);grid.Children.Add(detail);
        list.SelectionChanged+=(_,_)=> {
            detail.Children.Clear();
            if (list.SelectedItem is not MapInfo m) return;
            _selected=m;
            detail.Children.Add(Label(m.Name,28,Brushes.White,true));
            detail.Children.Add(Label($"{m.Host} · {m.ModCount} mod · 2026.11.02-i audit",14,Aqua));
            detail.Children.Add(Box(V(
                Label("Hálózat",18,Mint,true),
                Label($"Game UDP: {m.GamePort}   ·   Query: {m.QueryPort}   ·   RCON: {m.RconPort}"),
                Label("Untamed Cluster Vault · kompatibilis adattár",12,Soft))));
            detail.Children.Add(Label("Szervervezérlés",18,Brushes.White,true));
            detail.Children.Add(Box(V(Label("Start, Stop és Restart: biztonsági okból tiltva, amíg a hostagent nem ellenőrzött.",13,Soft))));
            detail.Children.Add(Label("Modpéldák az auditcsomagból",16,Brushes.White,true));
            detail.Children.Add(List(_modNames,240));
        };
        list.SelectedItem=_selected??_maps.FirstOrDefault();
        Add(grid);
    }
    private void Mods()
    {
        Header("Mod Center","Helyi mapok tényleges indítási modlistája és a beépített CurseForge kereső.");
        var summary=Label("Modlista beolvasása...",13,Soft);
        var search=new TextBox{Height=40,Background=Panel,Foreground=Brushes.White,
            BorderBrush=Aqua,FontSize=15,Padding=new Thickness(10),
            ToolTip="Keresés a helyi mapnevek és konfigurált modazonosítók között"};
        var results=List(Array.Empty<string>(),250);
        string[] rows=Array.Empty<string>();
        void Filter() => results.ItemsSource=rows
            .Where(s=>s.Contains(search.Text,StringComparison.OrdinalIgnoreCase))
            .ToArray();
        void Refresh()
        {
            try
            {
                var snapshot=LocalContentSnapshotReader.Inspect(_cluster.Config.LocalMaps);
                var mods=snapshot.Mods;
                rows=mods.Maps.SelectMany(m=>m.Error!=null
                    ?new[]{m.Name+" · HIBÁS MODPARAMÉTER · kézi ellenőrzést igényel"}
                    :m.OrderedIds.Count==0
                        ?new[]{m.Name+" · nincs konfigurált mod"}
                        :m.OrderedIds.Select((id,i)=>m.Name+" · "+(i+1)+". · "+id))
                    .ToArray();
                if(rows.Length==0)rows=new[]{
                    "Nincs helyi map. A Szerverek oldalon manuálisan regisztrálhatsz mapokat."};
                summary.Text=mods.Maps.Count+" helyi map · "+
                    snapshot.KnownModAssignments+" igazolt konfigurációs modhozzárendelés · "+
                    mods.UniqueIdCount+" különböző ID · "+
                    (mods.Complete?"a helyi konfigurációk olvashatók":
                    "HIÁNYOS LELTÁR – legalább egy map modparaméterei hibásak")+
                    "\nEz az indítási lista; nem bizonyít telepítést vagy futásidejű modbetöltést.";
                Filter();
            }
            catch(Exception ex)
            {
                WriteLog("mod-center-inventory",ex);
                rows=new[]{"A helyi modleltár nem ellenőrizhető; az adatok ismeretlenek."};
                summary.Text="MODLELTÁR NEM ELLENŐRIZHETŐ – a szerverállapot nem módosult.";
                Filter();
            }
        }
        search.TextChanged+=(_,_)=>Filter();
        Add(Box(V(Label("Helyi mapok konfigurált modjai",20,Mint,true),
            summary,Btn("Helyi modleltár frissítése",Refresh),
            Label("Mapnév vagy mod ID keresése a helyi konfigurációban",12,Aqua),
            search,results)));
        Refresh();
        ShowLocalModAssignmentInventory();
        ShowModStateEditor();
        ShowBulkModsControls();
        ShowSteamCmdPreflightControls();
        ShowAsaInstallationPreflightControls();
        ShowCurseForgeSearchControls();
        ShowModUpdateCenter();
    }
    private void Plugins()
    {
        Header("ASA API / pluginok","Maponként ellenőrzött helyi pluginmappák és a hivatalos ASA API kezelése.");
        var summary=Label("Helyi pluginleltár beolvasása...",13,Soft);
        var list=List(Array.Empty<string>(),250);
        void Refresh()
        {
            try
            {
                var snapshot=LocalContentSnapshotReader.Inspect(_cluster.Config.LocalMaps);
                var rows=snapshot.Plugins.Select(p=>p.MapName+" · "+
                    (p.Enabled?"ENGEDÉLYEZETT":"LETILTOTT")+" · "+p.PluginName)
                    .Concat(snapshot.UnreadablePluginMaps.Select(name=>
                        name+" · PLUGINLELTÁR NEM OLVASHATÓ"))
                    .ToArray();
                list.ItemsSource=rows.Length==0
                    ?new[]{snapshot.LocalMapCount==0
                        ?"Nincs helyi map – előbb regisztrálj egyet a Szerverek oldalon."
                        :"A vizsgált helyi telepítéseken nincs pluginmappa."}
                    :rows;
                summary.Text=snapshot.LocalMapCount+" helyi map · "+
                    snapshot.Plugins.Count+" megfigyelt map-plugin hozzárendelés · "+
                    snapshot.ReadablePluginMaps+" / "+snapshot.LocalMapCount+" map leltározva"+
                    (snapshot.PluginInventoryComplete?"":" · HIÁNYOS/ISMERETLEN")+
                    "\nA lemezen lévő plugin nem bizonyítja az ASA API futásidejű betöltését.";
            }
            catch(Exception ex)
            {
                WriteLog("plugin-center-inventory",ex);
                summary.Text="PLUGINLELTÁR NEM ELLENŐRIZHETŐ – a szerverállapot nem módosult.";
                list.ItemsSource=new[]{"A mapok telepítési állapotát ellenőrizni kell."};
            }
        }
        Add(Box(V(Label("Helyi telepítések és pluginmappák",20,Mint,true),
            summary,Btn("Helyi pluginleltár frissítése",Refresh),list)));
        Refresh();
        ShowLocalPluginInventoryControls();
        ShowBulkPluginsControls();
        ShowPluginRollbackControls();
        ShowAsaApiAndPluginStates();
        ShowSafeTextEditor(ConfigKind.PluginJson);
    }
    private void Ini()
    {
        Header("INI Editor 2.0","SHA-256 ellenőrzés, BOM/sorvégek megőrzése, atomikus mentés és .bak védőmásolat.");
        ShowSafeTextEditor(ConfigKind.GameIni);
    }
    private void Backups()
    {
        Header("Mentések","Helyi mapfájlok és háromadatbázisos SQL-mentés; visszatöltés külön üres SQL-célra.");
        Add(Box(V(Label("Untamed Cluster Vault · védett",19,Mint,true),
            Label("Csak a kiválasztott helyi map fájljairól készül ellenőrzött másolat. "+
                  "A közös Cluster Vault és a karakterátvitel nem része ennek a fájlmentésnek.",12,Soft))));
        ShowSqlBackupControls();
        if(_cluster.Config.LocalMaps.Count==0)
        {
            Add(Label("Nincs kézzel regisztrált helyi map. Előbb a Szerverek nézetben adj hozzá egyet.",13,Soft));
            return;
        }
        ShowMultiMapBackupOverview();
        var choice=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",SelectedIndex=0};
        var pane=new StackPanel();
        void SelectMap()
        {
            pane.Children.Clear();
            if(choice.SelectedItem is LocalMap map)pane.Children.Add(MapBackupPane(map));
        }
        choice.SelectionChanged+=(_,_)=>SelectMap();
        Add(Box(V(Label("Helyi map kiválasztása",13,Aqua),choice,pane)));
        SelectMap();
        ShowRetentionPreviewControls();
    }
    private void Rcon()
    {
        Header("RCON konzol","A távoli parancsküldés a külön tesztgépen nincs engedélyezve.");
        Add(Box(V(Label("NINCS AGENTKAPCSOLAT",21,Mint,true),
            Label("A hitelesített hostagent és a szerverenkénti jogosultságellenőrzés bevezetéséig nem küldünk parancsokat éles gépre.",13,Soft))));
        var input=new TextBox{Text="ListPlayers",IsEnabled=false,Height=40,Background=Panel,Foreground=Brushes.White,FontSize=15};
        Add(input);
        ShowRconActionPreviews();
    }
    private void Schedules()
    {
        Header("Ütemezések","A korábbi két gazdagép feladatainak történeti összefoglalója.");
        Add(Box(V(Label("20 auditált ütemezett feladat",20,Mint,true),
            Label("A legutóbbi eltárolt 'skipped' eredmény nem bizonyítja, hogy az aktuális ütemezés hibás.",13,Soft))));
        foreach(var item in new[]{"Cluster Vault Backup / világmentések","Untamed Watchdog / AutoStart",
              "Napi 07:30 vad dínó wipe","Egyedi boostok, restartok és házirendek"})
            Add(Box(V(Label(item,16,Brushes.White,true),Label("Csak történeti auditadat, szerkesztés tiltva.",12,Soft))));
    }
    private void License()
    {
        Header("Licenc / Frissítés","A korábbi Cloudflare-háttér elérhetőségének tesztje. A korábbi titkos licencet nem másoljuk át automatikusan.");
        var response=Label("Még nem ellenőrizve",13,Soft);
        Add(Box(V(Label("Untamed License Hub",20,Mint,true),
            Label("untamed-license-hub.untamedkingdom.workers.dev",12,Aqua),
            Label("Ez a WPF-előnézet még nem aktiválhat licenckulcsot; a meglévő hitelesítést nem módosítja.",12,Soft))));
        Add(Btn("Cloudflare /health ellenőrzése",async ()=>{
            response.Text="Kapcsolódás...";
            try {
                var res=await _http.GetStringAsync("https://untamed-license-hub.untamedkingdom.workers.dev/health");
                using var doc=JsonDocument.Parse(res);
                response.Text=doc.RootElement.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.True
                    ?"API elérhető. Ez még nem licencaktiválás.":"API nem adott megfelelő választ.";
            }catch(Exception ex){WriteLog("health",ex);response.Text="Kapcsolódási hiba: "+ex.Message;}
        }));
        Add(response);
        Add(Label("Automatikus önfrissítés: további aláírás- és visszaállítási teszt szükséges.",12,Soft));
    }
    private void Settings()
    {
        Header("Beállítások","Windows-értesítési terület és diagnosztika.");
        Add(Box(V(Label("Windows integráció",19,Mint,true),
             Label("Az X gomb tálcára rejti az alkalmazást. Végleges kilépés: tálcaikon → Kilépés.",13,Soft))));
        var animated=new CheckBox { Content="Animált háttér és finom fényhatások", IsChecked=_animationsEnabled,
            Foreground=Brushes.White,FontSize=14,Margin=new Thickness(0,12,0,12) };
        animated.Checked+=(_,_)=>SetAnimations(true);
        animated.Unchecked+=(_,_)=>SetAnimations(false);
        Add(Box(V(Label("Megjelenés / animáció",19,Mint,true),
            Label("A mozgás külön kikapcsolható; a gombok és menük továbbra is működnek.",12,Soft), animated)));
        ShowSafeProfileTransfer();
        Add(Box(V(Label("Naplózás és diagnosztika",19,Mint,true),
             Label("A naplómappa csak helyben vizsgálható. A támogatási ZIP külön titokszűréssel készül.",12,Soft),
             Btn("Diagnosztikai mappa megnyitása",()=>{
                 Process.Start(new ProcessStartInfo{FileName=App.LogDir,UseShellExecute=true});
             }))));
        ShowSafeSupportCenter();
    }
}

