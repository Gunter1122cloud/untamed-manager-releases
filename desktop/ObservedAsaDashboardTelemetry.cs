using System;
using System.Collections.Generic;
using System.Linq;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Read-only WMI evidence projected independently of Manager process ownership.
/// A profileCandidate is NOT an adopted PID, confirmed stop, RCON authorization,
/// or Store-v1 session. Missing/duplicate/unreadable observations stay unverified.
/// </summary>
internal static class ObservedAsaDashboardTelemetry
{
    internal sealed record State(string Code,int? ObservedPid);

    internal static State For(LocalMap map,
        IReadOnlyList<LocalRunningAsaDiscovery.Observation> observations)
    {
        if(!map.ObservedReadOnlyImported)
            return new("notApplicable",null);
        if(map.Id==Guid.Empty || observations==null)
            return new("unverified",null);

        var matches=observations.Where(o=>
            o.ProcessKind=="asaServer" &&
            o.MatchState=="profileCandidate" &&
            o.ObservedReadOnlyImported &&
            o.StartedUtc.HasValue && o.Pid>0 &&
            Guid.TryParse(o.ProfileMapId,out var id) && id==map.Id)
            .ToArray();
        // Two matching PIDs are never a single confirmed observed process.
        // Unreadable WMI, stale, ambiguous or absent identity is never "stopped".
        if(matches.Length!=1)return new("unverified",null);
        return new("observedRunning",matches[0].Pid);
    }
}
