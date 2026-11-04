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
    private void ShowManagerExtensions()
    {
        Header("Aláírt Manager-panelek","Külön jóváhagyott, adatleíró panelek helyi mutatókkal és korlátozott navigációval.");
        var package=new TextBox{Height=32,Background=Panel,Foreground=Brushes.White};
        var key=new TextBox{Height=32,Background=Panel,Foreground=Brushes.White};
        var status=Label("Válassz aláírt panelcsomagot és külön, megbízható forrásból kapott nyilvános kiadói kulcsot.",12,Soft);
        var panels=new StackPanel();VerifiedManagerPanel? preview=null;
        SignedManagerPanelRegistry Registry()=>Guid.TryParse(_cluster.Config.LocalHostId,out Guid host)?
            new(_cluster.Store.Root,host):throw new InvalidOperationException("Helyi Manager-azonosító szükséges.");
        void Pick(TextBox input,string filter)
        {using var dialog=new System.Windows.Forms.OpenFileDialog{Filter=filter,CheckFileExists=true};
            if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK){input.Text=dialog.FileName;preview=null;}}
        ManagerPanelSnapshot Snapshot()
        {
            int? running=null;long? memory=null;var observed=DateTimeOffset.MinValue;
            try
            {
                var sample=new SupervisorChannel(_cluster.Store.Root).Read(_cluster.Config.LocalHostId);
                var ids=_cluster.Config.LocalMaps.Select(x=>x.Id).ToHashSet();
                var owned=sample.Maps.Where(x=>ids.Contains(x.Map)).ToArray();observed=sample.ObservedUtc;
                if(owned.Length==ids.Count&&owned.All(x=>x.Running.HasValue&&
                    DateTimeOffset.UtcNow-x.ObservedUtc<=TimeSpan.FromSeconds(30)&&x.ObservedUtc<=DateTimeOffset.UtcNow.AddSeconds(5)))
                {
                    running=owned.Count(x=>x.Running==true);
                    if(owned.All(x=>x.MemoryBytes.HasValue&&x.MemoryBytes>=0))memory=owned.Sum(x=>x.MemoryBytes!.Value);
                }
            }
            catch{ /* Missing/stale Supervisor data is unknown, never a zero runtime sample. */ }
            return new(observed,_cluster.Config.LocalHostName,_cluster.Config.LocalMaps.Count,running,memory);
        }
        void Refresh()
        {
            panels.Children.Clear();
            try
            {
                var registry=Registry();var snapshot=Snapshot();var loaded=registry.Load();
                if(loaded.Count==0)panels.Children.Add(Label("Még nincs külön jóváhagyott panel.",12,Soft));
                foreach(var item in loaded)
                {
                    var contents=new StackPanel();
                    if(item.Panel is not VerifiedManagerPanel panel)contents.Children.Add(Label("UM-PANEL-REJECTED · A panel ellenőrzése sikertelen.",12,Soft));
                    else
                    {
                        contents.Children.Add(Label(panel.Title+" · "+panel.Version,18,Mint,true));
                        foreach(var line in panel.Build(snapshot))
                        {
                            if(line.Kind=="navigate")
                            {string destination=line.Value;contents.Children.Add(Btn(line.Label,()=>Navigate(destination)));}
                            else contents.Children.Add(Label(line.Label+(line.Value.Length>0?": "+line.Value:""),13,Brushes.White));
                        }
                    }
                    Guid id=item.Id;
                    contents.Children.Add(Btn("Panel jóváhagyásának visszavonása",()=>{
                        try{registry.Revoke(id);Refresh();status.Text="A jóváhagyás visszavonva. A panelcsomag megőrzött állomány maradt.";}
                        catch(Exception e){WriteLog("panel-revoke",e);status.Text="UM-PANEL-REVOKE: a módosítás nem igazolt.";}
                    }));
                    panels.Children.Add(Box(contents));
                }
            }
            catch(Exception e){WriteLog("panel-render",e);panels.Children.Add(Label("UM-PANEL-LOAD · A jóváhagyott panelek nem ellenőrizhetők.",12,Soft));}
        }
        Add(Box(V(Label("Panelcsomag és kiadói kulcs",18,Mint,true),
            Label("A csomag aláírása, kiadói kulcsának ujjlenyomata és pontos tartalmi hash-e a helyi jóváhagyáshoz kötött. A panelek szöveget, négy helyi mutatót és engedélyezett oldallinkeket jeleníthetnek meg.",12,Soft),
            package,Btn("Panelcsomag kiválasztása",()=>Pick(package,"Aláírt panel|*.json")),
            key,Btn("Külön nyilvános kiadói kulcs kiválasztása",()=>Pick(key,"Nyilvános PEM-kulcs|*.pem|Minden fájl|*.*")),
            Btn("Aláírás, tartalom és jogosultságok ellenőrzése",()=>{
                try
                {
                    preview=SignedManagerPanelRegistry.Inspect(package.Text.Trim(),key.Text.Trim());
                    status.Text=preview.Title+" · "+preview.Version+"\nKiadói kulcs SHA-256: "+preview.PublisherFingerprint+
                        "\nPaneltartalom SHA-256: "+preview.PayloadSha256+"\nEllenőrizd a kulcs ujjlenyomatát a kiadó külön megbízható csatornáján.";
                }
                catch(Exception e){preview=null;WriteLog("panel-inspect",e);status.Text="UM-PANEL-VERIFY: az aláírás, formátum vagy kért jogosultság nem elfogadható.";}
            }),
            Btn("Ellenőrzött panel kifejezett jóváhagyása és telepítése",()=>{
                if(preview==null){status.Text="Előbb ellenőrizd a panel aláírását és kiadói kulcsát.";return;}
                if(System.Windows.MessageBox.Show("Panel: "+preview.Title+"\nKiadói kulcs SHA-256:\n"+
                    preview.PublisherFingerprint+"\n\nMegbízhatónak jelölöd ezt a pontos panelt és kiadót?",
                    "Manager-panel jóváhagyása",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                try{Registry().Install(package.Text.Trim(),key.Text.Trim(),preview.PublisherFingerprint,preview.PayloadSha256);
                    preview=null;status.Text="A panel aláírva és külön engedélyezve települt.";Refresh();}
                catch(Exception e){WriteLog("panel-install",e);status.Text="UM-PANEL-INSTALL: a csomag vagy a kiadó megváltozott, illetve a telepítés nem igazolt.";}
            }),Btn("Jóváhagyott panelek és friss helyi adatok ellenőrzése",Refresh),status)));
        Add(panels);Refresh();
    }
}
