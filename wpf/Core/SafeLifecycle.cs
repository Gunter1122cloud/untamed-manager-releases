using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public interface ILocalServerSession
{
    ProcessCheck Check();
    Task Command(string command, CancellationToken ct);
    Task<bool> SaveCompleted(CancellationToken ct);
    void Start();
}

// No kill path. A response to SaveWorld is deliberately insufficient evidence.
public static class SafeLifecycle
{
    public static async Task Stop(ILocalServerSession session, bool restart, TimeSpan timeout,
        CancellationToken cancellationToken=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct=deadline.Token;
        var original=session.Check();
        if(!original.Running || original.Identity==null)
            throw new InvalidOperationException("Server identity is not verified");
        void VerifyIdentity()
        {
            ct.ThrowIfCancellationRequested();
            var current=session.Check();
            if(!current.Running || current.Identity==null ||
               current.Identity.Pid!=original.Identity.Pid ||
               current.Identity.StartedUtc!=original.Identity.StartedUtc ||
               current.Identity.ExecutablePath!=original.Identity.ExecutablePath ||
               current.Identity.Arguments!=original.Identity.Arguments)
                throw new InvalidOperationException("Server identity changed during stop");
        }
        await session.Command("ServerChat Server maintenance: saving world before shutdown",ct);
        VerifyIdentity();
        await session.Command("SaveWorld",ct);
        if(!await session.SaveCompleted(ct))
            throw new InvalidOperationException("Save completion is unproven; DoExit and restart blocked");
        VerifyIdentity();
        await session.Command("DoExit",ct);
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var current=session.Check();
            if(current.ConfirmedStopped)break;
            if(!current.Running || current.Identity?.Pid!=original.Identity.Pid ||
                current.Identity.StartedUtc!=original.Identity.StartedUtc)
                throw new InvalidOperationException("Exit cannot be verified");
            await Task.Delay(100,ct);
        }
        if(restart) { ct.ThrowIfCancellationRequested(); session.Start(); }
    }
}
