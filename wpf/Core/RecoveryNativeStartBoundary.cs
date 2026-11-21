using System;
using System.Threading;
namespace UntamedManager.Core;
internal static class RecoveryNativeStartBoundary
{
    private sealed record Scope(Action<Guid> Verify,Guid? Claim);
    private static readonly AsyncLocal<Scope?> Guard=new();
    internal static Guid? Claim=>Guard.Value?.Claim;
    internal static IDisposable Pin(Action<Guid> verify,Guid? claim=null)
    {
        if(Guard.Value!=null)throw new InvalidOperationException("UM-WATCHDOG-NESTED-LAUNCH");
        Guard.Value=new Scope(verify,claim);return new Unpin();
    }
    internal static void Demand(Guid mapId)=>Guard.Value?.Verify(mapId);
    private sealed class Unpin:IDisposable {public void Dispose()=>Guard.Value=null;}
}
