using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Native file-picker delegates use a synthetic external JSON file in TEMP.
// No RCON, SteamCMD, LAN writer, ASA process or production Legacy data touched.
internal static class HistoricalModProfileTests
{
    private static void Check(bool passed,string label)
    {
        if(!passed)throw new InvalidOperationException(
            "HISTORICAL MOD PROFILE FAILED: "+label);
        Console.WriteLine("PASS original Legacy portable mod profile: "+label);
    }
    private static async Task<JsonElement> Call(DesktopModProfileBridge bridge,
        string method,object? args=null)
    {
        var payload=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method};
        if(args!=null)
        {
            using var parsed=JsonDocument.Parse(JsonSerializer.Serialize(args));
            foreach(var item in parsed.RootElement.EnumerateObject())
                payload[item.Name]=item.Value.Clone();
        }
        using var response=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(payload)));
        return response.RootElement.Clone();
    }
    private static LocalMap Map(string folder,string name,string mapKey,
        int gamePort,string[] mods,string[] passive,string[] disabled)
    {
        string exe=Path.Combine(folder,"ShooterGame","Binaries","Win64",
            "ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe,new byte[]{0x4d,0x5a,0,1});
        return new LocalMap{
            Id=Guid.NewGuid(),Name=name,InstallationPath=folder,
            ExecutablePath=exe,HistoricalStoreProtected=true,
            HistoricalMapKey=mapKey,
            HistoricalArchiveSha256=new string('E',64),
            Arguments="",ApiLoaderArguments="",
            GamePort=gamePort,QueryPort=gamePort+20000,
            HistoricalEnabledMods=mods.ToList(),
            HistoricalPassiveMods=passive.ToList(),
            DisabledModIds=disabled.ToList()
        };
    }
    public static async Task Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"historical-mod-profile");
        string json=Path.Combine(testRoot,"historical-legacy-mod-profile-transfer.json");
        await using var cluster=new ClusterService("historical-mod-profile-ci",
            Path.Combine(root,"local-manager"),
            _=>new ProcessCheck(false,"legacy process arguments uncertain",null,false));
        var source=Map(Path.Combine(root,"TheIsland"),"The Island",
            "TheIsland_WP",7777,new[]{"123456","234567"},
            new[]{"234567"},new[]{"345678"});
        var target=Map(Path.Combine(root,"TheCenter"),"The Center",
            "TheCenter_WP",7797,new[]{"456789"},
            Array.Empty<string>(),new[]{"567890"});
        cluster.AddMap(source);
        cluster.AddMap(target);
        using var bridge=new DesktopModProfileBridge(cluster,
            nativeSave:_=>json,nativeOpen:()=>json);
        var exported=await Call(bridge,"mods.profileExport",new{mapId=source.Id});
        Check(exported.GetProperty("ok").GetBoolean()&&File.Exists(json),
            "original Store-v1 source mod profile exported via real native file operation");
        string data=File.ReadAllText(json);
        Check(!data.Contains("TheIsland_WP",StringComparison.Ordinal)&&
              !data.Contains("ArkAscendedServer",StringComparison.Ordinal)&&
              !data.Contains("HistoricalArchiveSha256",StringComparison.Ordinal),
            "portable profile contains mod IDs only, never Store identity or launch parameters");
        var preview=await Call(bridge,"mods.profileImportPreview",new{
            targets=new[]{target.Id},mode="replace"
        });
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("changed").GetBoolean()&&
              target.HistoricalEnabledMods.SequenceEqual(new[]{"456789"}),
            "destination historical map receives genuine preview without writes");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var applied=await Call(bridge,"mods.profileImportApply",
            new{token,confirm=true});
        Check(applied.GetProperty("ok").GetBoolean(),
            "approved portable Legacy mod profile imported into Manager");
        var current=cluster.Config.LocalMaps.Single(x=>x.Id==target.Id);
        Check(current.HistoricalEnabledMods.SequenceEqual(
                new[]{"123456","234567"})&&
              current.HistoricalPassiveMods.SequenceEqual(new[]{"234567"})&&
              current.DisabledModIds.SequenceEqual(new[]{"345678"})&&
              current.Arguments==""&&current.ApiLoaderArguments==""&&
              current.HistoricalMapKey=="TheCenter_WP",
            "target mod order persisted while original Store launch fields remain unchanged");
        var replay=await Call(bridge,"mods.profileImportApply",new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "native portable mod-profile import approval is single-use");
    }
}
