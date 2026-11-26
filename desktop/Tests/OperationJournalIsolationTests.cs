using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Separate Windows process must NEVER fall back to machine-wide ProgramData.
// The child refuses to run without an explicit fully-qualified TEMP root.
// No current user's Kova, ASA, RCON or Store data is opened.
internal static class OperationJournalIsolationTests
{
    internal const string ChildSwitch="--isolated-operation-journal-test-child";

    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(
            "OPERATIONS JOURNAL ISOLATION FAILED: "+message);
        Console.WriteLine("PASS journal isolation: "+message);
    }

    public static int RunChild(string[] args)
    {
        if(args.Length!=3||args[0]!=ChildSwitch)return 64;
        string temp=Path.GetFullPath(Path.GetTempPath());
        string root=Path.GetFullPath(args[1]);
        string install=Path.GetFullPath(args[2]);
        string prefix=Path.TrimEndingDirectorySeparator(temp)+
            Path.DirectorySeparatorChar;
        // Never let malformed parameters choose the production default.
        if(!Path.IsPathFullyQualified(root)||
           !root.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||
           !install.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
            return 65;
        try
        {
            using var scope=UntamedOperationCoordinator.UseIsolatedRootForTests(root);
            if(UntamedOperationCoordinator.DefaultRoot!=root)return 66;
            var coordinator=new UntamedOperationCoordinator();
            using(var lease=coordinator.Acquire(
                install,UntamedOperationKind.Configuration))lease.Complete();
            var restart=new AsaManualRestartJournal(install);
            var entry=restart.Begin(Guid.NewGuid(),43210);
            restart.Change(entry.Id,AsaRestartPhase.NeedsOperator);
            string key=UntamedOperationCoordinator.ResourceKey(install);
            return File.Exists(Path.Combine(root,key+".json"))&&
                File.Exists(Path.Combine(root,"Restart-v1",key+".json"))
                ?0:67;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name);
            return 68;
        }
    }

    private static void StoppedStartReconciliation(string testsRoot)
    {
        string folder=Path.Combine(testsRoot,"stopped-start-reconciliation");
        string installation=Path.Combine(folder,"map-a");
        string other=Path.Combine(folder,"map-b");
        Directory.CreateDirectory(installation);Directory.CreateDirectory(other);
        var operations=new UntamedOperationCoordinator(Path.Combine(folder,"journals"));
        using(var interrupted=operations.Acquire(installation,UntamedOperationKind.Launch))
            interrupted.MarkMutationStarted();
        var previous=operations.Observe(installation)!;
        string key=UntamedOperationCoordinator.ResourceKey(installation);
        string journal=Path.Combine(folder,"journals",key+".json");
        byte[] original=File.ReadAllBytes(journal);
        bool denied=false;
        try{using var refused=operations.AcquireStoppedStart(installation,()=>false);}
        catch(IOException){denied=true;}
        Check(denied&&File.ReadAllBytes(journal).SequenceEqual(original),
            "unknown physical state preserves abandoned launch receipt");
        int observations=0;
        using(var next=operations.AcquireStoppedStart(installation,()=>++observations>0))
        {
            Check(operations.Observe(installation)?.Operation!=previous.Operation&&observations==2,
                "proven stopped map gets a fresh operation ID without operator review");
            bool busy=false;
            try{using var conflict=operations.AcquireStoppedStart(installation,()=>true);}
            catch(IOException){busy=true;}
            Check(busy,"a live installation lease cannot be bypassed");
            next.Complete();
        }
        string audit=Path.Combine(folder,"journals",key+"-"+previous.Operation.ToString("N")+".interrupted-runtime.json");
        Check(File.ReadAllBytes(audit).SequenceEqual(original),
            "old uncertain outcome is archived byte-identically, never reported successful");
        using(var restore=operations.Acquire(other,UntamedOperationKind.Restore))
            restore.MarkMutationStarted();
        bool protectedRestore=false;
        try{using var refused=operations.AcquireStoppedStart(other,()=>true);}
        catch(IOException){protectedRestore=true;}
        Check(protectedRestore&&operations.Observe(other)?.Kind==UntamedOperationKind.Restore,
            "runtime Start does not override an interrupted destructive restore");
        using(var repeat=operations.Acquire(installation,UntamedOperationKind.Launch))
            repeat.MarkMutationStarted();
        var interruptedAgain=operations.Observe(installation)!;
        int calls=0;bool changed=false;
        try{using var refused=operations.AcquireStoppedStart(installation,()=>++calls==1);}
        catch(IOException){changed=true;}
        Check(changed&&operations.Observe(installation)==interruptedAgain,
            "changed second observation prevents fresh Start");
        using(var independent=operations.Acquire(Path.Combine(folder,"map-c"),UntamedOperationKind.Launch))
            independent.Complete();
        Check(operations.Observe(installation)==interruptedAgain,
            "failed launch does not reserve an unrelated installation");
    }

    private static void PairedStopReconciliation(string testsRoot)
    {
        string folder=Path.Combine(testsRoot,"paired-stop-reconciliation");
        string installation=Path.Combine(folder,"installation");
        string journals=Path.Combine(folder,"journals");
        Directory.CreateDirectory(installation);
        using var scope=UntamedOperationCoordinator.UseIsolatedRootForTests(journals);
        var operations=new UntamedOperationCoordinator(journals);
        var stop=new AsaManualStopJournal(installation);
        Guid map=Guid.NewGuid();
        var attempt=stop.Begin(map,12345,DateTimeOffset.UtcNow.AddHours(-1));
        using(var interrupted=operations.Acquire(installation,UntamedOperationKind.Launch))
            interrupted.MarkMutationStarted();
        var stoppedRecord=stop.Finish(attempt.Id,AsaStopPhase.NeedsOperator);
        // Age the synthetic receipts, not the actual Windows clock.
        var timestamp=DateTimeOffset.UtcNow.AddMinutes(-5);
        string key=UntamedOperationCoordinator.ResourceKey(installation);
        string commonPath=Path.Combine(journals,key+".json");
        string stopPath=Path.Combine(journals,"GracefulStop-v1",key+".json");
        var common=operations.Observe(installation)! with{UpdatedUtc=timestamp};
        stoppedRecord=stoppedRecord with{CreatedUtc=timestamp.AddSeconds(-2),ChangedUtc=timestamp};
        File.WriteAllText(commonPath,JsonSerializer.Serialize(common));
        File.WriteAllText(stopPath,JsonSerializer.Serialize(stoppedRecord,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var commonBytes=File.ReadAllBytes(commonPath);
        var stopBytes=File.ReadAllBytes(stopPath);
        Check(!operations.TryReconcileStoppedReceipt(installation,map,stop,stoppedRecord,()=>false)&&
            File.ReadAllBytes(commonPath).SequenceEqual(commonBytes)&&File.ReadAllBytes(stopPath).SequenceEqual(stopBytes),
            "unverified process state never clears either paired receipt");
        Check(operations.TryReconcileStoppedReceipt(installation,map,stop,stoppedRecord,()=>true),
            "stopped process releases paired runtime receipts without operator action");
        stop.DemandNoPending(map);
        Check(operations.Observe(installation)?.Phase==UntamedOperationPhase.OperatorReviewed&&
            stop.Observe()?.Phase==AsaStopPhase.OperatorReviewed,
            "both interrupted receipts close without claiming success");
        Check(File.ReadAllBytes(Path.Combine(journals,key+"-"+common.Operation.ToString("N")+".interrupted-runtime.json")).SequenceEqual(commonBytes)&&
            File.ReadAllBytes(Path.Combine(journals,"GracefulStop-v1",key+"-"+attempt.Id.ToString("N")+".operator-reviewed.json")).SequenceEqual(stopBytes),
            "paired runtime reconciliation preserves both original receipts");
        using(var next=operations.Acquire(installation,UntamedOperationKind.Launch))next.Complete();
    }

    public static async Task Run(string testsRoot)
    {
        StoppedStartReconciliation(testsRoot);
        PairedStopReconciliation(testsRoot);
        string root=Path.Combine(testsRoot,"child-operation-journal-isolation");
        string journals=Path.Combine(root,"journals");
        string install=Path.Combine(root,"fake-asa-install");
        Directory.CreateDirectory(install);
        string executable=Environment.ProcessPath??
            throw new InvalidOperationException("Test host process path missing");
        var psi=new ProcessStartInfo(executable){
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        if(Path.GetFileNameWithoutExtension(executable).Equals(
            "dotnet",StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach(var arg in new[]{ChildSwitch,journals,install})
            psi.ArgumentList.Add(arg);
        using var child=Process.Start(psi)??
            throw new InvalidOperationException("Isolated child was not launched");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try{await child.WaitForExitAsync(timeout.Token);}
        catch(OperationCanceledException)
        {
            if(!child.HasExited)child.Kill(entireProcessTree:true);
            throw new TimeoutException("Isolated journal test child hung");
        }
        string error=await child.StandardError.ReadToEndAsync();
        Check(child.ExitCode==0,
            "separate process uses only explicit TEMP journal roots (exit "+
            child.ExitCode+"; "+error+")");
        string key=UntamedOperationCoordinator.ResourceKey(install);
        Check(File.Exists(Path.Combine(journals,key+".json"))&&
              File.Exists(Path.Combine(journals,"Restart-v1",key+".json")),
            "generic and Restart-v1 journals are both beneath TEMP");

        // P1/P2: a directory occupying a journal filename must not be
        // mistaken for a missing file (File.Exists returns false), and an
        // exclusively locked journal must not become an automatic unlock.
        // All fixtures live beneath a separate TEMP-backed coordinator root.
        string recoveryRoot=Path.Combine(root,"unreadable-journal-regressions");
        string recoveryInstall=Path.Combine(recoveryRoot,"original-asa");
        string recoveryJournals=Path.Combine(recoveryRoot,"journals");
        Directory.CreateDirectory(recoveryInstall);
        using var isolated=UntamedOperationCoordinator.UseIsolatedRootForTests(recoveryJournals);
        await using var cluster=new ClusterService("local-journal-read-only-tests",
            Path.Combine(recoveryRoot,"profile"),
            _=>new ProcessCheck(true,"synthetic only",null,false));
        var map=new LocalMap{Id=Guid.NewGuid(),Name="Synthetic Aberration",
            InstallationPath=recoveryInstall,HistoricalStoreProtected=true,
            HistoricalMapKey="Aberration_WP",RconPort=37654};
        cluster.AddMap(map);
        using(var lease=cluster.Operations.Acquire(recoveryInstall,
            UntamedOperationKind.Backup))lease.MarkMutationStarted();
        var restart=new AsaManualRestartJournal(recoveryInstall);
        restart.Begin(map.Id,36541);
        string resource=UntamedOperationCoordinator.ResourceKey(recoveryInstall);
        string operationFile=Path.Combine(recoveryJournals,resource+".json");
        string restartFile=Path.Combine(recoveryJournals,"Restart-v1",resource+".json");
        byte[] originalOperation=File.ReadAllBytes(operationFile);
        byte[] originalRestart=File.ReadAllBytes(restartFile);

        File.Move(operationFile,operationFile+".synthetic-original");
        Directory.CreateDirectory(operationFile);
        var diagnosis=AsaLocalJournalReadOnly.Inspect(cluster.Operations,recoveryInstall);
        Check(diagnosis.OperationUnreadable&&!diagnosis.RestartUnreadable&&
            diagnosis.OperationErrorCode=="UM-OPERATION-JOURNAL-READ-INVALID",
            "directory-shaped Operations-v1 journal is explicitly unreadable");
        var lifecycleBridge=new DesktopAsaLifecycleJournalBridge(cluster);
        using(var response=JsonDocument.Parse(await lifecycleBridge.HandleAsync(
            JsonSerializer.Serialize(new{id=Guid.NewGuid(),
                method="servers.lifecycleJournal",mapId=map.Id}))))
        {
            var data=response.RootElement.GetProperty("data");
            Check(response.RootElement.GetProperty("ok").GetBoolean()&&
                data.GetProperty("needsOperatorReview").GetBoolean()&&
                !data.GetProperty("recoverySupported").GetBoolean()&&
                data.GetProperty("journalUnreadable").GetBoolean()&&
                data.GetProperty("blockingCode").GetString()==
                    "UM-OPERATION-JOURNAL-READ-INVALID",
                "lifecycle UI reports corrupt coordinator and denies recovery approval");
        }
        using var stop=new DesktopHistoricalGracefulStopBridge(cluster);
        using var restartBridge=new DesktopHistoricalRestartBridge(cluster);
        async Task<JsonElement> Status(Func<string,Task<string>> route,string method)
        {
            using var packet=JsonDocument.Parse(await route(JsonSerializer.Serialize(
                new{id=Guid.NewGuid(),method,mapId=map.Id})));
            return packet.RootElement.Clone();
        }
        var stopStatus=await Status(stop.HandleAsync,"historical.gracefulStopRecovery");
        var restartStatus=await Status(restartBridge.HandleAsync,"historical.restartRecovery");
        Check(stopStatus.GetProperty("data").GetProperty("blocked").GetBoolean()&&
              restartStatus.GetProperty("data").GetProperty("blocked").GetBoolean()&&
              stopStatus.GetProperty("data").GetProperty("operationJournalUnreadable").GetBoolean()&&
              restartStatus.GetProperty("data").GetProperty("operationJournalUnreadable").GetBoolean(),
            "SaveWorld/Stop and Restart recovery remain blocked on corrupt coordinator");
        bool strictRefused=false;
        try{_ = cluster.Operations.Observe(recoveryInstall);}
        catch(InvalidDataException){strictRefused=true;}
        Check(strictRefused,"strict coordinator read never treats a directory as absent");
        Directory.Delete(operationFile);
        File.Move(operationFile+".synthetic-original",operationFile);

        File.Move(restartFile,restartFile+".synthetic-original");
        Directory.CreateDirectory(restartFile);
        diagnosis=AsaLocalJournalReadOnly.Inspect(cluster.Operations,recoveryInstall);
        Check(!diagnosis.OperationUnreadable&&diagnosis.RestartUnreadable&&
              diagnosis.RestartErrorCode=="UM-ASA-RESTART-JOURNAL-READ-INVALID",
            "directory-shaped Restart-v1 journal is explicitly unreadable");
        var secondStatus=await Status(stop.HandleAsync,"historical.gracefulStopRecovery");
        Check(secondStatus.GetProperty("data").GetProperty("blocked").GetBoolean()&&
              secondStatus.GetProperty("data").GetProperty("restartJournalUnreadable").GetBoolean(),
            "Stop remains blocked by a broken independent Restart-v1 receipt");
        strictRefused=false;
        try{_ = restart.Observe();}
        catch(InvalidDataException){strictRefused=true;}
        Check(strictRefused,"strict Restart read refuses directory-shaped receipt");
        Directory.Delete(restartFile);
        File.Move(restartFile+".synthetic-original",restartFile);
        using(var held=new FileStream(operationFile,FileMode.Open,
            FileAccess.ReadWrite,FileShare.None))
        {
            diagnosis=AsaLocalJournalReadOnly.Inspect(cluster.Operations,recoveryInstall);
            Check(diagnosis.OperationUnreadable&&
                diagnosis.OperationErrorCode=="UM-OPERATION-JOURNAL-READ-IO-UNAVAILABLE",
                "OS file-share denial surfaces an explicit I/O blocker, not absence");
        }
        Check(File.ReadAllBytes(operationFile).SequenceEqual(originalOperation)&&
              File.ReadAllBytes(restartFile).SequenceEqual(originalRestart),
            "read-only diagnostics and strict refusals preserve both original journal bytes");
    }
}
