using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Every 15 seconds, inspect ALL local ASA and ASA API Loader processes
/// through read-only WMI. Works while Obsidian is minimized. Unknown processes
/// remain visible, but are NEVER granted lifecycle, RCON or Store write rights.
/// </summary>
public sealed class DesktopAutomaticAsaDiscoverySupervisor : IDisposable
{
    public sealed record State(DateTimeOffset? ObservedUtc,string Status,
        LocalRunningAsaDiscovery.Observation[] Processes,bool Fresh);
    private readonly Func<IReadOnlyList<LocalRunningAsaDiscovery.Sample>> read;
    private readonly Func<LocalMap[]> maps;
    private readonly object gate=new();
    private readonly CancellationTokenSource cancel=new();
    private Task? loop;
    private int started,disposed;
    private State state=new(null,"not-started",
        Array.Empty<LocalRunningAsaDiscovery.Observation>(),false);

    public DesktopAutomaticAsaDiscoverySupervisor(ClusterService cluster):
        this(LocalRunningAsaDiscovery.ReadSamples,
            ()=>cluster.Config.LocalMaps.ToArray()) { }

    internal DesktopAutomaticAsaDiscoverySupervisor(
        Func<IReadOnlyList<LocalRunningAsaDiscovery.Sample>> read,
        Func<LocalMap[]> maps)
    {
        this.read=read??throw new ArgumentNullException(nameof(read));
        this.maps=maps??throw new ArgumentNullException(nameof(maps));
    }

    public State Snapshot()
    {
        lock(gate)
        {
            bool fresh=state.ObservedUtc is DateTimeOffset seen &&
                DateTimeOffset.UtcNow-seen<TimeSpan.FromSeconds(45) &&
                state.Status=="ok";
            return fresh?state with {Fresh=true}:
                new(state.ObservedUtc,
                    state.Status=="ok"?"stale":state.Status,
                    Array.Empty<LocalRunningAsaDiscovery.Observation>(),false);
        }
    }

    public State RefreshOnce()
    {
        if(Volatile.Read(ref disposed)!=0)
            throw new ObjectDisposedException(nameof(DesktopAutomaticAsaDiscoverySupervisor));
        try
        {
            var observed=LocalRunningAsaDiscovery.Classify(read(),maps()).ToArray();
            var next=new State(DateTimeOffset.UtcNow,"ok",observed,true);
            lock(gate)state=next;
            return next;
        }
        catch(Exception)
        {
            var unavailable=new State(DateTimeOffset.UtcNow,"wmi-unavailable",
                Array.Empty<LocalRunningAsaDiscovery.Observation>(),false);
            lock(gate)state=unavailable;
            return unavailable;
        }
    }

    public void Start()
    {
        if(Volatile.Read(ref disposed)!=0)
            throw new ObjectDisposedException(nameof(DesktopAutomaticAsaDiscoverySupervisor));
        if(Interlocked.Exchange(ref started,1)!=0)return;
        var token=cancel.Token;
        loop=Task.Run(async()=>{
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                do
                {
                    if(token.IsCancellationRequested)break;
                    RefreshOnce();
                }while(await timer.WaitForNextTickAsync(token));
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested)
            { /* Manager closed; never touch the running ASA processes */ }
        });
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        cancel.Cancel();
        // Do not block the UI waiting on a potentially stalled WMI provider.
        cancel.Dispose();
    }
}
