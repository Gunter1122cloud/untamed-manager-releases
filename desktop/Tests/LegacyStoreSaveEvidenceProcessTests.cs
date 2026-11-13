using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

// Real two-process regression on the MANAGER-OWNED LOCAL receipt, not on any
// Legacy original, shared Store writer, world, player or production ASA process.
internal static class LegacyStoreSaveEvidenceProcessTests
{
    internal const string ChildSwitch="--isolated-store-save-journal-child";
    private const string SourceSha="AABBCCDDEEFF00112233445566778899"+
        "00112233445566778899AABBCCDDEEFF";

    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(
            "STORE SAVE JOURNAL PROCESS TEST FAILED: "+reason);
        Console.WriteLine("PASS real two-process Store SaveComplete journal: "+reason);
    }

    public static int RunChild(string[] args)
    {
        if(args.Length!=5||args[0]!=ChildSwitch||
           !Guid.TryParse(args[2],out var mapId))return 64;
        try
        {
            string root=args[1], barrier=args[3], signal=args[4];
            File.WriteAllText(signal+".ready","ready");
            var clock=Stopwatch.StartNew();
            while(!File.Exists(barrier)&&clock.Elapsed<TimeSpan.FromSeconds(25))
                Thread.Sleep(15);
            if(!File.Exists(barrier))return 65;

            var operation=Guid.NewGuid();
            try
            {
                LegacyStoreV1SaveEvidenceJournal.Begin(root,operation,mapId,
                    48031,DateTimeOffset.UtcNow.AddMinutes(-4),SourceSha);
            }
            catch(Exception ex) when(ex is IOException or InvalidOperationException)
            {
                File.WriteAllText(signal+".denied",ex.Message);
                return 17;
            }
            File.WriteAllText(signal+".won",operation.ToString("D"));
            // Parent kills this process abruptly after seeing both results.
            // A crash must never erase or automatically complete the receipt.
            for(int n=0;n<4500;n++)Thread.Sleep(20);
            return 0;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 67;
        }
    }

    private static Process Start(string root,Guid mapId,string barrier,string signal)
    {
        string host=Environment.ProcessPath??throw new InvalidOperationException(
            "Native test process path is unavailable");
        var start=new ProcessStartInfo(host)
        {
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardError=true,RedirectStandardOutput=true
        };
        if(Path.GetFileNameWithoutExtension(host).Equals("dotnet",
            StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach(string value in new[]{ChildSwitch,root,mapId.ToString("D"),barrier,signal})
            start.ArgumentList.Add(value);
        return Process.Start(start)??throw new InvalidOperationException(
            "Windows child process could not start");
    }

    private static async Task WaitForExit(Process child)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try{await child.WaitForExitAsync(timeout.Token);}
        catch(OperationCanceledException)
        {
            if(!child.HasExited)child.Kill(entireProcessTree:true);
            throw new TimeoutException("Store journal child process timed out");
        }
    }

    public static async Task Run(string sandbox)
    {
        string root=Path.Combine(sandbox,"two-process-local-save-journal");
        Directory.CreateDirectory(root);
        var map=Guid.NewGuid();
        string barrier=Path.Combine(root,"start.gate");
        string[] signal={Path.Combine(root,"first"),Path.Combine(root,"second")};
        Process? first=null,second=null,retry=null;
        try
        {
            first=Start(root,map,barrier,signal[0]);
            second=Start(root,map,barrier,signal[1]);
            var readyClock=Stopwatch.StartNew();
            while((!File.Exists(signal[0]+".ready")||
                   !File.Exists(signal[1]+".ready"))&&
                   !first.HasExited&&!second.HasExited&&
                   readyClock.Elapsed<TimeSpan.FromSeconds(25))
                await Task.Delay(30);
            Check(File.Exists(signal[0]+".ready")&&
                  File.Exists(signal[1]+".ready")&&
                  !first.HasExited&&!second.HasExited,
                "two independent Windows processes reached the same-map write barrier");
            File.WriteAllText(barrier,"go");
            var raceClock=Stopwatch.StartNew();
            while(signal.Any(s=>!File.Exists(s+".won")&&!File.Exists(s+".denied"))&&
                  raceClock.Elapsed<TimeSpan.FromSeconds(25))
                await Task.Delay(20);
            string[] winners=signal.Where(s=>File.Exists(s+".won")).ToArray();
            string[] denied=signal.Where(s=>File.Exists(s+".denied")).ToArray();
            Check(winners.Length==1&&denied.Length==1,
                "exactly one process begins the durable SaveComplete receipt");
            Process loser=winners[0]==signal[0]?second:first;
            Process winner=winners[0]==signal[0]?first:second;
            await WaitForExit(loser);
            Check(loser.ExitCode==17,
                "competing writer explicitly fails closed without changing receipt");
            Guid operation=Guid.Parse(File.ReadAllText(winners[0]+".won"));
            var pending=LegacyStoreV1SaveEvidenceJournal.Inspect(root,map);
            Check(pending?.OperationId==operation&&pending.Phase=="prepared"&&
                  !pending.LocalSaveCompleteObserved,
                "durable journal belongs to the sole process that won the race");

            winner.Kill(entireProcessTree:true);
            await WaitForExit(winner);
            Check(LegacyStoreV1SaveEvidenceJournal.Inspect(root,map)?.Phase=="prepared",
                "abrupt process death preserves the pending receipt across restarts");
            string retrySignal=Path.Combine(root,"after-crash");
            retry=Start(root,map,barrier,retrySignal);
            await WaitForExit(retry);
            Check(retry.ExitCode==17&&File.Exists(retrySignal+".denied")&&
                  LegacyStoreV1SaveEvidenceJournal.Inspect(root,map)?.OperationId==operation,
                "fresh process cannot auto-retry or overwrite crashed writer's journal");

            var otherMap=Guid.NewGuid();
            var other=LegacyStoreV1SaveEvidenceJournal.Begin(root,Guid.NewGuid(),
                otherMap,48032,DateTimeOffset.UtcNow.AddMinutes(-3),SourceSha);
            Check(other.Phase=="prepared"&&other.MapId==otherMap,
                "independent map remains writable without global all-map lock");
        }
        finally
        {
            foreach(var p in new[]{first,second,retry})
            {
                if(p is null)continue;
                if(!p.HasExited)
                {
                    p.Kill(entireProcessTree:true);
                    await WaitForExit(p);
                }
                p.Dispose();
            }
        }
    }
}
