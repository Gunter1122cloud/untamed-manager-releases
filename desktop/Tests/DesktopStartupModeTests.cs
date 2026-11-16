using System;
using UntamedDesktop;

internal static class DesktopStartupModeTests
{
    private static void Check(bool valid,string label)
    {
        if(!valid)throw new InvalidOperationException(
            "MANUAL ACCEPTANCE STARTUP: "+label);
        Console.WriteLine("PASS manual acceptance startup: "+label);
    }

    public static void Run()
    {
        var normal=DesktopStartupMode.Parse(Array.Empty<string>());
        Check(!normal.SmokeTest&&!normal.ManualTest&&
              normal.RunScheduledMutations&&normal.RunReadOnlyDiscovery,
            "ordinary launch preserves explicitly configured schedules");
        var manual=DesktopStartupMode.Parse(new[]{"--MANUAL-TEST"});
        Check(!manual.SmokeTest&&manual.ManualTest&&
              !manual.RunScheduledMutations&&manual.RunReadOnlyDiscovery,
            "manual-test keeps real local map observation while suppressing scheduled mutations");
        var smoke=DesktopStartupMode.Parse(new[]{"--smoke-test","--manual-test"});
        Check(smoke.SmokeTest&&!smoke.RunScheduledMutations&&
              !smoke.RunReadOnlyDiscovery,
            "synthetic smoke mode remains separate from real-host manual acceptance");
        var ordinary=DesktopStartupMode.Parse(new[]{"--unknown"});
        Check(ordinary.RunScheduledMutations&&!ordinary.ManualTest,
            "unknown arguments cannot silently enable manual safety mode");
    }
}
