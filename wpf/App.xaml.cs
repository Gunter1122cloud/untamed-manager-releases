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
        IsSmokeTest = Array.Exists(e.Args, a => a == "--smoke-test" ||
            a == "--screenshot-test" || a == "--splash-screenshot-test");
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
        if(e.Args.Contains("--splash-screenshot-test"))
        {
            try
            {
                const int width=930, height=510;
                var window=new ManagerSplashWindow();
                window.SetStage(ManagerStartupStage.InitializingInterface);
                window.Show();
                if(window.Content is not FrameworkElement content)
                    throw new InvalidOperationException("WPF splash root missing");
                content.Width=width;content.Height=height;
                content.Measure(new Size(width,height));
                content.Arrange(new Rect(0,0,width,height));
                content.UpdateLayout();
                window.Dispatcher.Invoke(()=>{},DispatcherPriority.Render);
                var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string path=Environment.GetEnvironmentVariable("UNTAMED_SPLASH_SCREENSHOT_PATH") ??
                    Path.Combine(LogDir,"splash.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using(var output=File.Create(path))encoder.Save(output);
                if(new FileInfo(path).Length<8000)
                    throw new IOException("WPF splash screenshot unexpectedly small");
                Environment.Exit(0);
            }
            catch(Exception ex){Log(ex);Environment.Exit(1);}
            return;
        }
        if (Array.Exists(e.Args, a => a == "--screenshot-test"))
        {
            try
            {
                int width=int.TryParse(Environment.GetEnvironmentVariable("UNTAMED_CAPTURE_WIDTH"),
                    out var captureWidth)?Math.Clamp(captureWidth,780,2560):1650;
                int height=int.TryParse(Environment.GetEnvironmentVariable("UNTAMED_CAPTURE_HEIGHT"),
                    out var captureHeight)?Math.Clamp(captureHeight,450,1600):930;
                var window=new MainWindow {Width=width,Height=height};
                window.Show();
                if (window.Content is not FrameworkElement content)
                    throw new InvalidOperationException("WPF root missing");
                string dashboardShotPath=Environment.GetEnvironmentVariable("UNTAMED_SCREENSHOT_PATH") ??
                    Path.Combine(LogDir,"preview.png");
                string gallery=Path.Combine(Path.GetDirectoryName(dashboardShotPath)??LogDir,"UI-Screenshots");
                bool galleryEnabled=Environment.GetEnvironmentVariable("UNTAMED_SCREENSHOT_GALLERY")=="1";
                var pages=galleryEnabled
                    ? new[]{"dashboard","servers","mods","plugins","cluster","backups","settings","ini",
                        "rcon","monitoring","schedules","recovery","operations","network","data",
                        "steam","license","lan","readiness","extensions","setup","logs",
                        "demo-dashboard","demo-servers","demo-cluster","demo-mods","demo-plugins","demo-logs",
                        "demo-ini","demo-rcon","demo-backups","demo-schedules","demo-monitoring",
                        "demo-recovery","demo-settings"}
                    : new[]{"dashboard"};
                if(galleryEnabled)Directory.CreateDirectory(gallery);
                foreach(string page in pages)
                {
                    window.PrepareScreenshotPage(page);
                    // Real offscreen WPF layout, independent of the CI runner's
                    // 1024px virtual desktop. A root detached from its native
                    // Window has no painted background or guaranteed layout.
                    // Reparent into a full-size WPF Grid and measure that host.
                    var captureHost=new System.Windows.Controls.Grid {
                        Width=width,Height=height,
                        Background=window.Background
                    };
                    RenderTargetBitmap bitmap;
                    window.Content=null;
                    try
                    {
                        // Keep the production view, bindings, controls and
                        // event handlers. Do not substitute a mock screenshot.
                        content.Width=double.NaN;
                        content.Height=double.NaN;
                        captureHost.Children.Add(content);
                        captureHost.Measure(new Size(width,height));
                        captureHost.Arrange(new Rect(0,0,width,height));
                        captureHost.UpdateLayout();
                        window.Dispatcher.Invoke(()=>{},DispatcherPriority.Render);
                        if(content.RenderSize.Width<width-2 ||
                           content.RenderSize.Height<height-2)
                            throw new InvalidOperationException(
                                "WPF offscreen layout incomplete: rendered "+
                                content.RenderSize.Width+"x"+content.RenderSize.Height+
                                " instead of "+width+"x"+height);
                        bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                        bitmap.Render(captureHost);
                        // The offscreen host has an opaque graphite background;
                        // a transparent corner means the render failed outright.
                        byte[] pixel=new byte[4];
                        bitmap.CopyPixels(new Int32Rect(width-3,height-3,1,1),pixel,4,0);
                        if(pixel[3]<200)
                            throw new InvalidOperationException(
                                "WPF offscreen bitmap alpha invalid at full viewport: "+
                                width+"x"+height+" alpha="+pixel[3]);
                    }
                    finally
                    {
                        captureHost.Children.Remove(content);
                        window.Content=content;
                        window.UpdateLayout();
                    }
                    var png=new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    string path=page=="dashboard"?dashboardShotPath:
                        Path.Combine(gallery,"UntamedManager-"+page+".png");
                    using(var output=File.Create(path))png.Save(output);
                    if(new FileInfo(path).Length<8000)
                        throw new IOException("WPF page screenshot too small: "+page);
                }
                // Regress the actual native WPF mouse-wheel route, not just
                // ScrollBoundaryPolicy arithmetic. No real ASA state is touched.
                if(galleryEnabled)
                {
                    window.PrepareScreenshotPage("demo-dashboard");
                    window.UpdateLayout();
                    var scroller=window.Dashboard;
                    scroller.UpdateLayout();
                    if(scroller.ScrollableHeight<40)
                        throw new InvalidOperationException(
                            "Dashboard has no vertical scroll range in 10-map demo");
                    scroller.ScrollToTop();
                    var wheel=new System.Windows.Input.MouseWheelEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){
                            RoutedEvent=UIElement.PreviewMouseWheelEvent
                        };
                    scroller.RaiseEvent(wheel);
                    scroller.UpdateLayout();
                    if(scroller.VerticalOffset<1)
                        throw new InvalidOperationException(
                            "Dashboard does not scroll on actual WPF PreviewMouseWheel event");
                    scroller.ScrollToTop();
                    window.PrepareScreenshotPage("demo-mods");
                    window.UpdateLayout();
                    var other=window.OtherPage;
                    other.UpdateLayout();
                    if(other.ScrollableHeight<40)
                        throw new InvalidOperationException(
                            "Mod Center page has no usable vertical wheel scroll range");
                    other.ScrollToTop();
                    var innerWheel=new System.Windows.Input.MouseWheelEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){
                            RoutedEvent=UIElement.PreviewMouseWheelEvent
                        };
                    other.RaiseEvent(innerWheel);
                    other.UpdateLayout();
                    if(other.VerticalOffset<1)
                        throw new InvalidOperationException(
                            "Mod Center page does not react to WPF wheel preview");
                    other.ScrollToTop();
                }
                // Exercise the REAL five-step local server editor on Windows.
                // Only navigation clicks are synthesized. Saving is never invoked,
                // and no map/server/Store/SQL file is accessed or changed.
                if(galleryEnabled)
                {
                    var editor=window.PrepareMapEditorScreenshot();
                    try
                    {
                        if(editor.Content is not FrameworkElement editorContent)
                            throw new InvalidOperationException("Map setup editor missing WPF content");
                        System.Windows.Controls.Button? FindNext(DependencyObject root)
                        {
                            foreach(object element in LogicalTreeHelper.GetChildren(root))
                            {
                                if(element is System.Windows.Controls.Button button &&
                                   Equals(button.Content,"Következő →"))
                                    return button;
                                if(element is DependencyObject child)
                                {
                                    var found=FindNext(child);
                                    if(found!=null)return found;
                                }
                            }
                            return null;
                        }
                        // Reject a regression to the unstyled Windows white
                        // password input. The same implicit style covers
                        // RCON, CurseForge and LAN secret boxes everywhere.
                        bool HasNeonPassword(DependencyObject root)
                        {
                            if(root is System.Windows.Controls.PasswordBox input &&
                               input.Background is SolidColorBrush actual &&
                               Resources["SurfaceRaised"] is SolidColorBrush expected)
                                return actual.Color==expected.Color;
                            foreach(object child in LogicalTreeHelper.GetChildren(root))
                                if(child is DependencyObject node && HasNeonPassword(node))
                                    return true;
                            return false;
                        }
                        if(!HasNeonPassword(editorContent))
                            throw new InvalidOperationException(
                                "Map wizard RCON PasswordBox lacks Neon Kingdom styling");
                        const int wizardWidth=860, wizardHeight=660;
                        for(int step=1;step<=5;step++)
                        {
                            if(step>1)
                            {
                                var next=FindNext(editorContent) ??
                                    throw new InvalidOperationException("Map wizard Next button missing");
                                next.RaiseEvent(new RoutedEventArgs(
                                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent,next));
                            }
                            var renderHost=new System.Windows.Controls.Grid {
                                Width=wizardWidth,Height=wizardHeight,Background=editor.Background
                            };
                            editor.Content=null;
                            try
                            {
                                editorContent.Width=double.NaN;
                                editorContent.Height=double.NaN;
                                renderHost.Children.Add(editorContent);
                                renderHost.Measure(new Size(wizardWidth,wizardHeight));
                                renderHost.Arrange(new Rect(0,0,wizardWidth,wizardHeight));
                                renderHost.UpdateLayout();
                                editor.Dispatcher.Invoke(()=>{},DispatcherPriority.Render);
                                var screenshot=new RenderTargetBitmap(
                                    wizardWidth,wizardHeight,96,96,PixelFormats.Pbgra32);
                                screenshot.Render(renderHost);
                                var png=new PngBitmapEncoder();
                                png.Frames.Add(BitmapFrame.Create(screenshot));
                                string output=Path.Combine(gallery,
                                    $"UntamedManager-map-setup-step{step}.png");
                                using(var file=File.Create(output))png.Save(file);
                                if(new FileInfo(output).Length<8000)
                                    throw new IOException("WPF map wizard screenshot invalid: step "+step);
                            }
                            finally
                            {
                                renderHost.Children.Remove(editorContent);
                                editor.Content=editorContent;
                                editor.UpdateLayout();
                            }
                        }
                    }
                    finally { editor.Close(); }
                }
                Environment.Exit(0);
            }
            catch(Exception ex) {
                Log(ex);
                try{File.AppendAllText(Path.Combine(LogDir,"screenshot-diagnostics.log"),
                    DateTimeOffset.UtcNow.ToString("O")+" "+ex+"\n");}catch{}
                Environment.Exit(1);
            }
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
