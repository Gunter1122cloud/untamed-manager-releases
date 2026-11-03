using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using WinMessageBox=System.Windows.MessageBox;

namespace UntamedManager;
public partial class MainWindow
{
    private void ShowAsaApiAndPluginStates()
    {
        var mapList=new ComboBox{ItemsSource=_cluster.Config.LocalMaps,
            DisplayMemberPath="Name",SelectedIndex=0,Background=Panel,Foreground=Brushes.White};
        var plugin=new ComboBox{IsEditable=true,Height=34,Background=Panel,
            Foreground=Brushes.White};
        var status=Label("Válassz helyi mapot. PE64 fájlok és pluginállapotok csak lemezről igazolhatók.",12,Soft);
        LocalMap Selected()
        {
            if(mapList.SelectedItem is not LocalMap m ||
               !_cluster.Config.LocalMaps.Contains(m))
                throw new UnauthorizedAccessException("Csak regisztrált helyi map");
            return m;
        }
        void Inspect()
        {
            try
            {
                var m=Selected();
                var api=AsaApiInspector.Inspect(m.InstallationPath,_cluster.Check(m));
                var plugins=PluginStateManager.Inventory(m);
                plugin.ItemsSource=plugins.Select(x=>x.Name).ToArray();
                status.Text="API "+(api.Complete?"FÁJLOK TELJESEK":"HIÁNYOS")+
                    " · "+api.LoaderRuntimeState+"\n"+
                    string.Join("\n",api.Binaries.Select(x=>x.RelativePath+
                        ": "+(x.ValidPe64?"PE64 OK":"HIÁNYZIK/HIBÁS")+
                        " · "+(x.Version??"verzió ismeretlen")))+
                    "\nConfig JSON: "+(api.ConfigValid?"OK":"HIBA")+
                    " · Plugins mappa: "+(api.PluginsDirectoryPresent?"igen":"nem")+
                    "\nPlugin könyvtárak (runtime betöltés külön ellenőrizendő):\n"+
                    string.Join("\n",plugins.Select(x=>x.Name+
                        (x.Enabled?" ACTIVE":" DISABLED")))+
                    "\nAPI frissítés éles szerverhez még nem hitelesített.";
            }
            catch(Exception ex){status.Text="API/Plugin leltár sikertelen: "+ex.Message;}
        }
        Add(Box(V(Label("v1.5 · ASA API és pluginállapot",20,Mint,true),
            Label("PE64 loader/DLL-ellenőrzés, config validálás, lemezen lévő pluginok. "+
                "Kikapcsoláskor teljes pluginmappa kerül külön gyökérbe; SHA-256 "+
                "védőmásolat szükséges. Betöltött futásidejű modul nem következtethető a lemezről.",12,Soft),
            mapList,
            Btn("Helyi ASA API / pluginleltár újraellenőrzése",Inspect),
            Btn("Hivatalos ASA API legújabb verzió ellenőrzése (READ-ONLY)",async ()=>{
                try
                {
                    status.Text="Hivatalos ArkServerApi GitHub-verzió lekérdezése...";
                    var release=await AsaApiReleaseCatalog.FetchLatestAsync(_http);
                    status.Text="ASA API hivatalos upstream: "+release.Tag+
                        "\nKiadás (UTC): "+(release.PublishedUtc?.ToString("O")??"ismeretlen")+
                        "\nFájlok: "+string.Join(", ",release.Assets.Select(x=>x.Name+
                            " ("+(x.OfficialSha256==null?"nincs kiadói SHA-256":"kiadói SHA-256 ismert")+")"))+
                        "\n"+release.HtmlUrl+"\n"+release.Statement+
                        "\nAPI telepítés és valódi module-load változatlanul külön validációhoz kötött.";
                }
                catch(Exception ex)
                {
                    status.Text="ASA API verzió lekérdezése sikertelen: "+ex.Message;
                    WriteLog("asa-api-release",ex);
                }
            }),
            Label("Plugin mappaneve",12,Aqua),plugin,
            Btn("Kijelölt plugin KIKAPCSOLÁSA – csak igazoltan leállított map",()=>{
                Toggle(false);
            }),
            Btn("Kijelölt plugin BEKAPCSOLÁSA – csak igazoltan leállított map",()=>{
                Toggle(true);
            }),status)));
        void Toggle(bool enabling)
        {
            try
            {
                var map=Selected();
                var name=plugin.Text?.Trim()??"";
                if(WinMessageBox.Show(map.Name+" / "+name+"\n"+
                    (enabling?"Plugin engedélyezése":"Plugin letiltása")+
                    " – teljes pluginmappa és tartós adatok áthelyezése, "+
                    "előtte SHA-256 védőmásolat. A map nem indul újra.\nFolytatod?",
                    "Plugin állapotváltás",
                    MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                var result=PluginStateManager.Toggle(map,_cluster.Check(map),name,
                    enabling,_cluster.Store.Root);
                status.Text="Plugin tranzakció kész: "+result.Plugin+
                    (result.Enabled?" ENABLED":" DISABLED")+
                    "\nVédőmásolat: "+result.BackupPackage+
                    "\nKövetkező indításhoz restart szükséges (automatikus restart NINCS).";
                Inspect();
            }
            catch(Exception ex){status.Text="Plugin állapotváltás megtagadva: "+ex.Message;
                WriteLog("plugin-toggle",ex);}
        }
    }
}
