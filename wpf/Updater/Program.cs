using System;
using UntamedManager.Core;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UntamedUpdater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new UpdateForm(args));
    }
}

public sealed class UpdateForm : Form
{
    private readonly string[] _arguments;
    private readonly Label _status=new(){Dock=DockStyle.Top,Height=64,
        Font=new Font("Segoe UI",12,FontStyle.Bold),ForeColor=Color.FromArgb(218,248,238),
        Text="Frissítés előkészítése…",TextAlign=ContentAlignment.MiddleCenter};
    private readonly ProgressBar _progress=new(){Dock=DockStyle.Top,Height=28,
        Minimum=0,Maximum=100,Style=ProgressBarStyle.Continuous};
    public UpdateForm(string[] args)
    {
        _arguments=args;
        Text="Untamed Manager – Frissítés";
        Width=540;Height=188;
        BackColor=Color.FromArgb(8,31,32);
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
        StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(_progress);
        Controls.Add(_status);
        Shown+=async(_,_)=>await RunAsync();
    }
    private void Update(string text,int percent)
    {
        _status.Text=text;
        _progress.Value=Math.Clamp(percent,0,100);
        Application.DoEvents();
    }
    private string Required(string name)
    {
        int i=Array.IndexOf(_arguments,name);
        if(i<0||i+1>=_arguments.Length)throw new ArgumentException("Missing "+name);
        return _arguments[i+1];
    }
    private static void Restart(string exe,string profile)
    {
        if(!File.Exists(exe))return;
        var start=new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe)!,
            UseShellExecute=true};
        if(!string.IsNullOrWhiteSpace(profile)){start.ArgumentList.Add("--profile");start.ArgumentList.Add(profile);}
        Process.Start(start);
    }
    private async Task RunAsync()
    {
        DirectoryUpdateTransaction? transaction=null;
        string? appExe=null;
        string? profile=null;
        try
        {
            if(_arguments.Contains("--recover"))
            {
                appExe=Path.GetFullPath(Required("--app"));profile=Required("--profile");
                transaction=new DirectoryUpdateTransaction(Required("--backup"));
                transaction.Rollback();Restart(appExe,profile);Close();return;
            }
            int managerPid=int.Parse(Required("--pid"));
            string installer=Path.GetFullPath(Required("--installer"));
            appExe=Path.GetFullPath(Required("--app"));
            string expected=Required("--expected");
            profile=Required("--profile");
            string snapshotRoot=Path.GetFullPath(Required("--backup"));
            if(!File.Exists(installer))throw new FileNotFoundException("Installer not found",installer);
            if(!File.Exists(appExe))throw new FileNotFoundException("Installed Manager EXE missing",appExe);
            if(!Directory.Exists(Path.GetDirectoryName(appExe)))
                throw new DirectoryNotFoundException("Manager install folder missing");
            // Never access cluster config, save games, backups or ASA installation paths.
            Directory.CreateDirectory(snapshotRoot);
            transaction=new DirectoryUpdateTransaction(snapshotRoot);
            Update("Manager felületének bezárása…",10);
            try
            {
                using var previous=Process.GetProcessById(managerPid);
                if(!await Task.Run(()=>previous.WaitForExit(90000)))
                    throw new TimeoutException("Manager UI did not exit in 90 seconds");
            }
            catch(ArgumentException){} // Already exited.
            Update("Korábbi alkalmazás mentése…",25);
            transaction.Prepare(Path.GetDirectoryName(appExe)!);
            var log=Path.Combine(snapshotRoot,"setup.log");
            Update("Új Windows-verzió telepítése…",45);
            var start=new ProcessStartInfo(installer){UseShellExecute=false,
                CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(installer)!};
            foreach(var arg in new[]{"/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART",
                                      "/CLOSEAPPLICATIONS","/RESTARTAPPLICATIONS=0",
                                      "/DIR="+Path.GetDirectoryName(appExe)!,"/LOG="+log})start.ArgumentList.Add(arg);
            using var setup=Process.Start(start) ?? throw new InvalidOperationException("Could not launch installer");
            if(!await Task.Run(()=>setup.WaitForExit(180000)))
            {
                setup.Kill(true);
                throw new TimeoutException("Installer exceeded three minutes");
            }
            if(setup.ExitCode!=0)throw new IOException("Installer exit code "+setup.ExitCode);
            if(!File.Exists(appExe))throw new IOException("New application executable missing");
            var version=FileVersionInfo.GetVersionInfo(appExe).FileVersion??"";
            if(!version.StartsWith(expected+".",StringComparison.Ordinal) &&
               !version.Equals(expected,StringComparison.Ordinal))
                throw new InvalidDataException($"Installed version mismatch: expected {expected}, got {version}");
            Update("Új verzió elindítása…",95);
            var healthStart=new ProcessStartInfo(appExe){UseShellExecute=false,CreateNoWindow=true};
            healthStart.ArgumentList.Add("--smoke-test");
            healthStart.ArgumentList.Add("--profile");healthStart.ArgumentList.Add(profile);
            using var health=Process.Start(healthStart)??throw new IOException("Health check failed to start");
            if(!await Task.Run(()=>health.WaitForExit(60000)))
            { health.Kill();await health.WaitForExitAsync();throw new IOException("Health check timed out"); }
            if(health.ExitCode!=0)throw new IOException("Application health check failed");
            Restart(appExe,profile);
            transaction.Commit();
            Update("Frissítés sikeres!",100);
            await Task.Delay(1000);
            Close();
        }
        catch(Exception ex)
        {
            string message=ex.ToString();
            try
            {
                if(transaction!=null&&appExe!=null)
                {
                    Update("Korábbi verzió visszaállítása…",70);
                    transaction.Rollback();
                    if(profile!=null)Restart(appExe,profile);
                    message+="\nA teljes korábbi alkalmazáskönyvtár hash-ellenőrzéssel visszaállítva.";
                }
            }
            catch(Exception rollback){message+="\nA visszaállítás is hibára futott: "+rollback;}
            try{
                var logDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UntamedManager","Updates");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir,"updater-errors.log"),DateTimeOffset.Now+"\n"+message+"\n");
            }catch{}
            MessageBox.Show(message,"Untamed Manager – Frissítési hiba",
                MessageBoxButtons.OK,MessageBoxIcon.Error);
            Close();
        }
    }
}
