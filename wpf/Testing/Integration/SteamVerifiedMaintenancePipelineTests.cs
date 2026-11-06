using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class SteamVerifiedMaintenancePipelineTests
{
    private static void Assert(bool valid,string description)
    {
        if(!valid)throw new InvalidOperationException("STEAM MAINTENANCE PIPELINE: "+description);
        Console.WriteLine("PASS steam maintenance pipeline: "+description);
    }
    private sealed class FakeCertifiedAdapter : ISteamVerifiedMaintenanceAdapter
    {
        public LocalMap Map {get;}
        public bool Running {get;private set;}
        public bool DenySave {get;set;}
        public bool BadBackup {get;set;}
        public bool BadEvidence {get;set;}
        public bool FailUpdate {get;set;}
        public bool FailReadiness {get;set;}
        public int DoExitCount {get;private set;}
        public int UpdateCount {get;private set;}
        public int RestartCount {get;private set;}
        public int SaveCommandCount {get;private set;}
        public string Steps {get;private set;}="";
        private bool saveRequested;
        private readonly ProcessIdentity original;
        private ProcessIdentity current;
        private static readonly string Receipt=new string('A',64);
        public FakeCertifiedAdapter(string root,bool initiallyRunning)
        {
            string bin=Path.Combine(root,"ShooterGame","Binaries","Win64");
            Directory.CreateDirectory(bin);
            string exe=Path.Combine(bin,"ArkAscendedServer.exe");
            File.WriteAllText(exe,"CI fake ASA executable; never launched");
            string manifests=Path.Combine(root,"steamapps");
            Directory.CreateDirectory(manifests);
            File.WriteAllText(Path.Combine(manifests,"appmanifest_2430930.acf"),
                "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123456\"\n}\n");
            Map=new LocalMap{Id=Guid.NewGuid(),Name="The Island",
                InstallationPath=root,ExecutablePath=exe};
            original=new ProcessIdentity{Pid=1122,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-10),
                ExecutablePath=exe,Arguments="TheIsland_WP?listen"};
            current=original;
            Running=initiallyRunning;
        }
        public ProcessCheck Check()=>Running?
            new(true,"CI synthetic running",current,false):
            new(false,"CI synthetic stopped",null,true);
        public Task Command(string cmd,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(!Running)throw new InvalidOperationException("Synthetic server is stopped");
            if(cmd=="SaveWorld")
            {
                saveRequested=true;SaveCommandCount++;Steps+="save;";
            }
            else if(cmd=="DoExit")
            {
                if(!saveRequested||DenySave)
                    throw new InvalidOperationException("Synthetic SaveComplete not proven");
                DoExitCount++;Running=false;Steps+="stop;";
            }
            else if(cmd.StartsWith("ServerChat Server maintenance:",StringComparison.Ordinal))
                Steps+="notify;";
            else throw new UnauthorizedAccessException("Synthetic test command rejected");
            return Task.CompletedTask;
        }
        public Task<bool> SaveCompleted(CancellationToken ct)=>
            Task.FromResult(saveRequested&&!DenySave);
        public Task<string> CreateVerifiedBackup(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(Running)throw new InvalidOperationException("Backup must follow stop");
            Steps+="backup;";
            return Task.FromResult(BadBackup?"bad":Receipt);
        }
        public Task<SteamMaintenanceEvidence> UpdateAndVerifySteamAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(Running)throw new InvalidOperationException("SteamCMD must not touch running map");
            UpdateCount++;Steps+="update;";
            if(FailUpdate)throw new IOException("Synthetic SteamCMD failure");
            var manifest=Path.Combine(Map.InstallationPath,"steamapps",
                "appmanifest_2430930.acf");
            File.WriteAllText(manifest,
                "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123457\"\n}\n");
            return Task.FromResult(new SteamMaintenanceEvidence(Map.Id,"123456",
                "123457",BadEvidence?"wrong":Receipt,true,DateTimeOffset.UtcNow));
        }
        public void Start()
        {
            if(Running)throw new InvalidOperationException("Already running");
            RestartCount++;Steps+="restart;";
            Running=true;
            current=new ProcessIdentity{Pid=2233,StartedUtc=DateTimeOffset.UtcNow,
                ExecutablePath=Map.ExecutablePath,Arguments=original.Arguments};
        }
        public Task<AsaReadinessSnapshot> ProbeProductionReadiness(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Steps+="ready;";
            return Task.FromResult(new AsaReadinessSnapshot(Map.Id,DateTimeOffset.UtcNow,
                AsaRuntimeStage.RconAuthenticated,true,!FailReadiness,!FailReadiness,
                "CI ONLY synthetic API/RCON/join signal"));
        }
    }
    private static string Fixture(string root,string name)=>Path.Combine(
        root,"steam-maintenance-pipeline-"+name+"-"+Guid.NewGuid().ToString("N"));

    public static async Task Run(string root)
    {
        var successful=new FakeCertifiedAdapter(Fixture(root,"running-ok"),true);
        var allowed=ReleaseCapabilityGate.Check(ManagerOperation.LiveSteamUpdate,true,
            confirmedStopped:true,running:false,realSaveWorldProof:true);
        Assert(!allowed.Allowed,"a synthetic SaveComplete witness never unlocks production Steam update");
        bool publicBlocked=false;
        try{await SteamVerifiedMaintenancePipeline.ExecuteAsync(successful,true,
            TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5));}
        catch(InvalidOperationException){publicBlocked=true;}
        Assert(publicBlocked&&successful.SaveCommandCount==0&&successful.UpdateCount==0,
            "public pipeline does not alter a live server without production certification");
        var result=await SteamVerifiedMaintenancePipeline.RunSyntheticFixtureAsync(
            successful,true,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5));
        Assert(result.WasRunning&&result.Restarted&&
            result.OldBuild=="123456"&&result.NewBuild=="123457" &&
            successful.DoExitCount==1&&successful.UpdateCount==1&&
            successful.RestartCount==1&&successful.Running,
            "certified-contract fixture saves, stops, verifies Steam and restarts only once");
        Assert(successful.Steps=="notify;save;stop;backup;update;restart;ready;",
            "exact maintenance order: warn, SaveComplete, DoExit, backup, update, restart, readiness");

        var stopped=new FakeCertifiedAdapter(Fixture(root,"stopped"),false);
        var stoppedResult=await SteamVerifiedMaintenancePipeline.RunSyntheticFixtureAsync(
            stopped,true,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5));
        Assert(!stoppedResult.WasRunning&&!stoppedResult.Restarted&&
            stopped.SaveCommandCount==0&&stopped.RestartCount==0&&!stopped.Running &&
            stopped.UpdateCount==1,
            "originally stopped ASA remains stopped after file update");

        var unproven=new FakeCertifiedAdapter(Fixture(root,"save-not-complete"),true){
            DenySave=true};
        bool blockedSave=false;
        try{await SteamVerifiedMaintenancePipeline.RunSyntheticFixtureAsync(
            unproven,true,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5));}
        catch(InvalidOperationException){blockedSave=true;}
        Assert(blockedSave&&unproven.Running&&unproven.DoExitCount==0&&
            unproven.UpdateCount==0&&unproven.RestartCount==0,
            "failure to prove actual SaveComplete blocks DoExit, SteamCMD and restart");

        var badBackup=new FakeCertifiedAdapter(Fixture(root,"bad-backup"),true){
            BadBackup=true};
        bool blockedBackup=false;
        try{await SteamVerifiedMaintenancePipeline.RunSyntheticFixtureAsync(
            badBackup,true,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5));}
        catch(InvalidDataException){blockedBackup=true;}
        Assert(blockedBackup&&!badBackup.Running&&badBackup.UpdateCount==0,
            "missing backup digest blocks update after safe stop");

        var failedUpdate=new FakeCertifiedAdapter(Fixture(root,"steam-fail"),true){
            FailUpdate=true};
        bool updateFailed=false;
        try{await SteamVerifiedMaintenancePipeline.RunSyntheticFixtureAsync(
            failedUpdate,true,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5));}
        catch(IOException){updateFailed=true;}
        Assert(updateFailed&&!failedUpdate.Running&&failedUpdate.RestartCount==0,
            "failed SteamCMD prevents automatic restart or next-map progression");

        var fakeEvidence=new FakeCertifiedAdapter(Fixture(root,"bad-evidence"),false){
            BadEvidence=true};
        bool evidenceRejected=false;
        try{await SteamVerifiedMaintenancePipeline.RunSyntheticFixtureAsync(
            fakeEvidence,true,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5));}
        catch(InvalidDataException){evidenceRejected=true;}
        Assert(evidenceRejected&&fakeEvidence.RestartCount==0,
            "wrong backup identity cannot certify a Steam update");
    }
}
