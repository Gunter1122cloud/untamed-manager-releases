using System;
using System.IO;
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
    public static bool IsSmokeTest { get; private set; }
    public static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UntamedManager","WPF");
    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(LogDir);
        IsSmokeTest = Array.Exists(e.Args, a => a == "--smoke-test" || a == "--screenshot-test");
        mutex = new Mutex(true,@"Local\UntamedManager_WPF_Desktop",out bool first);
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
            System.Windows.MessageBox.Show("Hiba történt. Részletek a crash.log fájlban.\n"+a.Exception.Message,"Untamed Manager",MessageBoxButton.OK,MessageBoxImage.Warning);
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
                window.Measure(new Size(width,height));
                window.Arrange(new Rect(0,0,width,height));
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { },DispatcherPriority.Render);
                var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                bitmap.Render((Visual)window.Content);
                var png=new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                string path=Environment.GetEnvironmentVariable("UNTAMED_SCREENSHOT_PATH") ?? Path.Combine(LogDir,"preview.png");
                using (var output=File.Create(path)) png.Save(output);
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
                var window = new MainWindow();
                File.AppendAllText(Path.Combine(LogDir,"smoke.log"),"constructed\n");
                window.UpdateLayout();
                File.AppendAllText(Path.Combine(LogDir,"smoke.log"),"passed\n");
                Environment.Exit(0);
            }
            catch (Exception ex) { Log(ex); Environment.Exit(1); }
            return;
        }
        base.OnStartup(e);
    }
    private static void Log(Exception e) {try {File.AppendAllText(Path.Combine(LogDir,"crash.log"),DateTimeOffset.Now+"\n"+e+"\n");}catch{}}
    protected override void OnExit(ExitEventArgs e) { try{mutex?.ReleaseMutex();}catch{} mutex?.Dispose();base.OnExit(e); }
}