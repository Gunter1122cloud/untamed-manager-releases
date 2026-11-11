using System;
using System.Linq;
using UntamedDesktop;

/// <summary>
/// Pure classifier tests only. No WMI, COM, Task Scheduler, Kova or ASA access.
/// </summary>
internal static class KovaLegacySupervisorReadOnlyInventoryTests
{
    private static void Check(bool valid,string reason)
    {
        if(!valid)throw new InvalidOperationException(
            "KOVA WATCHDOG READ-ONLY INVENTORY TEST FAILED: "+reason);
        Console.WriteLine("PASS Kova read-only supervisor inventory: "+reason);
    }

    public static void Run()
    {
        var report=KovaLegacySupervisorReadOnlyInventory.Assess(
            new string?[]{"ArkAscendedServer.exe","node.exe local-manager.cjs",
                "ObsidianCommand.exe"},
            new string?[]{"WinDefend","Another Service"},
            new string?[]{"WindowsUpdate","Task action launches Kova watchdog"},
            true,true,true);
        Check(report.SuspectedProcessCount==1&&
              report.SuspectedServiceCount==0&&
              report.SuspectedScheduledTaskCount==1,
            "only suspect controllers returned, never command lines");
        Check(!report.KovaWatchdogDisabledVerified&&
              !report.ExclusiveHandoffVerified,
            "matching Kova process or task never certifies exclusive owner");
        Check(report.EvidenceGaps.Contains(
            "LEGACY_KOVA_CONTROLLER_OR_AUTORUN_CANDIDATE"),
            "found watchdog trigger prevents takeover claim");
        var empty=KovaLegacySupervisorReadOnlyInventory.Assess(
            Array.Empty<string?>(),Array.Empty<string?>(),
            Array.Empty<string?>(),true,true,true);
        Check(empty.SuspectedProcessCount==0&&
              !empty.ExclusiveHandoffVerified&&
              !empty.KovaWatchdogDisabledVerified&&
              empty.EvidenceGaps.Contains(
                  "NO_EXCLUSIVE_STORE_WRITER_LEASE_OR_TAKEOVER_CERTIFICATE"),
            "zero suspect matches is not evidence of a disabled in-app Kova watchdog");
        var incomplete=KovaLegacySupervisorReadOnlyInventory.Assess(
            Array.Empty<string?>(),Array.Empty<string?>(),
            Array.Empty<string?>(),true,false,false);
        Check(incomplete.EvidenceGaps.Contains("SERVICE_INVENTORY_INCOMPLETE")&&
              incomplete.EvidenceGaps.Contains(
                  "TASK_SCHEDULER_INVENTORY_INCOMPLETE"),
            "denied services and scheduled tasks are unknown, never green");
    }
}
