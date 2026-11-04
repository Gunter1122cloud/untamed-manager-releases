using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;
public partial class MainWindow
{
    private void ShowModUpdateCenter()
    {
        var maps=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,DisplayMemberPath="Name",
            SelectedIndex=0,Background=Panel,Foreground=Brushes.White};
        var project=new TextBox{Text="928548",Height=34,Background=Panel,
            Foreground=Brushes.White};
        var status=Label("CurseForge-fájlverziók még nem lettek lekérdezve.",12,Soft);
        Add(Box(V(Label("v"+UiVersion+" · Telepített modok és CurseForge fájlfrissítések",19,Mint,true),
            Label("Hivatalos fájlazonosítók, dátumok és függőségek. A helyi "+
                "modkönyvtár jelenléte NEM bizonyítja a telepített fájlverziót, "+
                "crossplay-t vagy vásárlási jogosultságot.",12,Soft),
            maps,Label("CurseForge projektazonosító",12,Aqua),project,
            Btn("Helyi modkönyvtár ellenőrzése (csak olvasás)",()=>{
                try
                {
                    if(maps.SelectedItem is not LocalMap map ||
                       !_cluster.Config.LocalMaps.Contains(map))
                        throw new UnauthorizedAccessException("Csak helyi map");
                    var local=ModUpdatePlanner.InspectCandidate(map.InstallationPath,project.Text.Trim());
                    status.Text=local.ModId+" · "+
                        (local.DirectoryPresent?"Lehetséges telepítési mappa":"Mappa nem található")+
                        "\n"+local.Source+"\n"+local.Note;
                }
                catch(Exception ex){status.Text="Modleltár hiba: "+ex.Message;}
            }),
            Btn("Hivatalos CurseForge fájlok és frissítési előnézet",async ()=>{
                try
                {
                    if(maps.SelectedItem is not LocalMap map ||
                       !_cluster.Config.LocalMaps.Contains(map))
                        throw new UnauthorizedAccessException("Csak helyi map");
                    if(!int.TryParse(project.Text.Trim(),out var id) || id<10000)
                        throw new InvalidDataException("Érvénytelen mod ID");
                    string encrypted=_cluster.Config.CurseForgeApiKeyProtected;
                    if(string.IsNullOrWhiteSpace(encrypted))
                        throw new InvalidOperationException("Előbb add meg az engedélyezett CurseForge API-kulcsot");
                    var files=await new CurseForgeCatalog(_http).GetModFilesAsync(
                        SecureStore.Unprotect(encrypted),id);
                    var local=ModUpdatePlanner.InspectCandidate(map.InstallationPath,id.ToString());
                    var state=ModUpdatePlanner.Assess(local,files);
                    var latest=files.Files.Where(x=>x.Available)
                        .OrderByDescending(x=>x.Published??DateTimeOffset.MinValue)
                        .ThenByDescending(x=>x.FileId).FirstOrDefault();
                    var localModes=ModStateController.Read(map);
                    var review=latest==null?null:ModDependencyPlanner.Review(id,
                        new[]{latest},localModes.Active.Concat(localModes.Passive));
                    string RelationName(CurseForgeRelationType type)=>type switch{
                        CurseForgeRelationType.EmbeddedLibrary=>"beágyazott könyvtár",
                        CurseForgeRelationType.RequiredDependency=>"kötelező",
                        CurseForgeRelationType.OptionalDependency=>"opcionális",
                        CurseForgeRelationType.Incompatible=>"ütköző",
                        CurseForgeRelationType.Tool=>"segédeszköz",
                        CurseForgeRelationType.Include=>"csomagolt tartalom",
                        _=>"ismeretlen – ellenőrzendő"
                    };
                    status.Text="MOD "+id+" · "+state.State+"\n"+state.Reason+
                        "\nFájlok az első oldalon: "+files.Files.Count+
                        " / "+files.Total+"\n"+
                        string.Join("\n",files.Files.Take(8).Select(x=>"#"+x.FileId+" "+x.Name+
                            " · "+(x.Published?.ToString("yyyy-MM-dd")??"dátum ismeretlen")+
                            " · kapcsolatok: "+string.Join(", ",x.Relations.Select(r=>
                                r.ModId+" ("+RelationName(r.Type)+")"))))+
                        (review==null?"\nNincs elérhető fájlmetaadat a függőségi előnézethez.":
                            "\nKötelező függőségek ellenőrizetlen fájljai: "+
                            (review.MissingRequired.Count==0?"nincs":string.Join(",",review.MissingRequired))+
                            "\nOpcionális kiegészítők: "+
                            (review.OptionalMods.Count==0?"nincs":string.Join(",",review.OptionalMods))+
                            "\nEltérés/ütközés: "+
                            (review.IncompatibleEnabled.Count==0?"nincs":string.Join(",",review.IncompatibleEnabled)))+
                        "\nFüggőségi előnézet ≠ telepítési jogosultság; nincs automatikus letöltés vagy restart.";
                    // File IDs and verified observation time only: no API secrets.
                    var location=Path.Combine(_cluster.Store.Root,"mod-update-checks.jsonl");
                    File.AppendAllText(location,
                        System.Text.Json.JsonSerializer.Serialize(new{
                            map=map.Id,modId=id,utc=DateTimeOffset.UtcNow,
                            state=state.State.ToString(),latestFileId=state.LatestFileId})+"\n");
                }
                catch(Exception ex){
                    WriteLog("mod-file-preview",ex);
                    status.Text="UM-CURSEFORGE-META: nem sikerült ellenőrizni a modfájlokat.";
                }
            }),
            Btn("Teljes kötelező modfüggőségi lánc ellenőrzése (csak API)",async ()=>{
                if(maps.SelectedItem is not LocalMap localMap ||
                   !_cluster.Config.LocalMaps.Contains(localMap))
                {status.Text="Válassz helyi mapot.";return;}
                if(!int.TryParse(project.Text.Trim(),out int selectedId) || selectedId<10000)
                {status.Text="Érvénytelen projektazonosító.";return;}
                string keyEncrypted=_cluster.Config.CurseForgeApiKeyProtected;
                if(string.IsNullOrWhiteSpace(keyEncrypted))
                {status.Text="Előbb állíts be jóváhagyott CurseForge API-kulcsot.";return;}
                status.Text="CurseForge API: kötelező fájlok és kapcsolatok bejárása...";
                try
                {
                    var local=ModStateController.Read(localMap);
                    var result=await new CurseForgeDependencyResolver(
                        new CurseForgeCatalog(_http)).ResolveAsync(
                            SecureStore.Unprotect(keyEncrypted),selectedId,
                            local.Active.Concat(local.Passive));
                    status.Text="CurseForge függőségi ellenőrzés: "+
                        (result.FullMetadataRetrieved?"TELJES METAADAT":"HIÁNYOS / BLOKKOLVA")+
                        "\nLekérdezett API-oldalak: "+result.HttpPagesRead+
                        "\nKötelező betöltési sorrend: "+
                        (result.Preview==null?"nem igazolható":
                            string.Join(" → ",result.Preview.DependencyFirstOrder))+
                        "\nHiányzó kötelező projektek: "+
                        (result.Preview==null?"ismeretlen":
                            string.Join(", ",result.Preview.MissingRequired))+
                        "\nOpcionális modok: "+
                        (result.Preview==null?"ismeretlen":
                            string.Join(", ",result.Preview.OptionalMods))+
                        "\n"+result.Reason+
                        "\nAutomatikus telepítés/éles restart: NINCS.";
                }
                catch(Exception ex)
                {
                    WriteLog("mod-dependency-chain",ex);
                    status.Text="UM-CURSEFORGE-CHAIN: a függőségi lánc nem igazolható. "+
                        "Nem történt módosítás.";
                }
            }),status)));
    }

    private void ShowRconActionPreviews()
    {
        var mapBox=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,
            DisplayMemberPath="Name",SelectedIndex=0,Background=Panel,Foreground=Brushes.White};
        var actions=new ComboBox{ItemsSource=new[]{
            RconAction.ListPlayers,RconAction.Broadcast,RconAction.ServerChat,
            RconAction.SaveWorld,RconAction.WildDinoWipe},
            SelectedIndex=0,Background=Panel,Foreground=Brushes.White};
        var message=new TextBox{Text="",Height=34,Background=Panel,Foreground=Brushes.White};
        var status=Label("A módosító RCON-parancsok éles küldése továbbra is tiltott.",12,Soft);
        RconDraft Draft()
        {
            if(mapBox.SelectedItem is not LocalMap map ||
               !_cluster.Config.LocalMaps.Contains(map))
                throw new UnauthorizedAccessException("Helyi map szükséges");
            var state=_cluster.Check(map);
            return RconCommandPolicy.Preview(map.Id,(RconAction)actions.SelectedItem,
                message.Text,true,map.Maintenance,state.Running);
        }
        Add(Box(V(Label("v1.5 · RCON Adminpanel – ellenőrzött előnézet",19,Mint,true),
            Label("ListPlayers, Broadcast, ServerChat, SaveWorld, Wild Dino Wipe. "+
                "A tesztek a valódi parancsszöveget állítják elő, de csak FakeRcon-adapterrel "+
                "küldik el. SaveWorld válasza NEM mentésigazolás.",12,Soft),
            mapBox,actions,message,
            Btn("Parancs előnézete",()=>{
                try
                {
                    var draft=Draft();
                    status.Text="MAP: "+draft.Map+"\nPARANCS: "+draft.Command+
                        "\nÉles küldés: "+(draft.LiveAllowed?"Csak read-only ListPlayers":"TILTVA")+
                        "\n"+draft.Warning;
                }
                catch(Exception ex){status.Text="Előnézet tiltva: "+ex.Message;}
            }),
            Btn("FakeRcon küldés tesztelése – NEM ÉLES",async ()=>{
                try
                {
                    var draft=Draft();
                    if(draft.Destructive &&
                       System.Windows.MessageBox.Show(
                           "Ez a parancs éles ASA-n minden vad dínót törölne!\n"+
                           "A teszt viszont semmilyen valódi szervert nem ér el. Folytatod?",
                           "Csak FakeRcon",MessageBoxButton.YesNo,
                           MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                    var receipt=await new FakeRconQueue(_cluster.Store.Root).ExecuteOnceAsync(
                        Guid.NewGuid(),draft,(cmd,ct)=>Task.FromResult("FAKE "+cmd));
                    status.Text=receipt.Status+" · "+receipt.Kind+
                        "\n"+draft.Command+"\nMűvelet: "+receipt.Operation+
                        "\nHálózati RCON-kapcsolat nem történt.";
                }
                catch(Exception ex){status.Text="FakeRcon művelet megtagadva: "+ex.Message;}
            }),status)));
    }
}
