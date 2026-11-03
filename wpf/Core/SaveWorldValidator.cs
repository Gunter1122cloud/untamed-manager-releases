using System;
using System.Collections.Generic;
using System.Linq;

namespace UntamedManager.Core;

// v1.3.7 synthetic proof format for FakeAsa fixtures only. An RCON success,
// changed timestamp, file size, or generic log line is never sufficient.
public enum SyntheticSaveSignalKind { Started, Completed }
public sealed record SyntheticSaveSignal(Guid Map,string OperationToken,SyntheticSaveSignalKind Kind,
    ProcessIdentity Server,DateTimeOffset Utc);
public sealed record SyntheticSaveResult(bool Proven,string Reason,DateTimeOffset? CompletedUtc);

public static class SaveWorldValidator
{
    public static SyntheticSaveResult VerifySynthetic(Guid ownedMap,ProcessIdentity expected,
        string operationToken,DateTimeOffset commandUtc,DateTimeOffset now,
        IEnumerable<SyntheticSaveSignal> observations)
    {
        if(ownedMap==Guid.Empty || expected==null || expected.Pid<=0 ||
           string.IsNullOrWhiteSpace(operationToken) || operationToken.Length>128)
            throw new ArgumentException("Explicit owned map, process and test token required");
        if(observations==null)throw new ArgumentNullException(nameof(observations));
        bool SameProcess(ProcessIdentity p)=>p!=null && p.Pid==expected.Pid &&
            p.StartedUtc==expected.StartedUtc &&
            string.Equals(p.ExecutablePath,expected.ExecutablePath,StringComparison.OrdinalIgnoreCase) &&
            p.Arguments==expected.Arguments;
        if(now<commandUtc || now-commandUtc>TimeSpan.FromMinutes(3))
            return new(false,"Synthetic proof expired or time reversed",null);
        var signals=observations.Where(x=>x.OperationToken==operationToken).ToArray();
        if(signals.Any(x=>x.Map!=ownedMap))
            return new(false,"Synthetic save evidence belongs to a different map",null);
        if(signals.Length!=2 || signals.Count(x=>x.Kind==SyntheticSaveSignalKind.Started)!=1 ||
           signals.Count(x=>x.Kind==SyntheticSaveSignalKind.Completed)!=1)
            return new(false,"Expected exactly one correlated start and completion",null);
        var start=signals.Single(x=>x.Kind==SyntheticSaveSignalKind.Started);
        var end=signals.Single(x=>x.Kind==SyntheticSaveSignalKind.Completed);
        if(!SameProcess(start.Server)||!SameProcess(end.Server))
            return new(false,"Synthetic server process identity changed",null);
        if(start.Utc<commandUtc || end.Utc<=start.Utc || end.Utc>now ||
           end.Utc-start.Utc>TimeSpan.FromMinutes(2))
            return new(false,"Synthetic save signals are stale, reversed or out of window",null);
        return new(true,"FakeAsa synthetic completion correlated to the exact process",end.Utc);
    }

    // Intentionally fail closed. A real ASA adapter requires verified authoritative
    // semantics and server tests; this method must not accept generic RCON/log cues.
    public static SyntheticSaveResult VerifyLiveUnavailable(string? rconReply,DateTimeOffset? fileModifiedUtc)
        => new(false,"Real ASA SaveWorld completion adapter not validated",null);
}
