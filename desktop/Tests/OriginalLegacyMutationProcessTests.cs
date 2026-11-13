using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

// A real two-Windows-process test of Legacy's ORIGINAL shared-store.mjs
// fs.mkdir(".legacy-store-v1/mutation.lock") / EEXIST contract.
// Synthetic TEMP only. No production Store, RCON, transfer or ASA touched.
internal static class OriginalLegacyMutationProcessTests
{
    internal const string ChildSwitch="--isolated-original-legacy-mutation-child";

    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(
            "ORIGINAL LEGACY MUTATION PROCESS TEST FAILED: "+reason);
        Console.WriteLine("PASS Legacy original mutation.lock: "+reason);
    }
    private static bool Denies(Action action)
    {
        try{action();return false;}
        catch(Exception ex) when(ex is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException){return true;}
    }
    public static int RunChild(string[] args)
    {
        if(args.Length!=4||args[0]!=ChildSwitch)return 64;
        try
        {
            var control=args[1];
            var barrier=args[2];
            var signal=args[3];
            File.WriteAllText(signal+".ready","ready");
            var wait=Stopwatch.StartNew();
            while(!File.Exists(barrier)&&wait.Elapsed<TimeSpan.FromSeconds(25))
                Thread.Sleep(15);
            if(!File.Exists(barrier))return 65;
            try
            {
                using var lease=LegacyStoreV1OriginalMutationDirectory.Acquire(control);
                File.WriteAllText(signal+".won","original-directory-acquired");
                // Deliberate abrupt termination by parent below: Dispose
                // does not release a crashed/uncertain original maintenance.
                while(true)Thread.Sleep(100);
            }
            catch(IOException ex)
            {
                File.WriteAllText(signal+".denied",ex.Message);
                return 17;
            }
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 67;
        }
    }

    private static Process Start(string control,string barrier,string signal)
    {
        string host=Environment.ProcessPath??throw new InvalidOperationException(
            "Native Windows test process path is unavailable");
        var info=new ProcessStartInfo(host){
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardError=true,RedirectStandardOutput=true};
        if(Path.GetFileNameWithoutExtension(host).Equals("dotnet",
            StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach(var arg in new[]{ChildSwitch,control,barrier,signal})
            info.ArgumentList.Add(arg);
        return Process.Start(info)??throw new InvalidOperationException(
            "Two-process Legacy mutation test child failed to start");
    }

    private static async Task AwaitResult(Func<bool> done,string message)
    {
        var time=Stopwatch.StartNew();
        while(!done()&&time.Elapsed<TimeSpan.FromSeconds(25))
            await Task.Delay(20);
        Check(done(),message);
    }

    public static async Task Run(string sandbox)
    {
        if(!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Original Legacy lock test needs Windows");
        string control=Path.Combine(sandbox,"isolated-original-legacy-mutation",
            ".\u006Bova-store-v1");
        Directory.CreateDirectory(control);
        string marker=Path.Combine(control,"mutation.lock");
        string barrier=Path.Combine(sandbox,"isolated-original-legacy-mutation",
            "begin.gate");
        string[] signals={
            Path.Combine(sandbox,"isolated-original-legacy-mutation","host-one"),
            Path.Combine(sandbox,"isolated-original-legacy-mutation","host-two")
        };
        Process? a=null,b=null;
        try
        {
            a=Start(control,barrier,signals[0]);
            b=Start(control,barrier,signals[1]);
            await AwaitResult(()=>signals.All(p=>File.Exists(p+".ready")),
                "two independent Windows processes reached shared original lock barrier");
            File.WriteAllText(barrier,"go");
            await AwaitResult(()=>signals.All(p=>
                File.Exists(p+".won")||File.Exists(p+".denied")),
                "two independent contenders returned definite original lock outcomes");
            var wins=signals.Where(p=>File.Exists(p+".won")).ToArray();
            var losses=signals.Where(p=>File.Exists(p+".denied")).ToArray();
            Check(wins.Length==1&&losses.Length==1&&Directory.Exists(marker),
                "one and only one process owns original Legacy mutation.lock directory");
            Process winner=wins[0]==signals[0]?a:b;
            Process loser=wins[0]==signals[0]?b:a;
            await AwaitResult(()=>loser.HasExited,
                "losing process exits without changing original mutation directory");
            Check(loser.ExitCode==17,
                "second process is refused by real Windows CreateDirectoryW EEXIST");
            winner.Kill(entireProcessTree:true);
            await winner.WaitForExitAsync();
            Check(Directory.Exists(marker)&&
                  Denies(()=>LegacyStoreV1OriginalMutationDirectory.Acquire(control)),
                "abrupt process death does not release durable original mutex or permit takeover");

            // Simulate explicit LAB-ONLY operator recovery after inspected
            // crash. Production Original Legacy locks must NEVER be cleared
            // automatically or by these tests.
            Directory.Delete(marker,recursive:false);
            using(var approved=LegacyStoreV1OriginalMutationDirectory.Acquire(control))
            {
                Check(Directory.Exists(marker),
                    "verified synthetic maintenance can re-acquire after explicit recovery");
                approved.ReleaseAfterVerifiedCompletion();
                Check(!Directory.Exists(marker),
                    "source-compatible normal completion removes only the owned empty directory");
            }
            using(var uncertain=LegacyStoreV1OriginalMutationDirectory.Acquire(control))
            {
                // No completed protocol: Dispose must NOT release it.
            }
            Check(Directory.Exists(marker)&&
                  Denies(()=>LegacyStoreV1OriginalMutationDirectory.Acquire(control)),
                "disposing uncertain maintenance does not cause unsafe automatic unlock");
            Directory.Delete(marker,recursive:false);
            using(var identity=LegacyStoreV1OriginalMutationDirectory.Acquire(control))
            {
                string moved=Path.Combine(control,"moved-synthetic-mutation.lock");
                Directory.Move(marker,moved);
                Directory.CreateDirectory(marker);
                Check(Denies(identity.ReleaseAfterVerifiedCompletion),
                    "replaced mutation.lock directory cannot be released using stale ownership");
                Directory.Delete(marker);
                Directory.Move(moved,marker);
                identity.ReleaseAfterVerifiedCompletion();
            }
            Check(!Directory.Exists(marker),
                "only verified same-file-ID completion clears the test mutex");
        }
        finally
        {
            foreach(var child in new[]{a,b})
            {
                if(child==null)continue;
                try {if(!child.HasExited)child.Kill(entireProcessTree:true);}
                catch(InvalidOperationException){}
                child.Dispose();
            }
        }
    }
}
