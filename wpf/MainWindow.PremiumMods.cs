using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using UntamedManager.Core;

namespace UntamedManager;

// Each visible row describes configured launch arguments, NOT a proven installation.
// All edits remain local until explicit preview + operator approval.
internal sealed record PremiumModRow(string Id, string State, int Position, string Information)
{
    // Live profiles identify a mod by configured ID until a trusted catalog resolves its name.
    public string? DisplayTitle { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(DisplayTitle) ? "Modprojekt · "+Id : DisplayTitle;
    public string ProjectLabel => "CURSEFORGE ID  "+Id;
    // Images only appear once a real, authenticated CurseForge catalog result
    // supplied a validated HTTPS thumbnail; unverified project IDs keep a glyph.
    public string? ImageUrl { get; init; }
    public Brush StateBrush => State switch {
        "Aktív" => Brushes.MediumAquamarine,
        "Passzív" => Brushes.Goldenrod,
        "Letiltva" => Brushes.IndianRed,
        _ => Brushes.LightGray
    };
    public bool IsActive => State=="Aktív";
    public ICommand? ToggleCommand { get; init; }
    public bool CanToggle => ToggleCommand is not null;
}

// Used by both live and in-memory DEMO rows; delegates never bypass their existing guards.
internal sealed class PremiumUiCommand : ICommand
{
    private readonly Action _run;
    public PremiumUiCommand(Action run) => _run=run;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

public partial class MainWindow
{
    private Action<string>? _catalogSelectForMod;
    private readonly Dictionary<Guid,ModDraftSession> _modDraftSessions=new();
    // Display names are added only after an actual authenticated CurseForge catalog read.
    // No hard-coded IDs/names are substituted for the real local profile.
    private readonly Dictionary<string,string> _trustedModNames=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> _trustedModArt=new(StringComparer.Ordinal);
    private Action? _catalogMetadataUpdated;
    private string? VerifiedModName(string projectId)=>
        _trustedModNames.TryGetValue(projectId,out var name)?name:null;
    private string? VerifiedModArt(string projectId)=>
        _trustedModArt.TryGetValue(projectId,out var url)?url:null;

    private void ShowPremiumMods()
    {
        Header("Modok","Maponkénti modlista, állapot és sorrend – előnézet után, biztonságos helyi mentéssel.");
        _catalogSelectForMod=null;
        _catalogMetadataUpdated=null;
        var maps=_cluster.Config.LocalMaps.ToArray();
        if(maps.Length==0)
        {
            var options=new WrapPanel {Margin=new Thickness(0,10,0,0)};
            options.Children.Add(Btn("Map hozzáadása",()=>Navigate("servers")));
            options.Children.Add(Btn("Offline DEMO kipróbálása",EnterDemoMode));
            Add(Box(V(Label("Még nincs hozzáadott szerver",19,Mint,true),
                Label("Adj hozzá egy helyi mapot, vagy próbáld ki a DEMO módot. A program nem talál ki modtelepítési adatokat.",12,Soft),
                options)));
            var extras=new StackPanel();
            var beforeEmpty=PageBody.Children.Count;
            ShowCurseForgeSearchControls();
            foreach(var child in PageBody.Children.Cast<UIElement>().Skip(beforeEmpty).ToArray())
            {
                PageBody.Children.Remove(child);extras.Children.Add(child);
            }
            Add(new Expander {Header="CurseForge katalógus – csak olvasás",
                Content=extras,Padding=new Thickness(12),Margin=new Thickness(0,0,10,10)});
            return;
        }

        var selector=new ComboBox {ItemsSource=maps,DisplayMemberPath="Name",
            SelectedItem=maps.FirstOrDefault(x=>x.Id==_preferredModMapId)??maps[0],
            Width=190,Margin=new Thickness(0,0,10,8)};
        var search=new TextBox {Width=205,Height=36,Margin=new Thickness(0,0,10,8),
            Tag="Keresés: név vagy projekt ID",
            ToolTip="Modnév, projektazonosító vagy konfigurált állapot"};
        var filter=new ComboBox {ItemsSource=new[]{"Összes","Aktív","Passzív","Letiltva"},
            SelectedIndex=0,Width=128,Margin=new Thickness(0,0,10,8)};
        var addId=new TextBox {Width=135,Height=36,Margin=new Thickness(0,0,9,8),
            Tag="CurseForge ID",
            ToolTip="Új mod CurseForge projektazonosítója (5–12 számjegy)"};
        var rows=new ListView {MinHeight=230,MaxHeight=440,
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("PremiumModRow")};
        NestedWheelScroll.SetChainToParent(rows,true);
        var summary=Label("",12,Soft);
        var selectedTitle=Label("Válassz modot",19,Mint,true);
        var selectedInfo=Label("A lista az indítási konfigurációt mutatja; a telepítés és a tényleges betöltés nincs igazolva.",12,Soft);
        var modeBox=new ComboBox {ItemsSource=new[]{"Aktív","Passzív","Letiltva","Eltávolítás"},
            SelectedIndex=0,MinWidth=180,Margin=new Thickness(0,7,0,8)};
        var status=Label("",12,Soft);
        var preview=Label("",12,Soft);
        PremiumModRow? chosen=null;
        Expander? catalogExpander=null;
        Expander? advancedExpander=null;

        LocalMap CurrentMap()=>selector.SelectedItem as LocalMap
            ??throw new InvalidOperationException("Előbb válassz helyi mapot.");
        ModDraftSession GetDraft(LocalMap map)
        {
            // Preserve unsaved edits across tab and map navigation. Nothing
            // leaves memory before explicit, confirmed Save.
            if(_modDraftSessions.TryGetValue(map.Id,out var existing) &&
               (existing.Steps>0 || existing.MatchesLive(map)))return existing;
            var fresh=new ModDraftSession(map);
            _modDraftSessions[map.Id]=fresh;
            return fresh;
        }
        ModDraftSession draft=GetDraft(CurrentMap());
        Button? undoButton=null,redoButton=null,applyButton=null;
        LocalMap PreviewCopy()=>draft.Projected();
        void ShowDetails()
        {
            chosen=rows.SelectedItem as PremiumModRow;
            selectedTitle.Text=chosen==null?"Válassz modot":chosen.DisplayName;
            selectedInfo.Text=chosen==null?
                "Válassz ki egy modot. A sorok kapcsolója csak függő módosítást készít elő.":
                "CURSEFORGE PROJEKT  #"+chosen.Id+"\n"+
                "Konfigurált állapot: "+chosen.State+" · Sorrend: "+chosen.Position+"\n"+
                chosen.Information+"\n"+
                "Telepített fájl: nem igazolt · Függőségek/verzió: nincs ellenőrizve.\n"+
                "A váltás a biztonságos előnézetig csak memóriában történik.";
            if(chosen!=null)modeBox.SelectedItem=chosen.State;
        }
        void Render()
        {
            string selectedId=chosen?.Id??(rows.SelectedItem as PremiumModRow)?.Id??"";
            var map=PreviewCopy();
            var state=ModStateController.Read(map);
            var all=state.Active.Select((id,i)=>new PremiumModRow(id,"Aktív",i+1,
                    draft.TouchedModIds.Contains(id)?"Függőben lévő módosítás":"Konfigurált indítás"))
                .Concat(state.Passive.Select((id,i)=>new PremiumModRow(id,"Passzív",i+1,
                    draft.TouchedModIds.Contains(id)?"Függőben lévő módosítás":"Konfigurált passzív indítás")))
                .Concat(state.Disabled.Select((id,i)=>new PremiumModRow(id,"Letiltva",i+1,
                    draft.TouchedModIds.Contains(id)?"Függőben lévő módosítás":"Csak Manager-profil")))
                .Select(x=>x with {
                    DisplayTitle=VerifiedModName(x.Id),
                    ImageUrl=VerifiedModArt(x.Id),
                    ToggleCommand=new PremiumUiCommand(()=>Safely(()=>Stage(x.Id,
                        x.IsActive?ModMode.Disabled:ModMode.Active)))
                }).ToArray();
            string term=search.Text.Trim();
            string selectedFilter=filter.SelectedItem as string??"Összes";
            var visible=all.Where(x=>
                (term.Length==0 || x.Id.Contains(term,StringComparison.OrdinalIgnoreCase)||
                 x.DisplayName.Contains(term,StringComparison.OrdinalIgnoreCase)||
                 x.State.Contains(term,StringComparison.OrdinalIgnoreCase)) &&
                (selectedFilter=="Összes" || x.State==selectedFilter)).ToArray();
            var previousOffset=NestedWheelScroll.CaptureVerticalOffset(rows);
            rows.ItemsSource=visible;
            rows.SelectedItem=visible.FirstOrDefault(x=>x.Id==selectedId)
                ??visible.FirstOrDefault();
            NestedWheelScroll.RestoreVerticalOffset(rows,previousOffset);
            summary.Text=CurrentMap().Name+" · "+all.Length+" konfigurált mod · "+
                state.Active.Count+" aktív · "+state.Passive.Count+" passzív · "+
                state.Disabled.Count+" tiltott · "+visible.Length+" megjelenítve";
            status.Text=!draft.IsDirty?
                "Nincs mentendő eltérés. Nem történt fájlírás vagy szerverújraindítás.":
                draft.Steps+" előkészített lépés · "+
                (draft.MatchesLive(CurrentMap())?
                    "mentés előtt ellenőrizd az előnézetet.":
                    "A helyi konfiguráció időközben megváltozott! Töltsd újra a modlistát.");
            if(undoButton!=null)undoButton.IsEnabled=draft.CanUndo;
            if(redoButton!=null)redoButton.IsEnabled=draft.CanRedo;
            if(applyButton!=null)applyButton.IsEnabled=draft.IsDirty && draft.MatchesLive(CurrentMap());
            ShowDetails();
        }
        static string ModStateLabel(ModMode? mode)=>mode switch {
            ModMode.Active=>"aktív",ModMode.Passive=>"passzív",
            ModMode.Disabled=>"letiltva",_=>"nincs"
        };
        static string ChangeLine(ModDraftDifference change)
        {
            var before=change.BeforeMode.HasValue?
                ModStateLabel(change.BeforeMode)+" #"+change.BeforePosition:"nincs";
            var after=change.AfterMode.HasValue?
                ModStateLabel(change.AfterMode)+" #"+change.AfterPosition:"eltávolítva";
            return change.ProjectId+"   "+before+" → "+after;
        }
        void Stage(string id,ModMode? target,int? position=null)
        {
            draft.Stage(id,target,position); // atomic in-memory validation, ordered history
            Render();
            preview.Text="Előkészítve · visszavonás/újra elérhető · még nem mentettük.";
        }
        void AddProject(string id)
        {
            id=id.Trim();
            var stagedState=ModStateController.Read(draft.Projected());
            if(stagedState.Active.Concat(stagedState.Passive).Concat(stagedState.Disabled)
                .Contains(id,StringComparer.Ordinal))
            {
                search.Text=id;
                status.Text="A projekt már a map modlistájában szerepel. Válaszd ki az állapot vagy sorrend módosításához.";
                return;
            }
            Stage(id,ModMode.Active);
        }
        void Safely(Action action)
        {
            try {action();}
            catch(Exception ex)
            {
                WriteLog("premium-mod-center",ex);
                status.Text="Művelet elutasítva: "+UntamedPresentation.Redact(ex.Message);
            }
        }
        void ChangeMode()
        {
            if(chosen==null)throw new InvalidOperationException("Válassz egy modot.");
            var target=(string?)modeBox.SelectedItem switch {
                "Aktív" => ModMode.Active,"Passzív" => ModMode.Passive,
                "Letiltva" => ModMode.Disabled,"Eltávolítás" => (ModMode?)null,
                _ => throw new InvalidOperationException("Ismeretlen mód")
            };
            if((target==ModMode.Active && chosen.State=="Aktív") ||
               (target==ModMode.Passive && chosen.State=="Passzív") ||
               (target==ModMode.Disabled && chosen.State=="Letiltva"))
            {
                status.Text="A mod már ebben az állapotban van. Sorrendhez használd a fel/le gombokat.";
                return;
            }
            Stage(chosen.Id,target);
        }
        void Move(int amount)
        {
            if(chosen==null)throw new InvalidOperationException("Válassz egy modot.");
            var target=chosen.State switch {
                "Aktív"=>ModMode.Active,"Passzív"=>ModMode.Passive,
                "Letiltva"=>ModMode.Disabled,
                _=>throw new InvalidDataException("Ismeretlen modállapot")
            };
            int count=ModStateController.Read(PreviewCopy()) switch {
                var state when target==ModMode.Active=>state.Active.Count,
                var state when target==ModMode.Passive=>state.Passive.Count,
                var state=>state.Disabled.Count
            };
            int wanted=Math.Clamp(chosen.Position-1+amount,0,count-1);
            if(wanted==chosen.Position-1)return;
            Stage(chosen.Id,target,wanted);
        }
        selector.SelectionChanged+=(_,_)=>{
            if(selector.SelectedItem is not LocalMap selectedMap)return;
            draft=GetDraft(selectedMap);
            chosen=null;preview.Text="";
            _preferredModMapId=selectedMap.Id;
            Safely(Render);
        };
        rows.SelectionChanged+=(_,_)=>ShowDetails();
        search.TextChanged+=(_,_)=>Safely(Render);
        filter.SelectionChanged+=(_,_)=>Safely(Render);
        var toolbar=new WrapPanel {Margin=new Thickness(0,3,0,8)};
        toolbar.Children.Add(selector);
        toolbar.Children.Add(search);
        toolbar.Children.Add(filter);
        toolbar.Children.Add(Btn("Konfiguráció újraolvasása",()=>Safely(()=>{
            if(draft.IsDirty && System.Windows.MessageBox.Show(
                "A még nem mentett modmódosítások elvesznek. Újraolvasod a helyi profilt?",
                "Modlista újraolvasása",MessageBoxButton.YesNo,
                MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            draft=new ModDraftSession(CurrentMap());
            _modDraftSessions[draft.MapId]=draft;
            chosen=null;preview.Text="A helyi konfiguráció újraolvasva.";
            Render();
        })));
        toolbar.Children.Add(addId);
        toolbar.Children.Add(Btn("＋ Mod hozzáadása",()=>Safely(()=>AddProject(addId.Text))));
        toolbar.Children.Add(Btn("CurseForge keresés",()=>{
            if(catalogExpander==null)return;
            catalogExpander.IsExpanded=true;
            catalogExpander.BringIntoView();
        }));
        toolbar.Children.Add(Btn("Tömeges / többmapos",()=>{
            if(advancedExpander==null)return;
            advancedExpander.IsExpanded=true;
            advancedExpander.BringIntoView();
        }));
        var left=V(Label("MOD CENTER  /  KONFIGURÁLT MODOK",16,Mint,true),
            Label("Sorrend · státusz · kapcsolók · változtatás csak jóváhagyás után",12,Soft),
            summary,rows);
        var right=V(Label("Mod részletei",19,Mint,true),selectedTitle,selectedInfo,
            Label("Betöltési mód",12,Aqua),modeBox,
            Btn("Állapot módosítása",()=>Safely(ChangeMode)),
            Btn("↑  Előrébb a sorrendben",()=>Safely(()=>Move(-1))),
            Btn("↓  Hátrébb a sorrendben",()=>Safely(()=>Move(1))),
            Btn("Mod keresése CurseForge-on",()=>{
                if(catalogExpander==null)return;
                catalogExpander.IsExpanded=true;
                catalogExpander.BringIntoView();
            }),
            Label("Mentés előtt minden eltérést az alsó műveleti sávban kell jóváhagyni. Szerver újraindítás nem történik.",11,Soft));
        var main=new Grid {Margin=new Thickness(0,2,0,10)};
        main.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(7,GridUnitType.Star),MinWidth=340});
        main.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(3,GridUnitType.Star),MinWidth=225});
        var leftSurface=Box(left);
        var rightSurface=Box(right);
        Grid.SetColumn(leftSurface,0);Grid.SetColumn(rightSurface,1);
        main.Children.Add(leftSurface);main.Children.Add(rightSurface);
        BindResponsiveDetails(main,leftSurface,rightSurface);
        Add(Box(toolbar));
        Add(main);

        var actionBar=new WrapPanel {Margin=new Thickness(0,4,0,7)};
        actionBar.Children.Add(Btn("Változások előnézete",()=>Safely(()=>{
            var map=CurrentMap();var temp=PreviewCopy();
            var oldState=ModStateController.Read(map);var newState=ModStateController.Read(temp);
            var differences=draft.Differences();
            preview.Text="ELŐNÉZET · "+map.Name+
                "\nElőtte: "+oldState.Active.Count+" aktív / "+oldState.Passive.Count+
                " passzív / "+oldState.Disabled.Count+" tiltott"+
                "\nUtána: "+newState.Active.Count+" aktív / "+newState.Passive.Count+
                " passzív / "+newState.Disabled.Count+" tiltott"+
                "\nLépések: "+draft.Steps+" · tényleges állapot/sorrend eltérések: "+differences.Count+
                (differences.Count>0?"\n"+string.Join("\n",differences.Take(25).Select(ChangeLine)):"")+
                (differences.Count>25?"\n... további "+(differences.Count-25)+" eltérés":"")+
                "\nA tényleges ASA-betöltés nincs igazolva. Újraindítás nem történik.";
        })));
        undoButton=Btn("↶  Visszavonás",()=>Safely(()=>{
            draft.Undo();Render();preview.Text="Az utolsó lépés visszavonva.";
        }));
        redoButton=Btn("↷  Újra",()=>Safely(()=>{
            draft.Redo();Render();preview.Text="Lépés újra alkalmazva a piszkozatban.";
        }));
        actionBar.Children.Add(undoButton);
        actionBar.Children.Add(redoButton);
        actionBar.Children.Add(Btn("Függőben lévő módosítások elvetése",()=>Safely(()=>{
            draft.Clear();preview.Text="Módosítások elvetve.";Render();
        })));
        var apply=Btn("Módosítások mentése",()=>Safely(()=>{
            if(!draft.IsDirty)throw new InvalidOperationException("Nincs mentendő eltérés.");
            var map=CurrentMap();var planned=PreviewCopy();
            if(!draft.MatchesLive(map))
                throw new InvalidOperationException("A modlista időközben megváltozott. Előbb olvasd újra!");
            var differences=draft.Differences();
            var confirmation="Map: "+map.Name+"\n"+
                draft.Steps+" előkészített modlépés, "+differences.Count+
                " tényleges állapot- vagy sorrendeltérés.\n"+
                string.Join("\n",differences.Take(15).Select(ChangeLine))+
                (differences.Count>15?"\n... további "+(differences.Count-15)+" eltérés":"")+
                "\nNincs szerverleállítás vagy -újraindítás.\n\nÚj modkapcsolók:\n"+
                UntamedPresentation.Redact(planned.Arguments);
            if(System.Windows.MessageBox.Show(confirmation,"Modmódosítások mentése",
                MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            draft.Save(_cluster); // optimistic check repeated under configuration lease
            draft=new ModDraftSession(CurrentMap());
            _modDraftSessions[draft.MapId]=draft;
            chosen=null;
            preview.Text="Módosítások elmentve a helyi Manager-profilba. ASA újraindítás NEM történt.";
            Render();
        }));
        apply.Style=(Style)System.Windows.Application.Current.FindResource("PrimaryAction");
        applyButton=apply;
        actionBar.Children.Add(apply);
        Add(Box(V(Label("Függőben lévő változások",16,Mint,true),
            status,actionBar,preview)));

        // Catalog updates refresh the existing live list, without changing a
        // single configured ID, order, saved draft or server process.
        _catalogMetadataUpdated=()=>Safely(Render);
        // The catalog selection stages the found project instead of asking for hand-entered IDs.
        _catalogSelectForMod=id=>Safely(()=>{
            addId.Text=id;AddProject(id);
            status.Text="CurseForge projekt "+id+" előkészítve – még nincs mentve.";
        });
        // The read-only official catalog is a first-class tool, not buried
        // among destructive/advanced operations. Do not introduce a second API client.
        int before=PageBody.Children.Count;
        ShowCurseForgeSearchControls();
        var catalogPane=new StackPanel();
        foreach(var element in PageBody.Children.Cast<UIElement>().Skip(before).ToArray())
        {
            PageBody.Children.Remove(element);
            catalogPane.Children.Add(element);
        }
        catalogExpander=new Expander {
            Header="CurseForge · online modkereső / hiteles modadatok",
            Foreground=Mint,Padding=new Thickness(10),
            Margin=new Thickness(0,3,12,12),Content=catalogPane,
            IsExpanded=false
        };
        Add(catalogExpander);
        before=PageBody.Children.Count;
        ShowModStateEditor();
        ShowBulkModsControls();
        ShowLocalModAssignmentInventory();
        ShowModUpdateCenter();
        ShowSteamCmdPreflightControls();
        ShowAsaInstallationPreflightControls();
        var advanced=new StackPanel();
        foreach(var element in PageBody.Children.Cast<UIElement>().Skip(before).ToArray())
        {
            PageBody.Children.Remove(element);
            advanced.Children.Add(element);
        }
        advancedExpander=new Expander {
            Header="Speciális modműveletek · tömeges szerkesztés / leltár / SteamCMD",
            Foreground=Soft,Padding=new Thickness(10),
            Margin=new Thickness(0,3,12,18),Content=advanced,
            IsExpanded=false
        };
        Add(advancedExpander);
        Safely(Render);
    }
}
