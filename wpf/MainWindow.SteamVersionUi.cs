using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms = System.Windows.Forms;

namespace UntamedManager;

public partial class MainWindow
{
    private SteamBuildStatusStore SteamMapJournal()=>new(_cluster.Store.Root);

    private static string SteamStateText(AsaSteamBuildState state)=>state switch
    {
        AsaSteamBuildState.Current=>"Naprakész",
        AsaSteamBuildState.UpdateAvailable=>"Frissítés elérhető",
        AsaSteamBuildState.Checking=>"Ellenőrzés folyamatban",
        AsaSteamBuildState.Installing=>"Frissítés folyamatban",
        AsaSteamBuildState.Restarting=>"Újraindítás folyamatban",
        AsaSteamBuildState.Failed=>"Hiba történt",
        _=>"Nem ellenőrizhető"
    };

    private async Task CheckSteamVersionForMap(LocalMap map,TextBlock message)
    {
        if(!_cluster.Config.LocalMaps.Contains(map))
        {
            message.Text="Csak a helyi Managerhez regisztrált map vizsgálható.";
            return;
        }
        string steam=_cluster.Config.SteamCmdExecutable;
        message.Text="Ellenőrzés folyamatban: Steam publikus ASA Build ID (csak olvasás)...";
        try
        {
            var latest=await AsaSteamBuildCatalog.CheckAsync(steam,map.InstallationPath);
            var state=SteamMapJournal().SaveCheck(map,latest);
            message.Text=SteamDescription(state)+
                "\nVerzióellenőrzés NEM indított app_update műveletet.";
        }
        catch(Exception ex)
        {
            WriteLog("steam-map-check",ex);
            message.Text="Nem ellenőrizhető: "+
                UntamedPresentation.Redact(ex.Message)+
                "\nAz ASA szerverfájlok változatlanok.";
        }
    }
    private static string SteamDescription(SteamMapUpdateStatus state)=>
        SteamStateText(state.State)+
        "\nHelyi Steam Build ID: "+(state.LocalBuild??"ismeretlen")+
        "\nSteam publikált Build ID: "+(state.AvailableBuild??"nem ellenőrizhető")+
        "\nUtolsó sikeres ellenőrzés: "+
            (state.LastCheckedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")??"még nincs")+
        "\nUtolsó sikeres frissítés: "+
            (state.LastUpdatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")??"még nincs")+
        "\n"+state.Message;

    private UIElement SteamMapVersionPane(LocalMap map)
    {
        SteamMapUpdateStatus existing;
        try{existing=SteamMapJournal().Read(map);}
        catch(Exception ex)
        {
            WriteLog("steam-map-status",ex);
            existing=SteamMapUpdateStatus.Unknown(map);
        }
        var status=Label(SteamDescription(existing),12,Soft);
        var actions=new WrapPanel{Margin=new Thickness(0,10,0,14)};
        var check=Btn("Frissítés ellenőrzése",async()=>{
            await CheckSteamVersionForMap(map,status);
        });
        var install=Btn("Frissítés telepítése",()=>{
            var readiness=ReleaseCapabilityGate.Check(ManagerOperation.LiveSteamUpdate,
                _cluster.Config.LocalMaps.Contains(map),
                map.Maintenance,_cluster.Check(map).ConfirmedStopped);
            if(!readiness.Allowed)
            {
                status.Text="ÉLES FRISSÍTÉS BLOKKOLVA: "+readiness.Reason+
                    "\nA SaveComplete, ellenőrzött backup/Stop és a teljes újraindítási elfogadási teszt"+
                    " nélkül a SteamCMD nem írhat egy futó ASA-szerver telepítésébe.";
                return;
            }
            status.Text="Frissítés csak igazolt, teljes mentés/backup/leállítási adapterrel indítható.";
        });
        actions.Children.Add(check);actions.Children.Add(install);
        actions.Children.Add(Btn("SteamCMD / frissítési központ",()=>Navigate("steam")));
        return V(
            Label("STEAM 2430930  /  BUILD OBSERVATION",11,Aqua,true),
            Label(map.Name+" – szervertelepítés",19,Mint,true),
            Label(map.InstallationPath,12,Soft),status,actions,
            Label("A Steam Build ID a dedikált alkalmazás buildje, nem az ARK kliens verziója. "+
                "A megosztott telepítések a többi helyi mapot is érinthetik. "+
                "Automatikus éles frissítés igazolt mentési/újraindítási képesség nélkül tiltva.",
                12,Soft));
    }

    private void ShowSteamPremiumCenter()
    {
        var settings=_cluster.Config;
        var cmd=new TextBox{Text=settings.SteamCmdExecutable,
            MinHeight=38,ToolTip="A helyi steamcmd.exe teljes elérési útja"};
        var status=Label("SteamCMD és ASA verziókezelés – helyi Manager. "+
            "Az éles frissítési kapu továbbra is külön ellenőrzött.",12,Soft);
        var downloadApproved=new CheckBox{Content="Hivatalos Valve SteamCMD letöltés jóváhagyása",
            IsChecked=false,Margin=new Thickness(0,7,0,7)};
        Add(Box(V(
            Label("STEAMCMD  /  KÖRNYEZET",11,Aqua,true),
            Label("SteamCMD telepítés és kiválasztás",20,Mint,true),
            Label("Az ASA 2430930 telepítéséhez használt helyi SteamCMD. "+
                  "A letöltés kizárólag külön jóváhagyásra indul.",12,Soft),
            cmd,
            Btn("steamcmd.exe tallózása",()=>{
                using var picker=new Forms.OpenFileDialog{
                    Title="Valve SteamCMD futtatható fájl",Filter="SteamCMD|steamcmd.exe"};
                if(picker.ShowDialog()==Forms.DialogResult.OK)cmd.Text=picker.FileName;
            }),
            downloadApproved,
            Btn("SteamCMD ellenőrzése / telepítése",async()=>{
                try
                {
                    var progress=new Progress<SteamInstallProgress>(p=>
                        status.Text=p.Stage+": "+p.Detail);
                    string exe=await SteamFreshInstall.PrepareSteamCmdAsync(
                        cmd.Text.Trim(),downloadApproved.IsChecked==true,progress);
                    _cluster.UpdateSettings(c=>c.SteamCmdExecutable=exe);
                    status.Text="SteamCMD elérhető: "+exe+
                        "\nSHA-256: "+SteamCmdPreflight.Inspect(exe).Sha256;
                }
                catch(Exception ex)
                {
                    WriteLog("steamcmd-provision",ex);
                    status.Text="SteamCMD nem telepíthető: "+UntamedPresentation.Redact(ex.Message);
                }
            }),
            Btn("SteamCMD útvonalának mentése (letöltés nélkül)",()=>{
                try
                {
                    SteamCmdPreflight.Inspect(cmd.Text.Trim());
                    _cluster.UpdateSettings(c=>c.SteamCmdExecutable=cmd.Text.Trim());
                    status.Text="SteamCMD elérési út mentve; semmit nem telepítettünk.";
                }
                catch(Exception ex)
                {
                    WriteLog("steamcmd-path",ex);
                    status.Text="SteamCMD útvonal nem menthető: "+
                        UntamedPresentation.Redact(ex.Message);
                }
            }),status)));

        var maps=_cluster.Config.LocalMaps;
        var choose=new ComboBox{ItemsSource=maps,DisplayMemberPath="Name",
            SelectedIndex=maps.Count>0?0:-1,MinHeight=38};
        var result=Label("Válassz egy helyi mapot és ellenőrizd a Steam buildet.",12,Soft);
        Add(Box(V(Label("STEAM BUILD ID  /  HELYI MAPOK",11,Aqua,true),
            Label("Maponkénti frissítésellenőrzés",20,Mint,true),choose,
            Btn("Kiválasztott map frissítésének ellenőrzése",async()=>{
                if(choose.SelectedItem is LocalMap map)
                    await CheckSteamVersionForMap(map,result);
            }),
            Btn("Összes helyi map verzióellenőrzése (írás nélkül)",async()=>{
                foreach(var local in maps.ToArray())
                {
                    result.Text="Steam Build ID ellenőrzés: "+local.Name;
                    await CheckSteamVersionForMap(local,result);
                    // Fail closed if any check was inconclusive. No app_update.
                    try{
                        if(SteamMapJournal().Read(local).State==AsaSteamBuildState.Unknown)
                        {result.Text+="\nTovábbi vizsgálat szükséges, ellenőrzés megállítva.";break;}
                    }
                    catch(Exception ex){result.Text=UntamedPresentation.Redact(ex.Message);break;}
                }
            }),
            result)));

        var enable=new CheckBox{Content="Automatikus frissítések engedélyezése",
            IsChecked=settings.AutoSteamUpdatesEnabled};
        var interval=new TextBox{Text=settings.SteamCheckIntervalMinutes.ToString()};
        var from=new TextBox{Text=settings.SteamMaintenanceStart};
        var until=new TextBox{Text=settings.SteamMaintenanceEnd};
        var notify=new CheckBox{Content="Játékosok előzetes RCON-értesítése",
            IsChecked=settings.SteamWarnPlayers};
        var wait=new TextBox{Text=settings.SteamWaitPlayersMinutes.ToString()};
        var restart=new CheckBox{Content="Frissítés utáni automatikus újraindítás",
            IsChecked=settings.SteamRestartAfterUpdate};
        var verify=new TextBox{Text=settings.SteamPostUpdateProbeMinutes.ToString()};
        var queueStatus=Label("Az automatikus éles karbantartás alapértelmezésben KI. "+
            "A helyi mapok egyenként frissülhetnek, de éles frissítéshez hitelesített SaveComplete szükséges.",
            12,Soft);
        var queueDisplay=Label("",12,Soft);
        Add(Box(V(
            Label("SZIGORÚ SOROS KARBANTARTÁS",11,Aqua,true),
            Label("Automatikus frissítési szabályok",20,Mint,true),
            enable,
            Label("Verzióellenőrzési időköz (perc, 30–1440)",12,Aqua),interval,
            Label("Karbantartási időablak kezdete (HH:mm)",12,Aqua),from,
            Label("Karbantartási időablak vége (HH:mm)",12,Aqua),until,
            notify,Label("Játékosok kilépésére várakozás (perc, 0–120)",12,Aqua),wait,
            restart,Label("Frissítés utáni ellenőrzés (perc, 1–60)",12,Aqua),verify,
            Btn("Frissítési szabályok mentése",()=>{
                try
                {
                    if(!int.TryParse(interval.Text,out int minutes) || minutes is <30 or >1440 ||
                       !int.TryParse(wait.Text,out int waitMinutes) || waitMinutes is <0 or >120 ||
                       !int.TryParse(verify.Text,out int verifyMinutes) || verifyMinutes is <1 or >60 ||
                       !TimeSpan.TryParseExact(from.Text,@"hh\:mm",null,out _) ||
                       !TimeSpan.TryParseExact(until.Text,@"hh\:mm",null,out _))
                        throw new InvalidDataException("Érvénytelen karbantartási szabály.");
                    if(enable.IsChecked==true &&
                       !ReleaseCapabilityGate.Check(ManagerOperation.LiveSteamUpdate,true).Allowed)
                        throw new InvalidOperationException(
                            "Az automatikus éles frissítés nem engedélyezhető, mert az ASA SaveComplete, backup, leállítás és újraindítás még nincs igazolva.");
                    _cluster.UpdateSettings(c=>{
                        c.SteamCheckIntervalMinutes=minutes;
                        c.SteamMaintenanceStart=from.Text;
                        c.SteamMaintenanceEnd=until.Text;
                        c.SteamWarnPlayers=notify.IsChecked==true;
                        c.SteamWaitPlayersMinutes=waitMinutes;
                        c.SteamRestartAfterUpdate=restart.IsChecked==true;
                        c.SteamPostUpdateProbeMinutes=verifyMinutes;
                        c.AutoSteamUpdatesEnabled=enable.IsChecked==true;
                    });
                    queueStatus.Text="Frissítési szabályok mentve; éles frissítés "+
                        (enable.IsChecked==true?"engedélyezett":"kikapcsolva")+".";
                }
                catch(Exception ex){queueStatus.Text="Nem menthető: "+UntamedPresentation.Redact(ex.Message);}
            }),
            Btn("Soros frissítési sor előnézete (csak olvasás)",()=>{
                try
                {
                    var candidates=maps.Where(m=>
                        SteamMapJournal().Read(m).State==AsaSteamBuildState.UpdateAvailable)
                        .ToArray();
                    if(candidates.Length==0)
                    {
                        queueDisplay.Text="Nincs hitelesen elavultként azonosított helyi map. "+
                            "Verzióellenőrzés szükséges.";
                        return;
                    }
                    var lines=candidates.Select((map,index)=>{
                        var status=SteamMapJournal().Read(map);
                        var sameRoot=maps.Where(other=>other.Id!=map.Id &&
                            !string.IsNullOrWhiteSpace(other.InstallationPath) &&
                            string.Equals(Path.GetFullPath(other.InstallationPath),
                                Path.GetFullPath(map.InstallationPath),
                                StringComparison.OrdinalIgnoreCase)).ToArray();
                        bool sharedUnsafe=sameRoot.Any(other=>{
                            var proc=_cluster.Check(other);
                            return proc.Running||!proc.ConfirmedStopped||
                                proc.Identity!=null||proc.LoaderIdentity!=null;
                        });
                        return (index+1)+". "+map.Name+"  ·  Steam "+
                            (status.LocalBuild??"ismeretlen")+" → "+
                            (status.AvailableBuild??"nem ellenőrizhető")+
                            (sharedUnsafe?"  · BLOKKOLVA: közös telepítésben más map fut":
                            sameRoot.Length>0?"  · KÖZÖS TELEPÍTÉS: teljes csoport ellenőrzése szükséges":
                            "  · külön telepítés")+
                            "  · helyi";
                    });
                    queueDisplay.Text="KARBANTARTÁSI TERV (NEM INDÍT FRISSÍTÉST):\n"+
                        string.Join("\n",lines)+
                        "\nMinden map csak az előző igazolt befejezése után következhet. "+
                        "A másik Windows Manager frissítései ettől a helyi sortól függetlenek. A valódi SaveComplete még nincs tanúsítva.";
                }
                catch(Exception ex)
                {
                    WriteLog("steam-queue-preview",ex);
                    queueDisplay.Text="Soros terv nem állítható össze: "+
                        UntamedPresentation.Redact(ex.Message);
                }
            }),
            Btn("Összes elavult szerver frissítése",()=>{
                try
                {
                    var candidates=maps.Where(m=>
                        SteamMapJournal().Read(m).State==AsaSteamBuildState.UpdateAvailable)
                        .ToArray();
                    if(candidates.Length==0)
                    {queueStatus.Text="Nincs hitelesen elavultként azonosított helyi map.";return;}
                    if(!ReleaseCapabilityGate.Check(ManagerOperation.LiveSteamUpdate,true).Allowed)
                        throw new InvalidOperationException(
                            "Az éles SteamCMD karbantartás nem indulhat igazolt SaveWorld/backup/Stop és újraindítási ellenőrzés nélkül.");
                    queueStatus.Text="A frissítéshez hitelesített éles karbantartási adapter szükséges.";
                }
                catch(Exception ex)
                {
                    queueStatus.Text="Frissítési sor BLOKKOLVA: "+
                        UntamedPresentation.Redact(ex.Message);
                }
            }),
            Btn("Karbantartási sor előzménye / megszakítás",()=>{
                try
                {
                    var journal=new SteamSerialMaintenanceQueue(_cluster.Store.Root,
                        _cluster.Config.LocalHostId).Observe();
                    queueDisplay.Text=journal==null?"Még nincs helyi sor":
                        "Azonosító: "+journal.QueueId+"\nÁllapot: "+journal.Phase+
                        "\nKövetkező index: "+journal.NextIndex+
                        "\nAktív map: "+(journal.ActiveMap?.ToString()??"nincs")+
                        "\n"+journal.Summary;
                }
                catch(Exception ex){queueDisplay.Text="Napló nem olvasható: "+
                    UntamedPresentation.Redact(ex.Message);}
            }),
            queueStatus,queueDisplay)));
    }
}
