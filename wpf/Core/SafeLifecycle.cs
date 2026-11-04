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

// T04 real-session contract: after restart, process verification alone does
// not mean API, RCON and client-session advertisement have completed.
public interface IVerifiedRestartSession : ILocalServerSession
{
    Guid MapId { get; }
    Task<AsaReadinessSnapshot> ProbeReadiness(CancellationToken ct);
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

    // Strong restart path for certified adapters. The legacy Stop(...,restart)
    // entry point remains for isolated FakeAsa regression compatibility only.
    // A production adapter must provide exact per-map API/RCON/join readiness;
    // a raw process-start or ListPlayers result never passes this method.
    public static async Task<AsaReadinessSnapshot> RestartVerified(
        IVerifiedRestartSession session,TimeSpan timeout,
        CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if(session.MapId==Guid.Empty||timeout<=TimeSpan.Zero||
           timeout>TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct=deadline.Token;
        ct.ThrowIfCancellationRequested();
        var before=session.Check();
        if(!before.Running||before.ConfirmedStopped||before.Identity==null)
            throw new InvalidOperationException("Original server process is not verified");
        await Stop(session,false,timeout,ct);
        var stopped=session.Check();
        if(!stopped.ConfirmedStopped||stopped.Running||
           stopped.Identity!=null||stopped.LoaderIdentity!=null)
            throw new InvalidOperationException("Restart denied: original process did not exit");
        ct.ThrowIfCancellationRequested();
        DateTimeOffset startedRequest=DateTimeOffset.UtcNow;
        session.Start();
        ProcessIdentity? initialReplacement=null;

        static bool Same(ProcessIdentity? x,ProcessIdentity? y)=>
            x==null?y==null:y!=null&&x.Pid==y.Pid&&x.StartedUtc==y.StartedUtc&&
                string.Equals(x.ExecutablePath,y.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&
                string.Equals(x.Arguments,y.Arguments,StringComparison.Ordinal);
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var current=session.Check();
            var child=current.Identity;
            if(current.Running&&!current.ConfirmedStopped&&child!=null&&
               child.Pid>0&&child.StartedUtc>before.Identity.StartedUtc&&
               !Same(child,before.Identity)&&
               string.Equals(child.ExecutablePath,before.Identity.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&
               string.Equals(child.Arguments,before.Identity.Arguments,StringComparison.Ordinal))
            {
                if(initialReplacement!=null && !Same(initialReplacement,child))
                    throw new InvalidOperationException("Restart replacement process changed during readiness");
                initialReplacement ??= child;
                var proof=await session.ProbeReadiness(ct).WaitAsync(ct);
                var after=session.Check();
                if(after.Running&&!after.ConfirmedStopped&&
                   Same(child,after.Identity)&&Same(current.LoaderIdentity,after.LoaderIdentity)&&
                   proof.MapId==session.MapId&&proof.ProductionReady&&
                   proof.ObservedUtc>=startedRequest&&
                   proof.ObservedUtc<=DateTimeOffset.UtcNow.AddSeconds(2))
                    return proof;
            }
            // No additional Start attempt, DoExit, Kill or forced restart.
            await Task.Delay(200,ct);
        }
    }
}

