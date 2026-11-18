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
        Check(!refused.GetProperty("ok").GetBoolean()&&
            refused.GetProperty("error").GetString()=="UM-BACKUP-CONFIRMED-STOP-REQUIRED",
            "uncertain original ASA process cannot authorize a Store map backup");
        independentlyStopped=true;
        var preview=await Call(bridge,"backup.preview",new{mapId=map.Id});
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("confirmedStopped").GetBoolean(),
            "dedicated historical PID observer authorizes only confirmed stopped map");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        string budgetFile=Path.Combine(cluster.Store.Root,
            BackupResourcePolicy.FileName);
        File.WriteAllText(budgetFile,
            "{\"schema\":1,\"maxFiles\":100000,\"maxTotalBytes\":1048576}");
        var staleBudget=await Call(bridge,"backup.create",
            new{token,confirm=true});
        Check(!staleBudget.GetProperty("ok").GetBoolean()&&
              staleBudget.GetProperty("error").GetString()==
                  "UM-BACKUP-RESOURCE-POLICY-CHANGED",
            "per-map approval cannot be reused after Manager backup policy changed");
        File.Delete(budgetFile);
        var nextPreview=await Call(bridge,"backup.preview",new{mapId=map.Id});
        Check(nextPreview.GetProperty("ok").GetBoolean(),
            "operator can re-preview a verified stopped map after correcting budget");
        token=nextPreview.GetProperty("data").GetProperty("token").GetString()!;
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
        Check(!restore.GetProperty("ok").GetBoolean()&&restore.GetProperty("error").GetString()=="UM-BACKUP-STORE-V1-RESTORE-LOCKED",
            "one-sided original Store world restore remains denied");
        independentlyStopped=false;
        var notApproved=await Call(bridge,"backup.worldStage",new{mapId=map.Id,point,confirm=false});
        Check(!notApproved.GetProperty("ok").GetBoolean()&&notApproved.GetProperty("error").GetString()=="UM-BACKUP-EXPLICIT-APPROVAL-REQUIRED",
            "isolated world stage requires explicit approval");
        var foreign=await Call(bridge,"backup.worldStage",new{mapId=Guid.NewGuid(),point,confirm=true});
        Check(!foreign.GetProperty("ok").GetBoolean()&&foreign.GetProperty("error").GetString()=="UM-BACKUP-MAP-NOT-LOCAL",
            "isolated world stage rejects foreign GUID");
        var blockedSharedRoot=false;
        try
        {
            _=UntamedWorldBackup.StageVerifiedWorld(map,cluster.Store.Root,point,
                new[]{install},new[]{manager});
        }
        catch(InvalidDataException e) when(e.Message=="UM-BACKUP-PATHS-OVERLAP")
        {blockedSharedRoot=true;}
        Check(blockedSharedRoot&&!Directory.Exists(Path.Combine(manager,"world-restore-staging")),
            "restore staging refuses output overlapping protected cluster metadata root");
        var stage=await Call(bridge,"backup.worldStage",new{mapId=map.Id,point,confirm=true});
        Check(stage.GetProperty("ok").GetBoolean(),"archive-only restore stage succeeds without stopping a map");
        var data=stage.GetProperty("data");
        string folder=Path.Combine(cluster.Store.Root,data.GetProperty("relativeFolder").GetString()!,"files");
        Check(!data.GetProperty("productionRestoreAllowed").GetBoolean()&&
            File.ReadAllBytes(Path.Combine(folder,Path.GetFileName(layout.WorldFile))).SequenceEqual(original)&&
            new FileInfo(Path.Combine(folder,Path.GetFileName(oldLoose))).Length==0&&
            File.ReadAllBytes(layout.WorldFile).SequenceEqual(original),
            "stage verifies world and zero-byte profile without touching live files");
        string package=Path.Combine(cluster.Store.Root,"world-backups",map.Id.ToString("N"),point,"files");
        File.WriteAllBytes(Path.Combine(package,Path.GetFileName(layout.WorldFile)),new byte[]{1,2,3});
        var rejected=await Call(bridge,"backup.worldStage",new{mapId=map.Id,point,confirm=true});
        Check(!rejected.GetProperty("ok").GetBoolean()&&rejected.GetProperty("error").GetString()=="UM-BACKUP-DIGEST-MISMATCH"&&
            File.ReadAllBytes(layout.WorldFile).SequenceEqual(original)&&
            Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!).Length==1,
            "corrupt archive cannot publish another stage or modify world");
    }
}
