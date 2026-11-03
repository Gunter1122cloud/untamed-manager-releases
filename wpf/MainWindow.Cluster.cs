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
    private readonly ClusterService _cluster = new(App.ProfileName);
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
        if (!App.IsSmokeTest) _ = BeginClusterNetworkingAsync();
    }
    private async Task BeginClusterNetworkingAsync()
    {
        try { await _cluster.BeginLanAsync(); }
        catch (Exception ex) { WriteLog("LAN-listener",ex); }
        _peerLoop=Task.Run(async ()=>{
            while(!_peerCts.IsCancellationRequested)
            {
                try {
                    await _cluster.PollPeerAsync(_peerCts.Token);
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
        foreach(var m in _cluster.Config.LocalMaps)
        {
            images.TryGetValue(m.Name,out var art);
            active.Add(new MapInfo {
                Id=m.Id,Name=m.Name,Host=_cluster.Config.LocalHostName,
                ModCount=0,GamePort=m.GamePort,
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
                    Name=m.Name,Host=_cluster.RemoteSnapshot.HostName,ModCount=0,
                    GamePort=m.GamePort,CoverImage=art?.CoverImage,
                    CoverBrush=art?.CoverBrush ?? Panel,IsRemote=true
                });
            }
        }
        ServerCards.ItemsSource=active;
        if(ClusterMapCount!=null) ClusterMapCount.Text=active.Count.ToString();
        if(ClusterHostCount!=null) ClusterHostCount.Text=_cluster.RemoteSnapshot==null?"1":"2";
        if(ClusterOnlineCount!=null) ClusterOnlineCount.Text=_cluster.RemoteIsFresh?"2 / 2":"1 / 2";
        if(ClusterStatusText!=null) ClusterStatusText.Text=
            $"{_cluster.Config.LocalHostName}: {_cluster.Config.LocalMaps.Count} helyi map · "+
            (_cluster.RemoteSnapshot==null?_cluster.RemoteMessage:
             $"{_cluster.RemoteSnapshot.HostName}: {_cluster.RemoteSnapshot.Maps.Count} távoli map · "+(_cluster.RemoteIsFresh?"friss, csak olvasás":"ELAVULT / OFFLINE"));
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
        startStop.Children.Add(Btn("Indítás (csak helyi)",()=>{
            try{
                if(WinMessageBox.Show($"Elindítod a helyi szervert?\n{map.Name}",
                    "Szerver indítása",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                _cluster.LaunchLocal(map.Id);
                Notice("Folyamat elindítva. A Manager bezárása nem állítja le.");
                ShowLocalServerPage();
            } catch(Exception e){WriteLog("launch",e);Notice("Indítás sikertelen: "+e.Message);}
        }));
        startStop.Children.Add(Btn("Státusz ellenőrzése",()=>{
            var x=_cluster.Check(map);
            Notice(x.Status);
        }));
        var stopUnavailable=Btn("Stop / Restart – mentésigazolás hiányzik",()=>{});
        stopUnavailable.IsEnabled=false;
        stopUnavailable.ToolTip="Az ASA mentés befejezését igazoló adapter még nincs validálva. RCON-válasz önmagában nem elég.";
        startStop.Children.Add(stopUnavailable);
        Tab("Áttekintés",V(
            Label("Végrehajtható fájl: "+map.ExecutablePath,12,Soft),
            Label("Indítási argumentumok: "+map.Arguments,12,Soft),
            Label($"Game {map.GamePort} / Query {map.QueryPort} / RCON {map.RconPort}",12,Aqua),
            Label(result.Running?"Futó szerver visszacsatlakoztatva":result.ConfirmedStopped?
                "Leállítva; automatikus indítás nincs":"Ismeretlen folyamatállapot; indítás tiltva",13,result.Running?Mint:Soft),
            startStop));
        Tab("Modok / CurseForge",V(
            Label("Helyi map modok",17,Mint,true),
            Label("A globális Mod Center megmarad; csak a helyi map indítási beállításai módosíthatók.",12,Soft),
            List(_modNames,230)));
        Tab("Pluginok / ASA API",V(Label("Helyi ASA API pluginok",17,Mint,true),
            Label("A telepítési mappát manuálisan állíthatod be. Távoli DLL-eket nem szerkesztünk.",12,Soft),
            List(_plugins,240)));
        Tab("INI",MapIniPane(map));
        Tab("RCON / Konzol",MapRconPane(map));
        Tab("Mentések",V(Label("Kova Store érintetlen",18,Mint,true),
            Label("A mentési és restore írási funkciók addig tiltva, amíg az 1:1 kompatibilitás nincs tesztelve.",12,Soft)));
        Tab("Naplók",MapLogPane(map));
        parent.Children.Add(tabs);
    }
    private UIElement MapIniPane(LocalMap map)
    {
        string iniRoot=Path.Combine(map.InstallationPath,"ShooterGame","Saved","Config","WindowsServer");
        var options=new[]{Path.Combine(iniRoot,"Game.ini"),Path.Combine(iniRoot,"GameUserSettings.ini")};
        var choice=new ComboBox{ItemsSource=options,SelectedIndex=0,Margin=new Thickness(0,6,0,9),
            Foreground=Brushes.White,Background=Panel};
        var editor=new TextBox {Height=240,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            FontFamily=new FontFamily("Consolas"),TextWrapping=TextWrapping.NoWrap,IsReadOnly=true};
        string? current=null;
        var load=Btn("Helyi INI beolvasása",()=>{
            var path=choice.SelectedItem as string;
            if(path==null||!File.Exists(path)){Notice("A kiválasztott INI nem található.");return;}
            if(!IsUnderRoot(path,map.InstallationPath)){Notice("INI elérési út kívül esik a helyi mapon.");return;}
            editor.Text=File.ReadAllText(path);current=path;editor.IsReadOnly=false;
        });
        var save=Btn("Biztonsági mentés + mentés",()=>{
            if(current==null || !IsUnderRoot(current,map.InstallationPath))return;
            try{
                if(WinMessageBox.Show($"Helyi INI mentése? A futó szerver csak újraindításkor olvashatja be.\n{current}",
                    "INI mentése",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                var backup=current+".untamed."+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".bak";
                File.Copy(current,backup,false);
                var temp=current+".untamed.tmp";File.WriteAllText(temp,editor.Text,Encoding.UTF8);File.Move(temp,current,true);
                Notice("INI mentve; az eredeti fájlról másolat készült.");
            }catch(Exception ex){WriteLog("ini",ex);Notice(ex.Message);}
        });
        return V(Label("Csak a helyi map WindowsServer/Game.ini vagy GameUserSettings.ini fájlja.",12,Soft),
            choice,load,save,editor);
    }
    private static bool IsUnderRoot(string path,string root)
    {
        if(string.IsNullOrWhiteSpace(root))return false;
        string parent=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string full=Path.GetFullPath(path);
        return full.StartsWith(parent,StringComparison.OrdinalIgnoreCase) &&
            !full.Contains("KovaCluster",StringComparison.OrdinalIgnoreCase);
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
                    if(!_cluster.Check(map).Running)throw new InvalidOperationException("A helyi szerver nem fut");
                    feedback.Text=await RconProtocol.Execute(map.RconHost,map.RconPort,password,command.Text);
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
        form.Children.Add(Label("Map neve",13,Aqua,true));form.Children.Add(names);
        Field("Map telepítési gyökérkönyvtára",map.InstallationPath);
        Field("Szerver EXE teljes elérési útja",map.ExecutablePath);
        Field("Indítási paraméterek (pontos egyezés)",map.Arguments);
        Field("API-loader EXE (opcionális)",map.ApiLoaderPath);
        Field("API-loader argumentumok",map.ApiLoaderArguments);
        var maintenance=new CheckBox{Content="Maintenance – helyi indítás tiltva",IsChecked=map.Maintenance,Foreground=Brushes.White};
        form.Children.Add(maintenance);
        var monitorRcon=new CheckBox{Content="Hitelesített RCON-monitoring (ListPlayers)",IsChecked=map.MonitorRcon,Foreground=Brushes.White};
        form.Children.Add(monitorRcon);
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
                var name=names.Text.Trim();
                if(!KnownMaps.Contains(name,StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Válassz egy ismert mapot a listából");
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
                var target=existing??new LocalMap();
                target.Name=name;target.InstallationPath=root;target.ExecutablePath=exe;
                target.Arguments=fields["Indítási paraméterek (pontos egyezés)"].Text.Trim();
                string loader=fields["API-loader EXE (opcionális)"].Text.Trim();
                if(loader.Length>0 && (!File.Exists(loader)||!IsUnderRoot(loader,root)))
                    throw new InvalidDataException("A loader EXE-nek a helyi mapgyökérben kell lennie");
                target.ApiLoaderPath=loader.Length==0?"":Path.GetFullPath(loader);
                target.ApiLoaderArguments=fields["API-loader argumentumok"].Text.Trim();
                target.Maintenance=maintenance.IsChecked==true;
                target.MonitorRcon=monitorRcon.IsChecked==true;
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
