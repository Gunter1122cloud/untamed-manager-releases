using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms = System.Windows.Forms;

namespace UntamedManager;

// Every row comes from SHA-256 inspection of a single explicitly local map.
// "ÉP" means file integrity ONLY; it is not proof of ASA save consistency.
internal sealed record PremiumBackupRow(
    LocalBackupHistoryEntry Entry,string Name,string Quality,string Description);

public partial class MainWindow
{
    private void ShowPremiumBackups()
    {
        Header("Mentések","Maponkénti offline fájlmásolat · SHA-256 ellenőrzés · biztonságos előnézet.");
        Add(Box(V(Label("BACKUP CENTER  /  OFFLINE MENTÉSEK",16,Mint,true),
            Label("Csak regisztrált helyi, igazoltan leállított map · SHA-256 ellenőrzés. "+
                  "A helyi csomag NEM bizonyítja az ASA SaveComplete, Store-v1, utazások vagy SQL konzisztenciáját.",12,Soft))));
        var maps=_cluster.Config.LocalMaps.ToArray();
        if(maps.Length==0)
        {
            Add(Box(V(Label("Nincs hozzáadott helyi map",17,Mint,true),
                Label("Helyi offline fájlmentéshez regisztrálj egy mapot a Szerverek oldalon.",12,Soft),
                Btn("Szerverek megnyitása",()=>Navigate("servers")))));
            AddAdvancedBackupTools(null);
            return;
        }

        var selector=new ComboBox {
            ItemsSource=maps,DisplayMemberPath="Name",Width=190,
            SelectedItem=maps.FirstOrDefault(x=>x.Id==_preferredBackupMapId)??maps[0],
            Margin=new Thickness(0,0,9,8)
        };
        var world=new CheckBox {Content="Világ + karakterek + tribe",IsChecked=true,
            Margin=new Thickness(0,2,0,5)};
        var ini=new CheckBox {Content="Game.ini és GameUserSettings.ini",IsChecked=true,
            Margin=new Thickness(0,2,0,5)};
        var plugins=new CheckBox {Content="Helyi API pluginfájlok + konfiguráció",IsChecked=false,
            Margin=new Thickness(0,2,0,5)};
        var root=new TextBox {MinHeight=38,Margin=new Thickness(0,0,0,8),
            ToolTip="Külön helyi lemezútvonal, az ASA telepítési könyvtáron kívül"};
        var preflightStatus=Label("Először ellenőrizd a kijelölt mentési adatkört.",12,Soft);
        var operationStatus=Label("Nincs folyamatban helyi mentési művelet.",12,Soft);
        var latestStatus=Label("Mentési előzmények: még nem ellenőrizve.",12,Soft);
        var detail=Label("Válassz mentést a fájlszintű integritás ellenőrzéséhez.",12,Soft);
        var list=new ListView{
            MinHeight=260,MaxHeight=530,Margin=new Thickness(0,7,0,8),
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("PremiumBackupRow")
        };
        var count=Label("",12,Soft);
        LocalBackupPreflight? preview=null;
        bool busy=false;
        int processed=0;
        Button previewButton=null!,createButton=null!,historyButton=null!,verifyButton=null!;
        var advancedHost=new StackPanel();

        LocalMap CurrentMap()=>selector.SelectedItem as LocalMap
            ??throw new InvalidOperationException("Válassz egy helyi mapot.");
        LocalBackupScope ChosenScope()
        {
            LocalBackupScope scope=LocalBackupScope.None;
            if(world.IsChecked==true)scope|=LocalBackupScope.WorldAndProfiles;
            if(ini.IsChecked==true)scope|=LocalBackupScope.Ini;
            if(plugins.IsChecked==true)scope|=LocalBackupScope.Plugins;
            if(scope==LocalBackupScope.None)
                throw new InvalidOperationException("Legalább egy mentési adatkört válassz.");
            return scope;
        }
        string DefaultRoot(LocalMap map)=>Path.Combine(_cluster.Store.Root,
            "local-backups",map.Id.ToString("N"));
        void SetBusy(bool value)
        {
            busy=value;
            previewButton.IsEnabled=!busy;
            historyButton.IsEnabled=!busy;
            verifyButton.IsEnabled=!busy&&list.SelectedItem is PremiumBackupRow;
            createButton.IsEnabled=!busy&&preview!=null;
            selector.IsEnabled=!busy;
            root.IsReadOnly=busy;
            world.IsEnabled=!busy;ini.IsEnabled=!busy;plugins.IsEnabled=!busy;
        }
        void Invalidate()
        {
            preview=null;
            if(createButton!=null)createButton.IsEnabled=false;
            preflightStatus.Text="Az adatkör vagy célútvonal módosult. Új előnézet szükséges.";
        }
        async Task ReadHistory()
        {
            if(busy)return;
            var map=CurrentMap();
            SetBusy(true);
            latestStatus.Text="Korábbi mentések SHA-256 ellenőrzése...";
            try
            {
                var packages=await Task.Run(()=>LocalBackupCatalog.Inspect(root.Text.Trim(),map.Id,25));
                if(CurrentMap().Id!=map.Id)return;
                var rows=packages.Select(p=>new PremiumBackupRow(p,
                    Path.GetFileName(p.PackageDirectory),
                    p.Valid?"SHA-256: ÉP":"HIBÁS / HIÁNYOS",
                    p.Valid
                        ?p.FileCount+" fájl · "+(p.TotalBytes/1048576d).ToString("F1")+
                         " MiB · "+(p.CreatedUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm")??"idő ismeretlen")
                        :UntamedPresentation.Display(p.Status))).ToArray();
                list.ItemsSource=rows;
                count.Text=rows.Length+" helyi csomag · "+rows.Count(x=>x.Entry.Valid)+
                    " fájlszinten ép · "+rows.Count(x=>!x.Entry.Valid)+" hibás / hiányos";
                latestStatus.Text=rows.Length==0?
                    "A kijelölt helyi mappában nem található mentés.":
                    "Az ellenőrzés befejeződött. Az ASA mentési konzisztenciája továbbra sem igazolt.";
            }
            catch(Exception ex)
            {
                list.ItemsSource=Array.Empty<PremiumBackupRow>();
                count.Text="A helyi mentéskatalógus nem ellenőrizhető.";
                latestStatus.Text="Leltározás sikertelen: "+UntamedPresentation.Redact(ex.Message);
                WriteLog("premium-backup-history",ex);
            }
            finally{SetBusy(false);}
        }
        async Task PreviewBackup()
        {
            if(busy)return;
            var map=CurrentMap();
            LocalBackupScope scope;
            try{scope=ChosenScope();}
            catch(Exception ex){preflightStatus.Text=ex.Message;return;}
            preview=null;
            SetBusy(true);
            preflightStatus.Text="Mentési cél, fájllista és szabad hely ellenőrzése...";
            try
            {
                string name=DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+
                    "-"+Guid.NewGuid().ToString("N")[..8];
                string output=Path.Combine(root.Text.Trim(),name);
                var observed=_cluster.Check(map);
                var next=await Task.Run(()=>LocalBackupWorkflow.Preview(map,observed,output,scope));
                if(CurrentMap().Id!=map.Id)return;
                preview=next;
                preflightStatus.Text="ELŐNÉZET · "+map.Name+
                    "\nAdatkör: "+next.Scope+
                    "\nFájlok: "+next.Files+
                    " · összesen "+(next.SelectedBytes/1048576d).ToString("F1")+" MiB"+
                    "\nSzabad hely: "+(next.FreeBytes/1073741824d).ToString("F1")+
                    " GiB · biztonsági tartalék: "+(next.ReserveBytes/1048576d).ToString("F1")+" MiB"+
                    "\nCél: "+next.Destination+
                    "\nAz ASA/SQL/Store konzisztenciája NINCS igazolva.";
            }
            catch(Exception ex)
            {
                preview=null;
                preflightStatus.Text="Nem készíthető mentés: "+
                    UntamedPresentation.Redact(ex.Message);
                WriteLog("premium-backup-preview",ex);
            }
            finally{SetBusy(false);}
        }
        async Task Create()
        {
            if(busy || preview==null)return;
            var planned=preview;
            var map=CurrentMap();
            try
            {
                if(map.Id!=planned.MapId||
                   ChosenScope()!=planned.Scope||
                   !Path.GetFullPath(root.Text.Trim()).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(Path.GetDirectoryName(planned.Destination)?.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {Invalidate();return;}
                if(System.Windows.MessageBox.Show(
                    "Helyi map: "+map.Name+
                    "\nFájlszintű mentés · "+planned.Files+" előzetesen ellenőrzött fájl"+
                    "\nCél: "+planned.Destination+
                    "\nCsak a leállított map helyi fájladatait másoljuk. "+
                    "A teljes ASA/Cluster Vault/SQL mentési konzisztencia NEM igazolt."+
                    "\nSem szerverindítás, sem restore nem történik.\n\nLétrehozod?",
                    "Untamed Manager · offline mentés",
                    MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                SetBusy(true);
                processed=0;
                operationStatus.Text="Offline fájlmásolat készítése és SHA-256 ellenőrzése...";
                var observed=_cluster.Check(map);
                var result=await Task.Run(()=>LocalBackupWorkflow.Create(
                    map,observed,planned.Destination,planned.Scope,_=>{
                        int n=Interlocked.Increment(ref processed);
                        if(n%25==0)Dispatcher.BeginInvoke(()=>{
                            if(busy)operationStatus.Text="Másolt fájlok: "+n+
                                " · ellenőrzés folyamatban...";
                        });
                    }));
                operationStatus.Text="ELLENŐRZÖTT FÁJLMÁSOLAT · "+
                    result.Manifest.Files.Count+" fájl\n"+result.PackageDirectory+
                    "\nFájlok SHA-256 szerint épek. Restore NEM történt; "+
                    "ASA mentési konzisztencia nincs igazolva.";
                preview=null;
            }
            catch(Exception ex)
            {
                operationStatus.Text="Mentés elutasítva vagy sikertelen: "+
                    UntamedPresentation.Redact(ex.Message);
                WriteLog("premium-backup-create",ex);
                preview=null;
            }
            finally{SetBusy(false);}
            await ReadHistory();
        }
        async Task VerifySelected()
        {
            if(busy)return;
            if(list.SelectedItem is not PremiumBackupRow selected)
            {detail.Text="Előbb válassz egy korábbi mentést.";return;}
            var map=CurrentMap();
            SetBusy(true);
            detail.Text="Kijelölt csomag fájlszintű SHA-256 ellenőrzése...";
            try
            {
                var verified=await Task.Run(()=>TestBackupPackage.Verify(
                    selected.Entry.PackageDirectory,map.Id));
                detail.Text="SHA-256 szerint ÉP · "+verified.Files.Count+
                    " fájl · "+verified.CreatedUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm")+
                    "\nAdatkör: "+verified.Scope+
                    "\nA csomag ASA/SQL/Store visszaállításra nem hitelesített.";
            }
            catch(Exception ex)
            {
                detail.Text="ÉPSÉG NEM IGAZOLT: "+UntamedPresentation.Redact(ex.Message);
                WriteLog("premium-backup-verify",ex);
            }
            finally{SetBusy(false);}
        }
        previewButton=Btn("Mentés előnézete",async ()=>await PreviewBackup());
        createButton=Btn("Ellenőrzött mentés készítése",async ()=>await Create());
        createButton.Style=(Style)System.Windows.Application.Current.FindResource("PrimaryAction");
        createButton.IsEnabled=false;
        historyButton=Btn("Előzmények frissítése",async ()=>await ReadHistory());
        verifyButton=Btn("Kijelölt csomag ellenőrzése",async ()=>await VerifySelected());
        verifyButton.IsEnabled=false;
        list.SelectionChanged+=(_,_)=>{
            if(!busy)verifyButton.IsEnabled=list.SelectedItem is PremiumBackupRow;
            detail.Text=list.SelectedItem is PremiumBackupRow selected?
                selected.Entry.Status+
                "\n"+selected.Entry.PackageDirectory+
                "\nFájlszintű ellenőrzés. ASA/Store/SQL konzisztencia NEM igazolt.":
                "Válassz mentést a fájlszintű ellenőrzéshez.";
        };
        selector.SelectionChanged+=(_,_)=>{
            if(selector.SelectedItem is not LocalMap map)return;
            _preferredBackupMapId=map.Id;
            root.Text=DefaultRoot(map);
            list.ItemsSource=Array.Empty<PremiumBackupRow>();
            count.Text="A kiválasztott map korábbi mentései még nincsenek ellenőrizve.";
            latestStatus.Text="Előzmények frissítésével kezdheted a katalógus megtekintését.";
            Invalidate();
            BuildAdvanced(map);
        };
        root.TextChanged+=(_,_)=>Invalidate();
        world.Checked+=(_,_)=>Invalidate();world.Unchecked+=(_,_)=>Invalidate();
        ini.Checked+=(_,_)=>Invalidate();ini.Unchecked+=(_,_)=>Invalidate();
        plugins.Checked+=(_,_)=>Invalidate();plugins.Unchecked+=(_,_)=>Invalidate();

        var scopePanel=V(world,ini,plugins);
        var folderPick=Btn("Célmappa kiválasztása",()=>{
            if(busy)return;
            using var pick=new Forms.FolderBrowserDialog{
                Description="Helyi, az ASA telepítési könyvtártól különálló mentési mappa",
                SelectedPath=root.Text.Trim()
            };
            if(pick.ShowDialog()==Forms.DialogResult.OK)root.Text=pick.SelectedPath;
        });
        var createActions=new WrapPanel {Margin=new Thickness(0,7,0,9)};
        createActions.Children.Add(previewButton);
        createActions.Children.Add(createButton);
        var select=V(Label("Map kiválasztása",12,Aqua),selector,
            Label("Mentendő adatkörök",12,Aqua),scopePanel,
            Label("Helyi mentési célkönyvtár",12,Aqua),root,
            folderPick,createActions,preflightStatus);
        var historyActions=new WrapPanel{Margin=new Thickness(0,3,0,7)};
        historyActions.Children.Add(historyButton);
        historyActions.Children.Add(verifyButton);
        var catalog=V(Label("MENTÉSI ELŐZMÉNYEK",17,Mint,true),
            Label("Fájlszintű SHA-256 ellenőrzés · nem garantál ASA-visszaállíthatóságot",12,Soft),
            count,list,detail);
        var grid=new Grid{Margin=new Thickness(0,0,0,7)};
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(4,GridUnitType.Star),MinWidth=285});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(6,GridUnitType.Star),MinWidth=320});
        var left=Box(V(Label("ÚJ HELYI MENTÉS",17,Mint,true),select));
        var right=Box(catalog);
        Grid.SetColumn(right,1);
        grid.Children.Add(left);grid.Children.Add(right);
        Add(Box(historyActions));
        Add(grid);
        Add(Box(V(Label("MŰVELETI ÁLLAPOT",15,Mint,true),operationStatus,latestStatus)));

        Add(new Expander{Header="Haladó mentési funkciók · retenció, SQL, teljes leltár",
            Padding=new Thickness(10),Margin=new Thickness(0,3,9,14),
            IsExpanded=false,Content=advancedHost});

        void BuildAdvanced(LocalMap? map)
        {
            advancedHost.Children.Clear();
            if(map!=null)
            {
                advancedHost.Children.Add(Box(V(Label("Helyi map · részletes mentéskezelés",17,Mint,true),
                    Label("Egyedi célmappák, külön ellenőrzés, karantén és visszahelyezés.",12,Soft),
                    MapBackupPane(map))));
            }
            advancedHost.Children.Add(Box(V(Label("Cluster helyi mentésleltár",17,Mint,true),
                Label("Az összes regisztrált helyi map SHA-256 csomagellenőrzése.",12,Soft))));
            int before=PageBody.Children.Count;
            ShowMultiMapBackupOverview();
            ShowSqlBackupControls();
            ShowRetentionPreviewControls();
            foreach(var e in PageBody.Children.Cast<UIElement>().Skip(before).ToArray())
            {
                PageBody.Children.Remove(e);
                advancedHost.Children.Add(e);
            }
        }
        _preferredBackupMapId=CurrentMap().Id;
        root.Text=DefaultRoot(CurrentMap());
        count.Text="Előzmények frissítésével ellenőrizheted a mentéseket.";
        BuildAdvanced(CurrentMap());
        SetBusy(false);
    }

    private void AddAdvancedBackupTools(LocalMap? selected)
    {
        var holder=new StackPanel();
        int before=PageBody.Children.Count;
        ShowSqlBackupControls();
        ShowMultiMapBackupOverview();
        ShowRetentionPreviewControls();
        foreach(var e in PageBody.Children.Cast<UIElement>().Skip(before).ToArray())
        {
            PageBody.Children.Remove(e);
            holder.Children.Add(e);
        }
        Add(new Expander{Header="Haladó SQL / helyi leltár / retenció",
            IsExpanded=false,Padding=new Thickness(10),Content=holder});
    }
}
