using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

// Each visible row describes configured launch arguments, NOT a proven installation.
// All edits remain local until explicit preview + operator approval.
internal sealed record PremiumModRow(string Id, string State, int Position, string Information);

public partial class MainWindow
{
    private Action<string>? _catalogSelectForMod;
    private readonly Dictionary<Guid,ModDraftSession> _modDraftSessions=new();

    private void ShowPremiumMods()
    {
        Header("Modok","Maponkénti modlista, állapot és sorrend – előnézet után, biztonságos helyi mentéssel.");
        _catalogSelectForMod=null;
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
        var search=new TextBox {Width=200,Height=36,Margin=new Thickness(0,0,10,8),
            ToolTip="Modazonosító vagy konfigurált állapot keresése"};
        var addId=new TextBox {Width=135,Height=36,Margin=new Thickness(0,0,9,8),
            ToolTip="Új mod CurseForge projektazonosítója (5–12 számjegy)"};
        var rows=new ListBox {MinHeight=220,MaxHeight=440,
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("PremiumModRow")};
        var summary=Label("",12,Soft);
        var selectedTitle=Label("Válassz modot",19,Mint,true);
        var selectedInfo=Label("A lista az indítási konfigurációt mutatja; a telepítés és a tényleges betöltés nincs igazolva.",12,Soft);
        var modeBox=new ComboBox {ItemsSource=new[]{"Aktív","Passzív","Letiltva","Eltávolítás"},
            SelectedIndex=0,MinWidth=180,Margin=new Thickness(0,7,0,8)};
        var status=Label("",12,Soft);
        var preview=Label("",12,Soft);
        PremiumModRow? chosen=null;

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
        LocalMap PreviewCopy()=>draft.Projected();
        void ShowDetails()
        {
            chosen=rows.SelectedItem as PremiumModRow;
            selectedTitle.Text=chosen==null?"Válassz modot":"Projekt "+chosen.Id;
            selectedInfo.Text=chosen==null?
                "Válassz egy sort a módosításhoz.":
                chosen.State+" · "+chosen.Information+
                "\nAz állapot az indítási konfigurációból származik; nem bizonyít telepített fájlokat vagy ASA-betöltést.";
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
                .ToArray();
            string term=search.Text.Trim();
            var visible=term.Length==0?all:all.Where(x=>
                x.Id.Contains(term,StringComparison.OrdinalIgnoreCase)||
                x.State.Contains(term,StringComparison.OrdinalIgnoreCase)).ToArray();
            rows.ItemsSource=visible;
            rows.SelectedItem=visible.FirstOrDefault(x=>x.Id==selectedId);
            summary.Text=CurrentMap().Name+" · "+all.Length+" konfigurált mod · "+
                state.Active.Count+" aktív · "+state.Passive.Count+" passzív · "+
                state.Disabled.Count+" tiltott · "+visible.Length+" megjelenítve";
            status.Text=!draft.IsDirty?
                "Nincs mentendő eltérés. Nem történt fájlírás vagy szerverújraindítás.":
                draft.Steps+" előkészített lépés · "+
                (draft.MatchesLive(CurrentMap())?
                    "mentés előtt ellenőrizd az előnézetet.":
                    "A helyi konfiguráció időközben megváltozott! Töltsd újra a modlistát.");
            ShowDetails();
        }
        void Stage(string id,ModMode? target,int? position=null)
        {
            draft.Stage(id,target,position); // atomic in-memory validation, ordered history
            Render();
            preview.Text="Előkészítve · visszavonás/újra elérhető · még nem mentettük.";
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
        var toolbar=new WrapPanel {Margin=new Thickness(0,3,0,8)};
        toolbar.Children.Add(selector);
        toolbar.Children.Add(search);
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
        var addBar=new WrapPanel {Margin=new Thickness(0,5,0,8)};
        addBar.Children.Add(addId);
        addBar.Children.Add(Btn("Mod hozzáadása",()=>Safely(()=>Stage(addId.Text,ModMode.Active))));
        var left=V(Label("Modlista",19,Mint,true),
            Label("Map kiválasztása, keresés és új mod felvétele",12,Soft),
            toolbar,summary,rows,addBar);
        var right=V(Label("Mod részletei",19,Mint,true),selectedTitle,selectedInfo,
            Label("Betöltési mód",12,Aqua),modeBox,
            Btn("Állapot módosítása",()=>Safely(ChangeMode)),
            Btn("↑  Előrébb a sorrendben",()=>Safely(()=>Move(-1))),
            Btn("↓  Hátrébb a sorrendben",()=>Safely(()=>Move(1))),
            Label("A modállapot módosítása önmagában nem indít újra ASA-folyamatot.",11,Soft));
        var main=new Grid {Margin=new Thickness(0,2,0,10)};
        main.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(2,GridUnitType.Star)});
        main.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star),MinWidth=225});
        var leftSurface=Box(left);
        var rightSurface=Box(right);
        Grid.SetColumn(leftSurface,0);Grid.SetColumn(rightSurface,1);
        main.Children.Add(leftSurface);main.Children.Add(rightSurface);
        Add(main);

        var actionBar=new WrapPanel {Margin=new Thickness(0,4,0,7)};
        actionBar.Children.Add(Btn("Változások előnézete",()=>Safely(()=>{
            var map=CurrentMap();var temp=PreviewCopy();
            var oldState=ModStateController.Read(map);var newState=ModStateController.Read(temp);
            preview.Text="ELŐNÉZET · "+map.Name+
                "\nElőtte: "+oldState.Active.Count+" aktív / "+oldState.Passive.Count+
                " passzív / "+oldState.Disabled.Count+" tiltott"+
                "\nUtána: "+newState.Active.Count+" aktív / "+newState.Passive.Count+
                " passzív / "+newState.Disabled.Count+" tiltott"+
                "\nElőkészített lépések: "+draft.Steps+
                "\nÉrintett modok: "+string.Join(", ",draft.TouchedModIds)+
                "\nA tényleges ASA-betöltés nincs igazolva. Újraindítás nem történik.";
        })));
        actionBar.Children.Add(Btn("↶  Visszavonás",()=>Safely(()=>{
            draft.Undo();Render();preview.Text="Az utolsó lépés visszavonva.";
        })));
        actionBar.Children.Add(Btn("↷  Újra",()=>Safely(()=>{
            draft.Redo();Render();preview.Text="Lépés újra alkalmazva a piszkozatban.";
        })));
        actionBar.Children.Add(Btn("Függőben lévő módosítások elvetése",()=>Safely(()=>{
            draft.Clear();preview.Text="Módosítások elvetve.";Render();
        })));
        var apply=Btn("Módosítások mentése",()=>Safely(()=>{
            if(!draft.IsDirty)throw new InvalidOperationException("Nincs mentendő eltérés.");
            var map=CurrentMap();var planned=PreviewCopy();
            if(!draft.MatchesLive(map))
                throw new InvalidOperationException("A modlista időközben megváltozott. Előbb olvasd újra!");
            var confirmation="Map: "+map.Name+"\n"+
                draft.Steps+" előkészített modmódosítás kerül az indítási konfigurációba.\n"+
                "Nincs szerverleállítás vagy -újraindítás.\n\nÚj modkapcsolók:\n"+
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
        actionBar.Children.Add(apply);
        Add(Box(V(Label("Függőben lévő változások",16,Mint,true),
            status,actionBar,preview)));

        // The catalog selection stages the found project instead of asking for hand-entered IDs.
        _catalogSelectForMod=id=>Safely(()=>{
            addId.Text=id;Stage(id,ModMode.Active);
            status.Text="CurseForge projekt "+id+" előkészítve – még nincs mentve.";
        });
        int before=PageBody.Children.Count;
        ShowCurseForgeSearchControls();
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
        Add(new Expander {
            Header="CurseForge katalógus és haladó modműveletek",
            Foreground=Brushes.White,Padding=new Thickness(10),
            Margin=new Thickness(0,3,10,18),Content=advanced,
            IsExpanded=false
        });
        Safely(Render);
    }
}
