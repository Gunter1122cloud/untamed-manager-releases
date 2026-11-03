using System;
using System.Collections.Generic;

namespace UntamedManager.Core;
public sealed record SupervisionState(Guid Map, int ConsecutiveFailures, bool Maintenance, bool AttentionRequired,
    DateTimeOffset ObservedUtc, string Status);
public sealed class SupervisorPolicy
{
    private readonly Dictionary<Guid,int> failures=new();
    private readonly HashSet<string> emittedSchedules=new();
    public SupervisionState Observe(Guid map,ProcessCheck check,bool maintenance,DateTimeOffset now,int limit=3)
    {
        if(limit<1||limit>100)throw new ArgumentOutOfRangeException(nameof(limit));
        int count=maintenance || check.Running || check.ConfirmedStopped?0:Math.Min(limit,failures.GetValueOrDefault(map)+1);
        failures[map]=count;
        return new(map,count,maintenance,!maintenance&&count>=limit,now,maintenance?"Maintenance; interventions disabled":check.Status);
    }
    // Scheduling currently emits due notifications only; it never starts or stops a process.
    public bool DueNotification(Guid map,DateTimeOffset due,DateTimeOffset now,bool maintenance)
        =>!maintenance && now>=due && emittedSchedules.Add(map.ToString("N")+":"+due.ToUniversalTime().Ticks);
}

public static class StatusFreshness
{
    public static bool IsFresh(DateTimeOffset observed,DateTimeOffset now)=>observed<=now.AddSeconds(5) && observed>=now.AddSeconds(-30);
}
