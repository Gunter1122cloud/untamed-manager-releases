using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using UntamedDesktop;
using UntamedManager.Core;

// Real existing native world backup engine across five synthetic original
// Store-owned maps. Does not read any original Kova user profile or UNC share.
internal static class OriginalStoreFiveMapBackupTests
{
    private static void Check(bool value,string reason)
    {
        if(!value)throw new InvalidOperationException(
            "FIVE-MAP STORE BACKUP REGRESSION: "+reason);
        Console.WriteLine("PASS original five-map SHA-256 backup: "+reason);
    }
    private static async Task<JsonElement> Call(DesktopWorldBackupBridge bridge,
        string method,object? fields=null)
    {
        var payload=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method};
        if(fields!=null)
        {
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(fields));
            foreach(var field in doc.RootElement.EnumerateObject())
                payload[field.Name]=field.Value.Clone();
        }
        var response=await bridge.HandleAsync(JsonSerializer.Serialize(payload));
        using var parsed=JsonDocument.Parse(response);
        return parsed.RootElement.Clone();
    }
    private static string Sha(string data)=>Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(data)));

    public static async Task Run(string sandbox)
    {
        string root=Path.Combine(sandbox,"synthetic-original-five-world-backups");
        string manager=Path.Combine(root,"manager");
        Directory.CreateDirectory(root);
        string[] keys={"TheIsland_WP","TheCenter_WP","ScorchedEarth_WP",
            "Aberration_WP","Extinction_WP"};
        string[] names={"The Island","The Center","Scorched Earth",
            "Aberration","Extinction"};
        bool allStopped=false;
        await using var cluster=new ClusterService("original-five-backup",manager,
            _=>new ProcessCheck(false,"synthetic historical processes",null,allStopped));
        var mapRows=new List<LegacyStoreV1OriginalMap>();
        var maps=new List<LocalMap>();
        var originalWorlds=new Dictionary<Guid,byte[]>();
        string sourceSha=Sha("synthetic-original-four-files");
        string launchSha=Sha("synthetic-original-launch-profile");
        for(int i=0;i<5;i++)
        {
            var id=Guid.NewGuid();
            string installation=Path.Combine(root,"original-map-"+i);
            string[] enabled={"1234567890","2345678901"};
            string[] passive={"1234567890"};
            var map=new LocalMap{
                Id=id,Name=names[i],InstallationPath=installation,
                HistoricalStoreProtected=true,HistoricalMapKey=keys[i],
                HistoricalArchiveSha256=sourceSha,
                HistoricalEnabledMods=enabled.ToList(),
                HistoricalPassiveMods=passive.ToList(),
                GamePort=7800+i*20,QueryPort=27800+i*20,
                Arguments="",AutoStart=false,RconPort=0
            };
            cluster.AddMap(map);
            var layout=AsaSaveLayoutResolver.Resolve(map);
            Directory.CreateDirectory(layout.WorldDirectory);
            byte[] original=Enumerable.Range(0,8192+i*128)
                .Select(x=>(byte)((x+i)%250+1)).ToArray();
            File.WriteAllBytes(layout.WorldFile,original);
            originalWorlds[id]=original;
            // Historical Store placeholders may be empty, never rewritten.
            File.WriteAllBytes(Path.Combine(layout.WorldDirectory,
                "legacy-player-"+i+".arkprofile"),Array.Empty<byte>());
            maps.Add(map);
            mapRows.Add(new(id,keys[i],installation,map.GamePort,map.QueryPort,
                enabled,passive));
        }
        // A sixth independent historical Store map must NOT impose a
        // five-map capacity restriction on original Kova backup selection.
        var independent=new LocalMap{
            Id=Guid.NewGuid(),Name="Valguero",
            InstallationPath=Path.Combine(root,"independent-sixth-map"),
            HistoricalStoreProtected=true,HistoricalMapKey="Valguero_WP",
            HistoricalArchiveSha256=Sha("another-store-source"),
            GamePort=7901,QueryPort=27901,AutoStart=false
        };
        cluster.AddMap(independent);
        LegacyStoreV1OriginalLocalSelection currentSource=new(
            sourceSha,launchSha,mapRows.ToArray());
        var bridge=new DesktopWorldBackupBridge(cluster,
            originalSelectionProvider:()=>currentSource);
        var unknown=await Call(bridge,"backup.originalBatchPreview");
        Check(!unknown.GetProperty("ok").GetBoolean(),
            "unknown stopped status on a single original map prevents five-map approval");
        allStopped=true;
        var preview=await Call(bridge,"backup.originalBatchPreview");
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("localMaps").GetInt32()==5&&
              preview.GetProperty("data").GetProperty("allMapsConfirmedStopped").GetBoolean(),
            "all five original GUID/port/mod roots and stopped processes verified");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var denied=await Call(bridge,"backup.originalBatchCreate",
            new{token,confirm=false});
        Check(!denied.GetProperty("ok").GetBoolean(),
            "five-map backup cannot begin without explicit consent");

        // Recheck the original source before a mutating *Manager local archive*
        // operation. The token is pinned to BOTH original file digests.
        currentSource=currentSource with{LaunchProfileSha256=Sha("changed-launch")};
        var changed=await Call(bridge,"backup.originalBatchCreate",
            new{token,confirm=true});
        Check(!changed.GetProperty("ok").GetBoolean()&&
              maps.All(map=>KovaCompatibleWorldBackup.List(
                  map,cluster.Store.Root).Length==0),
            "source fingerprint change rejects without creating any backup point");
        currentSource=new(sourceSha,launchSha,mapRows.ToArray());
        var approvedPreview=await Call(bridge,"backup.originalBatchPreview");
        Check(approvedPreview.GetProperty("ok").GetBoolean(),
            "operator can request a fresh five-map approval after source correction");
        string approved=approvedPreview.GetProperty("data")
            .GetProperty("token").GetString()!;
        var completed=await Call(bridge,"backup.originalBatchCreate",
            new{token=approved,confirm=true});
        Check(completed.GetProperty("ok").GetBoolean()&&
              completed.GetProperty("data").GetProperty("allFiveVerified").GetBoolean()&&
              completed.GetProperty("data").GetProperty("points").GetArrayLength()==5,
            "one consent produces five actual verified Core world backup packages");
        foreach(var map in maps)
        {
            var layout=AsaSaveLayoutResolver.Resolve(map);
            Check(File.ReadAllBytes(layout.WorldFile)
                    .SequenceEqual(originalWorlds[map.Id])&&
                  new FileInfo(Path.Combine(layout.WorldDirectory,
                    "legacy-player-"+maps.IndexOf(map)+".arkprofile")).Length==0,
                "all original world/profile bytes remain untouched by batch");
            Check(KovaCompatibleWorldBackup.List(map,cluster.Store.Root).Length==1,
                "five-map backup uses existing per-map archive history");
        }
        Check(KovaCompatibleWorldBackup.List(independent,cluster.Store.Root).Length==0,
            "sixth unrelated historical map remains untouched by original five-map batch");
        var replay=await Call(bridge,"backup.originalBatchCreate",
            new{token=approved,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "one-time batch approval cannot be replayed to trigger a second backup");
    }
}
