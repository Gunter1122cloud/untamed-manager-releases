using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Real native mode-persistence route against an isolated synthetic historical
// profile. Does not create an ASA process, RCON listener, Store writer or SQL.
internal static class HistoricalAsaApiModeTests
{
    private static void Check(bool pass,string what)
    {
        if(!pass)throw new InvalidOperationException(
            "HISTORICAL ASA API MODE REGRESSION FAILED: "+what);
        Console.WriteLine("PASS historical per-map ASA API: "+what);
    }

    private static async Task<JsonElement> Call(
        DesktopHistoricalActiveStartBridge bridge,string method,object? args=null)
    {
        var request=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(args!=null)
        {
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(args));
            foreach(var field in doc.RootElement.EnumerateObject())
                request[field.Name]=field.Value.Clone();
        }
        using var result=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(request)));
        return result.RootElement.Clone();
    }

    public static async Task Run(string root)
    {
        string installation=Path.Combine(root,"historical-api-mode-without-rcon");
        string binary=Path.Combine(installation,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binary);
        string server=Path.Combine(binary,"ArkAscendedServer.exe");
        File.WriteAllBytes(server,new byte[]{0x4d,0x5a,0x90,0x00});
        string profile=Path.Combine(root,"historical-api-mode-manager");
        await using var cluster=new ClusterService("historical-api-mode-ci",profile,
            _=>new ProcessCheck(false,"synthetic stopped",null,true));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",InstallationPath=installation,
            ExecutablePath=server,HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('A',64),
            HistoricalMapKey="TheIsland_WP",
            GamePort=7777,QueryPort=27015,RconPort=0,
            Arguments="",ApiLoaderArguments="",
            StartWithAsaApi=false,
            AutoStart=false,MonitorRcon=false
        };
        cluster.AddMap(map);
        // Exact process/PE64 checks belong to production PhysicalModePreflight.
        // Only the explicit stopped-family check is injected for this fixture.
        using var bridge=new DesktopHistoricalActiveStartBridge(
            cluster,null,null,loaderModePreflight:(_,_)=>true);
        var status=await Call(bridge,"historical.loaderModeStatus",
            new{mapId=map.Id});
        Check(status.GetProperty("ok").GetBoolean()&&
              status.GetProperty("data").GetProperty("canChangeMode").GetBoolean()&&
              !status.GetProperty("data").GetProperty("useApi").GetBoolean(),
            "original Store-v1 mode status is editable with RCONPort=0");

        var preview=await Call(bridge,"historical.loaderModePreview",
            new{mapId=map.Id,useApi=true});
        Check(preview.GetProperty("ok").GetBoolean()&&
              !map.StartWithAsaApi,
            "preview does not mutate historical map or launch ASA");
        string token=preview.GetProperty("data")
            .GetProperty("token").GetString()!;
        var committed=await Call(bridge,"historical.loaderModeApply",
            new{token,confirm=true});
        Check(committed.GetProperty("ok").GetBoolean()&&
              cluster.Config.LocalMaps.Single().StartWithAsaApi&&
              cluster.Config.LocalMaps.Single().RconPort==0,
            "one-shot approval persists ASA API preference without RCON");
        var replay=await Call(bridge,"historical.loaderModeApply",
            new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "historical mode approval cannot be replayed");
        var deniedStart=await Call(bridge,"historical.activeStartPreview",
            new{mapId=map.Id});
        Check(!deniedStart.GetProperty("ok").GetBoolean(),
            "missing real RCON still blocks Start even after choosing API");
    }
}
