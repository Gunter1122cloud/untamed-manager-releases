using System;
using System.IO;
using UntamedManager.Core;
using System.Threading;
using System.Threading.Tasks;
using UntamedDesktop;

// No network or ASA is started. The fixture feeds exact current-startup
// evidence directly to the production polling state machine.
internal static class RestartStartupReadinessTests
{
    private static void Check(bool value,string label)
    {
        if(!value)throw new InvalidOperationException(
            "RESTART STARTUP READINESS: "+label);
        Console.WriteLine("PASS restart readiness: "+label);
    }
    private static RestartStartupProof Proof(bool loader,bool asa,bool dll,
        bool plugins,bool rcon,bool join,string reason="WAITING")=>
        new(42887,DateTimeOffset.UtcNow,loader,asa,dll,plugins,rcon,join,reason);

    public static async Task Run(string root)
    {
        // Exact production host-lane implementation. A retry delay releases
        // only the host dispatch lane, not its durable per-map claim.
        using(var gate=new SemaphoreSlim(1,1))
        using(var astra=await ScheduledAsaDispatchLane.EnterAsync(gate,CancellationToken.None))
        {
            var delayDone=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var backoff=astra.BackoffAsync(_=>delayDone.Task,CancellationToken.None);
            using(var island=await ScheduledAsaDispatchLane.EnterAsync(gate,CancellationToken.None))
            {
                delayDone.SetResult(true);
                Check(!backoff.IsCompleted&&gate.CurrentCount==0,
                    "another map can run during Astra backoff but retry must wait for host lane");
            }
            await backoff.WaitAsync(TimeSpan.FromSeconds(5));
            Check(gate.CurrentCount==0,
                "retry reacquires the serialized host lane before another Start");
        }
        using(var gate=new SemaphoreSlim(1,1))
        using(var cancel=new CancellationTokenSource())
        using(var lane=await ScheduledAsaDispatchLane.EnterAsync(gate,CancellationToken.None))
        {
            var backoff=lane.BackoffAsync(token=>Task.Delay(Timeout.Infinite,token),cancel.Token);
            cancel.Cancel();
            try{await backoff;throw new InvalidOperationException("retry cancellation ignored");}
            catch(OperationCanceledException){}
            Check(gate.CurrentCount==1,"cancelled backoff leaves host lane available");
        }
        // Durable retry authorization belongs only to the original scheduled
        // slot. The test reopens the journal rather than sharing object state.
        string install=Path.Combine(root,"scheduled-startup-retry-journal");
        string journals=Path.Combine(root,"scheduled-startup-retry-receipts");
        Directory.CreateDirectory(install);
        var journal=new AsaManualRestartJournal(install,journals);
        Guid map=Guid.NewGuid(),run=Guid.NewGuid();
        var claim=journal.BeginScheduled(map,10001,run);
        journal.Change(claim.Id,AsaRestartPhase.Starting);
        journal.RecordNewAsaPid(claim.Id,20001);
        bool replayDenied=false;
        try{journal.DemandAuthorizedStart(claim.Id);}
        catch(InvalidOperationException){replayDenied=true;}
        Check(replayDenied,"a recorded Start PID consumes the old launch authority");
        bool Refused(Action action)
        {
            try{action();return false;}
            catch(InvalidOperationException){return true;}
        }
        Check(Refused(()=>journal.ClaimScheduledRetry(claim.Id,run,1,()=>false)),
            "occupied port, orphan loader or manually launched family blocks retry");
        Check(Refused(()=>journal.ClaimScheduledRetry(claim.Id,Guid.NewGuid(),1,()=>true)),
            "unrelated schedule slot cannot retry");
        Check(ScheduledRestartRetryScope.DelayAfter(1)==TimeSpan.FromMinutes(1)&&
              ScheduledRestartRetryScope.DelayAfter(2)==TimeSpan.FromMinutes(3)&&
              ScheduledRestartRetryScope.DelayAfter(3)==TimeSpan.FromMinutes(5),
            "three bounded retry intervals are 1/3/5 minutes");
        Check(!ScheduledRestartRetryScope.CanRetry(1,RestartModServiceFailureProbe.RequiredModsFailed)&&
              !ScheduledRestartRetryScope.CanRetry(4,RestartModServiceFailureProbe.TransientCurseForge),
            "nontransient mod errors and exhausted attempts cannot retry");
        for(int attempt=1;attempt<=3;attempt++)
        {
            var reserved=journal.ClaimScheduledRetry(claim.Id,run,attempt,()=>true);
            Check(reserved.StartupAttempt==attempt+1&&reserved.NewPid==0,
                "retry claim persisted before dispatch");
            var reopened=new AsaManualRestartJournal(install,journals);
            Check(reopened.Observe()?.StartupAttempt==attempt+1&&
                  Refused(()=>reopened.ClaimScheduledRetry(claim.Id,run,attempt,()=>true)),
                "reopened Manager cannot claim an already consumed attempt");
            journal.RecordNewAsaPid(claim.Id,20001+attempt);
        }
        Check(Refused(()=>journal.ClaimScheduledRetry(claim.Id,run,4,()=>true)),
            "four total starts maximum, no infinite restart loop");
        journal.Change(claim.Id,AsaRestartPhase.NeedsOperator);
        Check(journal.Observe()?.StartupAttempt==4&&
              Directory.GetFiles(journals,"*.failed.json").Length==3,
            "failed attempts preserved after retry exhaustion");

        var pidOnly=Proof(true,true,false,false,false,false);
        Check(!pidOnly.Ready(true)&&!pidOnly.Ready(false),
            "an ASA child PID alone never certifies restart");
        Check(!Proof(true,true,true,false,true,true).Ready(true),
            "API plugin loading is independently required");
        Check(!Proof(true,true,false,true,true,true).Ready(true),
            "AsaApi.dll evidence is independently required");
        Check(!Proof(true,true,true,true,false,true).Ready(true),
            "late RCON is not readiness");
        Check(!Proof(true,true,true,true,true,false).Ready(true),
            "no advertising-for-join means not ready");
        Check(!Proof(false,true,true,true,true,true).Ready(true),
            "orphaned Loader never certifies API mode");
        // A failure from yesterday's ASA run must not be mistaken for
        // this new restart; the parser is pinned to the latest log-open.
        DateTimeOffset today=DateTimeOffset.UtcNow;
        string Open(DateTimeOffset at)=>"["+at.UtcDateTime.ToString("yyyy.MM.dd-HH.mm.ss")+
            ":000] Log file open";
        var modFailure=new[]{Open(today),
            "[ASA] Error querying server mods: ApiError: Failed (serverUnreachable)",
            "[ASA] Requested mods failed to load on server."};
        Check(RestartModServiceFailureProbe.Classify(modFailure,today)==
            RestartModServiceFailureProbe.TransientCurseForge,
            "current-run CurseForge serverUnreachable is classed as transient");
        Check(RestartModServiceFailureProbe.Classify(modFailure,today.AddMinutes(1))==null,
            "the previous one-minute retry log cannot authorize another Start");
        Check(RestartModServiceFailureProbe.Classify(modFailure,
            today.AddHours(-3))==null,
            "stale CurseForge mod failure cannot authorize another restart");
        Check(RestartModServiceFailureProbe.Classify(new[]{Open(today),
            "Requested mods failed to load on server."},today)==
            RestartModServiceFailureProbe.RequiredModsFailed,
            "non-network mod failure stays distinct and is not transient");
        int attempts=0;
        var late=await RestartStartupReadiness.WaitAsync(_=>
        {
            attempts++;
            return Task.FromResult(attempts<3?
                Proof(true,true,true,true,false,true):
                Proof(true,true,true,true,true,true,"UM-RESTART-READY"));
        },true,TimeSpan.FromSeconds(3),TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Check(late.Ready(true)&&attempts==3,
            "late authenticated RCON becomes ready without a second Start");
        attempts=0;
        var earlyExit=await RestartStartupReadiness.WaitAsync(_=>
        {
            attempts++;
            return Task.FromResult(attempts==1?pidOnly:
                Proof(true,false,false,false,false,false,
                    "UM-RESTART-NEW-ASA-EXITED-OR-REPLACED"));
        },true,TimeSpan.FromSeconds(3),TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Check(!earlyExit.Ready(true)&&attempts==2&&
              earlyExit.Reason=="UM-RESTART-NEW-ASA-EXITED-OR-REPLACED",
            "ASA exit before join is terminal, not an automatic restart");
        attempts=0;
        var orphan=await RestartStartupReadiness.WaitAsync(_=>
        {
            attempts++;
            return Task.FromResult(Proof(false,true,false,false,false,false,
                "UM-RESTART-API-LOADER-EXITED-OR-REPLACED"));
        },true,TimeSpan.FromSeconds(3),TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Check(!orphan.Ready(true)&&attempts==1,
            "orphan loader is immediately terminal for API restart");
        Check(Proof(false,true,true,true,true,true,"UM-RESTART-READY").Ready(false),
            "direct ASA mode requires join and RCON but not an API loader");
    }
}
