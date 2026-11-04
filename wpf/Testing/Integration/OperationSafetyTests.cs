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
        foreach(string alias in new[]{resource+".\\child",resource+" \\child",resource+":stream"})
        {
            bool denied=false;try{UntamedOperationCoordinator.ResourceKey(alias);}catch(InvalidDataException){denied=true;}
            Check(denied,"operation path alias rejected before Windows normalization");
        }
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
        string batchRoot=Path.Combine(root,"batch-journals");
        var batchCoordinator=new UntamedOperationCoordinator(batchRoot);
        string batchOne=Path.Combine(root,"batch-one"),batchTwo=Path.Combine(root,"batch-two");
        Directory.CreateDirectory(batchOne);Directory.CreateDirectory(batchTwo);
        var batch=batchCoordinator.AcquireMany(new[]{batchOne,batchTwo,batchOne},UntamedOperationKind.Configuration);
        Directory.Move(batchRoot,batchRoot+"-preserved");
        File.WriteAllText(batchRoot,"synthetic journal write failure");
        bool cleanupFailed=false;try{batch.Dispose();}catch(IOException){cleanupFailed=true;}
        Check(cleanupFailed,"batch reports journal cleanup failure");
        var freshState=new UntamedOperationCoordinator(Path.Combine(root,"batch-release-proof"));
        using(var released=freshState.AcquireMany(new[]{batchOne,batchTwo},UntamedOperationKind.Configuration))
            released.Complete();
        Check(true,"all batch OS leases released even when each journal write fails");
        File.Delete(batchRoot);Directory.Move(batchRoot+"-preserved",batchRoot);
        Refused(()=>{using var pending=batchCoordinator.Acquire(batchOne,UntamedOperationKind.Configuration);},
            "failed cleanup retains original pending journal; no implicit recovery");
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
        Check(!MariaDbCompatibility.Evaluate("10.11.19-MariaDB","10.11.18-MariaDB","10.11.19-MariaDB",true,dbs).Compatible,
            "untested dump patch version cannot certify the 10.11.19 baseline");
        Check(!MariaDbCompatibility.Evaluate("10.11.19-MariaDB","10.11.19-MariaDB","10.11.18-MariaDB",true,dbs).Compatible,
            "older restore target is not accepted without compatibility evidence");
        bool fixedNames=false;
        try{((IList<string>)MariaDbCompatibility.Databases)[0]="unapproved_database";}catch(NotSupportedException){fixedNames=true;}
        Check(fixedNames&&MariaDbCompatibility.Databases.SequenceEqual(dbs)&&
            !MariaDbCompatibility.Evaluate("10.11.19-MariaDB","10.11.19-MariaDB","10.11.19-MariaDB",true,new[]{"unapproved_database","arkpermissions","lethalquests"}).Compatible,
            "public database list cannot be mutated to expand the permitted backup scope");
        Check(!MariaDbCompatibility.Evaluate("10.11.19-MariaDB","10.11.19-MariaDB","10.11.19-MariaDB",false,dbs).Compatible,"binary metadata cannot certify SQL server version");
        Check(!MariaDbCompatibility.Evaluate("10.11.19-MariaDB","10.11.19-MariaDB","10.11.19-MariaDB",true,new[]{"arkshop"}).Compatible,"incomplete database set rejected");
        var ledger=DevelopmentLedger.Read();
        Check(ledger.Records.Length==228&&ledger.SourceDocuments.Length==7&&!ledger.FinalReleaseReady,"all audit requirements tracked without false final acceptance");
        bool invalid=false;try{DevelopmentLedger.Validate(ledger with {Records=ledger.Records.Skip(1).ToArray()});}catch(InvalidDataException){invalid=true;}
        Check(invalid,"missing audit requirement fails ledger validation");
        VerifyConfigJson(root);
        VerifyBackupMembership(root);
        string redacted=UntamedPresentation.Redact("KovaCluster password=SECRET token='SECRET WITH SPACE' https://discord.com/api/webhooks/1/SECRET");
        Check(!redacted.Contains("SECRET")&&!redacted.Contains("Kova",StringComparison.OrdinalIgnoreCase),"user-facing branding and credential redaction");
        Check(!typeof(ClusterService).Assembly.GetTypes().Any(t=>t.Name.StartsWith("Kova",StringComparison.Ordinal)),"own wrapper types use neutral names");
        var conflicting=new Session{ContradictoryExit=true};
        bool stopped=false;try{await SafeLifecycle.Stop(conflicting,true,TimeSpan.FromSeconds(1));}catch(InvalidOperationException){stopped=true;}
        Check(stopped&&conflicting.Starts==0,"contradictory Running/ConfirmedStopped never restarts");
        var initiallyConflicting=new Session{InitialContradiction=true};bool initialDenied=false;
        try{await SafeLifecycle.Stop(initiallyConflicting,true,TimeSpan.FromSeconds(1));}catch(InvalidOperationException){initialDenied=true;}
        Check(initialDenied&&initiallyConflicting.Commands==0,"contradictory initial state sends no RCON command");
        var changedLoader=new Session{SwapLoaderAfterSave=true};bool familyDenied=false;
        try{await SafeLifecycle.Stop(changedLoader,true,TimeSpan.FromSeconds(1));}catch(InvalidOperationException){familyDenied=true;}
        Check(familyDenied&&changedLoader.Commands==2&&changedLoader.Starts==0,"changed loader after SaveComplete blocks DoExit and restart");
        var cancelledSession=new Session();using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();bool cancelledBeforeCommand=false;
            try{await SafeLifecycle.Stop(cancelledSession,true,TimeSpan.FromSeconds(1),cancelled.Token);}catch(OperationCanceledException){cancelledBeforeCommand=true;}
            Check(cancelledBeforeCommand&&cancelledSession.Commands==0,"pre-cancelled lifecycle never invokes an adapter that could ignore cancellation");
        }
        var hanging=new Session{Hang=true};var watch=Stopwatch.StartNew();bool timedOut=false;
        try{await SafeLifecycle.Stop(hanging,true,TimeSpan.FromMilliseconds(100));}catch(OperationCanceledException){timedOut=true;}
        Check(timedOut&&watch.Elapsed<TimeSpan.FromSeconds(3)&&hanging.Starts==0,"deadline enforced even when adapter ignores cancellation");
    }
    static void VerifyBackupMembership(string root)
    {
        foreach(string selected in new[]{"world","plugins"})
        {
            string install=Path.Combine(root,"backup-membership-"+selected);
            string source=selected=="world"?Path.Combine(install,"ShooterGame","Saved","SavedArks"):
                Path.Combine(install,"ShooterGame","Binaries","Win64","ArkApi","Plugins","Synthetic");
            Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"existing.dat"),"original");
            string late=Path.Combine(source,"late-profile.dat"),target=Path.Combine(root,"backup-membership-result-"+selected);
            var map=new LocalMap{Name="Synthetic inventory",InstallationPath=install};
            bool refused=false;
            try
            {
                LocalBackupWorkflow.Create(map,new ProcessCheck(false,"synthetic stopped",null,true),target,
                    selected=="world"?LocalBackupScope.WorldAndProfiles:LocalBackupScope.Plugins,
                    _=>File.WriteAllText(late,"added while copying"));
            }
            catch(InvalidDataException){refused=true;}
            Check(refused&&!Directory.Exists(target)&&Directory.Exists(target+".incomplete")&&
                !File.Exists(Path.Combine(target+".incomplete","complete")),
                "new "+selected+" file during copy prevents publishing incomplete backup scope");
            Check(File.ReadAllText(late)=="added while copying"&&File.ReadAllText(Path.Combine(source,"existing.dat"))=="original",
                "refused backup preserves all original and newly added source data");
            Check(new UntamedOperationCoordinator().Observe(install)!.Phase==UntamedOperationPhase.NeedsOperator,
                "changed backup source leaves durable operator state without replay");
        }
    }
    static void VerifyConfigJson(string root)
    {
        string install=Path.Combine(root,"json-config-install");
        string plugins=Path.Combine(install,"ShooterGame","Binaries","Win64","ArkApi","Plugins","Synthetic");
        Directory.CreateDirectory(plugins);
        string path=Path.Combine(plugins,"config.json");
        var stopped=new ProcessCheck(false,"synthetic stopped",null,true);
        string original="{\r\n  // retained comment\r\n  \"limits\": {\"a\": 1},\r\n}\r\n";
        File.WriteAllText(path,original,new System.Text.UTF8Encoding(true));
        var snapshot=SafeConfigEditor.Read(path,install,ConfigKind.PluginJson,stopped);
        foreach(var duplicate in new[]{"{\"a\":1,\"a\":2}","{\"items\":[{\"x\":1,\"x\":2}]}","{\"a\":1,\"\\u0061\":2}"})
        {
            bool denied=false;try{SafeConfigEditor.Preview(snapshot,duplicate);}catch(InvalidDataException){denied=true;}
            Check(denied,"duplicate JSON property rejected including nested arrays and escaped names");
        }
        Check(SafeConfigEditor.Preview(snapshot,"{\"a\":1,\"A\":2,\"other\":{\"a\":3}}").Valid,
            "JSON case and separate object scopes follow ordinal property identity");
        bool unknown=false;try{SafeConfigEditor.Read(path,install,(ConfigKind)99,stopped);}catch(InvalidDataException){unknown=true;}
        Check(unknown,"unknown config kind cannot bypass schema validation");
        string edited=original.Replace("\"a\": 1","\"a\": 2");
        string backup=SafeConfigEditor.Save(snapshot,edited,install,stopped);
        Check(File.ReadAllText(path)==edited&&File.ReadAllText(backup)==original&&File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[]{239,187,191}),
            "valid JSON save preserves comments CRLF BOM and exact protective backup");
        bool readDenied=false;File.WriteAllText(path,"{\"nested\":{\"a\":1,\"a\":2}}");
        try{SafeConfigEditor.Read(path,install,ConfigKind.PluginJson,stopped);}catch(InvalidDataException){readDenied=true;}
        Check(readDenied,"ambiguous existing JSON is refused before editing");
    }
    sealed class Session : ILocalServerSession
    {
        public bool ContradictoryExit,Hang,InitialContradiction,SwapLoaderAfterSave;
        bool exiting,saved;public int Starts,Commands;
        readonly ProcessIdentity identity=new(){Pid=1,StartedUtc=DateTimeOffset.UtcNow,ExecutablePath=@"C:\synthetic\server.exe",Arguments="synthetic"};
        readonly ProcessIdentity loader=new(){Pid=2,StartedUtc=DateTimeOffset.UtcNow,ExecutablePath=@"C:\synthetic\loader.exe",Arguments="synthetic loader"};
        public ProcessCheck Check()=>new(true,"synthetic",identity,InitialContradiction||(exiting&&ContradictoryExit),
            SwapLoaderAfterSave?(saved?new ProcessIdentity{Pid=3,StartedUtc=loader.StartedUtc.AddSeconds(1),ExecutablePath=loader.ExecutablePath,Arguments=loader.Arguments}:loader):null);
        public Task Command(string command,CancellationToken ct)
        {Commands++;if(Hang)return new TaskCompletionSource().Task;if(command=="DoExit")exiting=true;return Task.CompletedTask;}
        public Task<bool> SaveCompleted(CancellationToken ct){saved=true;return Task.FromResult(true);}
        public void Start()=>Starts++;
    }
}
