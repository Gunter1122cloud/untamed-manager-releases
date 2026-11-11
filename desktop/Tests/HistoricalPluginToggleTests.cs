using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// File-changing native plugin enable/disable against isolated historical
// Kova-shaped TEMP install; no ASA process, Store, RCON or SQL mutation.
internal static class HistoricalPluginToggleTests
{
    private static void Check(bool yes,string label)
    {
        if(!yes)throw new InvalidOperationException(
            "HISTORICAL PLUGIN TOGGLE FAILED: "+label);
        Console.WriteLine("PASS original Store-v1 plugin toggle: "+label);
    }
    private static async Task<JsonElement> Call(DesktopPluginStateBridge bridge,
        string method,object? args=null)
    {
        var payload=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method};
        if(args!=null)
        {
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(args));
            foreach(var property in doc.RootElement.EnumerateObject())
                payload[property.Name]=property.Value.Clone();
        }
        using var result=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(payload)));
        return result.RootElement.Clone();
    }
    public static async Task Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"historic-plugin-toggle");
        string installation=Path.Combine(root,"asa-install");
        string executable=Path.Combine(installation,"ShooterGame","Binaries",
            "Win64","ArkAscendedServer.exe");
        string active=Path.Combine(installation,"ShooterGame","Binaries",
            "Win64","ArkApi","Plugins","ExampleOriginalPlugin");
        string disabled=Path.Combine(installation,"ShooterGame","Binaries",
            "Win64","UntamedDisabledPlugins","ExampleOriginalPlugin");
        Directory.CreateDirectory(active);
        File.WriteAllBytes(executable,new byte[]{0x4d,0x5a,0x00,0x01});
        File.WriteAllBytes(Path.Combine(active,"ExampleOriginalPlugin.dll"),
            new byte[]{0x4d,0x5a,0x02,0x03});
        File.WriteAllText(Path.Combine(active,"config.json"),"{\"enabled\":true}");
        File.WriteAllText(Path.Combine(active,"license.key"),"KEEP-ORIGINAL-LICENSE");
        await using var cluster=new ClusterService("historical-plugin-toggle-ci",
            Path.Combine(root,"manager-profile"),
            _=>new ProcessCheck(false,"legacy original argv not recognized",null,false));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",InstallationPath=installation,
            ExecutablePath=executable,HistoricalStoreProtected=true,
            HistoricalMapKey="TheIsland_WP",
            HistoricalArchiveSha256=new string('F',64),
            Arguments="",ApiLoaderArguments="",
            GamePort=7777,QueryPort=27015
        };
        cluster.AddMap(map);
        using var bridge=new DesktopPluginStateBridge(cluster);
        var preview=await Call(bridge,"plugins.togglePreview",new{
            mapId=map.Id,plugin="ExampleOriginalPlugin",enabled=false
        });
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("allowed").GetBoolean(),
            "native original Kova plugin preview is executable despite uncertain generic Check");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var applied=await Call(bridge,"plugins.toggleApply",new{token,confirm=true});
        Check(applied.GetProperty("ok").GetBoolean()&&
              applied.GetProperty("data").GetProperty("backupCreated").GetBoolean(),
            "original Kova plugin folder disabled with verified on-disk backup");
        Check(!Directory.Exists(active)&&Directory.Exists(disabled)&&
              File.ReadAllText(Path.Combine(disabled,"config.json"))=="{\"enabled\":true}"&&
              File.ReadAllText(Path.Combine(disabled,"license.key"))=="KEEP-ORIGINAL-LICENSE",
            "all historical plugin data and license files move unchanged");
        preview=await Call(bridge,"plugins.togglePreview",new{
            mapId=map.Id,plugin="ExampleOriginalPlugin",enabled=true
        });
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("allowed").GetBoolean(),
            "historical disabled plugin supports native re-enable preview");
        token=preview.GetProperty("data").GetProperty("token").GetString()!;
        applied=await Call(bridge,"plugins.toggleApply",new{token,confirm=true});
        Check(applied.GetProperty("ok").GetBoolean()&&
              Directory.Exists(active)&&!Directory.Exists(disabled),
            "original Store-v1 plugin can be re-enabled without starting ASA");
    }
}
