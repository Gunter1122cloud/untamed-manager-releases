using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Real manager-profile mutations on isolated synthetic original Kova maps.
// Neither SteamCMD, RCON nor an ASA executable is ever started.
internal static class HistoricalModCopyTests
{
    private static void Check(bool ok,string label)
    {
        if(!ok)throw new InvalidOperationException(
            "HISTORICAL MOD COPY FAILED: "+label);
        Console.WriteLine("PASS original Kova multi-map mod sync: "+label);
    }

    private static async Task<JsonElement> Call(DesktopModCopyBridge bridge,
        string method,object? args=null)
    {
        var request=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method};
        if(args!=null)
        {
            using var source=JsonDocument.Parse(JsonSerializer.Serialize(args));
            foreach(var prop in source.RootElement.EnumerateObject())
                request[prop.Name]=prop.Value.Clone();
        }
        using var response=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(request)));
        return response.RootElement.Clone();
    }

    private static LocalMap Map(string root,string name,string key,
        int port,IEnumerable<string> enabled,IEnumerable<string> passive,
        IEnumerable<string> disabled)
    {
        string executable=Path.Combine(root,"ShooterGame","Binaries","Win64",
            "ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable,new byte[]{0x4d,0x5a,1,2});
        return new LocalMap{
            Id=Guid.NewGuid(),Name=name,InstallationPath=root,
            ExecutablePath=executable,HistoricalStoreProtected=true,
            HistoricalMapKey=key,HistoricalArchiveSha256=new string('D',64),
            GamePort=port,QueryPort=port+20000,RconPort=port+30000,
            Arguments="",ApiLoaderArguments="",
            HistoricalEnabledMods=enabled.ToList(),
            HistoricalPassiveMods=passive.ToList(),
            DisabledModIds=disabled.ToList()
        };
    }

    public static async Task Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"original-mod-sync");
        await using var cluster=new ClusterService("historical-mod-sync-ci",
            Path.Combine(root,"manager"),
            _=>new ProcessCheck(false,"original legacy argv unknown",null,false));
        var island=Map(Path.Combine(root,"TheIsland"),"The Island",
            "TheIsland_WP",7777,new[]{"123456","234567"},
            new[]{"234567"},new[]{"345678"});
        var center=Map(Path.Combine(root,"TheCenter"),"The Center",
            "TheCenter_WP",7797,new[]{"456789"},
            Array.Empty<string>(),new[]{"567890"});
        cluster.AddMap(island);
        cluster.AddMap(center);
        using var bridge=new DesktopModCopyBridge(cluster);
        var preview=await Call(bridge,"mods.copyPreview",new{
            sourceMapId=island.Id,targets=new[]{center.Id},mode="replace"
        });
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("changed").GetBoolean(),
            "replace preview accepts original Store-v1 source and target");
        Check(center.Arguments==""&&center.HistoricalEnabledMods.SequenceEqual(
                new[]{"456789"}),
            "preview does not edit original map parameters");
        string firstToken=preview.GetProperty("data").GetProperty("token").GetString()!;
        center.HistoricalEnabledMods=new List<string>{"999999"};
        var stale=await Call(bridge,"mods.copyApply",
            new{token=firstToken,confirm=true});
        Check(!stale.GetProperty("ok").GetBoolean()&&
              center.HistoricalEnabledMods.SequenceEqual(new[]{"999999"}),
            "changed historical target after approval cannot receive stale mod copy");
        center.HistoricalEnabledMods=new List<string>{"456789"};
        preview=await Call(bridge,"mods.copyPreview",new{
            sourceMapId=island.Id,targets=new[]{center.Id},mode="replace"
        });
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var saved=await Call(bridge,"mods.copyApply",new{token,confirm=true});
        Check(saved.GetProperty("ok").GetBoolean(),
            "native Manager commits approved historical mod replacement");
        var installed=cluster.Config.LocalMaps.Single(m=>m.Id==center.Id);
        Check(installed.HistoricalEnabledMods.SequenceEqual(new[]{"123456","234567"})&&
              installed.HistoricalPassiveMods.SequenceEqual(new[]{"234567"})&&
              installed.DisabledModIds.SequenceEqual(new[]{"345678"}),
            "original Store Start planner mod order and passive subset preserved");
        Check(installed.Arguments==""&&installed.ApiLoaderArguments==""&&
              installed.HistoricalMapKey=="TheCenter_WP"&&
              installed.HistoricalArchiveSha256==new string('D',64),
            "no raw Kova Store launch flags or identity modified by mod copy");
        var replay=await Call(bridge,"mods.copyApply",new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "approved copy token cannot be replayed");
    }
}
