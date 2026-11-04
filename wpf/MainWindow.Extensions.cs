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
    private void VerifySignedPanelsCiSmoke()
    {
        if(!App.IsSmokeTest||Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true")return;
        string fixture=Path.Combine(Path.GetTempPath(),"Untamed-PanelUi-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        using var signer=System.Security.Cryptography.RSA.Create(2048);
        var model=new ManagerPanelDefinition(1,Guid.NewGuid(),"1.0.0","CI aláírt panel",new[]{
            new ManagerPanelItem("metric","Helyi mapok","local.maps"),
            new ManagerPanelItem("navigate","CI: mentések","backups"),
            new ManagerPanelItem("text","Literális szöveg","<ObjectDataProvider />")});
        byte[] payload=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(model);
        byte[] signature=signer.SignData(payload,System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pss);
        string source=Path.Combine(fixture,"panel.json"),key=Path.Combine(fixture,"publisher.pem");
        File.WriteAllBytes(source,System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ManagerPanelPackage(
            Convert.ToBase64String(payload),Convert.ToBase64String(signature))));
        File.WriteAllText(key,signer.ExportSubjectPublicKeyInfoPem());
        var proof=SignedManagerPanelRegistry.Inspect(source,key);
        var registry=new SignedManagerPanelRegistry(_cluster.Store.Root,Guid.Parse(_cluster.Config.LocalHostId));
        registry.Install(source,key,proof.PublisherFingerprint,proof.PayloadSha256);
        static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
        {
            if(root is T item)yield return item;
            foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach(var nested in Descendants<T>(child))yield return nested;
        }
        try
        {
            Navigate("extensions");UpdateLayout();
            if(!Descendants<TextBlock>(PageBody).Any(x=>x.Text.Contains("CI aláírt panel"))||
               !Descendants<TextBlock>(PageBody).Any(x=>x.Text.Contains("<ObjectDataProvider />")))
                throw new InvalidOperationException("Signed panel WPF text/metric rendering failed");
            var navigation=Descendants<Button>(PageBody).Single(x=>Equals(x.Content,"CI: mentések"));
            navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(_page!="backups")throw new InvalidOperationException("Signed panel allowlisted navigation did not execute");
            File.WriteAllText(Path.Combine(_cluster.Store.Root,"ui-panels",proof.PayloadSha256+".panel.json"),"corrupted CI artifact");
            Navigate("extensions");UpdateLayout();
            if(!Descendants<TextBlock>(PageBody).Any(x=>x.Text.Contains("UM-PANEL-REJECTED")))
                throw new InvalidOperationException("Faulty panel must render isolated rejection state");
            Navigate("servers");UpdateLayout();
            if(_page!="servers"||string.IsNullOrWhiteSpace(PageTitle.Text))
                throw new InvalidOperationException("Panel failure blocked native Manager navigation");
        }
        finally{registry.Revoke(model.Id);}
    }

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
