using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer=new(){Interval=TimeSpan.FromHours(6)};
    private string _automaticUpdateStatus="Automatikus ellenőrzés még nem futott.";
    private bool _checkingUpdates;
    private void InitializeAutomaticUpdates()
    {
        _updateTimer.Tick+=async(_,_)=>await AutomaticUpdateCheck();
        Loaded+=async(_,_)=>{if(!App.IsSmokeTest){_updateTimer.Start();await AutomaticUpdateCheck();}};
        Closed+=(_,_)=>_updateTimer.Stop();
    }
    private async Task AutomaticUpdateCheck()
    {
        if(_checkingUpdates)return;
        _checkingUpdates=true;
        try
        {
            var release=await UpdateCenter.CheckAsync(_cluster.Config.GitHubRepository,_cluster.Config.UpdateChannel);
            _automaticUpdateStatus=release!=null && ReleaseCatalog.IsNewer(release,UpdateCenter.RunningVersion,ReleaseCatalog.InstalledTag)?
                "Elérhető frissítés: "+release.Tag:"Nincs újabb kiadás a kiválasztott csatornán.";
        }
        catch(Exception){_automaticUpdateStatus="A GitHub-ellenőrzés nem elérhető; a helyi felügyelet tovább működik.";}
        finally{_checkingUpdates=false;}
    }

    private void ShowUpdateCenter()
    {
        Header("Licenc / Frissítések","A telepített Windows-verziót ellenőrzi a konfigurált GitHub Releases stabil kiadásaival szemben.");
        var repository=new TextBox{Text=_cluster.Config.GitHubRepository,Margin=new Thickness(0,0,0,10)};
        var publisher=new TextBox{Text=_cluster.Config.PublisherCertificateThumbprint,
            Margin=new Thickness(0,0,0,10)};
        var channel=new ComboBox{ItemsSource=new[]{UpdateChannel.Stable,UpdateChannel.Preview},SelectedItem=_cluster.Config.UpdateChannel};
        var status=Label(_automaticUpdateStatus,13,Soft);
        var details=Label("",13,Brushes.White);
        var progressText=Label("",12,Soft);
        var bar=new ProgressBar{Minimum=0,Maximum=100,Height=20,Margin=new Thickness(0,8,0,10),
            Visibility=Visibility.Collapsed};
        var install=Btn("Frissítés telepítése",()=>{});
        install.Visibility=Visibility.Collapsed;
        UpdateRelease? candidate=null;

        Add(Box(V(Label($"Jelenleg telepítve: {UpdateCenter.RunningVersion.ToString(3)}",22,Mint,true),
            Label("Frissítési csatorna",12,Aqua),channel,
            Label("GitHub repository (tulajdonos/tárhely)",13,Soft),repository,
            Label("Opcionális kiadói Authenticode tanúsítvány SHA1 ujjlenyomat",13,Soft),
            publisher,
            Btn("Frissítési beállítások mentése",()=>{
                if(!UpdateCenter.ValidRepository(repository.Text.Trim()))
                {Notice("Érvénytelen owner/repository.");return;}
                _cluster.UpdateSettings(c=>{
                    c.GitHubRepository=repository.Text.Trim();
                    c.UpdateChannel=(UpdateChannel)channel.SelectedItem;
                    c.PublisherCertificateThumbprint=publisher.Text.Trim();
                });
                Notice("Beállítások mentve. A profilok és szervermentések érintetlenek.");
            }))));
        Add(Box(V(Label("GitHub frissítések ellenőrzése",19,Mint,true),
            Label("Hiteles forrás: api.github.com + az engedélyezett repository, HTTPS, a GitHub asset SHA-256 digestje.",12,Soft),
            Btn("Frissítések keresése",async()=>{
                try{
                    status.Text="GitHub-kiadás keresése…";install.Visibility=Visibility.Collapsed;
                    if(!UpdateCenter.ValidRepository(repository.Text.Trim()))
                        throw new InvalidDataException("Érvénytelen owner/repository.");
                    candidate=null;
                    candidate=await UpdateCenter.CheckAsync(repository.Text.Trim(),(UpdateChannel)channel.SelectedItem);
                    if(candidate==null) {
                        status.Text="Nincs megfelelő kiadás a kiválasztott csatornán.";
                        details.Text="";return;
                    }
                    if(!ReleaseCatalog.IsNewer(candidate,UpdateCenter.RunningVersion,ReleaseCatalog.InstalledTag)){
                        status.Text="Az alkalmazás naprakész.";details.Text="";return;
                    }
                    status.Text="Elérhető kiadás: "+candidate.Version;
                    details.Text=candidate.ChangeLog;
                    install.Visibility=Visibility.Visible;
                } catch(Exception ex){status.Text="Frissítési ellenőrzési hiba: "+ex.Message;WriteLog("updates-check",ex);}
            }),status,details,install,bar,progressText)));
        Add(Box(V(Label("Frissítési előzmények",18,Mint,true),Label(UpdateHistory.ReadRecent(),11,Soft))));
        // Keep the install control real and functional, never show it until update is verified as newer.
        install.Click+=async(_,_)=>{
            if(candidate==null)return;
            install.IsEnabled=false;
            bar.Visibility=Visibility.Visible;
            progressText.Text="Letöltés és SHA-256 ellenőrzés…";
            try{
                var progress=new Progress<(long received,long? total)>(p=>{
                    if(p.total.HasValue&&p.total.Value>0)bar.Value=Math.Min(95,100.0*p.received/p.total.Value);
                    progressText.Text=$"Letöltés: {p.received/1048576.0:F1} MiB"+
                        (p.total.HasValue?$" / {p.total.Value/1048576.0:F1} MiB":"");
                });
                UpdateHistory.Append("download-start",candidate.Tag);
                string package=await UpdateCenter.DownloadAndVerifyAsync(candidate,
                    UpdateCenter.UpdateRoot,progress);
                bar.Value=98;progressText.Text="SHA-256 ellenőrizve. Kiadói hitelesség ellenőrzése…";
                UpdateCenter.CheckAuthenticodePublisher(package,_cluster.Config.PublisherCertificateThumbprint);
                var exe=Environment.ProcessPath ?? throw new InvalidOperationException("Nincs alkalmazás EXE");
                var updater=UpdateCenter.StageUpdater(Path.Combine(Path.GetDirectoryName(exe)!,"UntamedUpdater.exe"));
                var backup=Path.Combine(UpdateCenter.UpdateRoot,"rollback-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                Directory.CreateDirectory(backup);
                var psi=new ProcessStartInfo(updater){UseShellExecute=false,
                    WorkingDirectory=Path.GetDirectoryName(updater)!};
                foreach(var arg in new[]{
                    "--pid",Environment.ProcessId.ToString(),
                    "--installer",package,
                    "--app",exe,
                    "--expected",candidate.Version.ToString(3),
                    "--tag",candidate.Tag,
                    "--profile",App.ProfileName,
                    "--backup",backup})
                    psi.ArgumentList.Add(arg);
                if(Process.Start(psi)==null)throw new InvalidOperationException("Updater indítása sikertelen");
                progressText.Text="Külső updater indult; a játék szerverfolyamatai futva maradnak.";
                // Closing the Manager NEVER terminates a launched ASA process.
                Exit();
            }
            catch(Exception ex){
                UpdateHistory.Append("installation-error",candidate.Tag);
                status.Text="Frissítés megszakítva: "+ex.Message;
                WriteLog("updates-install",ex);install.IsEnabled=true;
            }
        };
    }
}
