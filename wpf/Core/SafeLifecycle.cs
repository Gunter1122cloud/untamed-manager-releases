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
        ArgumentNullException.ThrowIfNull(session);
        if(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct=deadline.Token;
        var original=session.Check();
        if(!original.Running || original.Identity==null)
            throw new InvalidOperationException("Server identity is not verified");
        bool Same(ProcessIdentity? current)=>current!=null&&
            current.Pid==original.Identity.Pid&&current.StartedUtc==original.Identity.StartedUtc&&
            string.Equals(current.ExecutablePath,original.Identity.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&
            current.Arguments==original.Identity.Arguments;
        void VerifyIdentity()
        {
            ct.ThrowIfCancellationRequested();
            var current=session.Check();
            if(!current.Running || current.ConfirmedStopped||!Same(current.Identity))
                throw new InvalidOperationException("Server identity changed during stop");
        }
        await session.Command("ServerChat Server maintenance: saving world before shutdown",ct).WaitAsync(ct);
        VerifyIdentity();
        await session.Command("SaveWorld",ct).WaitAsync(ct);
        if(!await session.SaveCompleted(ct).WaitAsync(ct))
            throw new InvalidOperationException("Save completion is unproven; DoExit and restart blocked");
        VerifyIdentity();
        await session.Command("DoExit",ct).WaitAsync(ct);
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var current=session.Check();
            if(current.ConfirmedStopped&&!current.Running&&current.Identity==null&&current.LoaderIdentity==null)break;
            if(!current.Running||current.ConfirmedStopped||!Same(current.Identity))
                throw new InvalidOperationException("Exit cannot be verified");
            await Task.Delay(100,ct);
        }
        if(restart) { ct.ThrowIfCancellationRequested(); session.Start(); }
    }
}

