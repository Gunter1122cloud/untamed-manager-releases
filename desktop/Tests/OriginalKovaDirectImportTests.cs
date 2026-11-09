using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UntamedDesktop;
using UntamedManager.Core;

// Direct Kova import exercises the REAL native bridge and the source-pinned
// Core read path using only private TEMP files and fake ASA executable names.
// No original user Kova data, UNC share or ASA/RCON process is opened.
internal static class OriginalKovaDirectImportTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new InvalidOperationException("DIRECT KOVA IMPORT: "+reason);
        Console.WriteLine("PASS direct Kova import: "+reason);
    }

    private static async Task<JsonElement> Call(
        DesktopHistoricalStoreImportBridge bridge,string method,
        object? values=null)
    {
        var request=new System.Collections.Generic.Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(values!=null)
        {
            using var extra=JsonDocument.Parse(JsonSerializer.Serialize(values));
            foreach(var prop in extra.RootElement.EnumerateObject())
                request[prop.Name]=prop.Value.Clone();
        }
        string result=await bridge.HandleAsync(JsonSerializer.Serialize(request));
        using var parsed=JsonDocument.Parse(result);
        return parsed.RootElement.Clone();
    }

    public static async Task Run(string sandbox)
    {
        string baseRoot=Path.Combine(sandbox,"direct-kova-import-lab");
        string local=Path.Combine(baseRoot,"synthetic-kova");
        string manager=Path.Combine(baseRoot,"obsidian-profile");
        Directory.CreateDirectory(local);
        string[] mapKeys={"TheIsland_WP","Astraeos_WP","Valguero_WP",
            "Extinction_WP","Genesis_WP"};
        string[] ids=Enumerable.Range(0,5).Select(_=>Guid.NewGuid().ToString("D")).ToArray();
        string session=Guid.NewGuid().ToString("D");
        string device=Guid.NewGuid().ToString("D");
        var members=mapKeys.Select((name,i)=>new{
            id=ids[i],map=name,root=Path.Combine(baseRoot,"synthetic-asa-"+i)
        }).ToArray();
        foreach(var member in members)
        {
            string exe=Path.Combine(member.root,"ShooterGame","Binaries","Win64",
                "ArkAscendedServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe,new byte[]{0x4d,0x5a,0,0});
        }
        var originalCluster=new{
            id=VaultProtocol.ClusterId,mode="shared",
            path=@"\\synthetic-nonexistent\store-transfer",members
        };
        var states=ids.Select((id,i)=>new
        {
            id,
            value=new{
                phase="active",sharedSession=session,root=members[i].root,
                map=mapKeys[i],backupId=Guid.NewGuid().ToString("D")
            }
        }).ToDictionary(x=>x.id,x=>x.value);
        var store=new{schema=1,maps=states};
        var lockFile=new{schema=1,session,selected=ids,
            configHash=StoreV1Fingerprint.Compute(
                JsonSerializer.SerializeToElement(originalCluster)),
            config=originalCluster};
        string[] names={
            "cluster-v1.json","cluster-store-v1.json",
            "shared-store-device-v1.json","shared-store-lock-v1.json"
        };
        string[] source={
            JsonSerializer.Serialize(originalCluster),JsonSerializer.Serialize(store),
            JsonSerializer.Serialize(new{schema=1,id=device}),
            JsonSerializer.Serialize(lockFile)
        };
        for(int i=0;i<names.Length;i++)
            File.WriteAllText(Path.Combine(local,names[i]),source[i]);
        string launchProfilePath=Path.Combine(local,"launch-profiles-v1.json");
        var launchRows=ids.Select((id,i)=>new{
            id,root=members[i].root,
            launch=new{
                map=mapKeys[i],gamePort=7777+i*10,queryPort=27015+i*10,
                mods=new[]{"123456789","987654321"},
                passiveMods=new[]{"123456789"}
            }
        }).ToArray();
        string launchBytes=JsonSerializer.Serialize(new{version=1,rows=launchRows});
        File.WriteAllText(launchProfilePath,launchBytes);
        string launchHash=Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(launchProfilePath)));
        string[] hashes=names.Select(n=>Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(local,n))))).ToArray();
        var discovered=LegacyStoreV1OriginalImport.Inspect(local);
        Check(discovered.Maps.Length==5&&
              discovered.Maps.Select(x=>x.MapId.ToString("D")).SequenceEqual(ids)&&
              discovered.Maps.Select(x=>x.OriginalInstallationRoot)
                .SequenceEqual(members.Select(x=>x.root))&&
              discovered.Maps.All(x=>x.EnabledMods.SequenceEqual(
                  new[]{"123456789","987654321"})&&
                  x.PassiveMods.SequenceEqual(new[]{"123456789"}))&&
              discovered.LaunchProfileSha256==launchHash,
            "five original GUIDs, map keys and installation paths remain byte-bound");

        await using var cluster=new ClusterService(
            "direct-kova-test",manager,
            _=>new ProcessCheck(false,"isolated synthetic stopped",null,true));
        using var bridge=new DesktopHistoricalStoreImportBridge(
            cluster,
            _=>new ProcessCheck(false,"isolated synthetic stopped",null,true),local);
        var list=await Call(bridge,"historical.originalList");
        Check(list.GetProperty("ok").GetBoolean()&&
              list.GetProperty("data").GetProperty("maps").GetArrayLength()==5,
            "existing WebView route lists original source without external ZIP");

        var invalid=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[0],gamePort=7777,queryPort=7777});
        Check(!invalid.GetProperty("ok").GetBoolean(),
            "equal Game and Query ports are rejected before approval");
        var first=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[0],gamePort=7777,queryPort=27015});
        Check(first.GetProperty("ok").GetBoolean()&&
              first.GetProperty("data").GetProperty("originalIdsPreserved").GetBoolean(),
            "first synthetic map has a source-pinned, stopped-process preview");
        string token=first.GetProperty("data").GetProperty("token").GetString()!;
        var denied=await Call(bridge,"historical.originalRegister",
            new{token,confirm=false});
        Check(!denied.GetProperty("ok").GetBoolean(),
            "registration requires operator confirmation");
        var accepted=await Call(bridge,"historical.originalRegister",
            new{token,confirm=true});
        Check(accepted.GetProperty("ok").GetBoolean()&&
              !accepted.GetProperty("data").GetProperty("originalFilesWritten").GetBoolean(),
            "confirmed import registers Manager-only local map, no source writes");
        var imported=cluster.Config.LocalMaps.Single(x=>x.Id==Guid.Parse(ids[0]));
        Check(imported.HistoricalStoreProtected&&
              imported.InstallationPath==members[0].root&&
              imported.GamePort==7777&&imported.QueryPort==27015&&
              imported.Arguments==""&&imported.RconPort==0&&
              imported.HistoricalEnabledMods.SequenceEqual(
                  new[]{"123456789","987654321"})&&
              imported.HistoricalPassiveMods.SequenceEqual(new[]{"123456789"})&&
              !imported.AutoStart,
            "registered map keeps original identity and requires separate native Start/RCON");

        var duplicate=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[0],gamePort=7777,queryPort=27015});
        Check(!duplicate.GetProperty("ok").GetBoolean(),
            "duplicate original map cannot be silently registered twice");

        var preview=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[1],gamePort=7787,queryPort=27025});
        Check(preview.GetProperty("ok").GetBoolean(),
            "second local map may be staged independently");
        string secondToken=preview.GetProperty("data").GetProperty("token").GetString()!;
        File.WriteAllText(Path.Combine(local,names[3]),source[3]+" ");
        var tampered=await Call(bridge,"historical.originalRegister",
            new{token=secondToken,confirm=true});
        Check(!tampered.GetProperty("ok").GetBoolean()&&
              cluster.Config.LocalMaps.Count==1,
            "changed original lock source rejects registration after preview");
        File.WriteAllText(Path.Combine(local,names[3]),source[3]);

        var launchPreview=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[1],gamePort=7787,queryPort=27025});
        Check(launchPreview.GetProperty("ok").GetBoolean(),
            "launch profile and original metadata both verified at preview");
        string changedLaunch=launchBytes.Replace("987654321","555555555",
            StringComparison.Ordinal);
        File.WriteAllText(launchProfilePath,changedLaunch);
        var deniedLaunch=await Call(bridge,"historical.originalRegister",
            new{
                token=launchPreview.GetProperty("data").GetProperty("token").GetString(),
                confirm=true
            });
        Check(!deniedLaunch.GetProperty("ok").GetBoolean()&&
              cluster.Config.LocalMaps.Count==1,
            "changed original mod load-order bytes after preview deny registration");
        File.WriteAllText(launchProfilePath,launchBytes);

        for(int i=1;i<5;i++)
        {
            var staged=await Call(bridge,"historical.originalPreview",
                new{mapId=ids[i],gamePort=7777+i*10,queryPort=27015+i*10});
            Check(staged.GetProperty("ok").GetBoolean(),
                "fresh original map preview accepted for map "+i);
            var receipt=await Call(bridge,"historical.originalRegister",
                new{
                    token=staged.GetProperty("data").GetProperty("token").GetString(),
                    confirm=true
                });
            Check(receipt.GetProperty("ok").GetBoolean(),
                "original map registration succeeds for map "+i);
        }
        Check(cluster.Config.LocalMaps.Count==5&&
              cluster.Config.LocalMaps.All(m=>m.HistoricalStoreProtected),
            "five original local Store map registrations are available in real Manager registry");
        Check(names.Select(n=>Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(local,n)))))
                .SequenceEqual(hashes)&&
              Convert.ToHexString(SHA256.HashData(
                  File.ReadAllBytes(launchProfilePath)))==launchHash,
            "original four Store metadata and launch order remain byte-identical after imports");

        // A second empty Manager profile imports all five original maps in ONE
        // ConfigStore write, without ever calling original Kova/ASA mutation.
        string batchManager=Path.Combine(baseRoot,"atomic-batch-manager");
        await using var batchCluster=new ClusterService(
            "direct-kova-batch-test",batchManager,
            _=>new ProcessCheck(false,"isolated synthetic stopped",null,true));
        bool uncertainFifth=true;
        using var batchBridge=new DesktopHistoricalStoreImportBridge(
            batchCluster,map=>map.HistoricalMapKey==mapKeys[4]&&uncertainFifth
                ?new ProcessCheck(false,"synthetic uncertain",null,false)
                :new ProcessCheck(false,"isolated synthetic stopped",null,true),local);
        var deniedBatch=await Call(batchBridge,"historical.originalBatchPreview");
        Check(!deniedBatch.GetProperty("ok").GetBoolean()&&
              batchCluster.Config.LocalMaps.Count==0,
            "one unknown original process denies all-map import before ANY registration");
        uncertainFifth=false;
        var preparedBatch=await Call(batchBridge,"historical.originalBatchPreview");
        Check(preparedBatch.GetProperty("ok").GetBoolean()&&
              preparedBatch.GetProperty("data").GetProperty("localMaps").GetInt32()==5,
            "all five original map identities and stopped processes pass one batch preview");
        string batchToken=preparedBatch.GetProperty("data").GetProperty("token").GetString()!;
        var noConsent=await Call(batchBridge,"historical.originalBatchRegister",
            new{token=batchToken,confirm=false});
        Check(!noConsent.GetProperty("ok").GetBoolean()&&
              batchCluster.Config.LocalMaps.Count==0,
            "atomic import requires one explicit operator confirmation");
        var batchResult=await Call(batchBridge,"historical.originalBatchRegister",
            new{token=batchToken,confirm=true});
        Check(batchResult.GetProperty("ok").GetBoolean()&&
              batchResult.GetProperty("data").GetProperty("importedMaps").GetInt32()==5&&
              batchCluster.Config.LocalMaps.Count==5&&
              batchCluster.Config.LocalMaps.Select(m=>m.Id.ToString("D")).SequenceEqual(ids),
            "one approved native batch registers five original GUIDs atomically");
        Check(batchCluster.Config.LocalMaps.All(m=>m.HistoricalStoreProtected&&
              !m.AutoStart&&m.RconPort==0),
            "all batch-imported maps remain operator-controlled without auto-start");
        var afterBatch=await Call(batchBridge,"historical.originalBatchPreview");
        Check(!afterBatch.GetProperty("ok").GetBoolean(),
            "repeating an already imported five-map batch does not duplicate entries");
        Check(names.Select(n=>Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(local,n)))))
                .SequenceEqual(hashes)&&
              Convert.ToHexString(SHA256.HashData(
                  File.ReadAllBytes(launchProfilePath)))==launchHash,
            "atomic five-map batch leaves every synthetic original metadata byte unchanged");
    }
}
