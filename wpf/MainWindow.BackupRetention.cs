using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;
public partial class MainWindow
{
    private UIElement BackupRetentionPane(LocalMap map)
    {
        var keep=new TextBox{Text="3",Height=30,Foreground=Brushes.White,Background=Panel};
        var points=new ComboBox{DisplayMemberPath="Label",SelectedValuePath="Path",Foreground=Brushes.White,Background=Panel};
        var quarantined=new ComboBox{DisplayMemberPath="Label",SelectedValuePath="Path",Foreground=Brushes.White,Background=Panel};
        var status=Label("A Manager alapértelmezett helyi mentésgyűjteményét kezeli. A védett, hibás és legutóbbi ép pontokat megőrzi.",12,Soft);
        var controls=new StackPanel();bool busy=false;
        RetentionRule Rule()
        {if(!int.TryParse(keep.Text,out int count)||count is <1 or >10000)throw new InvalidDataException("Érvényes megőrzési darabszám szükséges.");
            return new(count,TimeSpan.FromDays(30),0);}
        async Task Refresh()
        {
            var rule=Rule();
            var plan=await Task.Run(()=>ManagedBackupRetention.Preview(_cluster,map.Id,rule));
            points.ItemsSource=plan.Items.Select(x=>new{Path=x.Backup.Package,Label=Path.GetFileName(x.Backup.Package)+" · "+
                (x.Eligible?"karanténjelölt":x.Backup.Protected?"VÉDETT":x.Backup.Verified?"megőrzendő":"hibás / hiányos")}).ToArray();
            points.SelectedIndex=points.Items.Count>0?0:-1;
            var quarantine=await Task.Run(()=>ManagedBackupRetention.InspectQuarantine(_cluster,map.Id));
            quarantined.ItemsSource=quarantine.Where(x=>x.Status=="verified-quarantine").Select(x=>new{
                Path=x.Transaction,Label=Path.GetFileName(x.Transaction)+" · ellenőrzött karantén"}).ToArray();
            quarantined.SelectedIndex=quarantined.Items.Count>0?0:-1;
            status.Text="Retenciós jelöltek: "+plan.Candidates+" · karanténban: "+quarantined.Items.Count+
                "\nA karantén áthelyezés, végleges törlés nélkül. A visszahelyezés a mentéskatalógusba történik.";
        }
        async Task Execute(Func<Task> action)
        {
            if(busy)return;busy=true;controls.IsEnabled=false;
            try{await action();}
            catch(Exception e){WriteLog("managed-backup-retention",e);status.Text="UM-BACKUP-RETENTION: a művelet megtagadva vagy nem igazolt. A csomagok és a műveletnapló ellenőrizendők.";}
            finally{controls.IsEnabled=true;busy=false;}
        }
        string Selected()=>points.SelectedValue as string??throw new InvalidOperationException("Válassz mentési pontot.");
        controls.Children.Add(V(Label("Védett retenció és visszafordítható karantén",17,Mint,true),
            Label("Megőrzendő legújabb ép mentések száma",12,Aqua),keep,
            Btn("Valódi helyi katalógus és karantén ellenőrzése",async ()=>await Execute(Refresh)),points,
            Btn("Kijelölt mentés VÉDELME",async ()=>await Execute(async ()=>{
                string point=Selected();await Task.Run(()=>ManagedBackupRetention.Protect(_cluster,map.Id,point,true));await Refresh();
            })),
            Btn("Kijelölt mentés védelmének feloldása",async ()=>await Execute(async ()=>{
                string point=Selected();
                if(System.Windows.MessageBox.Show("A kijelölt mentési pont a retenciós szabály alapján ismét karanténba helyezhető lesz. Folytatod?",
                    "Mentésvédelem feloldása",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                await Task.Run(()=>ManagedBackupRetention.Protect(_cluster,map.Id,point,false));await Refresh();
            })),
            Btn("Kijelölt régi pont áthelyezése karanténba",async ()=>await Execute(async ()=>{
                string point=Selected();var rule=Rule();
                if(System.Windows.MessageBox.Show("A kiválasztott mentés ellenőrzés után a külön karanténba kerül. Az utolsó ép mentést a program megőrzi.\n"+
                    Path.GetFileName(point)+"\nFolytatod?","Mentéskarantén",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                await Task.Run(()=>ManagedBackupRetention.Quarantine(_cluster,map.Id,point,rule));await Refresh();
            })),quarantined,
            Btn("Karanténpont visszahelyezése a mentéskatalógusba",async ()=>await Execute(async ()=>{
                string transaction=quarantined.SelectedValue as string??throw new InvalidOperationException("Válassz karanténpontot.");
                if(System.Windows.MessageBox.Show("Az ellenőrzött csomag az eredeti mentéskatalógusba kerül vissza, szabad név esetén. Folytatod?",
                    "Mentéskatalógus helyreállítása",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                await Task.Run(()=>ManagedBackupRetention.RestoreToCatalog(_cluster,map.Id,transaction));await Refresh();
            }))));
        return Box(V(controls,status));
    }
}
