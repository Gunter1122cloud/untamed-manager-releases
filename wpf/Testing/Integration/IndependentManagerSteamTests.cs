using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class IndependentManagerSteamTests
{
    private static void Verify(bool valid,string description)
    {
        if(!valid)throw new InvalidOperationException("INDEPENDENT MANAGERS: "+description);
        Console.WriteLine("PASS independent Manager Steam queue: "+description);
    }

    private static ProcessCheck Stopped(LocalMap _) =>
        new(false,"Synthetic map is stopped",null,true);

    public static async Task Run(string testRoot)
    {
        var aRoot=Path.Combine(testRoot,"independent-uk-home-01-"+Guid.NewGuid().ToString("N"));
        var bRoot=Path.Combine(testRoot,"independent-uk-home-02-"+Guid.NewGuid().ToString("N"));
        await using var home01=new ClusterService("UK-HOME-01-CI",aRoot,Stopped);
        await using var home02=new ClusterService("UK-HOME-02-CI",bRoot,Stopped);

        var island=new LocalMap{Name="The Island",
            InstallationPath=Path.Combine(aRoot,"island"),GamePort=7777,QueryPort=27015,RconPort=37015};
        var astraeos=new LocalMap{Name="Astraeos",
            InstallationPath=Path.Combine(aRoot,"astraeos"),GamePort=7847,QueryPort=27085,RconPort=37085};
        var ragnarok=new LocalMap{Name="Ragnarok",
            InstallationPath=Path.Combine(bRoot,"ragnarok"),GamePort=7807,QueryPort=27045,RconPort=37045};
        home01.AddMap(island);
        home01.AddMap(astraeos);
        home02.AddMap(ragnarok);

        // A LAN outage must never become a condition of local maintenance.
        Verify(await home01.PollPeerAsync()==null &&
            await home02.PollPeerAsync()==null,
            "both Managers can run while the peer is unconfigured/offline");

        var aQueue=new SteamSerialMaintenanceQueue(home01.Store.Root,home01.Config.LocalHostId);
        var bQueue=new SteamSerialMaintenanceQueue(home02.Store.Root,home02.Config.LocalHostId);
        int touched=0;
        bool localFailure=false;
        try
        {
            await aQueue.ExecuteAsync(new[]{island,astraeos},
                home01.Config.LocalMaps,home01.Check,(_,_)=>{
                    touched++;
                    if(touched==2)throw new IOException("Simulated UK-HOME-01 SteamCMD failure");
                    return Task.CompletedTask;
                });
        }
        catch(IOException){localFailure=true;}
        Verify(localFailure&&touched==2&&
            aQueue.Observe()?.Phase==SteamQueuePhase.NeedsOperator,
            "failure blocks UK-HOME-01's own remaining queue, recorded on that host");

        int home02Actions=0;
        var independent=await bQueue.ExecuteAsync(new[]{ragnarok},
            home02.Config.LocalMaps,home02.Check,(_,_)=>{
                home02Actions++;return Task.CompletedTask;
            });
        Verify(home02Actions==1 && independent.Phase==SteamQueuePhase.Completed &&
            aQueue.Observe()?.Phase==SteamQueuePhase.NeedsOperator,
            "UK-HOME-02's local queue succeeds despite UK-HOME-01 needing operator action");

        bool remoteRejected=false;
        try
        {
            await bQueue.ExecuteAsync(new[]{island},home02.Config.LocalMaps,home02.Check,
                (_,_)=>Task.CompletedTask);
        }
        catch(InvalidDataException){remoteRejected=true;}
        Verify(remoteRejected,"a Manager cannot schedule another machine's map");

        // GET_STATUS preserves remote Steam status viewing without any
        // remote-initiated maintenance controls or operation IDs.
        var snapshot=home01.LocalStatus();
        Verify(snapshot.Maps.Count==2 &&
            snapshot.Maps.Select(x=>x.Name).OrderBy(x=>x).SequenceEqual(
                new[]{"Astraeos","The Island"}) &&
            !typeof(HostStatus).GetProperties().Any(x=>
                x.Name.Contains("Lease",StringComparison.OrdinalIgnoreCase)),
            "LAN host snapshot remains read-only map status with no cross-host lease metadata");

        Verify(home01.Config.LocalMaps.Count==2 && home02.Config.LocalMaps.Count==1 &&
            !Directory.Exists(Path.Combine(aRoot,"steam-lease-isolated")) &&
            !Directory.Exists(Path.Combine(bRoot,"steam-lease-isolated")),
            "independent local maps and configs remain intact; no new cross-host lease storage is created");
    }
}
