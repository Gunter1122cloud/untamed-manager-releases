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
    private string? _curseForgeImportNotice;

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
            detail.Text=mod.Name+" · ID "+mod.Id+"\n"+
                (string.IsNullOrWhiteSpace(summary)?"Leírás nem elérhető.":summary)+"\n"+
                "Szerzők: "+(mod.Authors.Count==0?"nem közölt":
                    string.Join(", ",mod.Authors))+"\n"+
                "Kategóriák: "+(mod.Categories.Count==0?"nem közölt":
                    string.Join(", ",mod.Categories))+"\n"+
                "Utolsó ismert fájl: "+(mod.LatestFile??"ismeretlen")+"\n"+
                "Legutóbbi módosítás: "+(mod.ModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd")??"ismeretlen")+"\n"+
                "Forrás: "+(mod.Website??"nincs hiteles CurseForge-link")+
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
                    SecureStore.Unprotect(protectedKey),modId,index);
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
                    SecureStore.Unprotect(protectedKey),requestedId);
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
        var addFromCatalog=Btn("Kiválasztott mod hozzáadása a kijelölt maphoz",()=>{
            if(results.SelectedItem is not CurseForgeMod chosen)
            { status.Text="Előbb válassz egy találatot.";return; }
            if(_catalogSelectForMod==null)
            { status.Text="Nincs kiválasztott helyi map.";return; }
            _catalogSelectForMod(chosen.Id.ToString());
        });
        addFromCatalog.Visibility=_catalogSelectForMod==null?Visibility.Collapsed:Visibility.Visible;
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
                    int gameId=await catalog.VerifyAsaKeyAsync(candidate);
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
            Label("Modkeresés név alapján",12,Aqua),
            query,search,
            Label("Pontos ASA CurseForge projektazonosító",12,Aqua),
            projectId,lookup,
            previous,next,paging,results,
            addFromCatalog,
            detail,refreshLocalAssignment,localAssignments,
            inspectDescription,fullDescription,
            Label("FÁJLVERZIÓK / KAPCSOLATOK (HIVATALOS API)",12,Aqua,true),
            inspectFiles,filesPrevious,filesNext,filesStatus,fileList,fileDetail,
            status)));
    }
}
