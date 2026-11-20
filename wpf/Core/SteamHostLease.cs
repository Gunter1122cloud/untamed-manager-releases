using System;
using System.IO;
using System.Threading;

namespace UntamedManager.Core;

// Same kernel object for every Manager profile and every real SteamCMD
// entry point. Explicit scope spans bootstrap, process exit and verification.
// Never acquire recursively: internal helpers are called under the outer lease.
internal sealed class SteamHostLease : IDisposable
{
    private Semaphore? gate;
    private SteamHostLease(Semaphore held)=>gate=held;
    internal static SteamHostLease Acquire()
    {
        var candidate=new Semaphore(1,1,@"Global\Untamed-SteamQueue-Host-v1");
        try
        {
            if(!candidate.WaitOne(0))throw new IOException("UM-STEAM-HOST-BUSY");
            return new SteamHostLease(candidate);
        }
        catch{candidate.Dispose();throw;}
    }
    public void Dispose()
    {
        var held=Interlocked.Exchange(ref gate,null);
        if(held!=null){try{held.Release();}finally{held.Dispose();}}
    }
}
