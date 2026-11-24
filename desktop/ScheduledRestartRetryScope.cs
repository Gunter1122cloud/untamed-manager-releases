using System;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedDesktop;

// Native-only capability. No WebView parameter can grant automatic retry.
// Flows to the existing restart worker; never persists a replay authority.
internal sealed record ScheduledRestartRetryPermit(Guid MapId,Guid RunId,
    Func<bool> StillApproved,CancellationToken Cancellation,
    Func<TimeSpan,CancellationToken,Task> Backoff)
{
    internal void Demand(Guid mapId)
    {
        Cancellation.ThrowIfCancellationRequested();
        if(MapId!=mapId||RunId==Guid.Empty||!StillApproved())
            throw new InvalidOperationException("UM-RESTART-RETRY-SCHEDULE-CHANGED");
    }
}
internal static class ScheduledRestartRetryScope
{
    private static readonly AsyncLocal<ScheduledRestartRetryPermit?> CurrentPermit=new();
    internal static ScheduledRestartRetryPermit? Current=>CurrentPermit.Value;
    internal static IDisposable Enter(ScheduledRestartRetryPermit permit)
    {
        if(CurrentPermit.Value!=null)throw new InvalidOperationException("UM-RESTART-RETRY-NESTED");
        CurrentPermit.Value=permit;
        return new Scope();
    }
    private sealed class Scope:IDisposable { public void Dispose()=>CurrentPermit.Value=null; }
    internal static TimeSpan DelayAfter(int failedAttempt)=>failedAttempt switch{
        1=>TimeSpan.FromMinutes(1),2=>TimeSpan.FromMinutes(3),
        3=>TimeSpan.FromMinutes(5),_=>throw new ArgumentOutOfRangeException(nameof(failedAttempt))
    };
    internal static bool CanRetry(int attempt,string? code)=>
        attempt is >=1 and <4&&code==RestartModServiceFailureProbe.TransientCurseForge;
}

// The host still starts one scheduled operation at a time. Release the
// host dispatch lane only during a known startup retry delay; keep the
// map's worker and durable claim alive, then reacquire before any new Start.
internal sealed class ScheduledAsaDispatchLane : IDisposable
{
    private readonly SemaphoreSlim gate;
    private bool held;
    private ScheduledAsaDispatchLane(SemaphoreSlim gate){this.gate=gate;held=true;}
    internal static async Task<ScheduledAsaDispatchLane> EnterAsync(
        SemaphoreSlim gate,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return new(gate);
    }
    internal async Task BackoffAsync(TimeSpan delay,CancellationToken ct)
    {
        if(!held)throw new InvalidOperationException("UM-SCHEDULE-DISPATCH-LANE-NOT-HELD");
        held=false;gate.Release();
        await Task.Delay(delay,ct);
        await gate.WaitAsync(ct);
        held=true;
    }
    public void Dispose()
    {
        if(held){held=false;gate.Release();}
    }
}
