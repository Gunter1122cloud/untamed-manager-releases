using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

/// <summary>
/// Synthetic API-Loader map: a different, independently started ASA/Loader
/// family is reconciled against an abandoned Restart-v1 record. No ASA
/// process, RCON socket, Store data or live server is touched.
/// </summary>
internal static class FreshAsaRestartReplacementReviewTests
{
    private static void Check(bool value,string reason)
    {
        if(!value)throw new InvalidOperationException(
            "FRESH ASA REPLACEMENT REVIEW: "+reason);
        Console.WriteLine("PASS fresh ASA replacement review: "+reason);
    }

    private static async Task<JsonElement> Call(
        Func<string,Task<string>> handle,string method,object args)
    {
        var body=new System.Collections.Generic.Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        using var values=JsonDocument.Parse(JsonSerializer.Serialize(args));
        foreach(var item in values.RootElement.EnumerateObject())
            body[item.Name]=item.Value.Clone();
        using var response=JsonDocument.Parse(await handle(JsonSerializer.Serialize(body)));
        return response.RootElement.Clone();
    }

    public static async Task Run(string testRoot)
    {
        string install=Path.Combine(testRoot,"fresh-replacement-review");
        string bin=Path.Combine(install,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(bin);
        string server=Path.Combine(bin,"ArkAscendedServer.exe");
        string loader=Path.Combine(bin,"AsaApiLoader.exe");
        // Same inert PE64 fixture format used by AsaApiLaunchPolicyTests.
        // Never execute these headers as native programs.
        byte[] pe=new byte[256];
        using(var ms=new MemoryStream(pe))
        using(var w=new BinaryWriter(ms))
        {
            w.Write((ushort)0x5A4D);ms.Position=0x3c;w.Write(0x40);
            ms.Position=0x40;w.Write(0x4550);w.Write((ushort)0x8664);
            ms.Position=0x58;w.Write((ushort)0x20b);
        }
        Directory.CreateDirectory(Path.Combine(bin,"ArkApi","Plugins"));
        File.WriteAllBytes(server,pe);
        File.WriteAllBytes(loader,pe);
        File.WriteAllBytes(Path.Combine(bin,"ArkApi","AsaApi.dll"),pe);
        File.WriteAllText(Path.Combine(bin,"config.json"),"{}");
        string manifest=Path.Combine(install,"steamapps","appmanifest_2430930.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest,"\"AppState\"\r\n{\r\n  \"appid\" \"2430930\"\r\n  \"buildid\" \"12345678\"\r\n}\r\n");
        const string password="SyntheticRconPassword!2026";
        const int gamePort=7777,queryPort=27015,rconPort=37570;
        string ini=AsaInitialRconSettings.NewIniPath(install);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+rconPort+"\r\nServerAdminPassword="+password+"\r\n");
        string args="Astraeos_WP?listen -Port="+gamePort+" -QueryPort="+queryPort;
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="Astraeos",InstallationPath=install,
            ExecutablePath=server,Arguments=args,
            ApiLoaderPath=loader,ApiLoaderArguments=args,StartWithAsaApi=true,
            GamePort=gamePort,QueryPort=queryPort,RconPort=rconPort,
            RconHost="127.0.0.1",RconPasswordProtected=SecureStore.Protect(password),
            AutoStart=false,MonitorRcon=true
        };
        const int oldPid=16476,replacementPid=2272,loaderPid=18336;
        DateTimeOffset failedAt=DateTimeOffset.UtcNow.AddMinutes(-12);
        var currentAsa=new ProcessIdentity{
            Pid=replacementPid,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-5),
            ExecutablePath=server,Arguments=args
        };
        var currentLoader=new ProcessIdentity{
            Pid=loaderPid,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-6),
            ExecutablePath=loader,Arguments=args
        };
        await using var cluster=new ClusterService("fresh-replacement-review",
            Path.Combine(testRoot,"fresh-replacement-profile"),
            _=>new ProcessCheck(true,"synthetic exact ASA/Loader family",
                currentAsa,false,currentLoader));
        cluster.AddMap(map);
        Check(AsaApiLaunchPolicy.Resolve(map).UseApi,
            "isolated API fixture passes the production launch file policy");
        ProcessCheck ObserveFamily(LocalMap requested)
        {
            Check(ReferenceEquals(requested,map),"review only observes the registered fixture map");
            return new ProcessCheck(true,"synthetic exact ASA/Loader family",
                currentAsa,false,currentLoader);
        }
        using(var lease=cluster.Operations.Acquire(install,UntamedOperationKind.Launch))
            lease.Complete();
        var restart=new AsaManualRestartJournal(install);
        var interrupted=restart.Begin(map.Id,oldPid);
        restart.Change(interrupted.Id,AsaRestartPhase.Starting);
        restart.Change(interrupted.Id,AsaRestartPhase.NeedsOperator);
        string key=UntamedOperationCoordinator.ResourceKey(install);
        string restartFile=Path.Combine(UntamedOperationCoordinator.DefaultRoot,
            "Restart-v1",key+".json");
        var failed=restart.Observe()!;
        File.WriteAllText(restartFile,JsonSerializer.Serialize(failed with{
            ChangedUtc=failedAt
        }));
        string originalFailedJournal=File.ReadAllText(restartFile);
        int ownerChecks=0;
        using var bridge=new DesktopFreshAsaRestartBridge(cluster,
            (port,pid)=>{
                ownerChecks++;
                return port==rconPort&&pid==replacementPid;
            },
            (_,_,_,_)=>throw new InvalidOperationException(
                "UM-TEST-UNEXPECTED-ASA-LIFECYCLE-DISPATCH"),ObserveFamily);
        var preview=await Call(bridge.HandleAsync,"servers.restartReviewPreview",
            new{mapId=map.Id});
        Check(preview.GetProperty("ok").GetBoolean(),
            "a separately started, newer API-Loader family can be previewed for audit: "+preview.GetRawText());
        var proof=preview.GetProperty("data");
        Check(proof.GetProperty("reviewMode").GetString()=="replacement"&&
              proof.GetProperty("oldPid").GetInt32()==oldPid&&
              proof.GetProperty("currentRunningPid").GetInt32()==replacementPid&&
              proof.GetProperty("currentLoaderPid").GetInt32()==loaderPid&&
              proof.GetProperty("launchParametersVerified").GetBoolean()&&
              proof.GetProperty("rconOwnerVerified").GetBoolean()&&
              proof.GetProperty("restartWillNotReplay").GetBoolean(),
            "preview pins old failed PID, new ASA PID, Loader PID, launch parameters and RCON owner separately");
        string token=proof.GetProperty("token").GetString()!;
        var applied=await Call(bridge.HandleAsync,"servers.restartReviewApply",
            new{token,confirm=true});
        Check(applied.GetProperty("ok").GetBoolean(),
            "explicit approval audits the failed Restart without replaying it: "+applied.GetRawText());
        var receipt=applied.GetProperty("data");
        Check(receipt.GetProperty("state").GetString()=="operatorReviewed"&&
              receipt.GetProperty("reviewMode").GetString()=="replacement"&&
              receipt.GetProperty("currentRunningPid").GetInt32()==replacementPid&&
              receipt.GetProperty("currentLoaderPid").GetInt32()==loaderPid&&
              !receipt.GetProperty("newRestartSucceeded").GetBoolean()&&
              !receipt.GetProperty("serverStarted").GetBoolean()&&
              !receipt.GetProperty("serverStopped").GetBoolean()&&
              !receipt.GetProperty("rconSent").GetBoolean(),
            "receipt explicitly proves journal-only review with no Start, Stop, Restart or RCON");
        var auditJson=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        auditJson.Converters.Add(new JsonStringEnumConverter());
        var archived=JsonSerializer.Deserialize<AsaRestartRecord>(
            File.ReadAllText(Path.Combine(UntamedOperationCoordinator.DefaultRoot,
                "Restart-v1",key+"-"+interrupted.Id.ToString("N")+
                ".operator-reviewed.json")),auditJson);
        Check(archived?.Phase==AsaRestartPhase.NeedsOperator&&
              archived.NewPid==0&&restart.Observe()?.Phase==AsaRestartPhase.OperatorReviewed&&
              restart.Observe()?.NewPid==0&&
              File.ReadAllText(restartFile)!=originalFailedJournal,
            "the original failed NeedsOperator outcome and NewPid=0 remain preserved in the audit");
        Check(ObserveFamily(map).Identity?.Pid==replacementPid&&
              ObserveFamily(map).LoaderIdentity?.Pid==loaderPid&&
              cluster.Operations.Observe(install)?.Phase==UntamedOperationPhase.Completed&&
              ownerChecks>=2,
            "current ASA/Loader and completed Launch journal remain intact after reconciliation");
        var replay=await Call(bridge.HandleAsync,"servers.restartReviewApply",
            new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "the journal-review approval token is one-shot and cannot be replayed");
    }
}
