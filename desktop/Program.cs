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
        using var singleInstance = new Mutex(true, @"Local\Untamed-Obsidian-Desktop", out bool isFirst);
        if (!isFirst) return;
        Application.Run(new ObsidianWindow(args.Any(a => a == "--smoke-test")));
    }
}
