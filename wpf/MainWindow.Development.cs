using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowDevelopmentLedgerControls()
    {
        var ledger=DevelopmentLedger.Read();
        var filter=new ComboBox{ItemsSource=new[]{"Összes","MISSING","PARTIAL","IMPLEMENTED","TESTED","BLOCKED","VERIFIED_ON_ISOLATED_ASA","PRODUCTION_ACCEPTED"},SelectedIndex=0};
        var list=new ListBox{Height=260,Background=Panel,Foreground=Brushes.White,DisplayMemberPath="Title"};
        var details=Label("Válassz követelményt a megvalósítás és a bizonyítékok megtekintéséhez.",12,Soft);
        void Refresh()=>list.ItemsSource=ledger.Records.Where(r=>filter.SelectedIndex==0||r.Status==(string?)filter.SelectedItem)
            .Select(r=>new {Title=r.Id+" · "+UntamedPresentation.Display(r.Title)+" · "+r.Status,Record=r}).ToArray();
        filter.SelectionChanged+=(_,_)=>Refresh();
        list.SelectionChanged+=(_,_)=>{
            if(list.SelectedItem==null)return;
            var record=(DevelopmentRequirement)list.SelectedItem.GetType().GetProperty("Record")!.GetValue(list.SelectedItem)!;
            details.Text=UntamedPresentation.Redact(record.Id+" · "+record.Status+
                "\nKód: "+record.CodeReferences+"\nVáltozás: "+record.Changes+
                "\nTeszt: "+record.TestType+" / "+record.TestResult+
                "\nCommit: "+record.ImplementationCommit+"\nPR: "+record.ImplementationPr+
                "\nFennmaradó: "+record.Remaining);
        };
        Add(Box(V(Label("Központi fejlesztési munkaleltár",18,Mint,true),
            Label("7 forrásdokumentum · 61 művelet · 44 referenciamodul · 42 saját ötlet · 33 feladat. "+
                "A végleges kiadás még nincs elfogadva.",12,Soft),
            Label("MariaDB deployment baseline: "+ledger.DatabaseBaseline+". Nincs adatbáziskapcsolat vagy automatikus verzióváltás.",12,Aqua),
            filter,list,details)));
        var operations=Label("Műveleti állapot még nincs lekérdezve.",12,Soft);
        Add(Box(V(Label("Kizárólagos helyi műveletek",17,Mint,true),
            Btn("Műveleti állapotok újraolvasása",()=>{
                operations.Text=string.Join("\n",_cluster.Config.LocalMaps.Select(map=>{
                    try{return map.Name+": "+(_cluster.Operations.Observe(map.InstallationPath)?.Phase.ToString()??"Nincs rögzített művelet");}
                    catch{return map.Name+": állapot nem igazolt; kézi ellenőrzés szükséges";}
                }));
            }),operations)));
        Refresh();
    }

    public void VerifyModListFocusCiSmoke()
    {
        var mode=System.Windows.Application.Current.ShutdownMode;
        System.Windows.Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var fixture=new Window{Width=520,Height=330,ShowInTaskbar=false,Title="Untamed UI regression"};
        try
        {
            var list=new ListBox{Height=140};
            var a=new Button{Content="A",Height=40};var b=new Button{Content="B",Height=40};
            a.Style=(Style)System.Windows.Application.Current.Resources["ActionButton"];
            b.Style=(Style)System.Windows.Application.Current.Resources["ActionButton"];
            var body=new StackPanel();body.Children.Add(list);body.Children.Add(a);body.Children.Add(b);fixture.Content=body;
            int clicks=0;a.Click+=(_,_)=>{clicks++;RefreshModList(list,new[]{"PASSIVE 1. 111111","ACTIVE 1. 222222"});};
            b.Click+=(_,_)=>clicks++;
            RefreshModList(list,new[]{"ACTIVE 1. 111111","ACTIVE 2. 222222"});
            list.SelectedIndex=1;fixture.Show();fixture.Activate();fixture.UpdateLayout();
            var originalItems=list.ItemsSource;
            for(int i=0;i<100;i++)
            {
                Keyboard.Focus(b);a.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));fixture.UpdateLayout();
                if(!ReferenceEquals(originalItems,list.ItemsSource)||list.SelectedItem is not string selected||
                   !selected.EndsWith("222222",StringComparison.Ordinal)||Mouse.Captured!=null||
                   !b.IsEnabled||!b.IsHitTestVisible||Keyboard.FocusedElement!=b)
                    throw new InvalidOperationException("UNTAMED_MOD_LIST_FOCUS_REGRESSION");
                var hit=fixture.InputHitTest(b.TranslatePoint(new Point(b.ActualWidth/2,b.ActualHeight/2),fixture)) as DependencyObject;
                if(hit==null||(hit!=b&&!b.IsAncestorOf(hit)))throw new InvalidOperationException("UNTAMED_SECOND_BUTTON_OBSCURED");
                b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            if(clicks!=200)throw new InvalidOperationException("UNTAMED_MOD_CLICK_LOST");
        }
        finally{fixture.Close();System.Windows.Application.Current.ShutdownMode=mode;}
    }
}
