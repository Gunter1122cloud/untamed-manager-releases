using System;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedDesktop;

// Shared by scheduled restarts and future rolling-update orchestration.
// Each completed send is followed by a full minute: no catch-up burst
// after a stalled RCON response, sleep or temporary connection failure.
internal static class AsaShutdownCountdown
{
    internal const int Minutes=5;
    internal static string Message(int remaining,bool update)=>update
        ? $"[Untamed Kingdom] Szerverfrissites miatt leallas {remaining} perc mulva. Kerlek, menj biztonsagos helyre!"
        : $"[Untamed Kingdom] Utemezett ujrainditas: leallas {remaining} perc mulva. Kerlek, menj biztonsagos helyre!";

    internal static async Task RunAsync(bool update,Func<bool> stillApproved,
        Func<string,CancellationToken,Task> send,CancellationToken ct,
        Func<TimeSpan,CancellationToken,Task>? isolatedDelay=null)
    {
        var delay=isolatedDelay??((duration,token)=>Task.Delay(duration,token));
        for(int remaining=Minutes;remaining>=1;remaining--)
        {
            ct.ThrowIfCancellationRequested();
            if(!stillApproved())throw new InvalidOperationException("UM-SHUTDOWN-COUNTDOWN-CANCELLED");
            await send(Message(remaining,update),ct);
            await delay(TimeSpan.FromMinutes(1),ct);
        }
        ct.ThrowIfCancellationRequested();
        if(!stillApproved())throw new InvalidOperationException("UM-SHUTDOWN-COUNTDOWN-CANCELLED");
    }
}
