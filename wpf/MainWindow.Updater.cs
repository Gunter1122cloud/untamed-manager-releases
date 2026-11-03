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
    private void ShowUpdateCenter()
    {
        Header("Licenc / Frissítések","A telepített Windows-verziót ellenőrzi a konfigurált GitHub Releases stabil kiadásaival szemben.");
        var repository=new TextBox{Text=_cluster.Config.GitHubRepository,Margin=new Thickness(0,0,0,10)};
        var publisher=new TextBox{Text=_cluster.Config.PublisherCertificateThumbprint,
            Margin=new Thickness(0,0,0,10)};
        var status=Label("Még nem ellenőrizve",13,Soft);
        var details=Label("",13,Brushes.White);
        var progressText=Label("",12,Soft);
        var bar=new ProgressBar{Minimum=0,Maximum=100,Height=20,Margin=new Thickness(0,8,0,10),
            Visibility=Visibility.Collapsed};
        var install=Btn("Frissítés telepítése",()=>{});
        install.Visibility=Visibility.Collapsed;
        UpdateRelease? candidate=null;

        Add(Box(V(Label($"Jelenleg telepítve: {UpdateCenter.RunningVersion.ToString(3)}",22,Mint,true),
            Label("Frissítési csatorna: STABIL (prerelease és forráscsomag kihagyva).",12,Aqua),
            Label("GitHub repository (tulajdonos/tárhely)",13,Soft),repository,
            Label("Opcionális kiadói Authenticode tanúsítvány SHA1 ujjlenyomat",13,Soft),
            publisher,
            Btn("Frissítési beállítások mentése",()=>{
                if(!UpdateCenter.ValidRepository(repository.Text.Trim()))
                {Notice("Érvénytelen owner/repository.");return;}
                _cluster.UpdateSettings(c=>{
                    c.GitHubRepository=repository.Text.Trim();
                    c.PublisherCertificateThumbprint=publisher.Text.Trim();
                });
                Notice("Beállítások mentve. A profilok és szervermentések érintetlenek.");
            }))));
        Add(Box(V(Label("GitHub frissítések ellenőrzése",19,Mint,true),
            Label("Hiteles forrás: api.github.com + az engedélyezett repository, HTTPS, a GitHub asset SHA-256 digestje.",12,Soft),
            Btn("Frissítések keresése",async()=>{
                try{
                    status.Text="Stabil GitHub-kiadás keresése…";install.Visibility=Visibility.Collapsed;
                    if(!UpdateCenter.ValidRepository(repository.Text.Trim()))
                        throw new InvalidDataException("Érvénytelen owner/repository.");
                    candidate=await UpdateCenter.CheckStableAsync(repository.Text.Trim());
                    if(candidate==null) {
                        status.Text="Nincs elérhető stabil kiadás (előzetes verziókat nem telepítünk).";
                        details.Text="";return;
                    }
                    if(candidate.Version.CompareTo(UpdateCenter.RunningVersion)<=0){
                        status.Text="Az alkalmazás naprakész.";details.Text="";return;
                    }
                    status.Text="Új stabil verzió: "+candidate.Version;
                    details.Text=candidate.ChangeLog;
                    install.Visibility=Visibility.Visible;
                } catch(Exception ex){status.Text="Frissítési ellenőrzési hiba: "+ex.Message;WriteLog("updates-check",ex);}
            }),status,details,install,bar,progressText)));
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
                    "--profile",App.ProfileName,
                    "--backup",backup})
                    psi.ArgumentList.Add(arg);
                if(Process.Start(psi)==null)throw new InvalidOperationException("Updater indítása sikertelen");
                progressText.Text="Külső updater indult; a játék szerverfolyamatai futva maradnak.";
                // Closing the Manager NEVER terminates a launched ASA process.
                Exit();
            }
            catch(Exception ex){
                status.Text="Frissítés megszakítva: "+ex.Message;
                WriteLog("updates-install",ex);install.IsEnabled=true;
            }
        };
    }
}
