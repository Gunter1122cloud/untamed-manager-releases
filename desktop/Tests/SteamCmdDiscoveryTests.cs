using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

/// <summary>
/// Existing SteamCMD discovery is read-only until a one-shot operator
/// acceptance. Tests use temporary files and synthetic map observers only.
/// </summary>
internal static class SteamCmdDiscoveryTests
{
    private static void Check(bool value,string reason)
    {
        if(!value)throw new InvalidOperationException(
            "STEAMCMD DISCOVERY TEST FAILED: "+reason);
        Console.WriteLine("PASS SteamCMD discovery: "+reason);
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

    private static void WriteMinimalPe(string path,byte marker)
    {
        byte[] image=new byte[512];
        image[0]=(byte)'M';image[1]=(byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(image,0x3c);
        image[0x80]=(byte)'P';image[0x81]=(byte)'E';
        BitConverter.GetBytes((ushort)0x014c).CopyTo(image,0x84);
        BitConverter.GetBytes((ushort)1).CopyTo(image,0x86);
        image[0x88]=marker;
        BitConverter.GetBytes((ushort)0x00e0).CopyTo(image,0x94);
        File.WriteAllBytes(path,image);
    }

    public static async Task Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"steamcmd-discovery");
        string roaming=Path.Combine(root,"roaming");
        string local=Path.Combine(root,"local");
        string x86=Path.Combine(root,"program-files-x86");
        string pf=Path.Combine(root,"program-files");
        Guid mapId=Guid.NewGuid();
        var maps=new[]{new LocalMap{Id=mapId,Name="Astraeos"},
            new LocalMap{Id=Guid.NewGuid(),Name="Island"}};
        var paths=DesktopStoppedSteamUpdateBridge.DefaultSteamCmdCandidates(
            mapId,maps,roaming,local,x86,pf).ToArray();
        string expected=Path.Combine(roaming,"Kova","tools",mapId.ToString("D"),
            "SteamCMD","steamcmd.exe");
        Check(paths.Any(x=>string.Equals(x.Path,expected,StringComparison.OrdinalIgnoreCase)&&
            x.Source=="Kova korábbi map-eszköze"),
            "legacy Kova per-map SteamCMD location is derived from the selected MapID");
        Check(!paths.Any(x=>x.Path.Contains(maps[1].Id.ToString("D"),
            StringComparison.OrdinalIgnoreCase)),
            "discovery does not scan unrelated map tool folders");

        string first=Path.Combine(root,"legacy","steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        WriteMinimalPe(first,1);
        var before=SteamCmdPreflight.Inspect(first);
        Check(before.LengthBytes==512&&before.Sha256.Length==64,
            "SteamCMD candidate is fingerprinted without executing the binary");
        string invalid=Path.Combine(root,"invalid","steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(invalid)!);
        File.WriteAllBytes(invalid,new byte[512]);
        bool invalidRejected=false;
        try{_=SteamCmdPreflight.Inspect(invalid);}
        catch(InvalidDataException ex){invalidRejected=ex.Message=="UM-STEAMCMD-NOT-WINDOWS-PE";}
        Check(invalidRejected,
            "an arbitrary non-PE file cannot be persisted as a SteamCMD executable");

        string install=Path.Combine(root,"asa");
        string exe=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe,new byte[]{0x4d,0x5a,0x90,0x00});
        int processChecks=0;
        await using var cluster=new ClusterService("steamcmd-discovery-test",
            Path.Combine(root,"profile"),
            _=>{processChecks++;return new ProcessCheck(false,
                "synthetic stopped",null,true);});
        var map=new LocalMap{Id=mapId,Name="Astraeos",
            InstallationPath=install,ExecutablePath=exe};
        cluster.AddMap(map);
        using var bridge=new DesktopStoppedSteamUpdateBridge(cluster,
            candidatePaths:_=>new[]{(first,"Kova korábbi map-eszköze")});
        var discovery=await Call(bridge.HandleAsync,"steam.discoverExecutables",
            new{mapId});
        Check(discovery.GetProperty("ok").GetBoolean()&&
              discovery.GetProperty("data").GetProperty("updateStarted").GetBoolean()==false&&
              discovery.GetProperty("data").GetProperty("serverStopped").GetBoolean()==false,
            "discovery does not start, stop or update an ASA server");
        var candidates=discovery.GetProperty("data").GetProperty("candidates");
        Check(candidates.GetArrayLength()==1&&
              candidates[0].GetProperty("sha256").GetString()==before.Sha256,
            "only the verified existing local SteamCMD candidate is offered");
        string token=candidates[0].GetProperty("token").GetString()!;
        var accepted=await Call(bridge.HandleAsync,"steam.acceptDiscoveredExecutable",
            new{mapId,token,confirm=true});
        Check(accepted.GetProperty("ok").GetBoolean()&&
              accepted.GetProperty("data").GetProperty("configured").GetBoolean()&&
              accepted.GetProperty("data").GetProperty("updateStarted").GetBoolean()==false&&
              accepted.GetProperty("data").GetProperty("serverStopped").GetBoolean()==false&&
              cluster.Config.SteamCmdExecutable==Path.GetFullPath(first),
            "explicit one-shot approval persists the exact existing SteamCMD path without running it");
        var replay=await Call(bridge.HandleAsync,"steam.acceptDiscoveredExecutable",
            new{mapId,token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "accepted discovery token cannot be replayed");

        string changed=Path.Combine(root,"changed","steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(changed)!);
        WriteMinimalPe(changed,2);
        using var changedBridge=new DesktopStoppedSteamUpdateBridge(cluster,
            candidatePaths:_=>new[]{(changed,"synthetic existing SteamCMD")});
        var changedDiscovery=await Call(changedBridge.HandleAsync,
            "steam.discoverExecutables",new{mapId});
        string changedToken=changedDiscovery.GetProperty("data")
            .GetProperty("candidates")[0].GetProperty("token").GetString()!;
        WriteMinimalPe(changed,3);
        var changedApply=await Call(changedBridge.HandleAsync,
            "steam.acceptDiscoveredExecutable",new{mapId,token=changedToken,confirm=true});
        Check(!changedApply.GetProperty("ok").GetBoolean()&&
              changedApply.GetProperty("error").GetString()==
                "UM-STEAMCMD-DISCOVERY-CANDIDATE-CHANGED"&&
              cluster.Config.SteamCmdExecutable==Path.GetFullPath(first),
            "changed candidate bytes invalidate approval and leave the saved SteamCMD path unchanged");
        Check(processChecks==0,
            "SteamCMD discovery and configuration never inspect or mutate live ASA processes");

        string diagnosticInstall=Path.Combine(root,"diagnostic-asa");
        string diagnosticExe=Path.Combine(diagnosticInstall,"ShooterGame",
            "Binaries","Win64","ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(diagnosticExe)!);
        File.WriteAllBytes(diagnosticExe,new byte[]{0x4d,0x5a,0x90,0x00});
        await using var diagnosticCluster=new ClusterService("steam-journal-diagnostics",
            Path.Combine(root,"diagnostic-profile"),
            _=>new ProcessCheck(false,"synthetic stopped",null,true));
        var diagnosticMap=new LocalMap{Id=Guid.NewGuid(),Name="Astraeos",
            InstallationPath=diagnosticInstall,ExecutablePath=diagnosticExe,
            AutoStart=false,RconPort=37085};
        diagnosticCluster.AddMap(diagnosticMap);
        var restart=new AsaManualRestartJournal(diagnosticInstall);
        var claim=restart.Begin(diagnosticMap.Id,16476);
        restart.Change(claim.Id,AsaRestartPhase.Starting);
        restart.Change(claim.Id,AsaRestartPhase.NeedsOperator);
        using var diagnosticBridge=new DesktopStoppedSteamUpdateBridge(diagnosticCluster,
            candidatePaths:_=>Array.Empty<(string Path,string Source)>());
        var diagnostic=await Call(diagnosticBridge.HandleAsync,
            "steam.stoppedDiagnostics",new{mapId=diagnosticMap.Id});
        var data=diagnostic.GetProperty("data");
        Check(diagnostic.GetProperty("ok").GetBoolean()&&
              data.GetProperty("journalBlocked").GetBoolean()&&
              data.GetProperty("localJournalBlocked").GetBoolean()&&
              !data.GetProperty("queueJournalBlocked").GetBoolean()&&
              data.GetProperty("restartJournalBlockingCode").GetString()==
                "UM-STEAM-LOCAL-RESTART-JOURNAL-NEEDS-REVIEW"&&
              data.GetProperty("journalBlockers").GetArrayLength()>0,
            "update diagnostics report a pending local Restart-v1 journal even with no blocked Steam queue");
    }
}
