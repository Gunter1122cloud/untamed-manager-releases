using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

internal static class OwnedAsaKillBridgeTests
{
    private static void Check(bool pass,string label)
    {
        if(!pass)throw new InvalidOperationException("OWNED ASA KILL: "+label);
        Console.WriteLine("PASS owned ASA Kill bridge: "+label);
    }
    private static async Task<JsonElement> Call(DesktopOwnedAsaKillBridge bridge,
        string method,object? args=null)
    {
        var input=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(args!=null)
        {
            var other=JsonSerializer.SerializeToElement(args);
            foreach(var field in other.EnumerateObject())input[field.Name]=field.Value.Clone();
        }
        using var doc=JsonDocument.Parse(await bridge.HandleAsync(JsonSerializer.Serialize(input)));
        return doc.RootElement.Clone();
    }
    internal static async Task Run(string root)
    {
        string profile=Path.Combine(root,"owned-kill-manager");
        string install=Path.Combine(root,"owned-kill-asa");
        Directory.CreateDirectory(install);
        await using var cluster=new ClusterService("owned-kill-test",profile,
            _=>new ProcessCheck(false,"Synthetic verified stopped",null,true));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="Synthetic The Island",
            InstallationPath=install,ExecutablePath=Path.Combine(
                install,"ShooterGame","Binaries","Win64","ArkAscendedServer.exe"),
            GamePort=46500,QueryPort=46501,RconPort=46502
        };
        cluster.AddMap(map);
        var identity=new ProcessIdentity{Pid=23456,StartedUtc=DateTimeOffset.UtcNow,
            ExecutablePath=map.ExecutablePath,Arguments="TheIsland_WP?listen"};
        int kills=0;
        var bridge=new DesktopOwnedAsaKillBridge(cluster,
            _=>identity,
            current=>{kills++;return current.Pid==identity.Pid;});
        var unowned=await Call(bridge,"servers.ownedKillPreview",
            new{mapId=Guid.NewGuid()});
        Check(!unowned.GetProperty("ok").GetBoolean()&&kills==0,
            "unregistered remote or arbitrary map cannot preview Kill");
        var preview=await Call(bridge,"servers.ownedKillPreview",new{mapId=map.Id});
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("pid").GetInt32()==identity.Pid&&
              preview.GetProperty("data").GetProperty("serverDataLossPossible").GetBoolean(),
            "native preview pins exact process identity and discloses unsaved-world risk");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var refused=await Call(bridge,"servers.ownedKillApply",
            new{token,typedApproval="STOP",confirm=true});
        Check(!refused.GetProperty("ok").GetBoolean()&&kills==0,
            "one-use Kill cannot bypass typed approval");
        var applied=await Call(bridge,"servers.ownedKillApply",
            new{token,typedApproval="KILL LOCAL ASA",confirm=true});
        Check(applied.GetProperty("ok").GetBoolean()&&
              applied.GetProperty("data").GetProperty("confirmedStopped").GetBoolean()&&kills==1,
            "approved exact PID Kill runs one native termination and verifies stopped state");
        var replay=await Call(bridge,"servers.ownedKillApply",
            new{token,typedApproval="KILL LOCAL ASA",confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean()&&kills==1,
            "approved Kill token cannot be replayed");
        cluster.Config.LocalMaps.Single(m=>m.Id==map.Id).HistoricalStoreProtected=true;
        var protectedMap=await Call(bridge,"servers.ownedKillPreview",
            new{mapId=map.Id});
        Check(!protectedMap.GetProperty("ok").GetBoolean()&&kills==1,
            "original Kova Store-v1 map remains blocked from emergency Kill");
    }
}
