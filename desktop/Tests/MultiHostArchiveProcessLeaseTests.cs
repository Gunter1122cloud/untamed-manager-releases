using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

/// <summary>
/// Two REAL Windows child processes compete for a single synthetic offline
/// multi-host archive path. Never loads Kova, ASA or production data.
/// </summary>
internal static class MultiHostArchiveProcessLeaseTests
{
    internal const string ChildSwitch="--isolated-multihost-archive-lease-child";
    private static void Check(bool value,string context)
    {
        if(!value)throw new InvalidOperationException(
            "TWO-PROCESS MULTIHOST ARCHIVE LEASE: "+context);
        Console.WriteLine("PASS real two-process multi-host archive: "+context);
    }
    internal static int RunChild(string[] arguments)
    {
        if(arguments.Length!=4||arguments[0]!=ChildSwitch||
           arguments[1] is not ("hold" or "contend"))return 60;
        try
        {
            using var lease=MultiHostOfflineArchiveRecovery.AcquireLease(arguments[2]);
            if(arguments[1]=="contend")
            {
                Console.WriteLine("UNEXPECTED_LEASE_ACQUIRED");
                return 61;
            }
            File.WriteAllText(arguments[3],"LEASE_HELD");
            for(int i=0;i<1800;i++)
            {
                if(File.Exists(arguments[3]+".release"))return 0;
                Thread.Sleep(50);
            }
            return 62;
        }
        catch(IOException ex) when(ex.Message.Contains(
            "UM-MULTIHOST-RECOVERY-LEASE-BUSY",StringComparison.Ordinal))
        {
            Console.WriteLine("LEASE_BUSY");
            return arguments[1]=="contend"?0:63;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 64;
        }
    }
    private static Process Launch(string mode,string target,string ready)
    {
        string host=Environment.ProcessPath??
            throw new InvalidOperationException("Native test host path missing");
        var psi=new ProcessStartInfo(host){
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        if(Path.GetFileNameWithoutExtension(host).Equals(
            "dotnet",StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        psi.ArgumentList.Add(ChildSwitch);
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(ready);
        return Process.Start(psi)??
            throw new InvalidOperationException("Synthetic child process launch failed");
    }
    private static async Task Join(Process process,string context)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try{await process.WaitForExitAsync(timeout.Token);}
        catch(OperationCanceledException)
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            throw new TimeoutException("Windows archive child stuck: "+context);
        }
    }
    internal static async Task Run(string isolatedRoot)
    {
        string root=Path.Combine(isolatedRoot,"two-real-multihost-archive-writers");
        Directory.CreateDirectory(root);
        string target=Path.Combine(root,"Untamed-MultiHost-20261109-190000-"+
            Guid.NewGuid().ToString("N"));
        string ready=Path.Combine(root,"holder-ready.txt");
        Process? holder=null,contender=null;
        try
        {
            holder=Launch("hold",target,ready);
            var clock=Stopwatch.StartNew();
            while(!File.Exists(ready)&&!holder.HasExited&&
                clock.Elapsed<TimeSpan.FromSeconds(25))
                await Task.Delay(40);
            Check(File.Exists(ready)&&!holder.HasExited&&
                File.ReadAllText(ready)=="LEASE_HELD",
                "first isolated Windows process owns the exact archive lease");
            contender=Launch("contend",target,ready);
            await Join(contender,"contender");
            string stdout=await contender.StandardOutput.ReadToEndAsync();
            string stderr=await contender.StandardError.ReadToEndAsync();
            Check(contender.ExitCode==0&&stdout.Contains("LEASE_BUSY",
                    StringComparison.Ordinal)&&string.IsNullOrWhiteSpace(stderr),
                "second independent Windows process cannot write same archive");
            holder.Kill(entireProcessTree:true);
            await Join(holder,"terminated holder");
            // OS releases the native FileShare.None handle without any
            // special force-unlock, stale TTL, or journal file deletion.
            using(var reacquired=MultiHostOfflineArchiveRecovery.AcquireLease(target))
            {
                Check(reacquired.CanWrite,
                    "crash releases exclusive archive writer without deleting durable evidence");
            }
            Check(!Directory.Exists(target)&&
                  !Directory.Exists(target+".incomplete"),
                "lease alone never publishes, deletes, or alters archive bytes");
        }
        finally
        {
            if(contender is {HasExited:false})
            {
                contender.Kill(entireProcessTree:true);
                await Join(contender,"contender cleanup");
            }
            if(holder is {HasExited:false})
            {
                holder.Kill(entireProcessTree:true);
                await Join(holder,"holder cleanup");
            }
            contender?.Dispose();
            holder?.Dispose();
        }
    }
}
