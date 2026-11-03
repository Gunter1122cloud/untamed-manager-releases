using System;
using System.IO;
using System.Linq;
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
        var status=Label(
            string.IsNullOrEmpty(_cluster.Config.CurseForgeApiKeyProtected)
                ?"A CurseForge API-kulcs nincs beállítva. Kizárólag jóváhagyott saját kulccsal kereshetsz."
                :"Helyileg védett CurseForge API-kulcs beállítva. A keresés csak olvas.",
            12,Soft);
        var search=Btn("Keresés a CurseForge katalógusában (csak olvasás)",async ()=>{
            string protectedKey=_cluster.Config.CurseForgeApiKeyProtected;
            if(string.IsNullOrWhiteSpace(protectedKey))
            {
                status.Text="Előbb ments egy jóváhagyott API-kulcsot.";
                return;
            }
            try
            {
                status.Text="CurseForge katalógus lekérdezése...";
                results.ItemsSource=Array.Empty<string>();
                var catalogue=new CurseForgeCatalog(_http);
                var items=await catalogue.SearchAsaAsync(SecureStore.Unprotect(protectedKey),query.Text);
                results.ItemsSource=items.Mods.Select(mod=>
                    $"{mod.Name} (ID {mod.Id}) · {mod.LatestFile??"fájlverzió ismeretlen"}"+
                    (mod.ModifiedUtc.HasValue?$" · {mod.ModifiedUtc.Value:yyyy-MM-dd}":"")+
                    (mod.Website==null?"":" · "+mod.Website)).ToArray();
                status.Text=$"{items.Mods.Count} eredmény (összesen {items.TotalCount}); "+
                    "telepítés, crossplay és fizetős besorolás nincs ellenőrizve.";
            }
            catch(Exception e)
            {
                status.Text="CurseForge keresési hiba: "+e.Message;
                // Never include credentials or HTTP headers in any logged message.
                WriteLog("curseforge-search",e);
            }
        });
        Add(Box(V(
            Label("CurseForge online modkereső – csak olvasás",20,Mint,true),
            Label("Hivatalos CurseForge Core API; jóváhagyott kulcs szükséges. "+
                  "Modot nem tölt le, nem telepít és nem indít újra szervert.",12,Soft),
            Label("API-kulcs (Windows DPAPI-val védve, nem kerül a GitHubra)",12,Aqua),
            key,
            Btn("API-kulcs mentése helyileg",()=>{
                try
                {
                    if(string.IsNullOrWhiteSpace(key.Password) || key.Password.Length>512)
                        throw new InvalidDataException("Érvénytelen kulcs");
                    _cluster.UpdateSettings(c=>
                        c.CurseForgeApiKeyProtected=SecureStore.Protect(key.Password));
                    key.Clear();
                    status.Text="API-kulcs titkosítva elmentve a helyi Windows-profilban.";
                }
                catch(Exception e){status.Text="Kulcs nem menthető: "+e.GetType().Name;}
            }),
            Btn("API-kulcs törlése",()=>{
                _cluster.UpdateSettings(c=>c.CurseForgeApiKeyProtected="");
                key.Clear();
                status.Text="Helyi CurseForge API-kulcs beállítás törölve.";
            }),
            Label("Modkeresés név alapján",12,Aqua),query,search,results,status)));
    }
}
