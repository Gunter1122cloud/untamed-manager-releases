using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class SteamSerialQueueTests
{
    private static void Check(bool ok,string message)
    {
        if(!ok)throw new Exception("STEAM QUEUE: "+message);
        Console.WriteLine("PASS Steam queue: "+message);
    }
    private static LocalMap Map(string name,string folder)=>new(){
        Name=name,InstallationPath=folder,ExecutablePath=Path.Combine(
            folder,"ShooterGame","Binaries","Win64","ArkAscendedServer.exe")};

    private static readonly Func<LocalMap,ProcessCheck> Stopped=map=>
        new ProcessCheck(false,"CI process stopped",null,true);

    public static async Task Run(string root)
    {
        string installA=Path.Combine(root,"queue-asa-A");
        string installB=Path.Combine(root,"queue-asa-B");
        var a=Map("Island",installA);
        var b=Map("Astraeos",installB);
        var selected=new[]{a,b};
        var profile=Path.Combine(root,"queue-success-"+Guid.NewGuid().ToString("N"));
        var queue=new SteamSerialMaintenanceQueue(profile,"CI-ONE");
        int concurrency=0,max=0,processed=0;
        var result=await queue.ExecuteAsync(selected,selected,Stopped,async (map,ct)=>{
            int now=Interlocked.Increment(ref concurrency);
            max=Math.Max(max,now);
            await Task.Delay(30,ct);
            Interlocked.Decrement(ref concurrency);
            Interlocked.Increment(ref processed);
        });
        Check(result.Phase==SteamQueuePhase.Completed && max==1&&processed==2 &&
            result.NextIndex==2&&result.ActiveMap==null,
            "all local maps completed in strict serial order, never concurrently");
        Check(queue.Observe()?.Phase==SteamQueuePhase.Completed,
            "completed queue is journaled and can be inspected across Manager restarts");

        var failProfile=Path.Combine(root,"queue-fail-"+Guid.NewGuid().ToString("N"));
        var failQueue=new SteamSerialMaintenanceQueue(failProfile,"CI-TWO");
        int completed=0;
        bool failed=false;
        try{await failQueue.ExecuteAsync(selected,selected,Stopped,(map,ct)=>{
            if(map.Id==b.Id)throw new IOException("Synthetic failed SteamCMD download");
            completed++;return Task.CompletedTask;
        });}
        catch(IOException){failed=true;}
        var stranded=failQueue.Observe();
        Check(failed&&completed==1&&stranded?.Phase==SteamQueuePhase.NeedsOperator &&
            stranded.NextIndex==1 && stranded.ActiveMap==b.Id,
            "failure halts queue on exact map and persists operator-review state");
        bool refusedRestart=false;
        try{await new SteamSerialMaintenanceQueue(failProfile,"CI-TWO")
            .ExecuteAsync(selected,selected,Stopped,(_,_)=>Task.CompletedTask);}
        catch(IOException){refusedRestart=true;}
        Check(refusedRestart,"Manager restart cannot blindly replay a failed queue");

        var shared=Map("The Center",installA);
        var registered=new[]{a,shared,b};
        var sharedProfile=Path.Combine(root,"queue-shared-"+Guid.NewGuid().ToString("N"));
        var sharedQueue=new SteamSerialMaintenanceQueue(sharedProfile,"CI-THREE");
        int invoked=0;
        Func<LocalMap,ProcessCheck> runningPeer=m=>
            m.Id==shared.Id?new ProcessCheck(true,"running",new ProcessIdentity{
                Pid=9000,StartedUtc=DateTimeOffset.UtcNow,ExecutablePath=m.ExecutablePath
            }):Stopped(m);
        bool blocked=false;
        try{await sharedQueue.ExecuteAsync(new[]{a,b},registered,runningPeer,(_,_)=>{
            invoked++;return Task.CompletedTask;
        });}
        catch(InvalidOperationException){blocked=true;}
        Check(blocked&&invoked==0&&
            sharedQueue.Observe()?.Phase==SteamQueuePhase.NeedsOperator,
            "shared-install running sibling blocks binary maintenance before the first map");

        // Hold the host-wide semaphore across an await. A second profile must
        // fail immediately; asynchronous continuation must still release it.
        var lockProfile=Path.Combine(root,"queue-lock-"+Guid.NewGuid().ToString("N"));
        var lockQueue=new SteamSerialMaintenanceQueue(lockProfile,"CI-FOUR");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=lockQueue.ExecuteAsync(new[]{a},registered,Stopped,async (_,ct)=>{
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool exclusive=false;
        try
        {
            await new SteamSerialMaintenanceQueue(
                Path.Combine(root,"queue-other-profile-"+Guid.NewGuid().ToString("N")),
                "CI-FIVE").ExecuteAsync(new[]{b},registered,Stopped,(_,_)=>Task.CompletedTask);
        }
        catch(IOException){exclusive=true;}
        finally{release.TrySetResult();}
        await first;
        Check(exclusive,"different Manager profiles cannot perform simultaneous local maintenance");
    }
}
