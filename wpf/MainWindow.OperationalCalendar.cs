using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using UntamedManager.Core;
using Forms=System.Windows.Forms;

namespace UntamedManager;
public partial class MainWindow
{
    private void ShowRecurringOperationalCalendar()
    {
        var maps=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,
            DisplayMemberPath="Name",SelectedIndex=0,Background=Panel,Foreground=Brushes.White};
        var kinds=new ComboBox{ItemsSource=Enum.GetValues<OperationalTaskKind>(),
            SelectedItem=OperationalTaskKind.WildDinoWipe,
            Background=Panel,Foreground=Brushes.White};
        var frequency=new ComboBox{ItemsSource=Enum.GetValues<ScheduleFrequency>(),
            SelectedItem=ScheduleFrequency.Daily,Background=Panel,Foreground=Brushes.White};
        var time=new TextBox{Text="07:30",Height=32,Background=Panel,Foreground=Brushes.White};
        var weekday=new ComboBox{ItemsSource=Enum.GetValues<DayOfWeek>(),
            SelectedItem=DayOfWeek.Sunday,Background=Panel,Foreground=Brushes.White};
        var status=Label("Budapest-időzónás sablonok: alapértelmezésben mind kikapcsolva.",12,Soft);
        var entries=Label("",12,Soft);
        void Refresh()
        {
            try
            {
                var owned=_cluster.Config.LocalMaps.Select(m=>m.Id).ToHashSet();
                entries.Text=string.Join("\n",OperationalScheduler.Upcoming(
                    _cluster.Config.RecurringSchedules,DateTimeOffset.UtcNow,owned)
                    .Select(x=>x.Kind+" · "+x.DueUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")+
                        " · "+x.Status+" · "+x.Job));
                if(entries.Text.Length==0)entries.Text="Nincsenek ismétlődő feladatok.";
            }
            catch(Exception ex){entries.Text="Naptár nem olvasható: "+ex.Message;}
        }
        Add(Box(V(Label("v1.5 · Időzített műveletek / Europe/Budapest",20,Mint,true),
            Label("Napi, heti, intervallumos ütemezés és DST-ellenőrzés. "+
                "A veszélyes műveletek csak előnézetben érhetők el; "+
                "sem restart, sem wipe, sem Store/SQL írás nem indítható innen.",12,Soft),
            maps,kinds,frequency,
            Label("Helyi óra (HH:mm); intervallumosnál percek",12,Aqua),time,weekday,
            Btn("Ütemezés felvétele (alapértelmezetten letiltva)",()=>{
                try
                {
                    if(maps.SelectedItem is not LocalMap map ||
                       !_cluster.Config.LocalMaps.Contains(map))
                        throw new UnauthorizedAccessException("Csak helyi map");
                    var job=new RecurringSchedule{Map=map.Id,
                        Kind=(OperationalTaskKind)kinds.SelectedItem,
                        Frequency=(ScheduleFrequency)frequency.SelectedItem,
                        Enabled=false,NoticeRequired=true,
                        WeekDay=(DayOfWeek)weekday.SelectedItem};
                    if(job.Frequency==ScheduleFrequency.IntervalMinutes)
                    {
                        if(!int.TryParse(time.Text,out int minutes))
                            throw new InvalidDataException("Érvénytelen intervallum");
                        job.IntervalMinutes=minutes;
                    }
                    else if(!TimeSpan.TryParse(time.Text,out var local))
                        throw new InvalidDataException("Érvénytelen HH:mm");
                    else job.LocalClock=local;
                    OperationalScheduler.Next(job,DateTimeOffset.UtcNow);
                    _cluster.Config.RecurringSchedules.Add(job);_cluster.Save();
                    status.Text="Új, LETILTOTT feladat rögzítve: "+job.Id;
                    Refresh();
                }
                catch(Exception ex){status.Text="Ütemezés elutasítva: "+ex.Message;}
            }),
            Btn("07:30-as Untamed Kingdom Wild Dino Wipe sablonok",()=>{
                try
                {
                    var jobs=OperationalScheduler.UntamedTemplates(
                        _cluster.Config.LocalMaps.Select(x=>x.Id));
                    if(_cluster.Config.RecurringSchedules.Any(x=>x.Kind==
                        OperationalTaskKind.WildDinoWipe && x.LocalClock==new TimeSpan(7,30,0)))
                        throw new InvalidOperationException("07:30 sablon már szerepel");
                    _cluster.Config.RecurringSchedules.AddRange(jobs);_cluster.Save();
                    status.Text=jobs.Length+" darab 07:30-as sablon hozzáadva; mind DISABLED.";
                    Refresh();
                }
                catch(Exception ex){status.Text="Sablonimport elutasítva: "+ex.Message;}
            }),
            Btn("Ismétlődő időpontok újraszámítása",Refresh),
            status,entries)));
        Refresh();
    }

    private void ShowTelemetryHistoryControls()
    {
        var maps=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,
            DisplayMemberPath="Name",SelectedIndex=0,Background=Panel,Foreground=Brushes.White};
        var status=Label("Nincs kiválasztott történeti adat.",12,Soft);
        var canvas=new Canvas{Height=130,Background=Panel};
        Add(Box(V(Label("v1.5 · CPU/RAM/játékos történet (7 nap)",19,Mint,true),
            Label("A Supervisor a mért értékeket tárolja, nem talál ki "+
                "játékosszámot/RCON-latenciát/hálózati terhelést. "+
                "Az elavult minták nem valós idejű adatok.",12,Soft),
            maps,Btn("CPU/RAM előzmények és grafikon",()=>{
                try
                {
                    if(maps.SelectedItem is not LocalMap map ||
                       !_cluster.Config.LocalMaps.Contains(map))
                        throw new UnauthorizedAccessException("Csak helyi map");
                    if(_supervisorChannel==null)
                        throw new InvalidOperationException("Nincs hitelesített Supervisor csatorna");
                    var series=new TelemetryHistory(_supervisorChannel.Root)
                        .Read(map.Id,DateTimeOffset.UtcNow,200);
                    canvas.Children.Clear();
                    if(series.Count==0){status.Text="Még nincs hitelesített történeti minta.";return;}
                    var cpu=series.Where(x=>x.CpuPercent.HasValue).TakeLast(100).ToArray();
                    if(cpu.Length>=2)
                    {
                        var line=new Polyline{Stroke=Mint,StrokeThickness=2};
                        for(int i=0;i<cpu.Length;i++)
                            line.Points.Add(new System.Windows.Point(
                                i*440.0/Math.Max(cpu.Length-1,1),
                                125-Math.Clamp(cpu[i].CpuPercent!.Value,0,100)*1.15));
                        canvas.Children.Add(line);
                    }
                    var last=series[^1];
                    status.Text="Minták: "+series.Count+" · legutolsó: "+
                        last.ObservedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+
                        (last.IsStale?" (ELAVULT)":"")+"\nCPU "+
                        (last.CpuPercent?.ToString("F1")+"%"??"ismeretlen")+
                        " · RAM "+
                        (last.MemoryBytes.HasValue?
                            (last.MemoryBytes.Value/1048576)+" MiB":"ismeretlen")+
                        " · játékosok "+(last.Players?.ToString()??"ismeretlen");
                }
                catch(Exception ex){status.Text="Telemetria nem elérhető: "+ex.Message;}
            }),canvas,status)));
    }

    private void ShowKovaProfilePreviewControls()
    {
        var source=new TextBox{Height=32,IsReadOnly=true,
            Background=Panel,Foreground=Brushes.White};
        var store=new TextBox{Height=32,IsReadOnly=true,
            Background=Panel,Foreground=Brushes.White};
        var status=Label("Csak másolt Kova JSON állományokat olvas. Nem nyitja meg a Kova folyamatát.",12,Soft);
        KovaProfileReport? report=null;
        Add(Box(V(Label("v1.5 · Kova 1.8.3 profilimport / READ-ONLY",19,Mint,true),
            Label("kova-manager-v1 JSON export és opcionális cluster-store-v1 JSON másolata. "+
                "A jelenlegi Kova-profil, Store, transferfájlok és jelszavak változatlanok "+
                "maradnak. Csak letiltott, maintenance állapotú Untamed-tervezet készíthető.",12,Soft),
            source,Btn("Kova profil másolatának kiválasztása",()=>{
                using var picker=new Forms.OpenFileDialog{CheckFileExists=true,
                    Filter="Kova JSON másolat (*.json)|*.json"};
                if(picker.ShowDialog()==Forms.DialogResult.OK)
                {source.Text=picker.FileName;report=null;}
            }),
            store,Btn("Opcionális Store-v1 JSON másolat kiválasztása",()=>{
                using var picker=new Forms.OpenFileDialog{CheckFileExists=true,
                    Filter="Store-v1 JSON másolat (*.json)|*.json"};
                if(picker.ShowDialog()==Forms.DialogResult.OK)
                {store.Text=picker.FileName;report=null;}
            }),
            Btn("Kova profilok elemzése – kizárólag olvasás",()=>{
                try
                {
                    report=KovaProfileImporter.InspectCopy(source.Text.Trim(),
                        store.Text.Trim().Length==0?null:store.Text.Trim());
                    status.Text="Forrás SHA-256: "+report.SourceHash+
                        "\nHelyi profilok: "+report.Maps.Count+
                        "\nStore 1:1 kompatibilitás: NEM IGAZOLT\n"+
                        string.Join("\n",report.Maps.Select(x=>x.Name+" · "+
                            x.StorePhase+" · "+x.GamePort+"/"+x.QueryPort+
                            " · modok: "+x.Mods.Count))+
                        "\n"+string.Join("\n",report.Warnings);
                }
                catch(Exception ex){report=null;
                    status.Text="Kova profil elutasítva: "+ex.Message;}
            }),
            Btn("Titokmentes, INAKTÍV Untamed-tervezet exportálása",()=>{
                try
                {
                    if(report==null)throw new InvalidOperationException("Előbb vizsgáld meg az exportot");
                    using var picker=new Forms.SaveFileDialog{
                        FileName="Untamed-Kova-Import-PREVIEW.json",
                        Filter="JSON export|*.json"};
                    if(picker.ShowDialog()!=Forms.DialogResult.OK)return;
                    if(File.Exists(picker.FileName))
                        throw new IOException("Csak új célfájl engedélyezett");
                    var inert=KovaProfileImporter.BuildInertLocalDraft(report,
                        _cluster.Config.LocalHostId);
                    File.WriteAllText(picker.FileName,
                        System.Text.Json.JsonSerializer.Serialize(inert,
                            new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                    status.Text="INAKTÍV, titokmentes profil exportálva: "+
                        picker.FileName+"\nNincs automatikus import, indítás, Store-módosítás.";
                }
                catch(Exception ex){status.Text="Export sikertelen: "+ex.Message;}
            }),status)));
    }

    private void ShowRetentionPreviewControls()
    {
        var status=Label("Védett mentéseket nem törlünk; csak saját, igazolt csomagok helyezhetők karanténba.",12,Soft);
        Add(Box(V(Label("v1.5 · Mentés-retenció feladatonként",19,Mint,true),
            Label("Azonos mapon, de másik ütemezésből származó pontok "+
                "egymástól függetlenek. A nem ellenőrzött, védett és legfrissebb "+
                "mentések érintetlenek maradnak.",12,Soft),
            Btn("Retenciós döntési szimuláció – valódi fájlt nem töröl",()=>{
                try
                {
                    var job=Guid.NewGuid();var map=Guid.NewGuid();
                    var now=DateTimeOffset.UtcNow;
                    var records=Enumerable.Range(0,5).Select(i=>new RetainedBackup(
                        job,map,"synthetic-package-"+i,now.AddDays(-i),
                        50L*1048576,i==2,true,"Synthetic test"));
                    var plan=BackupRetention.Preview(records,
                        new RetentionRule(2,TimeSpan.FromDays(3),200L*1048576),now);
                    status.Text="KIZÁRÓLAG SZIMULÁCIÓ · jelölt: "+
                        plan.Candidates+" · becsült megtakarítás: "+
                        plan.EstimatedReclaimBytes/1048576+" MiB\n"+
                        string.Join("\n",plan.Items.Select(x=>x.Backup.Package+
                            " · "+(x.Eligible?"karanténjelölt":"megőrzendő")+
                            " · "+x.Reason))+
                        "\nValódi ASA/Kova/SQL mentés nem törlődött.";
                }
                catch(Exception ex){status.Text="Retenciós szimuláció elutasítva: "+ex.Message;}
            }),status)));
    }
}
