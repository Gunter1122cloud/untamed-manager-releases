using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Real existing SHA-256 world backup engine, native bridge with its DEFAULT
// historical process observer, isolated original-Store-shaped TEMP fixture.
internal static class HistoricalStoreWorldBackupTests
{
    private static void Check(bool value,string reason)
    {
        if(!value)throw new InvalidOperationException("HISTORICAL WORLD BACKUP TEST: "+reason);
        Console.WriteLine("PASS historical Store world backup: "+reason);
    }
    private static async Task<JsonElement> Call(DesktopWorldBackupBridge bridge,
        string method,object? fields=null)
    {
        var args=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method};
        if(fields!=null)
        {
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(fields));
            foreach(var field in doc.RootElement.EnumerateObject())
                args[field.Name]=field.Value.Clone();
        }
        string raw=await bridge.HandleAsync(JsonSerializer.Serialize(args));
        using var response=JsonDocument.Parse(raw);
        return response.RootElement.Clone();
    }

    public static async Task Run(string temp)
    {
        string root=Path.Combine(temp,"synthetic-historical-world-backup");
        string manager=Path.Combine(root,"manager");
        string install=Path.Combine(root,"asa-original-map");
        Directory.CreateDirectory(install);
        bool independentlyStopped=false;
        await using var cluster=new ClusterService("historical-backup-test",
            manager,_=>new ProcessCheck(false,"isolated Kova PID observer",null,
                independentlyStopped));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="Synthetic Island",InstallationPath=install,
            HistoricalStoreProtected=true,HistoricalMapKey="TheIsland_WP",
            AutoStart=false
        };
        cluster.AddMap(map);
        var layout=AsaSaveLayoutResolver.Resolve(map);
        Directory.CreateDirectory(layout.WorldDirectory);
        byte[] original=Enumerable.Range(0,4096)
            .Select(i=>(byte)((i%250)+1)).ToArray();
        File.WriteAllBytes(layout.WorldFile,original);
        string oldLoose=Path.Combine(layout.WorldDirectory,
            "synthetic-old.arkprofile");
        File.WriteAllBytes(oldLoose,Array.Empty<byte>());
        var bridge=new DesktopWorldBackupBridge(cluster);
        var refused=await Call(bridge,"backup.preview",new{mapId=map.Id});
        Check(!refused.GetProperty("ok").GetBoolean(),
            "uncertain original ASA process cannot authorize a Store map backup");
        independentlyStopped=true;
        var preview=await Call(bridge,"backup.preview",new{mapId=map.Id});
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("confirmedStopped").GetBoolean(),
            "dedicated historical PID observer authorizes only confirmed stopped map");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var result=await Call(bridge,"backup.create",
            new{token,confirm=true});
        Check(result.GetProperty("ok").GetBoolean()&&
              result.GetProperty("data").GetProperty("backupVerified").GetBoolean()&&
              result.GetProperty("data").GetProperty("embeddedSurvivorCountUnknown").GetBoolean(),
            "existing per-map SHA-256 engine creates verified historical Store world backup");
        Check(File.ReadAllBytes(layout.WorldFile).SequenceEqual(original)&&
              new FileInfo(oldLoose).Length==0,
            "backup preserves original world bytes and zero-byte loose profile");
        var history=await Call(bridge,"backup.history",new{mapId=map.Id});
        Check(history.GetProperty("ok").GetBoolean()&&
              history.GetProperty("data").GetProperty("points").GetArrayLength()==1,
            "verified backup appears in user-selectable native history");
        string point=history.GetProperty("data").GetProperty("points")[0]
            .GetProperty("id").GetString()!;
        var restore=await Call(bridge,"backup.restorePreview",
            new{mapId=map.Id,point});
        Check(!restore.GetProperty("ok").GetBoolean(),
            "one-sided original Store world restore remains denied");
    }
}
