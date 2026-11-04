using System.Diagnostics;
using UntamedManager.Core;

public static class SchedulerConcurrencyTests
{
    static readonly Guid ChildMap=Guid.Parse("99999999-1111-2222-3333-444444444444");
    static ScheduledTask ChildJob()=>new(){Id=Guid.Parse("99999999-5555-6666-7777-888888888888"),
        Map=ChildMap,Kind=ScheduledKind.Notification,DueUtc=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero)};
    static void Check(bool value,string reason)
    {if(!value)throw new Exception("Scheduler concurrency: "+reason);Console.WriteLine("PASS scheduler-claim "+reason);}
    public static async Task Hold(string root,string ready)
    {
        var job=ChildJob();
        await new PersistentScheduler(root).RunDue(new[]{job},new HashSet<Guid>{ChildMap},job.DueUtc,
            async _=>{File.WriteAllText(ready,"claimed");await Task.Delay(Timeout.Infinite);});
    }
    public static async Task Run(string root)
    {
        string state=Path.Combine(root,"scheduler-concurrent");Guid map=Guid.NewGuid();var owned=new HashSet<Guid>{map};
        var first=new PersistentScheduler(state);var stale=new PersistentScheduler(state);
        var due=DateTimeOffset.UtcNow;var job=new ScheduledTask{Map=map,Kind=ScheduledKind.Notification,DueUtc=due};
        int executed=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running=first.RunDue(new[]{job},owned,due,async task=>{
            Interlocked.Increment(ref executed);entered.SetResult();await release.Task;
            task.Id=Guid.NewGuid();task.DueUtc=task.DueUtc.AddDays(1);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try{await stale.RunDue(new[]{job},owned,due,_=>{Interlocked.Increment(ref executed);return Task.CompletedTask;});}
        finally{release.SetResult();}
        await running;
        Check(executed==1&&stale.Receipts.Single().Task==job.Id&&stale.Receipts.Single().Status=="succeeded",
            "stale concurrent instance cannot double-claim; callback cannot rewrite durable identity");
        var other=new ScheduledTask{Map=map,DueUtc=due,Kind=ScheduledKind.Observation};
        await stale.RunDue(new[]{other},owned,due,_=>Task.CompletedTask);
        Check(first.Receipts.Length==2,"completion re-reads journal and preserves another instance's receipts");
        var missed=new ScheduledTask{Map=map,DueUtc=due.AddHours(-1),Kind=ScheduledKind.Notification};
        var expired=new ScheduledTask{Map=map,DueUtc=due,UntilUtc=due,Kind=ScheduledKind.Notification};
        await first.RunDue(new[]{missed,expired},owned,due,_=>throw new Exception("must not execute"),
            _=>false);
        // A separate maintenance job proves the explicit maintenance predicate.
        var paused=new ScheduledTask{Map=map,DueUtc=due,Kind=ScheduledKind.Notification};
        await first.RunDue(new[]{paused},owned,due,_=>throw new Exception("must not execute"),_=>true);
        Check(first.Receipts.Single(x=>x.Task==missed.Id).Status=="missed-no-catchup"&&
            first.Receipts.Single(x=>x.Task==expired.Id).Status=="expired-no-auto-retry"&&
            first.Receipts.Single(x=>x.Task==paused.Id).Status=="maintenance-blocked",
            "missed, expired and maintenance slots are durably skipped without execution");

        string childState=Path.Combine(root,"scheduler-child"),ready=Path.Combine(root,"scheduler-child-ready");
        var psi=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
        if(Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(SchedulerConcurrencyTests).Assembly.Location);
        foreach(string argument in new[]{"--scheduler-claim-child",childState,ready})psi.ArgumentList.Add(argument);
        using(var child=Process.Start(psi)!)
        {
            try
            {
                var until=DateTimeOffset.UtcNow.AddSeconds(15);
                while(!File.Exists(ready)&&!child.HasExited&&DateTimeOffset.UtcNow<until)await Task.Delay(25);
                Check(File.Exists(ready)&&!child.HasExited,"independent Windows process durably claims before callback");
                int repeats=0;var childJob=ChildJob();
                await new PersistentScheduler(childState).RunDue(new[]{childJob},new HashSet<Guid>{ChildMap},childJob.DueUtc,
                    _=>{repeats++;return Task.CompletedTask;});
                Check(repeats==0,"another OS process cannot repeat an in-progress scheduler callback");
            }
            finally{if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}}
        }
        int replay=0;var interruptedJob=ChildJob();
        await new PersistentScheduler(childState).RunDue(new[]{interruptedJob},new HashSet<Guid>{ChildMap},interruptedJob.DueUtc,
            _=>{replay++;return Task.CompletedTask;});
        Check(replay==0&&new PersistentScheduler(childState).Receipts.Single().Status=="claimed",
            "killed claimant remains uncertain and is never retried after restart");

        var calendar=new RecurringSchedule{Map=map,LocalClock=new TimeSpan(2,30,45),Frequency=ScheduleFrequency.Daily};
        var spring=OperationalScheduler.Next(calendar,new DateTimeOffset(2026,3,28,23,0,0,TimeSpan.Zero));
        Check(spring.DueUtc==new DateTimeOffset(2026,3,29,1,0,0,TimeSpan.Zero),
            "Budapest spring gap uses the first valid wall-clock minute including a seconds-bearing request");
        calendar.LocalClock=new TimeSpan(2,30,0);
        var autumn=OperationalScheduler.Next(calendar,new DateTimeOffset(2026,10,25,0,0,0,TimeSpan.Zero));
        var following=OperationalScheduler.Next(calendar,autumn.DueUtc);
        Check(autumn.DueUtc==new DateTimeOffset(2026,10,25,1,30,0,TimeSpan.Zero)&&
            following.DueUtc==new DateTimeOffset(2026,10,26,1,30,0,TimeSpan.Zero),
            "Budapest autumn repeated hour has exactly one deterministic daily slot");
    }
}
