using System;
using System.Collections.Generic;
using System.Linq;

namespace UntamedManager.Core;

public sealed record ObservedProcess(int Pid, int ParentPid, DateTimeOffset StartedUtc,
    string ExecutablePath, string Arguments);
public static class ProcessFamily
{
    // Windows may no longer report the original ParentProcessId after the loader exits.
    // Reattach for observation only when BOTH identities came from an earlier verified
    // parent/child pair and the live child matches the persisted identity exactly.
    // Never use this to infer a stopped process, launch a replacement, or grant writes.
    public static bool IsPersistedSurvivor(ObservedProcess observedChild,
        ProcessIdentity? verifiedChild,ProcessIdentity? verifiedLoader,
        string expectedServerPath,string expectedServerArgs,
        string expectedLoaderPath,string expectedLoaderArgs)
    {
        if(verifiedChild==null || verifiedLoader==null)return false;
        return verifiedChild.Pid>0 && verifiedLoader.Pid>0 &&
            verifiedChild.Pid!=verifiedLoader.Pid &&
            verifiedLoader.StartedUtc<=verifiedChild.StartedUtc &&
            observedChild.Pid==verifiedChild.Pid &&
            observedChild.StartedUtc==verifiedChild.StartedUtc &&
            StringComparer.OrdinalIgnoreCase.Equals(observedChild.ExecutablePath,verifiedChild.ExecutablePath) &&
            StringComparer.OrdinalIgnoreCase.Equals(observedChild.ExecutablePath,expectedServerPath) &&
            StringComparer.OrdinalIgnoreCase.Equals(verifiedLoader.ExecutablePath,expectedLoaderPath) &&
            observedChild.Arguments==verifiedChild.Arguments &&
            observedChild.Arguments==expectedServerArgs &&
            verifiedLoader.Arguments==expectedLoaderArgs;
    }

    // Pure validation of a complete observation. Missing facts never imply a stopped server.
    public static bool IsVerifiedPair(IReadOnlyList<ObservedProcess> snapshot,
        ProcessIdentity loader, ProcessIdentity server)
    {
        bool Match(ObservedProcess p,ProcessIdentity expected)=>p.Pid==expected.Pid &&
            p.StartedUtc==expected.StartedUtc &&
            StringComparer.OrdinalIgnoreCase.Equals(p.ExecutablePath,expected.ExecutablePath) &&
            p.Arguments==expected.Arguments;
        var parents=snapshot.Where(x=>Match(x,loader)).ToArray();
        var children=snapshot.Where(x=>Match(x,server)).ToArray();
        if(parents.Length!=1 || children.Length!=1 || loader.Pid==server.Pid)return false;
        return children[0].ParentPid==loader.Pid && children[0].StartedUtc>=parents[0].StartedUtc &&
            snapshot.Count(x=>x.ParentPid==loader.Pid &&
                StringComparer.OrdinalIgnoreCase.Equals(x.ExecutablePath,server.ExecutablePath))==1;
    }
}
