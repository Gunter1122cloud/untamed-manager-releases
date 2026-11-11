using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

/// <summary>
/// Original Store-v1 live API Loader child -> existing RCON attach integration.
/// All fake ASA binaries, INI, Manager profile and process identities are
/// isolated under TEST TEMP. No real listener, RCON request or Store file is
/// opened; production has independent Windows WMI + child-port proof.
/// </summary>
internal static class OriginalStoreApiRconAttachTests
{
    private static void Check(bool pass,string reason)
    {
        if(!pass)throw new InvalidOperationException(
            "ORIGINAL STORE API RCON ATTACH FAILED: "+reason);
        Console.WriteLine("PASS original live Store-v1 API child RCON: "+reason);
    }
    private static async Task<JsonElement> Call(
        DesktopHistoricalRconAttachBridge bridge,string route,
        object? data=null)
    {
        var payload=new System.Collections.Generic.Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=route
        };
        if(data!=null)
        {
            using var parsed=JsonDocument.Parse(JsonSerializer.Serialize(data));
            foreach(var property in parsed.RootElement.EnumerateObject())
                payload[property.Name]=property.Value.Clone();
        }
        using var reply=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(payload)));
        return reply.RootElement.Clone();
    }

    public static async Task Run(string testRoot)
    {
        string install=Path.Combine(testRoot,"isolated-original-api-rcon-install");
        string binary=Path.Combine(install,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binary);
        string exe=Path.Combine(binary,"ArkAscendedServer.exe");
        string loader=Path.Combine(binary,"AsaApiLoader.exe");
        File.WriteAllBytes(exe,new byte[]{0x4d,0x5a,1,9});
        File.WriteAllBytes(loader,new byte[]{0x4d,0x5a,2,8});
        string ini=AsaInitialRconSettings.NewIniPath(install);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        const int port=37455;
        // Existing Kova INI may contain a short password with delimiters.
        // This is NOT eligible for fresh-server password generation.
        const string password="K0va=;[!]";
        File.WriteAllText(ini,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+port+"\r\nServerAdminPassword="+password+"\r\n");
        byte[] iniBefore=File.ReadAllBytes(ini);
        var now=DateTimeOffset.UtcNow.AddMinutes(-7);
        const int serverPid=70331,loaderPid=70330;
        var parent=new ProcessIdentity{
            Pid=loaderPid,StartedUtc=now,ExecutablePath=loader,
            Arguments="HISTORICAL_STORE_V1_API_LOADER_VERIFIED"
        };
        var child=new ProcessIdentity{
            Pid=serverPid,StartedUtc=now.AddSeconds(4),ExecutablePath=exe,
            Arguments="HISTORICAL_STORE_V1_API_CHILD_VERIFIED"
        };
        await using var cluster=new ClusterService(
            "isolated-original-api-rcon",
            Path.Combine(testRoot,"original-api-rcon-manager-profile"),
            _=>new ProcessCheck(false,"synthetic no real ASA",null,false));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="Genesis Part 1",
            InstallationPath=install,ExecutablePath=exe,
            HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('A',64),
            HistoricalMapKey="Genesis_WP",
            HistoricalEnabledMods=new(),
            HistoricalPassiveMods=new(),
            GamePort=7867,QueryPort=27167,
            Arguments="",ApiLoaderArguments="",
            StartWithAsaApi=true,ApiLoaderPath=loader,
            LastProcess=child,LastLoader=parent,
            AutoStart=false,MonitorRcon=false,RconPort=0
        };
        cluster.AddMap(map);
        var seen=new ProcessCheck(true,"Synthetic exact original API child",
            child,false,parent);
        bool portOwned=true,sourceVerified=true;
        using var bridge=new DesktopHistoricalRconAttachBridge(cluster,
            sourceGuard:_=>sourceVerified,
            apiObserver:_=>seen,
            listenerOwner:(p,pid)=>portOwned&&p==port&&pid==serverPid);

        var status=await Call(bridge,"historical.rconStatus",
            new{mapId=map.Id});
        Check(status.GetProperty("ok").GetBoolean()&&
              status.GetProperty("data").GetProperty("port").GetInt32()==0&&
              status.GetProperty("data").GetProperty("suggestedPort").GetInt32()==port,
            "original INI RCON port appears as setup suggestion without writing ASA or exposing password");
        var preview=await Call(bridge,"historical.rconPreview",
            new{mapId=map.Id,rconPort=port,password});
        Check(preview.GetProperty("ok").GetBoolean(),
            "exact original Loader child can attach existing RCON after #151 live import");
        string firstToken=preview.GetProperty("data")
            .GetProperty("token").GetString()!;

        seen=seen with{Identity=new ProcessIdentity{
            Pid=serverPid+1,StartedUtc=child.StartedUtc,
            ExecutablePath=exe,Arguments=child.Arguments
        }};
        var pidChanged=await Call(bridge,"historical.rconApply",
            new{token=firstToken,confirm=true});
        Check(!pidChanged.GetProperty("ok").GetBoolean() &&
              cluster.Config.LocalMaps.Single().RconPort==0,
            "PID change after preview denies RCON profile write");
        seen=new ProcessCheck(true,"Restored synthetic evidence",child,false,parent);

        portOwned=false;
        var wrongListener=await Call(bridge,"historical.rconPreview",
            new{mapId=map.Id,rconPort=port,password});
        Check(!wrongListener.GetProperty("ok").GetBoolean(),
            "another PID or missing TCP port ownership denies RCON attach");
        portOwned=true;
        seen=seen with{LoaderIdentity=new ProcessIdentity{
            Pid=loaderPid,StartedUtc=parent.StartedUtc.AddSeconds(11),
            ExecutablePath=loader,Arguments=parent.Arguments
        }};
        var parentReused=await Call(bridge,"historical.rconPreview",
            new{mapId=map.Id,rconPort=port,password});
        Check(!parentReused.GetProperty("ok").GetBoolean(),
            "reused Loader PID with changed start timestamp denies RCON attach");
        seen=new ProcessCheck(true,"Restored synthetic evidence",child,false,parent);
        sourceVerified=false;
        var sourceChanged=await Call(bridge,"historical.rconPreview",
            new{mapId=map.Id,rconPort=port,password});
        Check(!sourceChanged.GetProperty("ok").GetBoolean(),
            "missing original two-peer ACTIVE/DONE proof denies API RCON attach");
        sourceVerified=true;
        map.LastLoader!.Arguments="HISTORICAL_STORE_V1_API_LOADER_OBSERVATION";
        var forgedParent=await Call(bridge,"historical.rconPreview",
            new{mapId=map.Id,rconPort=port,password});
        Check(!forgedParent.GetProperty("ok").GetBoolean(),
            "unverified imported API Loader is not promoted to RCON owner");
        map.LastLoader.Arguments="HISTORICAL_STORE_V1_API_LOADER_VERIFIED";

        preview=await Call(bridge,"historical.rconPreview",
            new{mapId=map.Id,rconPort=port,password});
        Check(preview.GetProperty("ok").GetBoolean(),
            "fresh exact Loader/ASA child + real INI evidence yields new approval");
        var approvedToken=preview.GetProperty("data")
            .GetProperty("token").GetString()!;
        var noConsent=await Call(bridge,"historical.rconApply",
            new{token=approvedToken,confirm=false});
        Check(!noConsent.GetProperty("ok").GetBoolean(),
            "RCON profile attach requires an explicit user confirmation");
        var applied=await Call(bridge,"historical.rconApply",
            new{token=approvedToken,confirm=true});
        Check(applied.GetProperty("ok").GetBoolean() &&
              applied.GetProperty("data").GetProperty("originalStoreTouched")
                  .GetBoolean()==false,
            "fully verified API child RCON credentials committed only to Manager profile");
        var committed=cluster.Config.LocalMaps.Single();
        Check(committed.RconPort==port&&committed.StartWithAsaApi &&
              committed.LastProcess?.Pid==serverPid&&
              committed.LastLoader?.Pid==loaderPid&&
              SecureStore.Unprotect(committed.RconPasswordProtected)==password &&
              committed.RconHost=="127.0.0.1",
            "original GUID, launcher mode, child/parent PIDs and DPAPI credential preserved");
        var replay=await Call(bridge,"historical.rconApply",
            new{token=approvedToken,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "one-time API child RCON approval token cannot be replayed");
        Check(File.ReadAllBytes(ini).SequenceEqual(iniBefore),
            "original GameUserSettings.ini remains byte-identical");
    }
}
