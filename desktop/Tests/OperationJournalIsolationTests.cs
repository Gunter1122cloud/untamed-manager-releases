using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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

    public static async Task Run(string testsRoot)
    {
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
    }
}
