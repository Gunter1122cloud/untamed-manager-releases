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
    private void ShowCurseForgeSearchControls()
    {
        var key=new PasswordBox { Height=36,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua };
        var query=new TextBox { Height=38,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua,Text="Shiny" };
        var results=new ListBox { Height=260,Foreground=Brushes.White,Background=Panel,
            BorderBrush=Aqua };
        var detail=Label("Válassz modot a találati listából a részletekhez.",12,Soft);
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
        Add(Box(V(
            Label("CurseForge online modkereső – csak olvasás",20,Mint,true),
            Label("Hivatalos CurseForge Core API; saját jóváhagyott API-kulcs szükséges. "+
                "Nem tölt le vagy telepít modot, és nem indít újra szervert.",12,Soft),
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
                _cluster.UpdateSettings(c=>c.CurseForgeApiKeyProtected="");
                key.Clear();
                last=null;
                previous.IsEnabled=false;
                next.IsEnabled=false;
                status.Text="Helyi CurseForge API-kulcs törölve.";
            }),
            Label("Modkeresés név alapján",12,Aqua),
            query,search,previous,next,paging,results,detail,status)));
    }
}
