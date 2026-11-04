using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

// T17: durable, per-local-map watchdog evidence. No server lifecycle actions.
// A restart, late heartbeat, duplicate observation, maintenance interval or
// clock reversal cannot turn uncertain process states into an auto-start.
public sealed record DurableWatchdogState(Guid Map,DateTimeOffset LastObservationUtc,
    int ConsecutiveProcessFailures,int ConsecutiveRconFailures,
    bool PreviouslyRunning,bool Attention,bool Maintenance,string Reason)
{
    public bool AutoRecoveryAuthorized=>false;
}

public sealed class DurableWatchdogTracker
{
    private sealed record Store(int Schema,List<DurableWatchdogState> Maps);
    private readonly string file,gateName;
    private const int MaxMaps=100,MaxBytes=1024*1024;
    public DurableWatchdogTracker(string profileRoot)
    {
        if(string.IsNullOrWhiteSpace(profileRoot)||!Path.IsPathFullyQualified(profileRoot)||
           profileRoot.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("Local Manager watchdog profile required");
        string root=Path.GetFullPath(profileRoot);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Watchdog profile must be on a local fixed disk");
        for(var dir=new DirectoryInfo(root);dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked watchdog profile directory refused");
        Directory.CreateDirectory(root);
        file=Path.Combine(root,"watchdog-evidence-v1.json");
        gateName=@"Local\Untamed-WatchdogEvidence-"+Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())));
    }

    private T Guard<T>(Func<T> work)
    {
        using var mutex=new Mutex(false,gateName);bool held=false;
        try
        {
            try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}
            catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("Watchdog evidence is busy");
            return work();
        }
        finally{if(held)mutex.ReleaseMutex();}
    }
    private Store Read()
    {
        if(!File.Exists(file))return new(1,new());
        var info=new FileInfo(file);
        if(info.Length<1||info.Length>MaxBytes||
           (info.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Unsafe watchdog evidence journal");
        Store? data;
        try{data=JsonSerializer.Deserialize<Store>(File.ReadAllText(file));}
        catch(JsonException){throw new InvalidDataException("Damaged watchdog evidence journal");}
        if(data==null||data.Schema!=1||data.Maps==null||
           data.Maps.Count>MaxMaps||data.Maps.Any(x=>x==null||x.Map==Guid.Empty||
               x.ConsecutiveProcessFailures is <0 or >20||
               x.ConsecutiveRconFailures is <0 or >20)||
           data.Maps.Select(x=>x.Map).Distinct().Count()!=data.Maps.Count)
            throw new InvalidDataException("Untrusted watchdog journal state");
        return data;
    }
    private void Save(Store data)
    {
        string temp=file+"."+Guid.NewGuid().ToString("N")+".pending";
        try
        {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(output,data);output.Flush(true);}
            File.Move(temp,file,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    public DurableWatchdogState Observe(Guid map,ProcessCheck check,RconSignal rcon,
        bool maintenance,DateTimeOffset observedUtc,int threshold=3)
    {
        if(map==Guid.Empty||check==null||!Enum.IsDefined(rcon)||
           observedUtc==default||threshold is <1 or >20)
            throw new ArgumentException("Validated map/process/RCON evidence required");
        return Guard(()=>{
            var store=Read();
            var prior=store.Maps.SingleOrDefault(x=>x.Map==map);
            // UTC timestamp is a monotonic observation identity; never double
            // count the same Supervisor event after UI restarts.
            if(prior!=null && observedUtc<=prior.LastObservationUtc)
                return prior;
            if(prior==null && store.Maps.Count>=MaxMaps)
                throw new InvalidDataException("Watchdog map journal capacity exceeded");
            bool trustedRunning=check.Running&&check.Identity!=null &&
                check.Identity.Pid>0&&check.Identity.StartedUtc<=observedUtc&&
                !check.ConfirmedStopped;
            bool trustedStopped=check.ConfirmedStopped&&!check.Running &&
                check.Identity==null&&check.LoaderIdentity==null;
            bool previouslyRunning=prior?.PreviouslyRunning??false;
            bool gap=prior==null || observedUtc-prior.LastObservationUtc>TimeSpan.FromMinutes(5);
            // The first observation following a long outage is not a streak.
            // A confirmed stop after an earlier running state still warrants
            // investigation but must accrue new evidence before attention.
            bool processProblem=!trustedRunning &&
                (!trustedStopped || previouslyRunning);
            bool rconProblem=trustedRunning &&
                rcon is RconSignal.Unreachable or RconSignal.AuthenticationFailed;
            int processCount=maintenance||!processProblem?0:
                Math.Min(threshold,(gap?0:prior?.ConsecutiveProcessFailures??0)+1);
            int rconCount=maintenance||!rconProblem?0:
                Math.Min(threshold,(gap?0:prior?.ConsecutiveRconFailures??0)+1);
            bool attention=!maintenance &&
                (processCount>=threshold||rconCount>=threshold);
            string reason=maintenance?"maintenance-suppressed":
                attention?"operator-review-required":
                processProblem?"process-observation-uncertain":
                rconProblem?"rcon-observation-uncertain":
                trustedRunning?"running-observed":
                trustedStopped?"stopped-observed":"unverified";
            var current=new DurableWatchdogState(map,observedUtc,processCount,
                rconCount,trustedRunning || (!maintenance&&previouslyRunning),
                attention,maintenance,reason);
            if(prior!=null)store.Maps.Remove(prior);
            store.Maps.Add(current);
            Save(store);
            return current;
        });
    }

    public DurableWatchdogState? ReadLocal(Guid map)
    {
        if(map==Guid.Empty)throw new ArgumentException("Map required");
        return Guard(()=>Read().Maps.SingleOrDefault(x=>x.Map==map));
    }
}
