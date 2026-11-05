using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private string? _curseForgeImportNotice;

    private void ShowCurseForgeSearchControls()
    {
        var key=new PasswordBox { Height=36,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua };
        var query=new TextBox { Height=38,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua,Text="Shiny" };
        CancellationTokenSource requests=new();
        var gallery=new WrapPanel{Orientation=Orientation.Horizontal};
        var dependencyMap=new ComboBox{MinHeight=36,Width=280,
            DisplayMemberPath="Name",Foreground=Brushes.White,Background=Panel};
        var dependencyStatus=Label(
            "Függőségek: csak ellenőrzés, nincs telepítés vagy modsorrend-változtatás.",12,Soft);
        dependencyMap.ItemsSource=_cluster.Config.LocalMaps.ToArray();
        dependencyMap.SelectedIndex=dependencyMap.Items.Count>0?0:-1;
        var categories=new ComboBox{MinHeight=35,Width=280,DisplayMemberPath="Name",
            SelectedValuePath="Id",Foreground=Brushes.White,Background=Panel};
        categories.ItemsSource=new[]{new CurseForgeCategory(0,"Összes kategória",0,false,0)};
        categories.SelectedIndex=0;
        var sortOptions=new ComboBox{MinHeight=35,Width=280,Foreground=Brushes.White,
            Background=Panel,SelectedIndex=0};
        sortOptions.ItemsSource=new[]{
            "Alapértelmezett","Népszerűség","Legutóbb frissített","Letöltésszám",
            "Név","Szerző","Kiemelt"
        };
        var categoryInfo=Label("Kategóriák: még nem töltöttük be.",12,Soft);
        var featuredInfo=Label("Kiemelt, népszerű és frissen frissített modok: még nem ellenőrizve.",12,Soft);
        var projectId=new TextBox {Height=38,Width=180,Tag="CurseForge projekt ID",
            ToolTip="Pontosan az ASA-hoz tartozó CurseForge modazonosító (nem szabad szöveg)"};
        var results=new ListBox { MaxHeight=330,MinHeight=120,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua,
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("CurseForgeSearchRow") };
        NestedWheelScroll.SetChainToParent(results,true);
        var detail=Label("Válassz modot a találati listából a részletekhez.",12,Soft);
        var paging=Label("Nincs lekérdezés.",12,Soft);
        var localAssignments=Label(
            "Helyi map-hozzárendelések: válassz CurseForge projektet.",12,Soft);
        var fullDescription=Label("A projekt részletes leírása külön, csak olvasható API-kéréssel tölthető be.",12,Soft);
        var filesStatus=Label("Válassz egy ASA modot, majd kérd le a hozzá tartozó fájlverziókat.",12,Soft);
        var fileDetail=Label("Nincs kiválasztott fájl.",12,Soft);
        var fileList=new ListBox {
            MinHeight=100,MaxHeight=235,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua,DisplayMemberPath="Name"
        };
        NestedWheelScroll.SetChainToParent(fileList,true);
        CurseForgeFilesResult? loadedFiles=null;
        int filesProject=0;
        Button? filesPrevious=null,filesNext=null;
        var status=Label(
            _curseForgeImportNotice ??
            (string.IsNullOrEmpty(_cluster.Config.CurseForgeApiKeyProtected)
                ?"Nincs helyi CurseForge token. A kapott érték kézzel vagy egyszeri helyi importtal is beállítható."
                :"CurseForge token helyileg titkosítva eltárolva. A hitelesítést a kapcsolatteszt igazolja."),
            12,Soft);
        var catalog=new CurseForgeCatalog(_http);
        CurseForgeSearchResult? last=null;
        string lastTerm="";
        bool busy=false;
        Button? search=null,previous=null,next=null;
        void RefreshLocalAssignments()
        {
            if(results.SelectedItem is not CurseForgeMod chosen)
            {
                localAssignments.Text="Válassz projektet a helyi mapok modállapotának megtekintéséhez.";
                return;
            }
            string id=chosen.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var localMaps=_cluster.Config.LocalMaps.ToArray();
            var assignment=localMaps.Select(map=>{
                try
                {
                    var mod=ModStateController.Read(map);
                    string mode=mod.Active.Contains(id)?"ACTIVE":
                        mod.Passive.Contains(id)?"PASSIVE":
                        mod.Disabled.Contains(id)?"DISABLED":"nincs konfigurálva";
                    return map.Name+" · "+mode+(mod.Pending?" · újraindításra váró változás":"");
                }
                catch(Exception)
                {
                    return map.Name+" · modállapot nem ellenőrizhető";
                }
            });
            localAssignments.Text="HELYI MAP HOZZÁRENDELÉS – KONFIGURÁCIÓ, NEM FUTÓ MODLELTÁR"+
                "\n"+(localMaps.Length==0?"Nincs helyi map.":string.Join("\n",assignment))+
                "\nTávoli map konfigurációját nem módosítjuk.";
        }
        results.SelectionChanged+=(_,_)=>{
            loadedFiles=null;
            filesProject=0;
            fileList.ItemsSource=Array.Empty<CurseForgeFile>();
            fileDetail.Text="Nincs kiválasztott fájl.";
            fullDescription.Text="Részletes leírás: még nincs lekérve.";
            filesStatus.Text="A fájlok csak a kiválasztott projekthez kérdezhetők le.";
            if(filesPrevious!=null)filesPrevious.IsEnabled=false;
            if(filesNext!=null)filesNext.IsEnabled=false;
            if(results.SelectedItem is not CurseForgeMod mod)
            {
                detail.Text="Válassz modot a találati listából a részletekhez.";
                return;
            }
            string summary=mod.Summary;
            if(summary.Length>500)summary=summary[..500]+"…";
            RefreshLocalAssignments();
            gallery.Children.Clear();
            foreach(string trustedUrl in mod.Screenshots.Take(12))
            {
                try
                {
                    var picture=new BitmapImage();
                    picture.BeginInit();
                    picture.UriSource=new Uri(trustedUrl,UriKind.Absolute);
                    picture.CacheOption=BitmapCacheOption.OnDemand;
                    picture.CreateOptions=BitmapCreateOptions.DelayCreation;
                    picture.EndInit();
                    gallery.Children.Add(new Image{
                        Width=120,Height=80,Margin=new Thickness(4),
                        Stretch=Stretch.UniformToFill,Source=picture,
                        ToolTip="CurseForge galériakép – ellenőrzött HTTPS-forrás"
                    });
                }
                catch(Exception) { /* Missing optional provider artwork */ }
            }
            detail.Text=mod.Name+" · ID "+mod.Id+"\n"+
                (string.IsNullOrWhiteSpace(summary)?"Leírás nem elérhető.":summary)+"\n"+
                "Szerzők: "+(mod.Authors.Count==0?"nem közölt":
                    string.Join(", ",mod.Authors))+"\n"+
                "Kategóriák: "+(mod.Categories.Count==0?"nem közölt":
                    string.Join(", ",mod.Categories))+"\n"+
                "Utolsó ismert fájl: "+(mod.LatestFile??"ismeretlen")+"\n"+
                "Legutóbbi módosítás: "+(mod.ModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd")??"ismeretlen")+"\n"+
                "Forrás: "+(mod.Website??"nincs hiteles CurseForge-link")+
                "\nLetöltések: "+mod.DownloadCount.ToString("N0")+
                "\nSzolgáltatói terjesztés jelző: "+
                    (mod.AllowModDistribution==true?"engedélyezett jelző – külön feltételek is szükségesek":
                     mod.AllowModDistribution==false?"nem engedélyezett":"nincs adat")+
                "\nProjekt elérhetősége: "+(mod.IsAvailable==true?"igen":
                    mod.IsAvailable==false?"nem":"nincs adat")+
                "\nGalériaképek: "+mod.Screenshots.Count+
                "\nCrossplay és prémium státusz: nincs ellenőrizve.";
        };
        fileList.SelectionChanged+=(_,_)=>{
            if(fileList.SelectedItem is not CurseForgeFile file)
            {
                fileDetail.Text="Nincs kiválasztott fájl.";return;
            }
            var relations=file.Relations.Take(35).Select(item=>
                item.Type+" · projekt "+item.ModId);
            fileDetail.Text="Fájl ID: "+file.FileId+
                "\nKözzétéve: "+(file.Published?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")??"ismeretlen")+
                "\nCurseForge elérhetőség: "+(file.Available?"igen":"nem igazolt")+
                "\nMéret: "+(file.SizeBytes>0?(file.SizeBytes/1024d/1024d).ToString("F1")+" MiB":"nem közölt")+
                "\nJátékverziók: "+(file.GameVersions.Count==0?"nem közölt":
                    string.Join(", ",file.GameVersions))+
                "\nFüggőségek / ütközések: "+(file.Relations.Count==0?"nincs közölt kapcsolat":
                    "\n"+string.Join("\n",relations))+
                "\nMetaadat-ellenőrzés NEM jelent telepítést vagy licencjogosultságot.";
        };
        async Task LoadFilesPage(int modId,int index)
        {
            if(busy)return;
            string protectedKey=_cluster.Config.CurseForgeApiKeyProtected;
            if(string.IsNullOrWhiteSpace(protectedKey))
            {filesStatus.Text="CurseForge-token szükséges a fájlverziók lekéréséhez.";return;}
            busy=true;
            if(filesPrevious!=null)filesPrevious.IsEnabled=false;
            if(filesNext!=null)filesNext.IsEnabled=false;
            filesStatus.Text="Hivatalos CurseForge fájlverziók lekérése...";
            try
            {
                var page=await catalog.GetModFilesAsync(
                    SecureStore.Unprotect(protectedKey),modId,index,requests.Token);
                // Discard stale HTTP results if a different project was selected.
                if(results.SelectedItem is not CurseForgeMod chosen || chosen.Id!=modId)
                {filesStatus.Text="A kijelölt projekt időközben megváltozott.";return;}
                filesProject=modId;
                loadedFiles=page;
                fileList.ItemsSource=page.Files.ToArray();
                filesStatus.Text="Projekt "+modId+" · fájloldal "+(index/25+1)+
                    " · ezen az oldalon "+page.Files.Count+
                    " · összesen "+page.Total+
                    ". Fájlok és kapcsolatok: csak olvasás.";
            }
            catch(Exception ex)
            {
                WriteLog("curseforge-file-metadata",ex);
                loadedFiles=null;
                fileList.ItemsSource=Array.Empty<CurseForgeFile>();
                filesStatus.Text="CurseForge fájlverziók nem ellenőrizhetők: "+
                    UntamedPresentation.Redact(ex.Message);
            }
            finally
            {
                busy=false;
                if(filesPrevious!=null)filesPrevious.IsEnabled=loadedFiles?.Index>=25;
                if(filesNext!=null)filesNext.IsEnabled=loadedFiles?.HasMore==true;
            }
        }
        async Task LoadFullDescription()
        {
            if(busy || results.SelectedItem is not CurseForgeMod selected)return;
            int requestedId=selected.Id;
            string protectedKey=_cluster.Config.CurseForgeApiKeyProtected;
            if(string.IsNullOrWhiteSpace(protectedKey))
            {fullDescription.Text="A CurseForge-token nincs beállítva.";return;}
            busy=true;
            fullDescription.Text="CurseForge teljes leírás betöltése – szkript nélküli szövegként...";
            try
            {
                string content=await catalog.GetModDescriptionTextAsync(
                    SecureStore.Unprotect(protectedKey),requestedId,requests.Token);
                if(results.SelectedItem is CurseForgeMod current && current.Id==requestedId)
                    fullDescription.Text=string.IsNullOrWhiteSpace(content)?
                        "A szolgáltató nem közölt részletes leírást.":content;
            }
            catch(Exception ex)
            {
                WriteLog("curseforge-description",ex);
                fullDescription.Text="A részletes leírás nem ellenőrizhető: "+
                    UntamedPresentation.Redact(ex.Message);
            }
            finally{busy=false;}
        }
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
                int categoryId=categories.SelectedItem is CurseForgeCategory selected?
                    selected.Id:0;
                CurseForgeSearchSortField order=sortOptions.SelectedIndex switch{
                    1=>CurseForgeSearchSortField.Popularity,
                    2=>CurseForgeSearchSortField.LastUpdated,
                    3=>CurseForgeSearchSortField.TotalDownloads,
                    4=>CurseForgeSearchSortField.Name,
                    5=>CurseForgeSearchSortField.Author,
                    6=>CurseForgeSearchSortField.Featured,
                    _=>CurseForgeSearchSortField.ProviderDefault
                };
                var page=await catalog.SearchAsaAsync(
                    SecureStore.Unprotect(protectedKey),
                    new CurseForgeSearchOptions(term,CategoryId:categoryId,
                        Sort:order,Descending:true),index,requests.Token);
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
                status.Text="CurseForge keresési hiba: "+e.Message;
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
                    SecureStore.Unprotect(protectedKey),id,requests.Token);
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
        var categoriesRefresh=Btn("ASA modkategóriák betöltése",async ()=>{
            if(busy)return;
            string protectedKey=_cluster.Config.CurseForgeApiKeyProtected;
            if(string.IsNullOrWhiteSpace(protectedKey))
            {categoryInfo.Text="CurseForge API-kulcs szükséges.";return;}
            busy=true;
            categoryInfo.Text="CurseForge ASA-kategóriák lekérése...";
            try
            {
                var groups=await catalog.GetAsaCategoriesAsync(
                    SecureStore.Unprotect(protectedKey),requests.Token);
                categories.ItemsSource=new[]{
                    new CurseForgeCategory(0,"Összes kategória",0,false,0)
                }.Concat(groups).ToArray();
                categories.SelectedIndex=0;
                categoryInfo.Text="Hivatalos ASA-kategóriák betöltve: "+groups.Count;
            }
            catch(Exception ex)
            {
                WriteLog("curseforge-categories",ex);
                categoryInfo.Text="A kategóriák nem tölthetők be: "+
                    UntamedPresentation.Redact(ex.Message);
            }
            finally{busy=false;}
        });
        var featuredBrowse=Btn("Kiemelt / népszerű / friss ASA modok",async ()=>{
            if(busy)return;
            string protectedKey=_cluster.Config.CurseForgeApiKeyProtected;
            if(string.IsNullOrWhiteSpace(protectedKey))
            {featuredInfo.Text="CurseForge API-kulcs szükséges.";return;}
            busy=true;
            featuredInfo.Text="Hivatalos CurseForge kiemeltlisták lekérése...";
            try
            {
                var featured=await catalog.GetAsaFeaturedAsync(
                    SecureStore.Unprotect(protectedKey),requests.Token);
                var combined=featured.Featured.Concat(featured.Popular)
                    .Concat(featured.RecentlyUpdated)
                    .GroupBy(x=>x.Id).Select(x=>x.First()).ToArray();
                foreach(var mod in combined)
                {
                    string id=mod.Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    _trustedModNames[id]=UntamedPresentation.Display(mod.Name);
                    if(mod.ThumbnailUrl is not null)_trustedModArt[id]=mod.ThumbnailUrl;
                }
                _catalogMetadataUpdated?.Invoke();
                results.ItemsSource=combined;
                last=null;
                previous.IsEnabled=false;
                next.IsEnabled=false;
                featuredInfo.Text="Kiemelt: "+featured.Featured.Count+
                    " · népszerű: "+featured.Popular.Count+
                    " · frissen frissített: "+featured.RecentlyUpdated.Count+
                    " · egyedi ASA-projektek: "+combined.Length;
                paging.Text="CurseForge /v1/mods/featured – szolgáltatói metaadat";
            }
            catch(Exception ex)
            {
                WriteLog("curseforge-featured",ex);
                featuredInfo.Text="Kiemelt modok nem ellenőrizhetők: "+
                    UntamedPresentation.Redact(ex.Message);
            }
            finally{busy=false;}
        });
        var dependencyInspect=Btn("Függőségi lánc ellenőrzése a kiválasztott mapon",async ()=>{
            if(busy)return;
            if(results.SelectedItem is not CurseForgeMod selectedMod ||
               dependencyMap.SelectedItem is not LocalMap selectedMap)
            {dependencyStatus.Text="Válassz CurseForge-modot és helyi mapot.";return;}
            var protectedKey=_cluster.Config.CurseForgeApiKeyProtected;
            if(string.IsNullOrWhiteSpace(protectedKey))
            {dependencyStatus.Text="Nincs CurseForge-token.";return;}
            busy=true;
            dependencyStatus.Text="Kötelező függőségek és inkompatibilitások ellenőrzése...";
            try
            {
                var current=ModStateController.Read(selectedMap);
                var installed=current.Active.Concat(current.Passive).ToArray();
                var resolver=new CurseForgeDependencyResolver(catalog);
                var report=await resolver.ResolveAsync(
                    SecureStore.Unprotect(protectedKey),
                    selectedMod.Id,installed,requests.Token);
                if(report.Preview==null)
                {dependencyStatus.Text="NEM ELLENŐRIZHETŐ: "+report.Reason;return;}
                var p=report.Preview;
                var missingLocal=p.DependencyFirstOrder.Where(id=>
                    id!=selectedMod.Id&&!installed.Contains(id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))).ToArray();
                dependencyStatus.Text=selectedMap.Name+" · "+selectedMod.Name+
                    "\nAPI-oldalak: "+report.HttpPagesRead+
                    "\nFüggőségi sorrend (csak tájékoztatás): "+
                        string.Join(" → ",p.DependencyFirstOrder)+
                    "\nHiányzik a helyi modkonfigurációból: "+
                        (missingLocal.Length==0?"nincs":string.Join(", ",missingLocal))+
                    "\nOpcionális: "+string.Join(", ",p.OptionalMods)+
                    "\nInkompatibilis helyi projektek: "+
                        string.Join(", ",p.IncompatibleEnabled)+
                    "\nIsmeretlen kapcsolatok / ciklusok: "+
                        string.Join(", ",p.Unclassified.Concat(p.Cyclic))+
                    "\nAPI-metaadatok teljesek: "+
                        (report.FullMetadataRetrieved?"IGEN":"NEM")+
                    "\nNincs automatikus telepítés vagy mod sorrendmódosítás.";
            }
            catch(OperationCanceledException)
            {dependencyStatus.Text="Függőség-ellenőrzés megszakítva.";}
            catch(Exception ex)
            {
                WriteLog("curseforge-dependency-review",ex);
                dependencyStatus.Text="Függőségek nem ellenőrizhetők: "+
                    UntamedPresentation.Redact(ex.Message);
            }
            finally{busy=false;}
        });
        var refreshMaps=Btn("Helyi maplista frissítése",()=>{
            dependencyMap.ItemsSource=_cluster.Config.LocalMaps.ToArray();
            dependencyMap.SelectedIndex=dependencyMap.Items.Count>0?0:-1;
        });
        var cancelApi=Btn("Futó CurseForge-kérés megszakítása",()=>{
            requests.Cancel();
            requests.Dispose();
            requests=new CancellationTokenSource();
            status.Text="Az aktuális API-lekérdezés megszakítását kértük.";
        });
        var openProject=Btn("Kiválasztott mod CurseForge-oldalának megnyitása",()=>{
            if(results.SelectedItem is not CurseForgeMod chosen ||
               string.IsNullOrWhiteSpace(chosen.Website))
            {status.Text="Nincs hivatalosan ellenőrzött CurseForge-projektlink.";return;}
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    chosen.Website){UseShellExecute=true});
            }
            catch(Exception ex)
            {
                WriteLog("curseforge-open-official-project",ex);
                status.Text="A hivatalos projektoldal nem nyitható meg.";
            }
        });
        var addFromCatalog=Btn("Kiválasztott mod hozzáadása a kijelölt maphoz",()=>{
            if(results.SelectedItem is not CurseForgeMod chosen)
            { status.Text="Előbb válassz egy találatot.";return; }
            if(_catalogSelectForMod==null)
            { status.Text="Nincs kiválasztott helyi map.";return; }
            _catalogSelectForMod(chosen.Id.ToString());
        });
        addFromCatalog.Visibility=_catalogSelectForMod==null?Visibility.Collapsed:Visibility.Visible;
        var sharedModReport=Label(
            "Közös helyi modok: még nem olvastuk be a Manager-konfigurációkat.",12,Soft);
        var inspectSharedModUsage=Btn("Közös és eltérő modok a helyi mapokon",()=>{
            try
            {
                var report=LocalModInventory.Read(_cluster.Config.LocalMaps);
                var shared=report.SharedProjects.OrderByDescending(p=>p.EnabledMapCount)
                    .ThenBy(p=>p.ProjectId,StringComparer.Ordinal)
                    .Take(60).Select(p=>p.ProjectId+" · "+p.EnabledMapCount+
                        " map: "+string.Join(", ",
                            p.Assignments.Where(x=>x.Mode!=ModMode.Disabled)
                                .Select(x=>x.MapName).Distinct(StringComparer.Ordinal)));
                sharedModReport.Text="HELYI MODLELTÁR – csak konfiguráció, nem futó modverzió"+
                    "\nMapok: "+report.MapCount+" · projektek: "+report.Projects.Count+
                    " · közös modok: "+report.SharedProjects.Count+
                    "\n"+(report.SharedProjects.Count==0?
                        "Nincs több helyi mapon engedélyezett közös projekt.":
                        string.Join("\n",shared))+
                    (report.UnreadableMaps.Count==0?"":"\nHibás modkonfigurációjú mapok: "+
                        string.Join(", ",report.UnreadableMaps))+
                    "\nA sorrend, -mods/-passivemods és minden mentés változatlan.";
            }
            catch(Exception ex)
            {
                WriteLog("curseforge-shared-mod-inventory",ex);
                sharedModReport.Text="A helyi modleltár nem készíthető el: "+
                    UntamedPresentation.Redact(ex.Message);
            }
        });
        var refreshLocalAssignment=Btn("Maponkénti modállapot újraolvasása",
            RefreshLocalAssignments);
        var inspectFiles=Btn("Kijelölt mod fájlverziói és függőségei",async ()=>{
            if(results.SelectedItem is not CurseForgeMod selected)
            {filesStatus.Text="Előbb válassz ki egy modot.";return;}
            await LoadFilesPage(selected.Id,0);
        });
        var inspectDescription=Btn("Teljes modleírás lekérése",async ()=>await LoadFullDescription());
        filesPrevious=Btn("Előző fájloldal",async ()=>{
            if(loadedFiles!=null && loadedFiles.Index>=25)
                await LoadFilesPage(filesProject,loadedFiles.Index-25);
        });
        filesNext=Btn("Következő fájloldal",async ()=>{
            if(loadedFiles?.HasMore==true)
                await LoadFilesPage(filesProject,loadedFiles.Index+25);
        });
        filesPrevious.IsEnabled=false;
        filesNext.IsEnabled=false;
        Add(Box(V(
            Label("CurseForge online modkereső – csak olvasás",20,Mint,true),
            Label("Hivatalos CurseForge Core API; saját jóváhagyott API-kulcs szükséges. "+
                "Nem tölt le vagy telepít modot, és nem indít újra szervert.",12,Soft),
            Label("API-kulcs (Windows DPAPI-val helyileg védve, nem kerül a GitHubra)",12,Aqua),
            key,
            Btn("CurseForge API-kapcsolat ellenőrzése",async ()=>{
                if(busy)return;
                string candidate=key.Password;
                if(string.IsNullOrWhiteSpace(candidate))
                {
                    var stored=_cluster.Config.CurseForgeApiKeyProtected;
                    if(string.IsNullOrWhiteSpace(stored))
                    {
                        status.Text="Előbb állítsd be a kapott CurseForge tokent.";
                        return;
                    }
                    try { candidate=SecureStore.Unprotect(stored); }
                    catch
                    {
                        status.Text="A korábban elmentett kulcs nem olvasható ebben a Windows-profilban.";
                        return;
                    }
                }
                busy=true;
                status.Text="Hivatalos CurseForge ASA-katalógus ellenőrzése...";
                try
                {
                    int gameId=await catalog.VerifyAsaKeyAsync(candidate,requests.Token);
                    status.Text="CurseForge kapcsolat sikeres. ASA katalógus ID: "+
                        gameId+". Csak metaadatot kértünk le, modot nem telepítettünk.";
                }
                catch(ArgumentException)
                {
                    status.Text="A token hiányzik, túl hosszú vagy teljes JSON-dokumentum. "+
                        "A kapott tokenértéket önmagában add meg.";
                }
                catch(UnauthorizedAccessException)
                {
                    status.Text="CurseForge 401/403: ezt a tokent az API nem fogadta el. A mentett tokent nem módosítottuk.";
                }
                catch(Exception)
                {
                    status.Text="A CurseForge API ellenőrzése nem sikerült. "+
                        "Ellenőrizd az internetkapcsolatot és a jóváhagyott hozzáférést.";
                }
                finally { busy=false; }
            }),
            Btn("API-kulcs mentése helyileg",()=>{
                try
                {
                    CurseForgeCatalog.ValidateApiKey(key.Password);
                    var protectedValue=SecureStore.Protect(key.Password);
                    _cluster.UpdateSettings(c=>c.CurseForgeApiKeyProtected=protectedValue);
                    key.Clear();
                    last=null;
                    previous.IsEnabled=false;
                    next.IsEnabled=false;
                    status.Text="A kulcs helyileg titkosítva tárolva. "+
                        "Érvényességét az API-kapcsolat ellenőrzésével igazolhatod.";
                }
                catch(ArgumentException)
                {
                    status.Text="Nem menthető: csak a kapott tokent add meg, ne teljes JSON-t.";
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
            Label("Modkeresés név / szerző alapján",12,Aqua),
            query,
            Label("ASA kategória",12,Aqua),categoriesRefresh,categories,categoryInfo,
            Label("Hivatalos CurseForge rendezés",12,Aqua),sortOptions,
            search,featuredBrowse,featuredInfo,
            Label("Pontos ASA CurseForge projektazonosító",12,Aqua),
            projectId,lookup,
            previous,next,paging,results,
            openProject,addFromCatalog,
            detail,Label("CURSEFORGE GALÉRIA",12,Aqua,true),gallery,
            refreshLocalAssignment,localAssignments,
            inspectSharedModUsage,sharedModReport,
            inspectDescription,fullDescription,
            Label("FÁJLVERZIÓK / KAPCSOLATOK (HIVATALOS API)",12,Aqua,true),
            inspectFiles,filesPrevious,filesNext,filesStatus,fileList,fileDetail,
            Label("MAPONKÉNTI FÜGGŐSÉG-ELLENŐRZÉS",12,Aqua,true),
            refreshMaps,dependencyMap,dependencyInspect,dependencyStatus,
            cancelApi,status)));
    }
}
