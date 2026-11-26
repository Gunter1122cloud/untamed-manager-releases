// Portions adapted from DiFuks/ark-manager (MIT), commit
// 63ab649f12efdebd036a8474d8aa1cb676fc5aae.
// Copyright (c) 2026 Dmitry Fuks
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public sealed record ObservedProcess(int Pid, int ParentPid, DateTimeOffset StartedUtc,
    string ExecutablePath, string Arguments);
public static class ProcessFamily
{
    // Adapted from DiFuks/ark-manager ServerManager.WaitForExitAsync
    // (63ab649f12efdebd036a8474d8aa1cb676fc5aae, MIT).
    // The caller owns the deadline. Unlike a PID-only check, an absent
    // member of a draining Loader/ASA family is not a replacement.
    internal static async Task WaitForOwnedExitAsync(
        Func<ProcessCheck> observeStopped,Func<ProcessCheck>? observeLive,
        ProcessIdentity expectedAsa,ProcessIdentity? expectedLoader,
        Func<ProcessIdentity?,ProcessIdentity?,bool> same,
        CancellationToken ct,TimeSpan? pollInterval=null)
    {
        void RejectReplacement(ProcessCheck check)
        {
            if((check.Identity!=null&&!same(check.Identity,expectedAsa))||
               (check.LoaderIdentity!=null&&
                (expectedLoader==null||!same(check.LoaderIdentity,expectedLoader))))
                throw new InvalidOperationException("UM-ASA-STOP-REPLACEMENT-FAMILY");
        }
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var check=observeStopped();
            ct.ThrowIfCancellationRequested();
            RejectReplacement(check);
            if(check.ConfirmedStopped&&!check.Running&&
               check.Identity==null&&check.LoaderIdentity==null)
                return;
            if(observeLive!=null)RejectReplacement(observeLive());
            await Task.Delay(pollInterval??TimeSpan.FromMilliseconds(300),ct);
        }
    }

    // Read-only classifier shared by actual Windows inspection and tests.
    // A configured-but-unused API Loader cannot prevent direct ASA from
    // being recognized. Conversely a previously verified Loader child
    // must NEVER be silently treated as direct if its parent has exited.
    public static bool IsVerifiedDirectServer(ObservedProcess candidate,
        ProcessIdentity? lastServer,ProcessIdentity? lastLoader,
        bool startWithApi,string serverPath,string serverArguments)
    {
        if(startWithApi || lastLoader!=null || candidate.Pid<=0 ||
           candidate.StartedUtc==default ||
           !StringComparer.OrdinalIgnoreCase.Equals(candidate.ExecutablePath,serverPath) ||
           !StringComparer.Ordinal.Equals(candidate.Arguments,serverArguments))
            return false;
        // PID reuse must not inherit previously granted process ownership.
        return lastServer==null || lastServer.Pid!=candidate.Pid ||
            lastServer.StartedUtc==candidate.StartedUtc &&
            StringComparer.OrdinalIgnoreCase.Equals(lastServer.ExecutablePath,candidate.ExecutablePath) &&
            StringComparer.Ordinal.Equals(lastServer.Arguments,candidate.Arguments);
    }

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
