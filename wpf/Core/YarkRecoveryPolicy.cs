using System;

namespace UntamedManager.Core;

/// <summary>
/// C# port of YARK's crash-recovery-plan.ts, crash-recovery-policy.ts and
/// update-server-jobs.ts recovery predicates (gabomarin/yark).
/// Keeps YARK's bounded 3-attempt, 30/60/90-second backoff and 10-minute
/// stability window, plus its pre-SteamCMD server-restoration decision.
/// </summary>
internal static class YarkRecoveryPolicy
{
    internal const int MaxAttempts=3;
    internal const int BackoffSeconds=30;
    internal const int StabilitySeconds=600;

    internal static TimeSpan BackoffAfter(int attempt)=>
        TimeSpan.FromSeconds(Math.Max(1,attempt)*BackoffSeconds);

    internal static bool BudgetResetByStableRun(TimeSpan uptime)=>
        uptime>=TimeSpan.FromSeconds(StabilitySeconds);

    internal static bool ShouldRestartServerAfterPreSteamCmdAbort(
        bool wasRunning,bool installMayHaveChanged,bool serverIsActive)=>
        wasRunning&&!installMayHaveChanged&&!serverIsActive;

    internal static bool UpdateInstallMayHaveChanged(string? nativeSteamStage)=>
        nativeSteamStage is "SteamCmd" or "VerifyBuild" or
            "VerifyProtectedFiles" or "Completed";
}
