using System;
using System.Collections.Generic;
using System.Linq;

namespace UntamedManager.Core;

// Watchdog 2.0 evaluates observations only. No Process.Kill, RCON lifecycle,
// automatic restart, remote mutation, or file/Store/SQL access.
public enum WatchdogIssue
{
    ProcessUncertain, UnexpectedStop, LoaderUncertain, RconUnreachable,
    RconAuthentication, SupervisorStale, NetworkUncertain
}
public enum RconSignal { NotConfigured, Responding, Unreachable, AuthenticationFailed }
public sealed record WatchdogEvidence(
    Guid Map,ProcessCheck Process,bool PreviouslyRunning,bool LoaderConfigured,
    bool LoaderPresent,RconSignal Rcon,bool SupervisorFresh,bool NetworkAvailable,
    bool Maintenance,DateTimeOffset ObservedUtc);
public sealed record WatchdogDiagnosis(Guid Map,bool Maintenance,bool NeedsAttention,
    bool ConfirmedRunning,bool ConfirmedStopped,IReadOnlyList<WatchdogIssue> Issues,
    IReadOnlyDictionary<WatchdogIssue,int> FailureCounts,string Recommendation,
    DateTimeOffset ObservedUtc);

public sealed class WatchdogDiagnostics
{
    private readonly Dictionary<(Guid,WatchdogIssue),int> counts=new();
    private readonly Dictionary<Guid,string> lastStatus=new();

    public WatchdogDiagnosis Observe(WatchdogEvidence e,int threshold=3)
    {
        if(e.Map==Guid.Empty)throw new ArgumentException("Local map ID required",nameof(e));
        if(threshold<1 || threshold>20)throw new ArgumentOutOfRangeException(nameof(threshold));
        if(e.Process==null)throw new ArgumentNullException(nameof(e.Process));
        var issues=new List<WatchdogIssue>();
        bool running=e.Process.Running && e.Process.Identity!=null;
        bool stopped=e.Process.ConfirmedStopped && !e.Process.Running;
        if(!e.Maintenance)
        {
            if(!e.SupervisorFresh)issues.Add(WatchdogIssue.SupervisorStale);
            if(!e.NetworkAvailable)issues.Add(WatchdogIssue.NetworkUncertain);
            if(!running && !stopped)issues.Add(WatchdogIssue.ProcessUncertain);
            else if(stopped && e.PreviouslyRunning)issues.Add(WatchdogIssue.UnexpectedStop);
            if(running && e.LoaderConfigured && !e.LoaderPresent)
                issues.Add(WatchdogIssue.LoaderUncertain);
            if(running && e.Rcon==RconSignal.Unreachable)
                issues.Add(WatchdogIssue.RconUnreachable);
            if(running && e.Rcon==RconSignal.AuthenticationFailed)
                issues.Add(WatchdogIssue.RconAuthentication);
        }
        var active=new Dictionary<WatchdogIssue,int>();
        foreach(WatchdogIssue kind in Enum.GetValues<WatchdogIssue>())
        {
            int count=e.Maintenance || !issues.Contains(kind)?0:
                Math.Min(threshold,counts.GetValueOrDefault((e.Map,kind))+1);
            counts[(e.Map,kind)]=count;
            if(count>0)active[kind]=count;
        }
        bool attention=!e.Maintenance && active.Values.Any(x=>x>=threshold);
        string recommendation=e.Maintenance?"Maintenance – automatikus beavatkozás tiltva":
            attention?"Kézi ellenőrzés szükséges; szerverművelet tiltva":
            issues.Count>0?"Ismételt megfigyelés szükséges; még nincs megerősített hiba":
            stopped?"Igazoltan leállítva; automatikus indítás tiltva":
            running?"Fut; nincs helyreállítási művelet":"Állapot nem igazolt";
        lastStatus[e.Map]=string.Join(",",issues.OrderBy(x=>x))+"|"+attention;
        return new(e.Map,e.Maintenance,attention,running,stopped,issues,active,
            recommendation,e.ObservedUtc);
    }

    // For event deduplication; never logs arbitrary process/RCON payloads.
    public static string EventCode(WatchdogDiagnosis diagnosis)
    {
        if(diagnosis.Maintenance)return "watchdog-maintenance";
        if(diagnosis.NeedsAttention)return "watchdog-attention-required";
        return diagnosis.Issues.Count==0?"watchdog-observed-ok":"watchdog-issue-observed";
    }
}
