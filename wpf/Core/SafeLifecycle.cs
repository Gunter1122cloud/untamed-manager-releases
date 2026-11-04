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
        ct.ThrowIfCancellationRequested();
        var original=session.Check();
        if(!original.Running || original.ConfirmedStopped || original.Identity==null)
            throw new InvalidOperationException("Server identity is not verified");
        bool SameIdentity(ProcessIdentity? current,ProcessIdentity? expected)=>
            current==null?expected==null:expected!=null&&
            current.Pid==expected.Pid&&current.StartedUtc==expected.StartedUtc&&
            string.Equals(current.ExecutablePath,expected.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&
            current.Arguments==expected.Arguments;
        bool SameFamily(ProcessCheck current)=>SameIdentity(current.Identity,original.Identity)&&
            SameIdentity(current.LoaderIdentity,original.LoaderIdentity);
        void VerifyIdentity()
        {
            ct.ThrowIfCancellationRequested();
            var current=session.Check();
            if(!current.Running || current.ConfirmedStopped||!SameFamily(current))
                throw new InvalidOperationException("Server identity changed during stop");
        }
        ct.ThrowIfCancellationRequested();
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
            if(!current.Running||current.ConfirmedStopped||!SameFamily(current))
                throw new InvalidOperationException("Exit cannot be verified");
            await Task.Delay(100,ct);
        }
        if(restart) { ct.ThrowIfCancellationRequested(); session.Start(); }
    }
}

