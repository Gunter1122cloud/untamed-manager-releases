using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

// Disk state is not the same as runtime-loaded state. Rows never invent plugin
// versions, loaded process state, dependencies or update availability.
internal sealed record PremiumPluginRow(string Name,string State,string Hint,bool Enabled)
{
    public System.Windows.Input.ICommand? ToggleCommand { get; init; }
    public bool CanToggle => ToggleCommand is not null;
}

public partial class MainWindow
{
    private void ShowPremiumPlugins()
    {
        Header("Plugin Center","Helyi ASA API pluginok · állapot, DLL-ek, védett váltás és karbantartás.");
        var maps=_cluster.Config.LocalMaps.ToArray();
        if(maps.Length==0)
        {
            var emptyActions=new WrapPanel();
            emptyActions.Children.Add(Btn("Helyi map hozzáadása",()=>Navigate("servers")));
            emptyActions.Children.Add(Btn("Offline DEMO",EnterDemoMode));
            Add(Box(V(Label("Nincs még helyi szerver",19,Mint,true),
                Label("A pluginlista a regisztrált helyi ASA telepítésekből készül. A távoli gazdagép csak megfigyelhető.",12,Soft),
                emptyActions)));
            return;
        }
        var selector=new ComboBox {
            ItemsSource=maps,DisplayMemberPath="Name",
            SelectedItem=maps.FirstOrDefault(m=>m.Id==_preferredPluginMapId)??maps[0],
            Width=195,Margin=new Thickness(0,0,9,8)
        };
        var search=new TextBox { Width=200,Height=36,Margin=new Thickness(0,0,9,8),
            ToolTip="Keresés a helyi pluginmappák neve alapján" };
        var filter=new ComboBox { ItemsSource=new[]{"Összes","Engedélyezett","Letiltott"},
            SelectedIndex=0,Width=140,Margin=new Thickness(0,0,9,8) };
        var list=new ListView {MinHeight=260,MaxHeight=510,
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("PremiumPluginRow"),
            Margin=new Thickness(0,10,0,0)};
        var count=Label("",12,Soft);
        var selectedName=Label("Válassz plugint",19,Mint,true);
        var selectedState=Label("Kattints egy sorra a részletekért.",12,Soft);
        var readiness=Label("Állapotváltás csak igazoltan leállított helyi mapon.",12,Soft);
        var activity=Label("A frissítés csak olvasás. Nincs automatikus restart.",12,Soft);
        var binaries=new ListBox {MinHeight=50,MaxHeight=145,FontSize=11,Margin=new Thickness(0,7,0,4)};
        PremiumPluginRow? chosen=null;
        PremiumPluginRow[] all=Array.Empty<PremiumPluginRow>();
        bool busy=false;
        bool refreshing=false;
        // Initialized before any callback closes over the controls.
        Button enable=null!,disable=null!,inventory=null!;
        enable=Btn("Plugin bekapcsolása",()=>ChangePlugin(true));
        disable=Btn("Plugin kikapcsolása",()=>ChangePlugin(false));
        inventory=Btn("Kiválasztott DLL-ek ellenőrzése",InspectBinaries);
        var advancedInfo=Label("Az ASA API futásidejű pluginbetöltése nem következtethető a fájlokból.",11,Soft);

        LocalMap CurrentMap()=>selector.SelectedItem as LocalMap
            ??throw new InvalidOperationException("Előbb válassz helyi mapot.");

        void Report(string message)=>activity.Text=UntamedPresentation.Display(message);

        void Safely(Action action)
        {
            try{action();}
            catch(Exception ex)
            {
                WriteLog("premium-plugin-center",ex);
                Report("Pluginművelet elutasítva. "+UntamedPresentation.Redact(ex.Message));
            }
        }

        void ShowDetails()
        {
            chosen=list.SelectedItem as PremiumPluginRow;
            enable.IsEnabled=false;
            disable.IsEnabled=false;
            inventory.IsEnabled=chosen!=null&&!busy;
            if(chosen==null)
            {
                selectedName.Text="Válassz plugint";
                selectedState.Text=all.Length==0?
                    "A kiválasztott mapon nincs látható pluginmappa.":
                    "Kattints egy sorra az állapotváltáshoz.";
                readiness.Text="Az állapotváltás előtt minden közös telepítésű mapot ellenőrzünk.";
                return;
            }
            selectedName.Text=chosen.Name;
            selectedState.Text=chosen.State+
                " · A fájl jelenléte önmagában nem bizonyít ASA API betöltést.";
            if(busy)
            {
                readiness.Text="Pluginművelet folyamatban.";
                return;
            }
            try
            {
                var check=PluginStateManager.InspectLocalToggle(
                    _cluster,CurrentMap().Id,chosen.Name,!chosen.Enabled);
                readiness.Text=(check.Allowed?"Végrehajtható: ":"Tiltva: ")+check.Reason;
                enable.IsEnabled=!chosen.Enabled&&check.Allowed;
                disable.IsEnabled=chosen.Enabled&&check.Allowed;
            }
            catch(Exception ex)
            {
                WriteLog("premium-plugin-readiness",ex);
                readiness.Text="Nem igazolható a pluginművelet előfeltétele.";
            }
        }

        void RenderList()
        {
            string old=chosen?.Name??(list.SelectedItem as PremiumPluginRow)?.Name??"";
            string term=search.Text.Trim();
            string state=(string?)filter.SelectedItem??"Összes";
            var visible=all.Where(x=>
                (term.Length==0||x.Name.Contains(term,StringComparison.OrdinalIgnoreCase)) &&
                (state=="Összes" || (state=="Engedélyezett"&&x.Enabled) ||
                 (state=="Letiltott"&&!x.Enabled))).ToArray();
            list.ItemsSource=visible;
            list.SelectedItem=visible.FirstOrDefault(x=>
                x.Name.Equals(old,StringComparison.OrdinalIgnoreCase))
                ??visible.FirstOrDefault();
            count.Text=CurrentMap().Name+" · "+all.Length+" lemezen talált plugin"+
                " · "+all.Count(x=>x.Enabled)+" engedélyezett"+
                " · "+all.Count(x=>!x.Enabled)+" letiltott"+
                " · "+visible.Length+" megjelenítve";
            ShowDetails();
        }

        void Refresh()
        {
            if(busy)return;
            refreshing=true;
            try
            {
                var map=CurrentMap();
                _preferredPluginMapId=map.Id;
                var observed=PluginStateManager.Inventory(map);
                all=observed.Select(p=>new PremiumPluginRow(p.Name,
                    p.Enabled?"Engedélyezett":"Letiltott",
                    p.Enabled?"ASA API plugin gyökér · lemezállapot":
                              "Elkülönített letiltott pluginmappa",
                    p.Enabled) {
                        ToggleCommand=new PremiumUiCommand(()=>{
                            list.SelectedItem=list.Items.OfType<PremiumPluginRow>()
                                .FirstOrDefault(x=>x.Name.Equals(p.Name,StringComparison.OrdinalIgnoreCase));
                            ChangePlugin(!p.Enabled);
                        })
                    }).ToArray();
                RenderList();
                Report("Lemezleltár frissítve. A runtime betöltés ismeretlen. Nem módosult szerverállapot.");
            }
            catch(Exception ex)
            {
                all=Array.Empty<PremiumPluginRow>();
                list.ItemsSource=Array.Empty<PremiumPluginRow>();
                chosen=null;
                count.Text="A helyi pluginleltár nem olvasható.";
                selectedName.Text="Pluginmappa hiba";
                readiness.Text="A pluginváltás tiltva, amíg a lemezállapot nem ellenőrizhető.";
                enable.IsEnabled=false;disable.IsEnabled=false;inventory.IsEnabled=false;
                WriteLog("premium-plugin-inventory",ex);
                Report("Leltározás nem sikerült: "+UntamedPresentation.Redact(ex.Message));
            }
            finally{refreshing=false;}
        }

        void InspectBinaries()
        {
            Safely(()=>{
                if(chosen==null)throw new InvalidOperationException("Válassz plugint.");
                var target=chosen.Name;
                // LocalPluginInventory deliberately does not open JSON, secrets,
                // licenses or Store. Disabled folders remain unscanned.
                if(!chosen.Enabled)
                {
                    binaries.ItemsSource=new[]{
                        "Letiltott mappa: DLL-vizsgálat csak a bekapcsolt pluginoknál érhető el."
                    };
                    return;
                }
                var plugin=LocalPluginInventory.Scan(CurrentMap())
                    .FirstOrDefault(x=>x.PluginName.Equals(target,StringComparison.OrdinalIgnoreCase));
                if(plugin==null)throw new IOException("A kiválasztott plugin leltára időközben megváltozott.");
                binaries.ItemsSource=plugin.Protected?
                    new[]{"Védett plugin – a DLL-fájlok nem vizsgálhatók."}:
                    plugin.Binaries.Count==0?
                        new[]{"Nem található gyökérszintű DLL. Futtatás nem igazolt."}:
                        plugin.Binaries.Select(x=>x.Name+" · v"+x.Version+
                            " · "+x.Bytes+" bájt · SHA-256 "+x.Sha256).ToArray();
                Report("Csak olvasás: "+target+" DLL-leltár elkészült.");
            });
        }

        void ChangePlugin(bool enabling)
        {
            if(busy)return;
            Safely(()=>{
                var row=chosen??throw new InvalidOperationException("Válassz plugint.");
                var map=CurrentMap();
                var readinessNow=PluginStateManager.InspectLocalToggle(
                    _cluster,map.Id,row.Name,enabling);
                if(!readinessNow.Allowed)throw new InvalidOperationException(readinessNow.Reason);
                var answer=System.Windows.MessageBox.Show(
                    map.Name+" / "+row.Name+"\n"+
                    (enabling?"Plugin bekapcsolása":"Plugin kikapcsolása")+
                    "\n"+readinessNow.Reason+
                    "\nA teljes pluginmappa biztonsági másolatot kap, majd helyben átkerül "+
                    "az aktív vagy letiltott könyvtárba.\nA konfiguráció, adatfájlok és licencek megmaradnak."+
                    "\nAutomatikus ASA újraindítás NINCS. Végrehajtod?",
                    "Untamed Plugin Center · művelet megerősítése",
                    MessageBoxButton.YesNo,MessageBoxImage.Warning);
                if(answer!=MessageBoxResult.Yes)return;
                busy=true;enable.IsEnabled=false;disable.IsEnabled=false;
                PluginToggleResult result;
                try
                {
                    // Recheck at execution time, never trust the stale UI badge.
                    result=PluginStateManager.ToggleLocalChecked(_cluster,map.Id,row.Name,enabling);
                }
                finally{busy=false;}
                string keep=row.Name;
                Refresh();
                list.SelectedItem=list.Items.OfType<PremiumPluginRow>()
                    .FirstOrDefault(x=>x.Name.Equals(keep,StringComparison.OrdinalIgnoreCase));
                Report((result.Enabled?"Bekapcsolva: ":"Kikapcsolva: ")+result.Plugin+
                    "\nSHA-256 védőmásolat: "+result.BackupPackage+
                    "\nA szerver automatikus újraindítása nem történt meg.");
            });
        }

        selector.SelectionChanged+=(_,_)=>{
            if(refreshing)return;
            chosen=null;binaries.ItemsSource=null;Refresh();
        };
        search.TextChanged+=(_,_)=>RenderList();
        filter.SelectionChanged+=(_,_)=>RenderList();
        list.SelectionChanged+=(_,_)=>{
            binaries.ItemsSource=null;ShowDetails();
        };

        var toolbar=new WrapPanel {Margin=new Thickness(0,4,0,7)};
        toolbar.Children.Add(GraphiteField("MAP",selector));
        toolbar.Children.Add(GraphiteField("PLUGINKERESÉS",search));
        toolbar.Children.Add(GraphiteField("ÁLLAPOT",filter));
        toolbar.Children.Add(GraphiteField("ÚJRAOLVASÁS",Btn("Frissítés",Refresh)));
        var left=Box(V(Label("PLUGIN CENTER  /  ASA API",16,Mint,true),
            Label("Helyi pluginmappák · kapcsolás ellenőrzött leállítással és mentéssel",12,Soft),
            count,list));
        var actions=new WrapPanel {Margin=new Thickness(0,4,0,7)};
        actions.Children.Add(enable);
        actions.Children.Add(disable);
        var right=Box(V(Label("Plugin részletei",19,Mint,true),
            selectedName,selectedState,readiness,
            Label("Állapot",12,Aqua),actions,inventory,
            Label("DLL / fájlverzió / SHA-256",12,Aqua),binaries,advancedInfo));
        var layout=new Grid {Margin=new Thickness(0,0,0,3)};
        layout.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(7,GridUnitType.Star),MinWidth=340});
        layout.ColumnDefinitions.Add(new ColumnDefinition {
            Width=new GridLength(3,GridUnitType.Star),MinWidth=225});
        Grid.SetColumn(left,0);
        Grid.SetColumn(right,1);
        layout.Children.Add(left);
        layout.Children.Add(right);
        Add(Box(toolbar));
        Add(layout);
        Add(Box(V(Label("Műveleti visszajelzés",15,Mint,true),activity)));

        // Advanced modules stay discoverable without dominating the primary UI.
        StackPanel Capture(Action build)
        {
            int start=PageBody.Children.Count;
            build();
            var container=new StackPanel();
            foreach(var child in PageBody.Children.Cast<UIElement>().Skip(start).ToArray())
            {
                PageBody.Children.Remove(child);
                container.Children.Add(child);
            }
            return container;
        }
        _preferredPluginMapId=CurrentMap().Id;
        Add(new Expander {Header="ASA API verziók, frissítési csomagok és helyreállítás",
            Margin=new Thickness(0,5,9,9),Padding=new Thickness(12),
            IsExpanded=false,Content=Capture(ShowAsaApiAndPluginStates)});
        Add(new Expander {Header="Haladó pluginműveletek · tömeges kezelés, rollback, JSON",
            Margin=new Thickness(0,0,9,14),Padding=new Thickness(12),
            IsExpanded=false,Content=Capture(()=>{
                ShowLocalPluginInventoryControls();
                ShowBulkPluginsControls();
                ShowPluginRollbackControls();
                ShowSafeTextEditor(ConfigKind.PluginJson);
            })});
        Refresh();
    }
}
