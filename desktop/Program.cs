using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace UntamedDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Do not permit two desktop shells to race on the same user profile.
        bool smokeTest=args.Any(a=>a=="--smoke-test");
        string mutexName=smokeTest
            ?@"Local\Untamed-Obsidian-Desktop-Smoke"
            :@"Local\Untamed-Obsidian-Desktop";
        using var singleInstance = new Mutex(true, mutexName, out bool isFirst);
        if (!isFirst) return;
        Application.Run(new ObsidianWindow(smokeTest));
    }
}
