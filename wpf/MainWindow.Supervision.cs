using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UntamedManager.Core;

namespace UntamedManager;
public partial class MainWindow
{
    public void CompleteSupervisionCiTest()=>Exit();
    private SupervisorChannel? _supervisorChannel;
    private SupervisorSnapshot? _supervisorSnapshot;
    private readonly DispatcherTimer _supervisorTimer=new(){Interval=TimeSpan.FromSeconds(5)};
    private string _supervisorMessage="Supervisor: nem ellenőrzött";
    private bool _supervisorStarting;
    private DateTimeOffset _lastSupervisorLaunch;
    private EventJournal? _events;
    private void InitializeSupervision()
    {
        _supervisorTimer.Tick+=(_,_)=>RefreshSupervision();
        Loaded+=(_,_)=>
        {
            if(App.IsSmokeTest)return;
            try
            {
                if(!File.Exists(_cluster.Store.FilePath))_cluster.Save();
                _supervisorChannel=new(_cluster.Store.Root);_events=new(_supervisorChannel.Root);
                _events.Append(_cluster.Config.LocalHostName,null,"Information","manager-start");
                _supervisorTimer.Start();RefreshSupervision();
            }
            catch(Exception){_supervisorMessage="Supervisor csatlakozás nem elérhető";}
        };
        Closed+=(_,_)=>
        {
            _supervisorTimer.Stop();
            try{_events?.Append(_cluster.Config.LocalHostName,null,"Information","manager-stop");}catch{}
            // The independent worker is deliberately left running.
        };
    }
    private void RefreshSupervision()
    {
        if(_supervisorChannel==null)return;
        try
        {
            var snapshot=_supervisorChannel.Read(_cluster.Config.LocalHostId);
            if(snapshot.Maps.Select(x=>x.Map).Distinct().Count()!=snapshot.Maps.Length || snapshot.Maps.Any(x=>!_cluster.Config.LocalMaps.Any(m=>m.Id==x.Map)))throw new IOException("Supervisor map ownership mismatch");
            using var process=Process.GetProcessById(snapshot.Pid);
            if(new DateTimeOffset(process.StartTime.ToUniversalTime())!=snapshot.StartedUtc||
                !string.Equals(process.MainModule?.FileName,snapshot.Executable,StringComparison.OrdinalIgnoreCase))throw new IOException("Supervisor identity mismatch");
            bool reconnect=_supervisorSnapshot==null;
            _supervisorSnapshot=snapshot;_supervisorMessage="Supervisor kapcsolódva · "+snapshot.ObservedUtc.LocalDateTime.ToString("HH:mm:ss");
            if(reconnect)_events?.Append(_cluster.Config.LocalHostName,null,"Information","supervisor-reconnected");
        }
        catch(Exception)
        {
            if(_supervisorSnapshot!=null)try{_events?.Append(_cluster.Config.LocalHostName,null,"Warning","supervisor-heartbeat-lost");}catch{}
            _supervisorSnapshot=null;_supervisorMessage="Supervisor nem elérhető vagy elavult heartbeat";
            if(!_supervisorStarting && DateTimeOffset.UtcNow-_lastSupervisorLaunch>TimeSpan.FromMinutes(1))
            {
                _supervisorStarting=true;_lastSupervisorLaunch=DateTimeOffset.UtcNow;
                try
                {
                    string source=Path.Combine(AppContext.BaseDirectory,"UntamedSupervisor.exe");
                    using var file=File.OpenRead(source);string hash=Convert.ToHexString(SHA256.HashData(file));
                    string runtime=Path.Combine(_supervisorChannel.Root,"runtime",hash);Directory.CreateDirectory(runtime);
                    string executable=Path.Combine(runtime,"UntamedSupervisor.exe");
                    if(!File.Exists(executable))File.Copy(source,executable);
                    var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=runtime};
                    foreach(string arg in new[]{"--profile",App.ProfileName,"--root",_cluster.Store.Root})start.ArgumentList.Add(arg);
                    using var process=Process.Start(start);
                    _events?.Append(_cluster.Config.LocalHostName,null,"Information","supervisor-launch-requested");
                }
                catch(Exception){_supervisorMessage="Supervisor indítása sikertelen; újrapróbálás legfeljebb percenként";}
                finally{_supervisorStarting=false;}
            }
        }
        var lines=_cluster.Config.LocalMaps.Select(map=>
        {
            var observation=_supervisorSnapshot?.Maps.SingleOrDefault(x=>x.Map==map.Id);
            return observation==null?map.Name+": Ismeretlen – nincs friss, hitelesített mérés":FormatObservation(observation);
        });
        SupervisorStatus.Text=_supervisorMessage+Environment.NewLine+string.Join(Environment.NewLine,lines);
        SupervisorBadge.Text=_supervisorSnapshot==null?"Supervisor: nem elérhető":"Supervisor: kapcsolódva";
    }
    private static string FormatObservation(MapObservation x)=>!StatusFreshness.IsFresh(x.ObservedUtc,DateTimeOffset.UtcNow)?
        x.Name+": ELAVULT MÉRÉS · "+x.ObservedUtc.ToString("O") :
        $"{x.Name}: {(x.Running==true?"Fut":x.Running==false?"Leállítva":"Ismeretlen")} · CPU {(x.CpuPercent.HasValue?x.CpuPercent.Value.ToString("F1")+"%":"Ismeretlen")} · RAM {(x.MemoryBytes.HasValue?(x.MemoryBytes.Value/1048576)+" MiB":"Ismeretlen")} · "+
        $"Uptime {(x.UptimeSeconds.HasValue?TimeSpan.FromSeconds(x.UptimeSeconds.Value).ToString():"Ismeretlen")}\n"+
        $"Loader: {x.Loader} · RCON: {x.Rcon} · Játékosok: {(x.Players.HasValue?x.Players.Value.ToString():"Ismeretlen")} · Maintenance: {x.Maintenance}\n"+
        $"Watchdog: {x.Watchdog} · Napló: {x.Log} · Művelet: {x.Operation} · {x.Error} · {x.ObservedUtc.LocalDateTime:HH:mm:ss}";
    private string _eventHost="",_eventSeverity="",_eventMap="";
    private int _eventHours=24;
    private void ShowMonitoring()
    {
        Header("Monitoring / Események","Hitelesített helyi Supervisor-adatok és tartós eseményidővonal.");PageBody.Children.Clear();
        Add(Box(V(Label(_supervisorMessage,16,Aqua,true),Label(_supervisorSnapshot==null?"A mapok állapota ismeretlen.":string.Join("\n\n",_supervisorSnapshot.Maps.Select(FormatObservation)),12,Soft))));
        var host=new TextBox{Text=_eventHost};var map=new TextBox{Text=_eventMap};
        var severity=new ComboBox{ItemsSource=new[]{"","Information","Warning","Error"},SelectedItem=_eventSeverity};
        var hours=new TextBox{Text=_eventHours.ToString()};
        Add(Box(V(Label("Szűrők: gép, mapazonosító, súlyosság, elmúlt órák",13,Aqua),host,map,severity,hours,
            Btn("Szűrés",()=>{_eventHost=host.Text.Trim();_eventMap=map.Text.Trim();_eventSeverity=(string?)severity.SelectedItem??"";if(int.TryParse(hours.Text,out var n)&&n>0&&n<=8760)_eventHours=n;ShowMonitoring();}))));
        Add(Btn("Adatok frissítése",ShowMonitoring));
        if(_events!=null)
        {
            Guid? mapId=Guid.TryParse(_eventMap,out var parsed)?parsed:null;
            var records=_events.Read(_eventHost.Length==0?null:_eventHost,mapId,_eventSeverity.Length==0?null:_eventSeverity,DateTimeOffset.UtcNow.AddHours(-_eventHours))
                .Concat(new EventJournal(UpdateCenter.UpdateRoot).Read(_eventHost.Length==0?null:_eventHost,mapId,_eventSeverity.Length==0?null:_eventSeverity,DateTimeOffset.UtcNow.AddHours(-_eventHours)));
            foreach(var e in records.OrderByDescending(x=>x.Utc).Take(500))
                Add(Box(Label($"{e.Utc.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {e.Severity} · {e.Host} · {e.Map}\n{e.Code} · {e.Id}",12,Soft)));
        }
        Add(Box(V(Label("Frissítési események",16,Aqua),Label(UpdateHistory.ReadRecent(),11,Soft))));
        ShowTelemetryHistoryControls();
    }
    private void ShowBackgroundSchedules()
    {
        Header("Ütemezések / Automatizálás","A Supervisor sorosan futtatja az ellenőrzéseket. AutoStart, Restart és Kill letiltva.");
        var maps=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",SelectedIndex=0};
        var kind=new ComboBox{ItemsSource=new[]{ScheduledKind.Observation,ScheduledKind.Notification,ScheduledKind.BackupIntegrity,ScheduledKind.Maintenance,ScheduledKind.AutoStart},SelectedIndex=0};
        var due=new TextBox{Text=DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")};
        var until=new TextBox{Text=DateTimeOffset.UtcNow.AddMinutes(35).ToString("O")};
        var package=new TextBox{Text=""};
        Add(Box(V(Label("Helyi map",13,Aqua),maps,Label("Feladat",13,Aqua),kind,Label("Időpont (ISO 8601, időzónával)",13,Aqua),due,
            Label("Maintenance vége (ISO 8601)",13,Aqua),until,Label("Szintetikus tesztmentési csomag (csak ellenőrzéshez)",13,Aqua),package,
            Btn("Ütemezés mentése",()=>
            {
                if(maps.SelectedItem is not LocalMap map||!_cluster.Config.LocalMaps.Contains(map)||!DateTimeOffset.TryParse(due.Text,out var date)){Notice("Érvénytelen helyi map vagy időpont.");return;}
                var selected=(ScheduledKind)kind.SelectedItem;
                DateTimeOffset? end=DateTimeOffset.TryParse(until.Text,out var value)?value:null;
                if(selected==ScheduledKind.AutoStart){Notice("AutoStart továbbra is tiltva; a felügyelet nem indít szervert.");return;}
                if(selected==ScheduledKind.Maintenance&&(end==null||end<=date)){Notice("Érvénytelen maintenance időablak.");return;}
                _cluster.Config.ScheduledTasks.Add(new(){Map=map.Id,Kind=selected,DueUtc=date,UntilUtc=end,BackupTestPackage=package.Text.Trim()});_cluster.Save();Navigate("schedules");
            }))));
        foreach(var task in _cluster.Config.ScheduledTasks.ToArray())
            Add(Box(V(Label($"{task.Map} · {task.Kind} · {task.DueUtc.LocalDateTime} · engedélyezve: {task.Enabled}",12,Soft),
                Btn("Letiltás",()=>{task.Enabled=false;_cluster.Save();Navigate("schedules");}))));
        try
        {
            if(_supervisorChannel!=null)foreach(var receipt in new PersistentScheduler(_supervisorChannel.Root).Receipts.TakeLast(50).Reverse())
                Add(Box(Label($"{receipt.Task} · {receipt.DueUtc:O} · {receipt.Status} · kísérletek: {receipt.Attempts}",12,Soft)));
        }catch{Add(Box(Label("Ütemezőállapot nem olvasható.",12,Soft)));}
        ShowRecurringOperationalCalendar();
    }
}
