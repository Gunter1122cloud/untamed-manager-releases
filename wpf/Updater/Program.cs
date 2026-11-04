using System;
using UntamedManager.Core;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private bool _ciTest;
    private string? _ciRoot;
    private string _phase="initialize";
    private static string FailureCode(Exception error)=>error.Message switch{
        "Restored Manager did not initialize"=>"RESTORED_SMOKE_FAILED",
        "Restored Manager smoke check timed out"=>"RESTORED_SMOKE_TIMEOUT",
        "Application health check failed"=>"NEW_HEALTH_FAILED",
        "Health check timed out"=>"NEW_HEALTH_TIMEOUT",
        "Update recovery ownership or phase differs"=>"RECOVERY_OWNER_MISMATCH",
        "Update snapshot inventory differs from protected owner binding"=>"SNAPSHOT_OWNER_HASH_MISMATCH",
        _=>error.GetType().Name
    };
    private void Report(string result,Exception? error=null)
    {
        if(_ciTest)File.WriteAllText(Path.Combine(_ciRoot!,"updater-result.json"),JsonSerializer.Serialize(new{
            result,utc=DateTimeOffset.UtcNow,phase=_phase,error=error?.GetType().Name,
            hresult=error?.HResult,code=error==null?null:FailureCode(error),
            method=error==null?null:new StackTrace(error,false).GetFrame(0)?.GetMethod()?.Name}));
    }
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
    private static void Restart(string exe,string profile,bool smoke=false)
    {
        if(!File.Exists(exe))return;
        var start=new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe)!,
            UseShellExecute=true};
        if(!string.IsNullOrWhiteSpace(profile)){start.ArgumentList.Add("--profile");start.ArgumentList.Add(profile);}
        start.ArgumentList.Add("--update-verified");
        if(smoke)start.ArgumentList.Add("--smoke-test");
        using var process=Process.Start(start)??throw new IOException("Manager restart failed");
        if(smoke)
        {
            if(!process.WaitForExit(90000)){process.Kill();process.WaitForExit();throw new IOException("Restored Manager smoke check timed out");}
            if(process.ExitCode!=0)throw new IOException("Restored Manager did not initialize");
        }
    }
    private async Task RunAsync()
    {
        DirectoryUpdateTransaction? transaction=null;
        bool snapshotPrepared=false;
        string? appExe=null;
        string? profile=null;
        try
        {
            if(_arguments.Contains("--ci-root"))
            {
                string root=Path.GetFullPath(Required("--ci-root")).TrimEnd(Path.DirectorySeparatorChar);
                string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!root.StartsWith(temp,StringComparison.OrdinalIgnoreCase))throw new IOException("CI harness restricted to temporary directories");
                foreach(string path in new[]{Required("--app"),Required("--backup")})
                    if(!Path.GetFullPath(path).StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("CI path escapes isolated harness");
                Directory.CreateDirectory(root);_ciRoot=root;_ciTest=true;
            }
            if((_arguments.Contains("--ci-fail-health")||_arguments.Contains("--ci-interrupt-after-snapshot"))&&!_ciTest)throw new IOException("Fault injection only permitted in isolated CI harness");
            if(_arguments.Contains("--recover"))
            {
                appExe=Path.GetFullPath(Required("--app"));profile=Required("--profile");
                transaction=new DirectoryUpdateTransaction(Required("--backup"),Path.GetDirectoryName(appExe)!);
                int index=Array.IndexOf(_arguments,"--pid");
                if(index>=0)
                {
                    try{using var old=Process.GetProcessById(int.Parse(Required("--pid")));if(!await Task.Run(()=>old.WaitForExit(90000)))throw new TimeoutException("Manager has not exited");}
                    catch(ArgumentException){}
                }
                snapshotPrepared=true;_phase="recover-directory";transaction.Rollback();UpdateHistory.Append("startup-rollback-verified","");
                _phase="restart-recovered";Restart(appExe,profile,_ciTest);Report("recovered");Close();return;
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
            transaction=new DirectoryUpdateTransaction(snapshotRoot,Path.GetDirectoryName(appExe)!);
            Update("Manager felületének bezárása…",10);
            try
            {
                using var previous=Process.GetProcessById(managerPid);
                if(!await Task.Run(()=>previous.WaitForExit(90000)))
                    throw new TimeoutException("Manager UI did not exit in 90 seconds");
            }
            catch(ArgumentException){} // Already exited.
            Update("Korábbi alkalmazás mentése…",25);
            _phase="snapshot";transaction.Prepare(Path.GetDirectoryName(appExe)!);snapshotPrepared=true;
            UpdateHistory.Append("snapshot-prepared",expected);
            if(_ciTest&&_arguments.Contains("--ci-interrupt-after-snapshot"))Environment.Exit(17);
            var log=Path.Combine(snapshotRoot,"setup.log");
            Update("Új Windows-verzió telepítése…",45);
            _phase="installer";
            var start=new ProcessStartInfo(installer){UseShellExecute=false,
                CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(installer)!};
            foreach(var arg in new[]{"/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART",
                                      "/NOCLOSEAPPLICATIONS","/RESTARTAPPLICATIONS=0",
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
            _phase="new-health";
            var healthStart=new ProcessStartInfo(appExe){UseShellExecute=false,CreateNoWindow=true};
            healthStart.ArgumentList.Add("--smoke-test");
            healthStart.ArgumentList.Add("--profile");healthStart.ArgumentList.Add(profile);
            using var health=Process.Start(healthStart)??throw new IOException("Health check failed to start");
            if(!await Task.Run(()=>health.WaitForExit(60000)))
            { health.Kill();await health.WaitForExitAsync();throw new IOException("Health check timed out"); }
            if(health.ExitCode!=0||(_ciTest&&_arguments.Contains("--ci-fail-health")))throw new IOException("Application health check failed");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(appExe)!,"installed-release.txt"),Required("--tag"));
            _phase="new-restart";Restart(appExe,profile,_ciTest);
            _phase="commit";
            transaction.Commit();
            UpdateHistory.Append("installation-committed",expected);
            Report("committed");
            Update("Frissítés sikeres!",100);
            await Task.Delay(1000);
            Close();
        }
        catch(Exception ex)
        {
            string message="UM-UPDATE: "+_phase+" / "+FailureCode(ex)+" / "+ex.HResult.ToString("X8");
            try
            {
                if(snapshotPrepared&&transaction!=null&&appExe!=null)
                {
                    Update("Korábbi verzió visszaállítása…",70);
                    _phase="rollback-directory";transaction.Rollback();
                    UpdateHistory.Append("rollback-verified","");
                    _phase="restart-previous";if(profile!=null)Restart(appExe,profile,_ciTest);
                    Report("rolledback");
                    message+="\nA teljes korábbi alkalmazáskönyvtár hash-ellenőrzéssel visszaállítva.";
                }
            }
            catch(Exception rollback){Report("rollback-failed",rollback);Environment.ExitCode=1;
                message+="\nUM-UPDATE-RECOVERY: "+_phase+" / "+FailureCode(rollback)+" / "+rollback.HResult.ToString("X8");}
            try{
                var logDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UntamedManager","Updates");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir,"updater-errors.log"),DateTimeOffset.Now+"\n"+message+"\n");
            }catch{}
            if(!_ciTest)MessageBox.Show(message,"Untamed Manager – Frissítési hiba",MessageBoxButtons.OK,MessageBoxIcon.Error);
            else if(!File.Exists(Path.Combine(_ciRoot!,"updater-result.json"))){Report("failed",ex);Environment.ExitCode=1;}
            Close();
        }
    }
}
