using System;
using System.IO;

namespace UntamedManager.Core;

// Shared launch bookkeeping for direct ASA and Loader starts. A new
// launch never inherits a previous process epoch. The Loader is not the
// game process; the latter must be independently discovered later.
internal static class NativeAsaStartIdentity
{
    internal static ProcessIdentity Record(LocalMap map,AsaApiLaunchPlan plan,
        ProcessIdentity started,bool redactHistoricalArguments)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(started);
        if(started.Pid<=0||started.StartedUtc==default||
           !string.Equals(Path.GetFullPath(started.ExecutablePath),
               Path.GetFullPath(plan.ExecutablePath),StringComparison.OrdinalIgnoreCase)||
           !string.Equals(started.Arguments,plan.Arguments,StringComparison.Ordinal))
            throw new InvalidDataException("UM-ASA-NATIVE-SPAWN-IDENTITY-INVALID");
        var identity=redactHistoricalArguments?new ProcessIdentity{
            Pid=started.Pid,StartedUtc=started.StartedUtc,
            ExecutablePath=started.ExecutablePath,
            Arguments=plan.UseApi?"HISTORICAL_STORE_V1_API_LOADER_START":
                "HISTORICAL_STORE_V1_NATIVE_START"
        }:started;
        map.LastProcess=plan.UseApi?null:identity;
        map.LastLoader=plan.UseApi?identity:null;
        return identity;
    }
}
