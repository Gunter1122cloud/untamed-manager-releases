using System.Diagnostics;
using System.Text.Json;
using UntamedManager.Core;

public static class OperationSafetyTests
{
    static void Check(bool ok,string reason)
    {if(!ok)throw new Exception("Operation safety failed: "+reason);Console.WriteLine("PASS operation-safety "+reason);}
    static void Refused(Action action,string reason)
    {
        bool refused=false;try{action();}catch(IOException){refused=true;}
        Check(refused,reason);
    }
    public static async Task Run(string root)
    {
        string resource=Path.Combine(root,"coordinator-install");Directory.CreateDirectory(resource);
        var a=new UntamedOperationCoordinator(Path.Combine(root,"operations-a"));
        var b=new UntamedOperationCoordinator(Path.Combine(root,"operations-b"));
        using(var lease=a.Acquire(resource,UntamedOperationKind.Update))
        {
            Refused(()=>{using var other=b.Acquire(resource,UntamedOperationKind.Backup);},"different profiles share machine-wide resource exclusion");
            lease.MarkMutationStarted();
            await Task.Run(()=>lease.Complete());
        }
        Check(a.Observe(resource)!.Phase==UntamedOperationPhase.Completed,"lease spans await/thread transition");
        using(a.Acquire(resource,UntamedOperationKind.Configuration)){}
        Check(a.Observe(resource)!.Phase==UntamedOperationPhase.CancelledBeforeMutation,"unused prepared lease cancels without mutation");
        using(var interrupted=a.Acquire(resource,UntamedOperationKind.Update)){interrupted.MarkMutationStarted();}
        Check(a.Observe(resource)!.Phase==UntamedOperationPhase.NeedsOperator,"failed mutation leaves persistent operator state");
        Refused(()=>{using var again=a.Acquire(resource,UntamedOperationKind.Update);},"no timeout or automatic journal recovery");
        string crashResource=Path.Combine(root,"crash-install");Directory.CreateDirectory(crashResource);
        string crashState=Path.Combine(root,"crash-operations"),ready=Path.Combine(root,"coordinator-ready");
        string processPath=Environment.ProcessPath!;
        var psi=new ProcessStartInfo(processPath){UseShellExecute=false,CreateNoWindow=true};
        if(Path.GetFileNameWithoutExtension(processPath).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(OperationSafetyTests).Assembly.Location);
        foreach(var arg in new[]{"--coordinator-hold",crashState,crashResource,ready})psi.ArgumentList.Add(arg);
        using(var child=Process.Start(psi)!)
        {
            var until=DateTime.UtcNow.AddSeconds(15);
            while(!File.Exists(ready)&&!child.HasExited&&DateTime.UtcNow<until)await Task.Delay(50);
            try
            {
                Check(File.Exists(ready)&&!child.HasExited,"independent process holds operation lease");
                Refused(()=>{using var other=new UntamedOperationCoordinator(crashState).Acquire(crashResource,UntamedOperationKind.Restore);},"cross-process concurrent writer denied");
            }
            finally{if(!child.HasExited)child.Kill();await child.WaitForExitAsync();}
        }
        Refused(()=>{using var other=new UntamedOperationCoordinator(crashState).Acquire(crashResource,UntamedOperationKind.Restore);},"crash releases OS handle but durable pending state still blocks replay");
        string profile=Path.Combine(root,"config-cas");
        var first=new ConfigStore("first",profile);var second=new ConfigStore("second",profile);
        var cfg=first.Read();cfg.LocalHostName="A";first.Save(cfg);
        Refused(()=>second.Save(new ManagerConfig{LocalHostName="STALE"}),"stale profile cannot overwrite a newer Manager configuration");
        Check(first.Read().LocalHostName=="A","CAS refusal preserves current configuration");
        var reloaded=second.Read();reloaded.LocalHostName="B";second.Save(reloaded);
        Check(first.Read().LocalHostName=="B","explicit reload permits next configuration revision");
        string[] dbs={"arkshop","arkpermissions","lethalquests"};
        Check(MariaDbCompatibility.Evaluate("10.11.19-MariaDB","Distrib 10.11.19-MariaDB","10.11.19-MariaDB",true,dbs).Compatible,"actual deployment series accepted by metadata policy");
        Check(!MariaDbCompatibility.Evaluate("10.11.19-MariaDB","11.4.13-MariaDB","10.11.19-MariaDB",true,dbs).Compatible,"11.4 tool is not assumed compatible");
        Check(!MariaDbCompatibility.Evaluate("10.11.19-MariaDB","10.11.19-MariaDB","10.11.19-MariaDB",false,dbs).Compatible,"binary metadata cannot certify SQL server version");
        Check(!MariaDbCompatibility.Evaluate("10.11.19-MariaDB","10.11.19-MariaDB","10.11.19-MariaDB",true,new[]{"arkshop"}).Compatible,"incomplete database set rejected");
        var ledger=DevelopmentLedger.Read();
        Check(ledger.Records.Length==228&&ledger.SourceDocuments.Length==7&&!ledger.FinalReleaseReady,"all audit requirements tracked without false final acceptance");
        bool invalid=false;try{DevelopmentLedger.Validate(ledger with {Records=ledger.Records.Skip(1).ToArray()});}catch(InvalidDataException){invalid=true;}
        Check(invalid,"missing audit requirement fails ledger validation");
        string redacted=UntamedPresentation.Redact("KovaCluster password=SECRET token='SECRET WITH SPACE' https://discord.com/api/webhooks/1/SECRET");
        Check(!redacted.Contains("SECRET")&&!redacted.Contains("Kova",StringComparison.OrdinalIgnoreCase),"user-facing branding and credential redaction");
        Check(!typeof(ClusterService).Assembly.GetTypes().Any(t=>t.Name.StartsWith("Kova",StringComparison.Ordinal)),"own wrapper types use neutral names");
        var conflicting=new Session{ContradictoryExit=true};
        bool stopped=false;try{await SafeLifecycle.Stop(conflicting,true,TimeSpan.FromSeconds(1));}catch(InvalidOperationException){stopped=true;}
        Check(stopped&&conflicting.Starts==0,"contradictory Running/ConfirmedStopped never restarts");
        var hanging=new Session{Hang=true};var watch=Stopwatch.StartNew();bool timedOut=false;
        try{await SafeLifecycle.Stop(hanging,true,TimeSpan.FromMilliseconds(100));}catch(OperationCanceledException){timedOut=true;}
        Check(timedOut&&watch.Elapsed<TimeSpan.FromSeconds(3)&&hanging.Starts==0,"deadline enforced even when adapter ignores cancellation");
    }
    sealed class Session : ILocalServerSession
    {
        public bool ContradictoryExit,Hang;bool exiting;public int Starts;
        readonly ProcessIdentity identity=new(){Pid=1,StartedUtc=DateTimeOffset.UtcNow,ExecutablePath=@"C:\synthetic\server.exe",Arguments="synthetic"};
        public ProcessCheck Check()=>new(true,"synthetic",identity,exiting&&ContradictoryExit);
        public Task Command(string command,CancellationToken ct)
        {if(Hang)return new TaskCompletionSource().Task;if(command=="DoExit")exiting=true;return Task.CompletedTask;}
        public Task<bool> SaveCompleted(CancellationToken ct)=>Task.FromResult(true);
        public void Start()=>Starts++;
    }
}
