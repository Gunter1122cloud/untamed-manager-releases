using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Isolated synthetic original Kova installation: tests actual native file
// replacement and backup. Never launches ASA, uses RCON, or opens a Store writer.
internal static class HistoricalPluginZipTests
{
    private static void Check(bool yes,string why)
    {
        if(!yes)throw new InvalidOperationException(
            "ORIGINAL KOVA PLUGIN ZIP TEST FAILED: "+why);
        Console.WriteLine("PASS original Kova offline plugin ZIP: "+why);
    }
    private static async Task<JsonElement> Call(DesktopPluginZipBridge bridge,
        string method,object? args=null)
    {
        var data=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method};
        if(args!=null)
        {
            using var document=JsonDocument.Parse(JsonSerializer.Serialize(args));
            foreach(var field in document.RootElement.EnumerateObject())
                data[field.Name]=field.Value.Clone();
        }
        using var reply=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(data)));
        return reply.RootElement.Clone();
    }

    public static async Task Run(string root)
    {
        string install=Path.Combine(root,"original-kova-offline-plugin-zip-install");
        string bin=Path.Combine(install,"ShooterGame","Binaries","Win64");
        string server=Path.Combine(bin,"ArkAscendedServer.exe");
        string plugin=Path.Combine(bin,"ArkApi","Plugins","ExampleKovaPlugin");
        Directory.CreateDirectory(plugin);
        File.WriteAllBytes(server,new byte[]{0x4d,0x5a,0x01,0x02});
        string dll=Path.Combine(plugin,"ExampleKovaPlugin.dll");
        string config=Path.Combine(plugin,"config.json");
        string license=Path.Combine(plugin,"license.key");
        File.WriteAllBytes(dll,new byte[]{0x4d,0x5a,1,1});
        File.WriteAllText(config,"{\"storeSetting\":true}");
        File.WriteAllText(license,"EXAMPLE-KEEP");
        string externalStore=Path.Combine(root,"original-store-v1-untouched.marker");
        File.WriteAllText(externalStore,"STORE-DO-NOT-WRITE");

        string archive=Path.Combine(root,"example-kova-upgrade.zip");
        using(var stream=File.Create(archive))
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
        {
            using var item=zip.CreateEntry(
                "ExampleKovaPlugin/ExampleKovaPlugin.dll").Open();
            byte[] newBytes={0x4d,0x5a,0x02,0x03,0x04};
            item.Write(newBytes);
        }
        bool running=false;
        await using var cluster=new ClusterService("original-plugin-zip-test",
            Path.Combine(root,"original-plugin-zip-manager"),
            _=>running?
                new ProcessCheck(true,"synthetic running",new ProcessIdentity{
                    Pid=55432,StartedUtc=DateTimeOffset.UtcNow,
                    ExecutablePath=server,Arguments="synthetic"},false):
                new ProcessCheck(false,"original argv unknown",null,false));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",InstallationPath=install,
            ExecutablePath=server,HistoricalStoreProtected=true,
            HistoricalMapKey="TheIsland_WP",
            HistoricalArchiveSha256=new string('C',64),
            Arguments="",ApiLoaderArguments="",
            GamePort=7777,QueryPort=27015
        };
        cluster.AddMap(map);
        using var bridge=new DesktopPluginZipBridge(cluster,()=>archive);
        var preview=await Call(bridge,"plugins.zipSelect",new{mapId=map.Id});
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("previousExists").GetBoolean(),
            "historical stopped map opens real native ZIP preview without RCON");
        string one=preview.GetProperty("data").GetProperty("token").GetString()!;
        running=true;
        var stopped=await Call(bridge,"plugins.zipApply",new{token=one,confirm=true});
        Check(!stopped.GetProperty("ok").GetBoolean()&&
              File.ReadAllBytes(dll).SequenceEqual(new byte[]{0x4d,0x5a,1,1}),
            "late synthetic running ASA refuses plugin ZIP mutation");
        running=false;
        preview=await Call(bridge,"plugins.zipSelect",new{mapId=map.Id});
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var applied=await Call(bridge,"plugins.zipApply",new{token,confirm=true});
        Check(applied.GetProperty("ok").GetBoolean()&&
              applied.GetProperty("data").GetProperty("backupCreated").GetBoolean(),
            "original Kova plugin DLL is replaced with real native backup");
        Check(File.ReadAllBytes(dll).SequenceEqual(
                new byte[]{0x4d,0x5a,0x02,0x03,0x04})&&
              File.ReadAllText(config)=="{\"storeSetting\":true}"&&
              File.ReadAllText(license)=="EXAMPLE-KEEP"&&
              File.ReadAllText(externalStore)=="STORE-DO-NOT-WRITE",
            "new plugin binary installed, existing configuration/license/Store preserved");
        var replay=await Call(bridge,"plugins.zipApply",new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "one-shot original Store plugin ZIP approval cannot be replayed");
    }
}
