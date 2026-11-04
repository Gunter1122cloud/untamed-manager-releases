using System;
using System.Diagnostics;
using UntamedManager.Core;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UntamedManager;
public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    private ManagerSplashWindow? _splash;
    public void NotifyMainUiReady(Window readyWindow)
    {
        if(_splash==null)return;
        // Loaded is the actual WPF lifecycle event, not an elapsed-time guess.
        MainWindow=readyWindow;
        _splash.SetStage(ManagerStartupStage.Ready);
        _splash.Close();
        _splash=null;
        ShutdownMode=ShutdownMode.OnMainWindowClose;
    }
    public static string ProfileName { get; private set; } = "default";
    public static bool IsSmokeTest { get; private set; }
    public static bool IsDemoStart { get; private set; }
    public static bool IsSupervisionTest { get; private set; }
    public static string? ProfileRootOverride { get; private set; }
    public static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UntamedManager","WPF");
    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(LogDir);
        int profileArg = Array.FindIndex(e.Args, x => x == "--profile");
        if (profileArg >= 0)
        {
            if (profileArg + 1 >= e.Args.Length) { MessageBox.Show("A --profile paraméternek értékre van szüksége."); Shutdown(2); return; }
            var requested = e.Args[profileArg+1];
            if (requested.Length is < 1 or > 40 || requested.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            { MessageBox.Show("Érvénytelen profilnév."); Shutdown(2); return; }
            ProfileName = requested;
        }
        IsSupervisionTest=e.Args.Contains("--supervisor-ci-test");
        if(IsSupervisionTest)
        {
            int i=Array.IndexOf(e.Args,"--profile-root");
            if(i<0||i+1>=e.Args.Length){Shutdown(2);return;}
            string root=Path.GetFullPath(e.Args[i+1]);
            if(!root.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)){Shutdown(2);return;}
            ProfileRootOverride=root;
        }
        IsSmokeTest = Array.Exists(e.Args, a => a == "--smoke-test" || a == "--screenshot-test");
        IsDemoStart = e.Args.Contains("--demo") && !IsSmokeTest && !IsSupervisionTest;
        if(IsDemoStart)
            ProfileRootOverride=Path.Combine(Path.GetTempPath(),
                "Untamed-Manager-Demo-"+Environment.ProcessId+"-"+Guid.NewGuid().ToString("N"));

        if(!IsSmokeTest && !IsSupervisionTest && !IsDemoStart && !e.Args.Contains("--update-verified"))
        {
            try
            {
                string app=Environment.ProcessPath??throw new IOException("Application path unavailable");
                string? pending=UpdateRecovery.FindPending(Path.GetDirectoryName(app)!,UpdateCenter.UpdateRoot);
                if(pending!=null)
                {
                    string helper=UpdateCenter.StageUpdater(Path.Combine(Path.GetDirectoryName(app)!,"UntamedUpdater.exe"));
                    var start=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true};
                    foreach(string arg in new[]{"--recover","--pid",Environment.ProcessId.ToString(),"--app",app,"--profile",ProfileName,"--backup",pending})start.ArgumentList.Add(arg);
                    if(Process.Start(start)==null)throw new IOException("Recovery process did not start");
                    UpdateHistory.Append("startup-recovery-requested","");Shutdown();return;
                }
            }
            catch(Exception ex){Log(ex);MessageBox.Show("Megszakadt frissítés: a biztonságos helyreállítás kézi ellenőrzést igényel.");Shutdown(2);return;}
        }
        mutex = new Mutex(true,@"Local\UntamedManager_WPF_Desktop_" + ProfileName,out bool first);
        if (!first)
        {
            System.Windows.MessageBox.Show("Az Untamed Manager már fut. A tálcaikonról nyitható meg.","Untamed Manager");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_,a) =>
        {
            Log(a.Exception);
            a.Handled=true;
            System.Windows.MessageBox.Show(UntamedPresentation.UnexpectedFailure(a.Exception),"Untamed Manager",MessageBoxButton.OK,MessageBoxImage.Warning);
            if(_splash!=null)
            {
                _splash.Close();_splash=null;
                Shutdown(2);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_,a) => { if(a.ExceptionObject is Exception x) Log(x); };
        TaskScheduler.UnobservedTaskException += (_,a) => { Log(a.Exception); a.SetObserved(); };
        if (Array.Exists(e.Args, a => a == "--screenshot-test"))
        {
            try
            {
                const int width=1650, height=930;
                var window=new MainWindow {Width=width,Height=height};
                window.Show();
                if (window.Content is not FrameworkElement content)
                    throw new InvalidOperationException("WPF root missing");
                string dashboardShotPath=Environment.GetEnvironmentVariable("UNTAMED_SCREENSHOT_PATH") ??
                    Path.Combine(LogDir,"preview.png");
                string gallery=Path.Combine(Path.GetDirectoryName(dashboardShotPath)??LogDir,"UI-Screenshots");
                bool galleryEnabled=Environment.GetEnvironmentVariable("UNTAMED_SCREENSHOT_GALLERY")=="1";
                var pages=galleryEnabled
                    ? new[]{"dashboard","servers","mods","plugins","cluster","backups","settings","ini"}
                    : new[]{"dashboard"};
                if(galleryEnabled)Directory.CreateDirectory(gallery);
                foreach(string page in pages)
                {
                    window.PrepareScreenshotPage(page);
                    content.Width=width;content.Height=height;
                    content.Measure(new Size(width,height));
                    content.Arrange(new Rect(0,0,width,height));
                    content.UpdateLayout();
                    window.Dispatcher.Invoke(() => { },DispatcherPriority.Render);
                    var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var png=new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    string path=page=="dashboard"?dashboardShotPath:
                        Path.Combine(gallery,"UntamedManager-"+page+".png");
                    using(var output=File.Create(path))png.Save(output);
                    if(new FileInfo(path).Length<8000)
                        throw new IOException("WPF page screenshot too small: "+page);
                }
                Environment.Exit(0);
            }
            catch(Exception ex) { Log(ex); Environment.Exit(1); }
            return;
        }
        if (IsSmokeTest)
        {
            try
            {
                File.WriteAllText(Path.Combine(LogDir,"smoke.log"),"starting\n");
                var splashCheck=new ManagerSplashWindow();
                splashCheck.SetStage(ManagerStartupStage.InitializingInterface);
                if(splashCheck.CurrentStage!=ManagerStartupStage.InitializingInterface)
                    throw new InvalidOperationException("Splash stage cannot reflect actual startup");
                File.AppendAllText(Path.Combine(LogDir,"smoke.log"),"splash-stage-passed\n");
                var window = new MainWindow();
                File.AppendAllText(Path.Combine(LogDir,"smoke.log"),"constructed\n");
                window.VerifyRecoveryCenterCiSmoke();
                File.AppendAllText(Path.Combine(LogDir,"smoke.log"),"recovery-center-passed\n");
                window.VerifyCompleteManagerCiSmoke();
                window.VerifyModListFocusCiSmoke();
                File.AppendAllText(Path.Combine(LogDir,"smoke.log"),"rc-all-pages-passed\n");
                window.UpdateLayout();
                File.AppendAllText(Path.Combine(LogDir,"smoke.log"),"passed\n");
                Environment.Exit(0);
            }
            catch (Exception ex) { Log(ex); Environment.Exit(1); }
            return;
        }
        if(IsSupervisionTest)
        {
            int tries=0;var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
            timer.Tick+=(_,_)=>
            {
                try
                {
                    var store=new ConfigStore(ProfileName,ProfileRootOverride);var channel=new SupervisorChannel(store.Root);
                    var snapshot=channel.Read(store.Read().LocalHostId);
                    using var worker=Process.GetProcessById(snapshot.Pid);
                    if(worker.HasExited||new DateTimeOffset(worker.StartTime.ToUniversalTime())!=snapshot.StartedUtc)throw new IOException("Worker not alive");
                    if(MainWindow is UntamedManager.MainWindow window){timer.Stop();window.CompleteSupervisionCiTest();return;}
                }catch{}
                if(++tries>=30){timer.Stop();Environment.Exit(3);}
            };
            timer.Start();
        }
        if(!IsSmokeTest&&!IsSupervisionTest)
        {
            try
            {
                // The splash is transient; it must never become the primary
                // application window or close the Manager when it dismisses.
                ShutdownMode=ShutdownMode.OnExplicitShutdown;
                _splash=new ManagerSplashWindow();
                _splash.SetStage(ManagerStartupStage.InitializingInterface);
                _splash.Show();
            }
            catch(Exception ex)
            {
                Log(ex);_splash=null;
                ShutdownMode=ShutdownMode.OnMainWindowClose;
            }
        }
        base.OnStartup(e);
    }
    private static void Log(Exception e)
    {
        try
        {
            File.AppendAllText(Path.Combine(LogDir,"crash.log"),DateTimeOffset.Now+"\n"+UntamedManager.Core.UntamedPresentation.Redact(e.ToString())+"\n");
            File.WriteAllText(Path.Combine(LogDir,"crash-code.json"),System.Text.Json.JsonSerializer.Serialize(new{
                utc=DateTimeOffset.UtcNow,type=e.GetType().Name,hresult=e.HResult,
                method=new StackTrace(e,false).GetFrame(0)?.GetMethod()?.Name,
                innerType=e.InnerException?.GetType().Name,innerHresult=e.InnerException?.HResult}));
        }
        catch{}
    }
    protected override void OnExit(ExitEventArgs e) { try{mutex?.ReleaseMutex();}catch{} mutex?.Dispose();base.OnExit(e); }
}
