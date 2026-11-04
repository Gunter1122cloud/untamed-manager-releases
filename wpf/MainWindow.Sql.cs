using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using Forms=System.Windows.Forms;

namespace UntamedManager;
public partial class MainWindow
{
    private void ShowSqlBackupControls()
    {
        TextBox Field(string text="")=>new(){Text=text,Height=32,Foreground=Brushes.White,Background=Panel};
        var tools=Field();var data=Field();var port=Field();var user=Field();
        var password=new PasswordBox{Height=32,Foreground=Brushes.White,Background=Panel};
        var folder=Field(Path.Combine(_cluster.Store.Root,"sql-backups"));
        var status=Label("Válassz saját MariaDB 10.11.19 klienseszközöket és külön SQL-fiókot a három adatbázishoz.",12,Soft);
        var panel=new StackPanel();CancellationTokenSource? pending=null;
        void Pick(TextBox field,string title)
        {using var dialog=new Forms.FolderBrowserDialog{Description=title};if(dialog.ShowDialog()==Forms.DialogResult.OK)field.Text=dialog.SelectedPath;}
        (MariaDbDumpAdapter Adapter,MariaDbEndpoint Endpoint,Guid Owner) Settings()
        {
            if(!Guid.TryParse(_cluster.Config.LocalHostId,out var owner)||owner==Guid.Empty)
                throw new InvalidOperationException("Érvényes helyi Manager-azonosító szükséges");
            if(!int.TryParse(port.Text.Trim(),out int number))throw new InvalidOperationException("Add meg a helyi SQL-portot");
            var endpoint=new MariaDbEndpoint(number,data.Text.Trim(),user.Text.Trim(),password.Password);
            var adapter=new MariaDbDumpAdapter(Path.Combine(tools.Text.Trim(),"mariadb.exe"),
                Path.Combine(tools.Text.Trim(),"mariadb-dump.exe"),Path.Combine(_cluster.Store.Root,"private-sql"));
            password.Clear();return(adapter,endpoint,owner);
        }
        async Task Execute(Func<CancellationToken,Task> action)
        {
            if(pending!=null)return;
            pending=new CancellationTokenSource();panel.IsEnabled=false;
            try{await action(pending.Token);}
            catch(OperationCanceledException){status.Text="SQL művelet megszakadt. Részleges visszatöltésnél operátori ellenőrzés szükséges.";}
            catch(Exception ex){status.Text="SQL művelet sikertelen: "+UntamedPresentation.Redact(ex.Message);WriteLog("sql-operation",ex);}
            finally{panel.IsEnabled=true;pending.Dispose();pending=null;}
        }
        panel.Children.Add(V(
            Label("Háromadatbázisos SQL-mentés és visszatöltés",19,Mint,true),
            Label("arkshop · arkpermissions · lethalquests. Visszatöltés csak olyan célra, ahol egyik adatbázis sem létezik. A meglévő adatbázisokat nem írja felül.",12,Soft),
            Label("MariaDB bin mappa – mariadb.exe és mariadb-dump.exe",12,Aqua),tools,
            Btn("SQL-eszközmappa",()=>Pick(tools,"MariaDB 10.11.19 bin mappája")),
            Label("SQL-szerver adatkönyvtára – az azonosság ellenőrzéséhez",12,Aqua),data,
            Btn("SQL-adatkönyvtár kijelölése",()=>Pick(data,"A választott helyi SQL-példány adatkönyvtára")),
            Label("Helyi TCP-port (127.0.0.1)",12,Aqua),port,
            Label("Külön SQL-felhasználó",12,Aqua),user,
            Label("SQL-jelszó – ideiglenes, Windows-jogosultsággal védett kliensfájlban",12,Aqua),password,
            Label("SQL-mentések helyi célmappája",12,Aqua),folder,
            Btn("SQL-mentési célmappa",()=>Pick(folder,"Külön helyi SQL-mentési célmappa")),
            Btn("Valódi SQL-dump készítése",async ()=>await Execute(async ct=>{
                var settings=Settings();string target=Path.Combine(folder.Text.Trim(),DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
                status.Text="SQL-dump készül; verzió, jogosultság, séma és SHA-256 ellenőrzés...";
                var result=await Task.Run(()=>settings.Adapter.DumpAsync(settings.Endpoint,target,settings.Owner,ct),ct);
                status.Text="SQL-mentés elkészült: "+result.Manifest.Bytes+" bájt\n"+result.Package+
                    "\nA világ/karakter/Store közös konzisztenciája külön ellenőrzést igényel.";
            })),
            Btn("SQL-csomag épségének ellenőrzése",async ()=>await Execute(async ct=>{
                using var dialog=new Forms.FolderBrowserDialog{Description="Korábban létrehozott SQL-csomag"};
                if(dialog.ShowDialog()!=Forms.DialogResult.OK)return;
                if(!Guid.TryParse(_cluster.Config.LocalHostId,out var owner))throw new InvalidOperationException("Helyi azonosító hiányzik");
                var m=await Task.Run(()=>MariaDbDumpAdapter.Verify(dialog.SelectedPath,owner),ct);
                status.Text="SQL-csomag ép: "+m.Bytes+" bájt · MariaDB "+m.Version+"\nSHA-256: "+m.Sha256;
            })),
            Btn("SQL visszatöltése új, üres célra",async ()=>await Execute(async ct=>{
                using var dialog=new Forms.FolderBrowserDialog{Description="Ellenőrzött SQL-mentéscsomag"};
                if(dialog.ShowDialog()!=Forms.DialogResult.OK)return;
                string selected=dialog.SelectedPath;
                if(!Guid.TryParse(_cluster.Config.LocalHostId,out var owner))throw new InvalidOperationException("Helyi azonosító hiányzik");
                var m=await Task.Run(()=>MariaDbDumpAdapter.Verify(selected,owner),ct);
                if(System.Windows.MessageBox.Show("Három adatbázis létrehozása a kiválasztott üres célon.\n"+
                    "Cél: 127.0.0.1:"+port.Text+"\nAdatkönyvtár: "+data.Text+"\nCsomag: "+selected+
                    "\nMéret: "+m.Bytes+" bájt\n\nIndulhat a visszatöltés?","SQL-visszatöltés",MessageBoxButton.YesNo,
                    MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                var settings=Settings();status.Text="SQL-visszatöltés és sémaellenőrzés folyamatban...";
                await Task.Run(()=>settings.Adapter.RestoreToEmptyAsync(settings.Endpoint,selected,settings.Owner,ct),ct);
                status.Text="A három SQL-adatbázis visszatöltve; séma és csomagintegritás ellenőrizve.";
            }))));
        Add(Box(V(panel,Btn("Futó SQL-művelet megszakítása",()=>pending?.Cancel()),status)));
    }
}
