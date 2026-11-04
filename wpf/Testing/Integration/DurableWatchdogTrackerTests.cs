using System;
using System.IO;
using UntamedManager.Core;

public static class DurableWatchdogTrackerTests
{
    private static void Check(bool success,string description)
    {
        if(!success)throw new Exception("T17 durable watchdog failed: "+description);
        Console.WriteLine("PASS T17 durable-watchdog "+description);
    }
    public static void Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"t17-persistent-watchdog");
        Guid map=Guid.NewGuid();
        var monitor=new DurableWatchdogTracker(root);
        var time=DateTimeOffset.UtcNow.AddMinutes(-20);
        var identity=new ProcessIdentity{Pid=59000,
            StartedUtc=time.AddMinutes(-20),
            ExecutablePath=@"C:\isolated\ArkAscendedServer.exe",
            Arguments="TheIsland_WP -log"};
        var running=new ProcessCheck(true,"observed",identity);
        var stopped=new ProcessCheck(false,"confirmed exit",null,true);
        var uncertain=new ProcessCheck(false,"unknown process",null,false);
        var initial=monitor.Observe(map,running,RconSignal.Responding,false,time);
        Check(initial.PreviouslyRunning&&!initial.Attention &&
            initial.ConsecutiveProcessFailures==0 &&
            !initial.AutoRecoveryAuthorized,
            "observed running server establishes baseline without authorizing restart");
        var first=monitor.Observe(map,stopped,RconSignal.NotConfigured,false,
            time.AddSeconds(12));
        Check(first.ConsecutiveProcessFailures==1&&!first.Attention,
            "confirmed unexpected stop creates first evidence point");
        var persisted=new DurableWatchdogTracker(root);
        var replay=persisted.Observe(map,stopped,RconSignal.NotConfigured,false,
            time.AddSeconds(12));
        Check(replay.ConsecutiveProcessFailures==1 &&
            persisted.ReadLocal(map)?.ConsecutiveProcessFailures==1,
            "Manager relaunch cannot replay or double-count same observation");
        persisted.Observe(map,stopped,RconSignal.NotConfigured,false,time.AddSeconds(25));
        var attention=persisted.Observe(map,stopped,RconSignal.NotConfigured,false,
            time.AddSeconds(40));
        Check(attention.Attention&&attention.ConsecutiveProcessFailures==3 &&
            !attention.AutoRecoveryAuthorized,
            "three independent confirmed stop observations require operator attention, never process kill");
        var backwards=persisted.Observe(map,uncertain,RconSignal.Unreachable,false,
            time.AddSeconds(20));
        Check(backwards.LastObservationUtc==attention.LastObservationUtc &&
            backwards.ConsecutiveProcessFailures==3,
            "old Supervisor event cannot roll back or raise incident counts");
        var maintenance=persisted.Observe(map,stopped,RconSignal.AuthenticationFailed,true,
            time.AddSeconds(55));
        Check(!maintenance.Attention&&maintenance.Maintenance&&
            maintenance.ConsecutiveProcessFailures==0,
            "maintenance suppresses and resets failure streaks");
        var restored=persisted.Observe(map,running,RconSignal.Unreachable,false,
            time.AddSeconds(70));
        Check(!restored.Attention&&restored.ConsecutiveRconFailures==1 &&
            restored.ConsecutiveProcessFailures==0,
            "RCON problem is counted separately from healthy running process");
        var afterRestart=new DurableWatchdogTracker(root);
        afterRestart.Observe(map,running,RconSignal.Unreachable,false,
            time.AddSeconds(90));
        var auth=afterRestart.Observe(map,running,RconSignal.AuthenticationFailed,false,
            time.AddSeconds(105));
        Check(auth.Attention&&auth.ConsecutiveRconFailures==3,
            "RCON unreachable/authentication streak survives Manager UI restart");
        var healthy=afterRestart.Observe(map,running,RconSignal.Responding,false,
            time.AddSeconds(120));
        Check(!healthy.Attention&&healthy.ConsecutiveRconFailures==0,
            "verified recovery automatically clears attention without sending RCON mutation");
        var afterGap=afterRestart.Observe(map,uncertain,RconSignal.NotConfigured,false,
            time.AddMinutes(12));
        Check(afterGap.ConsecutiveProcessFailures==1&&!afterGap.Attention,
            "monitoring gap cannot fabricate a three-event incident");

        string stateFile=Path.Combine(root,"watchdog-evidence-v1.json");
        var original=File.ReadAllBytes(stateFile);
        File.WriteAllText(stateFile,"broken JSON");
        bool denied=false;
        try{new DurableWatchdogTracker(root).Observe(map,uncertain,
            RconSignal.NotConfigured,false,time.AddMinutes(13));}
        catch(InvalidDataException){denied=true;}
        Check(denied&&File.ReadAllText(stateFile)=="broken JSON",
            "tampered persistent state fails closed and remains available for forensic inspection");
        Check(original.Length>50,"durable JSON was genuinely written to disk");

        Guid foreign=Guid.NewGuid();
        bool futureRejected=false;
        try{new DurableWatchdogTracker(root).Observe(foreign,uncertain,
            RconSignal.NotConfigured,false,DateTimeOffset.UtcNow.AddDays(1));}
        catch(ArgumentException){futureRejected=true;}
        Check(futureRejected,"future-dated events cannot poison persisted observation cursor");
        bool inputRejected=false;
        try{new DurableWatchdogTracker(root).Observe(foreign,null!,
            RconSignal.Responding,false,time.AddMinutes(14));}
        catch(ArgumentException){inputRejected=true;}
        Check(inputRejected,"null process evidence rejected before journal read or mutation");
    }
}


public static class RecurringReadOnlyRecoveryTests
{
    private static void Check(bool ok,string why)
    {
        if(!ok)throw new Exception("T17 recurring scheduler regression: "+why);
        Console.WriteLine("PASS T17 recurring "+why);
    }
    public static void Run(string temp)
    {
        string root=Path.Combine(temp,"t17-recurring-no-catchup");
        var clock=DateTimeOffset.UtcNow.AddHours(-2);
        Guid ownedMap=Guid.NewGuid();
        var job=new RecurringSchedule{
            Map=ownedMap,Kind=OperationalTaskKind.ModCheck,
            Frequency=ScheduleFrequency.IntervalMinutes,
            IntervalMinutes=5,LastDueUtc=clock,Enabled=true
        };
        var permissions=new HashSet<Guid>{ownedMap};
        int checks=0;
        var first=new RecurringReadOnlyRunner(root);
        Check(first.Tick(new[]{job},permissions,clock,
            _=>false,_=>checks++).Length==0&&checks==0,
            "first scheduling never executes a slot retroactively");
        var afterOutage=clock.AddMinutes(60).AddSeconds(1);
        var overdue=first.Tick(new[]{job},permissions,afterOutage,
            _=>false,_=>checks++);
        Check(overdue.Length==1&&
            overdue[0].Status=="missed-no-catchup"&&checks==0,
            "one missed slot recorded after hour-long outage; no process callback");
        var relaunched=new RecurringReadOnlyRunner(root);
        Check(relaunched.Tick(new[]{job},permissions,afterOutage.AddSeconds(1),
            _=>false,_=>checks++).Length==0,
            "restarted scheduler skips all other historical slots");
        var next=OperationalScheduler.Next(job,afterOutage).DueUtc;
        var executed=relaunched.Tick(new[]{job},permissions,next.AddSeconds(1),
            _=>false,_=>checks++);
        Check(checks==1&&executed.Any(x=>x.Status=="succeeded"),
            "only the first fresh future read-only slot is executed");
        Check(new RecurringReadOnlyRunner(root).Tick(new[]{job},permissions,
            next.AddSeconds(2),_=>false,_=>checks++).Length==0&&checks==1,
            "same completed due slot cannot be replayed after UI restart");
        var second=OperationalScheduler.Next(job,next.AddSeconds(1)).DueUtc;
        int bad=0;
        var failing=new RecurringReadOnlyRunner(root).Tick(new[]{job},
            permissions,second.AddSeconds(1),_=>false,
            _=>{bad++;throw new IOException("CI injected read-only failure");});
        Check(bad==1&&failing.Any(x=>x.Status=="failed-not-retried"),
            "a failed read-only callback is durable and not automatically retried");
        Check(new RecurringReadOnlyRunner(root).Tick(new[]{job},permissions,
            second.AddSeconds(2),_=>false,_=>bad++).Length==0 && bad==1,
            "failed read-only callback cannot replay");
        var unsafeSchedule=new RecurringSchedule{
            Map=ownedMap,Kind=OperationalTaskKind.ServerRestart,
            Frequency=ScheduleFrequency.IntervalMinutes,IntervalMinutes=5,
            Enabled=true,LastDueUtc=clock
        };
        int mutations=0;
        var safetyRunner=new RecurringReadOnlyRunner(Path.Combine(temp,
            "t17-recurring-no-writes"));
        safetyRunner.Tick(new[]{unsafeSchedule},permissions,clock,
            _=>false,_=>mutations++);
        var blocked=safetyRunner.Tick(new[]{unsafeSchedule},permissions,
            clock.AddMinutes(5).AddSeconds(1),_=>false,_=>mutations++);
        Check(mutations==0 && blocked.Any(x=>x.Status=="live-operation-disabled"),
            "scheduled server restart is never executed by the read-only dispatcher");
    }
}
