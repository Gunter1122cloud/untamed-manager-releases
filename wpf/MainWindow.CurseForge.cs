using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private void ShowCurseForgeSearchControls()
    {
        var key=new PasswordBox { Height=36,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua };
        var query=new TextBox { Height=38,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua,Text="Shiny" };
        var projectId=new TextBox {Height=38,Width=180,Tag="CurseForge projekt ID",
            ToolTip="Pontosan az ASA-hoz tartozó CurseForge modazonosító (nem szabad szöveg)"};
        var results=new ListBox { MaxHeight=330,MinHeight=120,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua,
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("CurseForgeSearchRow") };
        NestedWheelScroll.SetChainToParent(results,true);
        var detail=Label("Válassz modot a találati listából a részletekhez.",12,Soft);
        detail.TextWrapping=TextWrapping.Wrap;
        var paging=Label("Nincs lekérdezés.",12,Soft);
        var status=Label(
            string.IsNullOrEmpty(_cluster.Config.CurseForgeApiKeyProtected)
                ?"A CurseForge API-kulcs nincs beállítva. Csak jóváhagyott kulccsal lehet keresni."
                :"Helyileg védett CurseForge API-kulcs beállítva. A keresés csak olvas.",
            12,Soft);
        var catalog=new CurseForgeCatalog(_http);
        CurseForgeSearchResult? last=null;
        string lastTerm="";
        bool busy=false;
        Button? search=null,previous=null,next=null;
        results.SelectionChanged+=(_,_)=>{
            if(results.SelectedItem is not CurseForgeMod mod)
            {
                detail.Text="Válassz modot a találati listából a részletekhez.";
                return;
            }
            string summary=mod.Summary;
            if(summary.Length>500)summary=summary[..500]+"…";
            detail.Text=mod.Name+" · ID "+mod.Id+"\n"+
                (string.IsNullOrWhiteSpace(summary)?"Leírás nem elérhető.":summary)+"\n"+
                "Utolsó ismert fájl: "+(mod.LatestFile??"ismeretlen")+"\n"+
                "Forrás: "+(mod.Website??"nincs hiteles CurseForge-link")+
                "\nCrossplay és prémium státusz: nincs ellenőrizve.";
        };
        async Task LoadPage(int index,string term)
        {
            if(busy)return;
            var protectedKey=_cluster.Config.CurseForgeApiKeyProtected;
            if(string.IsNullOrWhiteSpace(protectedKey))
            {
                status.Text="Előbb ments egy jóváhagyott CurseForge API-kulcsot.";
                return;
            }
            busy=true;
            if(search!=null)search.IsEnabled=false;
            if(previous!=null)previous.IsEnabled=false;
            if(next!=null)next.IsEnabled=false;
            status.Text="CurseForge modkeresés folyamatban...";
            try
            {
                var page=await catalog.SearchAsaAsync(
                    SecureStore.Unprotect(protectedKey),term,index);
                last=page;
                lastTerm=term;
                results.ItemsSource=page.Mods.ToArray();
                foreach(var mod in page.Mods)
                {
                    string id=mod.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _trustedModNames[id]=UntamedPresentation.Display(mod.Name);
                    if(mod.ThumbnailUrl is not null)
                        _trustedModArt[id]=mod.ThumbnailUrl;
                }
                // Only update visual metadata for configured IDs; no mod
                // installation, load order, draft or disk state changes.
                _catalogMetadataUpdated?.Invoke();
                // The provider's pagination count can describe more results than one page.
                paging.Text=$"Oldal {index/25+1} · {page.Mods.Count} találat ezen az oldalon · "+
                    $"összesen {page.TotalCount} · további oldal: {(page.HasMore?"igen":"nincs")}";
                status.Text="A keresés csak CurseForge metaadatokat olvas. "+
                    "Modtelepítés, crossplay és fizetős besorolás nincs ellenőrizve.";
                detail.Text="Válassz modot a találati listából a részletekhez.";
            }
            catch(Exception e)
            {
                last=null;
                results.ItemsSource=Array.Empty<CurseForgeMod>();
                paging.Text="Nem sikerült ellenőrizni az oldalt.";
                status.Text="CurseForge keresési hiba: "+UntamedPresentation.Redact(e.Message);
                WriteLog("curseforge-search",e);
            }
            finally
            {
                busy=false;
                if(search!=null)search.IsEnabled=true;
                if(previous!=null)previous.IsEnabled=last!=null && last.Index>0;
                if(next!=null)next.IsEnabled=last?.HasMore==true;
            }
        }
        async Task LookupExact(string suppliedId)
        {
            if(busy)return;
            if(!int.TryParse(suppliedId.Trim(),out var id)||id<10000)
            {
                status.Text="Érvénytelen projektazonosító – legalább ötjegyű, pozitív szám szükséges.";
                return;
            }
            var protectedKey=_cluster.Config.CurseForgeApiKeyProtected;
            if(string.IsNullOrWhiteSpace(protectedKey))
            {
                status.Text="Előbb állíts be jóváhagyott CurseForge API-kulcsot.";
                return;
            }
            busy=true;
            if(search!=null)search.IsEnabled=false;
            if(previous!=null)previous.IsEnabled=false;
            if(next!=null)next.IsEnabled=false;
            status.Text="Projekt ellenőrzése az ASA CurseForge-katalógusában...";
            try
            {
                var mod=await catalog.GetAsaModAsync(
                    SecureStore.Unprotect(protectedKey),id);
                string identifier=mod.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _trustedModNames[identifier]=UntamedPresentation.Display(mod.Name);
                if(mod.ThumbnailUrl is not null)_trustedModArt[identifier]=mod.ThumbnailUrl;
                _catalogMetadataUpdated?.Invoke();
                last=null;lastTerm="";
                results.ItemsSource=new[]{mod};
                results.SelectedItem=mod;
                paging.Text="Pontos projekt ID  "+id+"  ·  ASA-katalógusban ellenőrizve";
                status.Text="Ellenőrzött CurseForge-metaadat. A mod telepítése, betöltése, "+
                    "crossplay- és prémiumstátusza továbbra sincs bizonyítva.";
            }
            catch(Exception ex)
            {
                WriteLog("curseforge-exact-lookup",ex);
                last=null;
                results.ItemsSource=Array.Empty<CurseForgeMod>();
                paging.Text="Nincs igazolt ASA-projekt ehhez az azonosítóhoz.";
                status.Text="Projektlekérdezés sikertelen: "+
                    UntamedPresentation.Redact(ex.Message);
            }
            finally
            {
                busy=false;
                if(search!=null)search.IsEnabled=true;
                if(previous!=null)previous.IsEnabled=false;
                if(next!=null)next.IsEnabled=false;
            }
        }
        _catalogLookupById=id=>{
            projectId.Text=id;
            _=LookupExact(id);
        };
        var lookup=Btn("Pontos projekt lekérése",async ()=>await LookupExact(projectId.Text));
        search=Btn("Modkeresés (csak olvasás)",async ()=>{
            last=null;await LoadPage(0,query.Text);
        });
        previous=Btn("Előző oldal",async ()=>{
            if(last!=null && last.Index>=25)await LoadPage(last.Index-25,lastTerm);
        });
        next=Btn("Következő oldal",async ()=>{
            if(last?.HasMore==true)await LoadPage(last.Index+25,lastTerm);
        });
        previous.IsEnabled=false;
        next.IsEnabled=false;
        // A short UI-thread debounce avoids one HTTP request per keypress;
        // Enter and explicit Search still work without waiting for the timer.
        var searchDelay=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(450)};
        searchDelay.Tick+=async (_,_)=>{
            searchDelay.Stop();
            if(query.Text.Trim().Length>=2 && !busy)
                await LoadPage(0,query.Text.Trim());
        };
        query.TextChanged+=(_,_)=>{
            searchDelay.Stop();
            if(query.Text.Trim().Length>=2)searchDelay.Start();
        };
        query.KeyDown+=async (_,e)=>{
            if(e.Key==System.Windows.Input.Key.Enter)
            {
                e.Handled=true;
                searchDelay.Stop();
                await LoadPage(0,query.Text.Trim());
            }
        };
        query.Unloaded+=(_,_)=>searchDelay.Stop();
        var addFromCatalog=Btn("Kiválasztott mod hozzáadása a kijelölt maphoz",()=>{
            if(results.SelectedItem is not CurseForgeMod chosen)
            { status.Text="Előbb válassz egy találatot.";return; }
            if(_catalogSelectForMod==null)
            { status.Text="Nincs kiválasztott helyi map.";return; }
            _catalogSelectForMod(chosen.Id.ToString());
        });
        addFromCatalog.Visibility=_catalogSelectForMod==null?Visibility.Collapsed:Visibility.Visible;
        var searchActions=new WrapPanel{Margin=new Thickness(0,8,0,8)};
        query.Width=360;
        query.Margin=new Thickness(0,0,12,8);
        searchActions.Children.Add(query);
        searchActions.Children.Add(search!);
        var exactActions=new WrapPanel{Margin=new Thickness(0,0,0,8)};
        projectId.Margin=new Thickness(0,0,12,8);
        exactActions.Children.Add(projectId);
        exactActions.Children.Add(lookup);
        var browser=new Grid{Margin=new Thickness(0,4,0,12)};
        browser.ColumnDefinitions.Add(new ColumnDefinition{
            Width=new GridLength(11,GridUnitType.Star),MinWidth=270});
        browser.ColumnDefinitions.Add(new ColumnDefinition{
            Width=new GridLength(9,GridUnitType.Star),MinWidth=240});
        results.MinHeight=300;
        results.MaxHeight=470;
        results.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        var discovery=Box(V(
            Label("CURSEFORGE  /  FELFEDEZÉS",16,Mint,true),
            Label("Valódi találatok · projekt ID és modkép · lapozható katalógus",12,Soft),
            results,paging,previous!,next!));
        var information=Box(V(
            Label("MOD DOSSIER  /  RÉSZLETEK",16,Mint,true),
            Label("A kiválasztott CurseForge-mod hitelesen lekért metaadatai",12,Soft),
            detail,addFromCatalog));
        Grid.SetColumn(discovery,0);
        Grid.SetColumn(information,1);
        browser.Children.Add(discovery);
        browser.Children.Add(information);
        var apiConfiguration=new Expander{
            Header="CurseForge API-kulcs · helyi, DPAPI-val védett integráció",
            Foreground=Mint,Padding=new Thickness(12),
            IsExpanded=string.IsNullOrWhiteSpace(_cluster.Config.CurseForgeApiKeyProtected),
            Margin=new Thickness(0,5,0,10),
            Content=V(
            Label("API-kulcs (Windows DPAPI-val helyileg védve, nem kerül a GitHubra)",12,Aqua),
            key,
            Btn("API-kulcs mentése helyileg",()=>{
                try
                {
                    if(string.IsNullOrWhiteSpace(key.Password) || key.Password.Length>512)
                        throw new InvalidDataException("Érvénytelen kulcs");
                    var protectedValue=SecureStore.Protect(key.Password);
                    _cluster.UpdateSettings(c=>c.CurseForgeApiKeyProtected=protectedValue);
                    key.Clear();
                    last=null;
                    previous.IsEnabled=false;
                    next.IsEnabled=false;
                    status.Text="Kulcs titkosítva elmentve a helyi Windows-profilban.";
                }
                catch(Exception e){status.Text="Kulcs nem menthető: "+e.GetType().Name;}
            }),
            Btn("API-kulcs törlése",()=>{
                try
                {
                    _cluster.UpdateSettings(c=>c.CurseForgeApiKeyProtected="");
                    key.Clear();
                    last=null;
                    previous.IsEnabled=false;
                    next.IsEnabled=false;
                    status.Text="Helyi CurseForge API-kulcs törölve.";
                }
                catch(Exception ex)
                {
                    WriteLog("curseforge-key-remove",ex);
                    status.Text="A kulcs nem törölhető a profilból: "+
                        UntamedPresentation.Redact(ex.Message);
                }
            }),
                Label("A kulcs kizárólag ezen a Windows-profilon tárolódik.",11,Soft))
        };
        Add(Box(V(
            Label("OBSIDIAN MOD GALAXY  /  CURSEFORGE",21,Mint,true),
            Label("Integrált ASA-modkatalógus, külön böngészés és modrészletek. "+
                  "A találatokból történő hozzáadás piszkozatot módosít, nem telepít fájlt.",12,Soft),
            Label("Mod keresése név alapján",12,Aqua),searchActions,
            Label("Pontos CurseForge projekt ID",12,Aqua),exactActions,
            browser,status,apiConfiguration)));

    }
}
