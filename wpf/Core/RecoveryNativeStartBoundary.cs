using System;
using System.Threading;
namespace UntamedManager.Core;
internal static class RecoveryNativeStartBoundary
{
    private static readonly AsyncLocal<Action<Guid>?> Guard=new();
    internal static IDisposable Pin(Action<Guid> verify)
    {
        if(Guard.Value!=null)throw new InvalidOperationException("UM-WATCHDOG-NESTED-LAUNCH");
        Guard.Value=verify;return new Unpin();
    }
    internal static void Demand(Guid mapId)=>Guard.Value?.Invoke(mapId);
    private sealed class Unpin:IDisposable {public void Dispose()=>Guard.Value=null;}
}
