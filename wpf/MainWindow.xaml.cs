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
using System.ComponentModel;
using Forms = System.Windows.Forms;
using WinMessageBox = System.Windows.MessageBox;

namespace UntamedManager;

public sealed class MapInfo
{
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int ModCount { get; init; }
    public int GamePort { get; init; }
    public int QueryPort { get; init; }
    public int RconPort { get; init; }
    public string Caption => $"{Host} · {ModCount} mod · UDP {GamePort}";
    public Brush CoverBrush { get; init; } = Brushes.DarkSlateGray;
    public ImageSource? CoverImage { get; init; }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MapInfo> _maps = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Forms.NotifyIcon _tray = new();
    private bool _exit;
    private MapInfo? _selected;
    private string _page = "dashboard";
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
        var rows = new (string name, string host, int mods, int game, int query, int rcon, string a, string b)[] {
            ("Astraeos","HP",30,7847,27016,27021,"#244A57","#4B8C72"),
            ("Genesis Part 1","HP",29,7867,27018,27023,"#2B255E","#7053A9"),
            ("The Island","HP",29,7777,27015,27020,"#28606B","#4F9274"),
            ("Extinction","HP",30,7817,27017,27022,"#4A464C","#7C7D62"),
            ("Valguero","HP",29,7837,27019,27024,"#165579","#559AB7"),
            ("Ragnarok","Ryzen",29,7827,27025,27030,"#554046","#A16A43"),
            ("Lost Colony","Ryzen",30,7857,27028,27033,"#4C2858","#8A5A96"),
            ("Scorched Earth","Ryzen",29,7787,27026,27031,"#6A4826","#B47D3D"),
            ("The Center","Ryzen",29,7797,27027,27032,"#245E58","#4D9B72"),
            ("Aberration","Ryzen",30,7807,27029,27034,"#2F295E","#8B4FA2")
        };
        var master = new BitmapImage(new Uri("pack://application:,,,/Assets/kingdom-hero.jpg", UriKind.Absolute));
        master.Freeze();
        int mapIndex = 0;
        foreach (var (name,host,mods,game,query,rcon,a,b) in rows)
        {
            var gradient = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a),
                (Color)ColorConverter.ConvertFromString(b), 20);
            gradient.Freeze();
            int cropX = 90 + (mapIndex * 43) % Math.Max(1,master.PixelWidth - 306);
            int cropY = (mapIndex++ * 17) % Math.Max(1,master.PixelHeight - 109);
            var photo = new CroppedBitmap(master,new Int32Rect(cropX,cropY,216,108));
            photo.Freeze();
            _maps.Add(new MapInfo { Name=name, Host=host, ModCount=mods, GamePort=game,
                QueryPort=query,RconPort=rcon,CoverBrush=gradient,CoverImage=photo });
        }
        ServerCards.ItemsSource = _maps;
        SetupTray();
    }

    private void SetupTray()
    {
        try
        {
            var exe = Environment.ProcessPath;
            _tray.Icon = exe is not null ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : System.Drawing.SystemIcons.Application;
            _tray.Text = "Untamed Kingdom Manager";
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
        _tray.Visible=false;_tray.Dispose();_http.Dispose();
        Close();System.Windows.Application.Current.Shutdown();
    }
    private void Window_Closing(object? sender,CancelEventArgs e)
    {
        if (!_exit) {e.Cancel=true;Hide();}
    }
    private void Window_Loaded(object sender,RoutedEventArgs e)
    {
        try { AnimateBackground(); } catch(Exception ex) { WriteLog("animation",ex); }
    }
    private void AnimateBackground()
    {
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
                $"{DateTimeOffset.Now:O} [{area}] {ex}\n");
        }
        catch { }
    }
    private static void Notice(string message) =>
        WinMessageBox.Show(message,"Untamed Kingdom Manager",MessageBoxButton.OK,MessageBoxImage.Information);

    private void Navigate_Click(object sender,RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page) Navigate(page);
    }
    private void Map_Click(object sender,RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MapInfo map)
        {
            _selected = map;
            Navigate("servers");
        }
    }
    private void Navigate(string page)
    {
        _page=page;
        Dashboard.Visibility=page=="dashboard" ? Visibility.Visible : Visibility.Collapsed;
        OtherPage.Visibility=page=="dashboard" ? Visibility.Collapsed : Visibility.Visible;
        if (page=="dashboard") { Dashboard.ScrollToTop(); return; }
        PageBody.Children.Clear();
        OtherPage.ScrollToTop();
        switch(page)
        {
            case "servers": Servers(); break;
            case "mods": Mods(); break;
            case "plugins": Plugins(); break;
            case "ini": Ini(); break;
            case "backups": Backups(); break;
            case "rcon": Rcon(); break;
            case "schedules": Schedules(); break;
            case "license": License(); break;
            case "settings": Settings(); break;
        }
    }
    private void Header(string title,string description)
    {
        PageTitle.Text=title;PageDescription.Text=description;
    }
    private static TextBlock Label(string text,int size=14,Brush? color=null,bool bold=false) =>
       new() { Text=text,Foreground=color??Brushes.White,FontSize=size,
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
        var b=new Button { Content=text,Style=(Style)System.Windows.Application.Current.Resources["ActionButton"] };
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
                Label("KovaCluster / -usestore · csak olvasás",12,Soft))));
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
        Header("Mod Center","CurseForge-tartalom és maponkénti modhozzárendelések. A jelenlegi nézet történeti auditot mutat.");
        Add(Box(V(Label("10 map  ·  294 modhozzárendelés",21,Mint,true),
            Label("A szervereken megfigyelt modok egy része; a teljes élő telepítés még nincs engedélyezve.",12,Soft))));
        var input=new TextBox{Height=40,Background=Panel,Foreground=Brushes.White,
            BorderBrush=Aqua,FontSize=15,Padding=new Thickness(10),ToolTip="Modkeresés"};
        var results=List(_modNames,340);
        input.TextChanged+=(_,_)=>results.ItemsSource=_modNames.Where(m=>m.Contains(input.Text,StringComparison.OrdinalIgnoreCase)).ToArray();
        Add(Label("Keresés a leltárban",13,Soft));Add(input);Add(results);
        Add(Label("CurseForge online telepítés: az ügynök és a telepítő tesztelése után kapcsolható be.",12,Soft));
    }
    private void Plugins()
    {
        Header("ASA API / pluginok","A két gazdagépen összegyűjtött pluginnevek. A fájl jelenléte nem bizonyítja a futás közbeni betöltést.");
        Add(Box(V(Label("ASA API pluginleltár",20,Mint,true),
            Label("Az auditált pluginrekordok száma 100. Itt a közös pluginfajták láthatók.",12,Soft))));
        Add(List(_plugins,380));
        Add(Label("A maponkénti DLL-állapotok végleges kezelése külön agentintegrációt igényel.",12,Soft));
    }
    private void Ini()
    {
        Header("INI szerkesztő","Kizárólag helyi, tesztelésre kiválasztott Game.ini / GameUserSettings.ini fájlok. Mentés előtt .bak készül.");
        var path=Label("Nincs fájl kiválasztva",13,Soft);
        var input=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            Background=Panel,Foreground=Brushes.White,BorderBrush=Aqua,
            FontFamily=new FontFamily("Consolas"),FontSize=12,Height=360,
            IsEnabled=false};
        string? location=null;
        var open=Btn("Helyi INI megnyitása",()=>{
            var dlg=new Microsoft.Win32.OpenFileDialog
            {Filter="ARK ini fájlok|Game.ini;GameUserSettings.ini",CheckFileExists=true};
            if(dlg.ShowDialog(this)!=true)return;
            var name=Path.GetFileName(dlg.FileName);
            if (name!="Game.ini" && name!="GameUserSettings.ini"){Notice("Csak Game.ini vagy GameUserSettings.ini engedélyezett.");return;}
            if(dlg.FileName.Contains("KovaCluster",StringComparison.OrdinalIgnoreCase)){Notice("Cluster Store-hoz nem nyúlunk.");return;}
            location=dlg.FileName;path.Text=location;input.Text=File.ReadAllText(location);input.IsEnabled=true;
        });
        var save=Btn("Mentés biztonsági másolattal",()=>{
            if(location is null)return;
            if(WinMessageBox.Show("Mented a kiválasztott helyi INI fájlt?","INI",MessageBoxButton.YesNo,
                 MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            var temp=location+".untamed.tmp";var backup=location+".untamed."+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".bak";
            File.Copy(location,backup,false);File.WriteAllText(temp,input.Text,Encoding.UTF8);
            File.Move(temp,location,true);Notice("Mentve. Biztonsági másolat: "+backup);
        });
        var actions=new StackPanel{Orientation=Orientation.Horizontal};
        actions.Children.Add(open);actions.Children.Add(save);
        Add(Box(V(path,actions,input)));
    }
    private void Backups()
    {
        Header("Mentések","A karakter- és clusteradatok védelme elsődleges. Élő mentési és visszaállítási műveletek még nem aktívak.");
        Add(Box(V(Label("Kova Store · védett",23,Mint,true),
            Label("Nem módosítjuk a KovaCluster formátumát. Az 1:1 kompatibilitás csak izolált mentés-visszaállítási tesztek után engedélyezhető.",13,Soft))));
        Add(Box(V(Label("Helyi tesztmentés",18,Brushes.White,true),
          Label("A jelenlegi kiadás nem készít mentést a HP/Ryzen szerverekről.",13,Soft))));
    }
    private void Rcon()
    {
        Header("RCON konzol","A távoli parancsküldés a külön tesztgépen nincs engedélyezve.");
        Add(Box(V(Label("NINCS AGENTKAPCSOLAT",21,Mint,true),
            Label("A hitelesített hostagent és a szerverenkénti jogosultságellenőrzés bevezetéséig nem küldünk parancsokat éles gépre.",13,Soft))));
        var input=new TextBox{Text="ListPlayers",IsEnabled=false,Height=40,Background=Panel,Foreground=Brushes.White,FontSize=15};
        Add(input);
    }
    private void Schedules()
    {
        Header("Ütemezések","A korábbi két gazdagép feladatainak történeti összefoglalója.");
        Add(Box(V(Label("20 auditált ütemezett feladat",20,Mint,true),
            Label("A legutóbbi eltárolt 'skipped' eredmény nem bizonyítja, hogy az aktuális ütemezés hibás.",13,Soft))));
        foreach(var item in new[]{"Kova Backup / világmentések","Kova Watchdog / AutoStart",
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
        Add(Box(V(Label("Naplózás",19,Mint,true),
             Label(App.LogDir,12,Soft),
             Btn("Diagnosztikai mappa megnyitása",()=>{
                Process.Start(new ProcessStartInfo{FileName=App.LogDir,UseShellExecute=true});
             }))));
    }
}
