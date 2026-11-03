using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace UntamedManager;
public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    public static bool IsSmokeTest { get; private set; }
    public static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UntamedManager","WPF");
    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(LogDir);
        IsSmokeTest = Array.Exists(e.Args, a => a == "--smoke-test");
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