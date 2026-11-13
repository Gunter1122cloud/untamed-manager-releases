using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using UntamedManager.Core;

public static class ManagerMigrationArchiveTests
{
    private static readonly JsonSerializerOptions Compact=new(){
        Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    private static void Check(bool b,string title)
    {
        if(!b)throw new Exception("INTEGRATION TEST FAILED Migration Vault: "+title);
        Console.WriteLine("PASS migration-vault "+title);
    }
    private static string Json(object obj)=>JsonSerializer.Serialize(obj,Compact);
    private static string Hash(byte[] data)=>
        Convert.ToHexString(SHA256.HashData(data));

    public static void Run(string integrationRoot,string? firstLocalRoot=null)
    {
        // Entirely fabricated configuration data. No private uploaded ZIP bytes
        // or real tokens, IPs, host paths, player details or credentials here.
        string dir=Path.Combine(integrationRoot,"untamed-manager-package-fixtures");
        Directory.CreateDirectory(dir);
        string session=Guid.NewGuid().ToString(),localDevice=Guid.NewGuid().ToString(),
            remoteDevice=Guid.NewGuid().ToString(),backup=Guid.NewGuid().ToString();
        string shared=@"\\192.0.2.55\fixture-share";
        string[] maps={"ScorchedEarth_WP","LostColony_WP","Ragnarok_WP",
            "TheCenter_WP","Aberration_WP"};
        string[] foreign={"Astraeos_WP","Genesis_WP","Valguero_WP",
            "TheIsland_WP","Extinction_WP"};
        var localIds=maps.Select(_=>Guid.NewGuid().ToString()).ToArray();
        var foreignIds=foreign.Select(_=>Guid.NewGuid().ToString()).ToArray();
        var members=maps.Select((m,i)=>new{
            id=localIds[i],map=m,root=i==0 && firstLocalRoot!=null ? firstLocalRoot : @"C:\synthetic\map-"+i}).ToArray();
        var other=foreign.Select((m,i)=>new{
            id=foreignIds[i],map=m,root=@"D:\synthetic\map-"+i}).ToArray();
        var config=new{id="\u004BovaCluster",mode="shared",path=shared,
            backupPath=@"C:\synthetic\backup",members};
        var remoteConfig=new{id="\u004BovaCluster",mode="shared",path=shared,
            backupPath=@"D:\synthetic\backup",members=other};
        string HashConfig(object value)=>Hash(
            JsonSerializer.SerializeToUtf8Bytes(value,Compact));
        string localHash=HashConfig(config),remoteHash=HashConfig(remoteConfig);
        var fileContents=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        void Add(string path,object value)=>
            fileContents.Add(path,Encoding.UTF8.GetBytes(Json(value)));
        Add("Legacy/cluster-v1.json",config);
        Add("Legacy/cluster-store-v1.json",new{
            schema=1,maps=localIds.Select((id,i)=>(id,i)).ToDictionary(
                x=>x.id,x=>(object)new{
                    root=members[x.i].root,map=members[x.i].map,
                    phase="active",backupId=backup,sharedSession=session,
                    preparedAt="2026-11-03T12:00:00Z"})
        });
        var launch=localIds.Select((id,i)=>new{
            id,root=members[i].root,
            launch=new{map=maps[i],gamePort=7777+i*2,
                queryPort=27015+i,mods=new[]{928548},passiveMods=Array.Empty<int>()}
        }).ToArray();
        Add("Legacy/launch-profiles-v1.json",new{version=1,rows=launch});
        Add("Legacy/local-servers.json",localIds.Select((id,i)=>(id,i))
            .ToDictionary(x=>x.id,x=>members[x.i].root));
        var scheduled=localIds.Select((id,i)=>(id,i)).ToDictionary(x=>x.id,x=>
        {
            string wipe=Guid.NewGuid().ToString();
            string backupTask=Guid.NewGuid().ToString();
            var tasks=new[]{
                new{id=wipe,type="wipe",mode="clock",paused=false},
                new{id=backupTask,type="backup",mode="clock",paused=false}
            };
            var runtime=new Dictionary<string,object>{
                [wipe]=new{last=new{at=1793687100000L,result="skipped",
                    message="Fixture missed notice"}},
                [backupTask]=new{last=new{at=1793688200000L,result="skipped",
                    message="Fixture missed schedule"}}
            };
            return (object)new{
                config=new{root=members[x.i].root,tasks},
                runtime,lastSyncError=new{
                    at=1793688366869L,message="Fixture manual profile synchronization required"}};
        });
        Add("Legacy/schedules-v1.json",new{version=1,profiles=scheduled});
        Add("Legacy/supervisor-v1.json",new{
            config=localIds.Select((id,i)=>new{
                id,name=maps[i],root=members[i].root,
                autoStart=true,watchdog=true,delaySeconds=30
            }),runtime=new{},blocked=(string?)null
        });
        Add("Legacy/discord-bot-v1.json",new{
            enabled=false,secret="SYNTHETIC_NOT_A_REAL_CREDENTIAL",
            controls=new{enabled=true,roles=new[]{"fixture-role"}},
            members=members.Select(x=>new{x.id,x.map}),notices=false
        });
        Add("Legacy/shared-store-device-v1.json",new{schema=1,id=localDevice});
        Add("Legacy/shared-store-lock-v1.json",new{
            schema=1,session,config,configHash=localHash,selected=localIds
        });
        Add("SHARED/.legacy-store-v1/operation.json",new{
            schema=1,session,cluster="\u004BovaCluster",path=shared,
            peers=new[]{localDevice,remoteDevice}
        });
        Add("SHARED/.legacy-store-v1/"+localDevice+".json",new{
            session,id=localDevice,phase="done",config,configHash=localHash,
            members,selected=localIds,backup=new{id=backup}
        });
        Add("SHARED/.legacy-store-v1/"+remoteDevice+".json",new{
            session,id=remoteDevice,phase="done",config=remoteConfig,
            configHash=remoteHash,members=other,selected=foreignIds,
            backup=new{id=Guid.NewGuid().ToString()}
        });
        Add("SHARED/.legacy-store-v1/attempted.json",new{session});
        for(int i=0;i<5;i++)
        {
            Add($"Legacy/tools/{localIds[i]}/owned-process-v1.json",
                new{
                    schema=1,map=maps[i],root=members[i].root,
                    api=new{requested=true,state="starting",serverPid=(int?)null},
                    identity=new{pid=500+i,exe="SyntheticFixture.exe"}
                });
            if(i!=4)Add($"Legacy/tools/{localIds[i]}/backup-settings.json",
                new{lastSuccessAt="2026-11-01T06:00:00Z"});
        }
        Add("Legacy/discord-history-v1.json",new{dummy=Array.Empty<string>()});
        fileContents.Add("Legacy/Local Storage/leveldb/000001.ldb",
            Encoding.UTF8.GetBytes("PRIVATE_SYNTHETIC_BINARY_DO_NOT_USE"));
        fileContents.Add("Legacy/Session Storage/000003.log",
            Encoding.UTF8.GetBytes("PRIVATE_SYNTHETIC_SESSION"));
        fileContents.Add("Legacy/device-identity.bin",
            Encoding.UTF8.GetBytes("PRIVATE_SYNTHETIC_IDENTITY"));
        // The actual archive has more binary browser-state members. They must
        // be checked in manifest but never interpreted or imported.
        while(fileContents.Count<39)
            fileContents.Add("Legacy/Local Storage/leveldb/synthetic-"+fileContents.Count+".bin",
                Encoding.UTF8.GetBytes("NOT_IMPORTABLE_PRIVATE_FIXTURE_"+fileContents.Count));

        string WriteArchive(string name,bool corruptManifest=false)
        {
            var package=Path.Combine(dir,name);
            var rows=fileContents.Select((item,i)=>new {
                Path=item.Key.Replace('/','\\'),
                Bytes=item.Value.Length,
                SHA256=corruptManifest&&i==0?new string('F',64):
                    Hash(item.Value)
            }).ToArray();
            using(var output=File.Create(package))
            using(var zip=new ZipArchive(output,ZipArchiveMode.Create))
            {
                foreach(var item in fileContents)
                {
                    var entry=zip.CreateEntry(item.Key.Replace('/','\\'),
                        CompressionLevel.Optimal);
                    using var stream=entry.Open();
                    stream.Write(item.Value);
                }
                var manifest=zip.CreateEntry("manifest.json");
                using(var writer=new StreamWriter(manifest.Open()))
                    writer.Write(Json(rows));
                var readme=zip.CreateEntry("README.txt");
                using(var writer=new StreamWriter(readme.Open()))
                    writer.Write("Synthetic manager-state copy; not a consistent backup.");
            }
            return package;
        }
        string path=WriteArchive("Untamed-Manager-migration-fixture.zip");
        string originalSha=Hash(File.ReadAllBytes(path));
        var result=UntamedManagerArchiveInspector.Inspect(path);
        Check(result.VerifiedFiles==39&&result.LocalMaps==5&&
            result.SharedMaps==10&&result.StorePeers==2&&
            result.SharedSessionConsistent&&result.PeerConfigHashesVerified,
            "real legacy manager ZIP shape verifies 39 files and two peer session/config hashes");
        Check(result.ScheduledTasks==10&&result.SkippedTaskResults==10&&
            result.ScheduleSyncErrors==5&&result.AutoStartProfiles==5&&
            result.WatchdogProfiles==5,
            "scheduler and old autorun/watchdog risks are identified but never activated");
        Check(result.PendingLoaderStates==5&&result.MissingServerPidStates==5&&
            result.BackupPolicyFiles==4&&result.DiscordDisabled&&
            result.DiscordControlsConfigured&&result.EmbeddedSensitiveState&&
            !result.ProductionImportAllowed,
            "process identity unknown, missing backup policies, disabled bot with controls and private browser state are flagged");
        var historical=HistoricalLaunchProfileReader.Inspect(path);
        Check(!historical.ReadyForLiveControl &&historical.Maps.Length==5 &&
            historical.Maps.All(m=>m.ExistingStoreActive&&m.EnabledMods==1&&
                m.PassiveMods==0&&m.ArgumentsPreview.StartsWith(m.MapId+"?listen -Port=")&&
                m.ArgumentsPreview.EndsWith(" -usestore -log")&&
                !m.ArgumentsPreview.Contains("-converttostore",StringComparison.OrdinalIgnoreCase)) &&
            historical.Maps.Select(m=>m.MapId).Distinct().Count()==5,
            "historical Manager launch profiles preserve five-map Store mode, no live control");
        Check(result.SourceSha256==originalSha&&
            Hash(File.ReadAllBytes(path))==originalSha&&
            result.Findings.All(x=>!x.Contains("SYNTHETIC_NOT_A_REAL_CREDENTIAL")&&
                !x.Contains("PRIVATE_SYNTHETIC_BINARY")),
            "archive is read-only and diagnostics never disclose synthetic secret material");
        bool badManifest=false;
        try{UntamedManagerArchiveInspector.Inspect(
            WriteArchive("tampered-manifest.zip",true));}
        catch(InvalidDataException){badManifest=true;}
        Check(badManifest,"tampered manifest SHA-256 fails closed");
        fileContents["SHARED/.legacy-store-v1/operation.json"]=
            Encoding.UTF8.GetBytes(Json(new{
                schema=1,session=Guid.NewGuid().ToString(),
                cluster="\u004BovaCluster",path=shared,
                peers=new[]{localDevice,remoteDevice}
            }));
        var stale=UntamedManagerArchiveInspector.Inspect(
            WriteArchive("stale-peer-session.zip"));
        Check(!stale.SharedSessionConsistent &&
            !stale.ProductionImportAllowed,
            "self-consistent file digests cannot hide inconsistent historical peer sessions");
    }
}
