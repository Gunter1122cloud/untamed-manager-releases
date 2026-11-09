using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using UntamedManager.Core;

// Actual immutable file bytes: two synthetic hosts, ten maps, the SAME
// cluster and shared Store/transfer/character/tribe/inventory/tribute data.
// This tests the shipping archive/restore engine, not a mocked serializer.
// No production Kova profile, UNC share, RCON or ASA process is accessed.
internal static class MultiHostOfflineCatalogAssemblerTests
{
    private static void Check(bool assertion,string text)
    {
        if(!assertion)throw new InvalidOperationException(
            "MULTIHOST TEN-MAP OFFLINE BACKUP TEST: "+text);
        Console.WriteLine("PASS native ten-map two-host archive: "+text);
    }
    private static void Write(string root,string relative,byte[] bytes)
    {
        string full=Path.Combine(root,relative.Replace('/',
            Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full,bytes);
    }
    private static void Write(string root,string relative,string content)=>
        Write(root,relative,Encoding.UTF8.GetBytes(content));

    public static void Run(string sandbox)
    {
        string source=Path.Combine(sandbox,"auto-two-host-ten-map-offline-source");
        string archive=Path.Combine(sandbox,"auto-two-host-ten-map-archive");
        Directory.CreateDirectory(source);
        string[] hosts={"test-host-hp","test-host-ryzen"};
        const string cluster="ten-map-cluster";
        var hostRecords=new[]{
            new MultiHostBackupHostOwnership(hosts[0],"Synthetic HP",true,false),
            new MultiHostBackupHostOwnership(hosts[1],"Synthetic Ryzen",false,true)
        };
        var clusterRecords=new[]{
            new MultiHostBackupClusterOwnership(cluster,"Synthetic Store cluster")
        };
        string[] mapNames={
            "Astraeos","Genesis Part 1","Valguero","The Island","Extinction",
            "Scorched Earth","Lost Colony","Ragnarok","The Center","Aberration"};
        var mapRecords=new List<MultiHostBackupMapOwnership>();
        var sourceHashes=new Dictionary<string,byte[]>();
        for(int i=0;i<10;i++)
        {
            var map=Guid.NewGuid();
            var host=hosts[i<5?0:1];
            mapRecords.Add(new(map,host,mapNames[i],cluster));
            string scope="maps/"+map.ToString("N");
            void Add(string relative,byte[] bytes)
            {
                string path=scope+"/"+relative;
                Write(source,path,bytes);
                sourceHashes[path]=bytes;
            }
            Add("Saved/world.ark",
                Enumerable.Range(0,1024+i*47).Select(x=>(byte)((x+i)%251+1)).ToArray());
            // Preserve original zero-byte player placeholder as-is. NEVER
            // "fix" or remove it. Other profiles carry synthetic payload.
            Add("Saved/character-"+i+".arkprofile",i==0?
                Array.Empty<byte>():Encoding.UTF8.GetBytes("survivor-"+i));
            Add("Saved/tribe-"+i+".arktribe",
                Encoding.UTF8.GetBytes("tribe ownership "+i));
            Add("Saved/inventory-"+i+".bin",
                Encoding.UTF8.GetBytes("embedded store adjunct "+i));
            Add("WindowsServer/Game.ini",
                Encoding.UTF8.GetBytes("[ServerSettings]\nSampleOnly=true"));
            Add("WindowsServer/GameUserSettings.ini",
                Encoding.UTF8.GetBytes("[ServerSettings]\nServerPassword=\n"));
            Add("Manager/mods.json",Encoding.UTF8.GetBytes("{\"mods\":[]}"));
            Add("Manager/plugins.json",Encoding.UTF8.GetBytes("{\"plugins\":[]}"));
            Add("ASAApi/empty-state.json",Encoding.UTF8.GetBytes("{\"files\":[]}"));
            Add("ASAApi/ArkShop/persistent-data.bin",
                Encoding.UTF8.GetBytes("plugin persistent payload "+i));
        }
        foreach(var host in hosts)
        {
            Write(source,"hosts/"+host+"/configuration/host-config.json","{\"version\":1}");
            Write(source,"hosts/"+host+"/clusters/"+cluster+"/Store-v1/session.json",
                "{\"host\":\""+host+"\",\"phase\":\"done\"}");
            Write(source,"clusters/"+cluster+"/Store-v1/"+host+"/store.json",
                "{\"saved\":\""+host+"\"}");
            Write(source,"clusters/"+cluster+"/Transfer/"+host+"/transfer-"+host+".bin",
                "transfer data for "+host);
        }
        // Exactly one transfer snapshot manifest per selected cluster, with
        // every file underneath the transfer roots listed in the catalog.
        Write(source,"clusters/"+cluster+"/Transfer/"+hosts[0]+"/snapshot.json",
            "{\"copies\":[\"player\",\"inventory\",\"tribute\",\"dinos\"]}");
        Write(source,"clusters/"+cluster+"/Transfer/"+hosts[0]+"/characters.arkprofile",
            "survivor bytes original");
        Write(source,"clusters/"+cluster+"/Transfer/"+hosts[0]+"/inventory.bin",
            "inventory bytes original");
        Write(source,"clusters/"+cluster+"/Transfer/"+hosts[0]+"/tribute.bin",
            "tribute bytes original");
        Write(source,"clusters/"+cluster+"/Transfer/"+hosts[0]+"/dinos.bin",
            "tamed dino transfer bytes original");
        // Synthetic equivalent of the inspected Kova transfer inventory:
        // 50 payload entries, including nine extensionless 0-byte placeholders.
        // Special dot-prefixed Store files are separate, NONEMPTY payloads.
        for(int i=0;i<9;i++)
            Write(source,"clusters/"+cluster+"/Transfer/"+hosts[0]+
                "/placeholder-"+i.ToString("D2"),Array.Empty<byte>());
        foreach(string special in new[]{".arkproftemp",".arktributetribe",
                     ".formertribeownerlog"})
            Write(source,"clusters/"+cluster+"/Transfer/"+hosts[0]+"/"+special,
                "original nonempty synthetic Store special file: "+special);
        for(int i=0;i<32;i++)
            Write(source,"clusters/"+cluster+"/Transfer/"+hosts[0]+
                "/extra-transfer-"+i.ToString("D2")+".bin","synthetic transfer "+i);
        var ownership=new MultiHostBackupOwnershipSnapshot(hosts[0],
            hostRecords,clusterRecords,mapRecords.ToArray());
        var draft=MultiHostOfflineCatalogAssembler.Inspect(source,ownership);
        Check(draft.Catalog.Hosts.Length==2&&draft.Catalog.Maps.Length==10&&
              draft.Catalog.Clusters.Length==1&&draft.FileCount>100,
            "all original two-host ten-map identities and transfer payloads are inventoried");
        Check(draft.Catalog.Items.Count(x=>x.Kind==
              MultiHostBackupComponentKind.ClusterTransferManifest)==1&&
              draft.Catalog.Items.Count(x=>x.Kind==
              MultiHostBackupComponentKind.ClusterTransferFile)==50&&
              draft.Catalog.Items.Count(x=>x.Kind==
              MultiHostBackupComponentKind.ClusterTransferFile&&
              x.RelativePath.StartsWith("placeholder-",StringComparison.Ordinal))==9,
            "50 transfer entries include nine extensionless placeholders and one nonempty snapshot manifest");

        string transfer=Path.Combine(source,"clusters",cluster,"Transfer",hosts[0],"tribute.bin");
        File.AppendAllText(transfer,"tamper");
        bool blocked=false;
        try{MultiHostOfflineCatalogAssembler.Publish(source,ownership,draft.Fingerprint);}
        catch(IOException){blocked=true;}
        Check(blocked&&!File.Exists(Path.Combine(source,
                MultiHostOfflineCatalogAssembler.CatalogName)),
            "original transfer change after preview denies catalog publication without writing one");
        // Re-preview precisely the updated OFFLINE source; never accept stale
        // content hashes, old Registry revisions or old confirmation tokens.
        draft=MultiHostOfflineCatalogAssembler.Inspect(source,ownership);
        var catalog=MultiHostOfflineCatalogAssembler.Publish(source,ownership,draft.Fingerprint);
        string catalogPath=Path.Combine(source,MultiHostOfflineCatalogAssembler.CatalogName);
        var inspected=MultiHostClusterBackupArchive.InspectCatalog(catalogPath,ownership);
        Check(inspected.Hosts==2&&inspected.Maps==10&&
              inspected.Components==catalog.Items.Length,
            "generated catalog is accepted unchanged by existing archive engine and ownership guard");
        var verified=MultiHostClusterBackupArchive.BuildFromCatalog(
            catalogPath,archive,ownership);
        string emptySha256=Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>()));
        Check(verified.Hosts.Length==2&&verified.Maps.Length==10&&
              verified.Components.Count(x=>x.Kind==
                MultiHostBackupComponentKind.ClusterTransferFile&&
                x.Length==0&&x.Sha256.Equals(emptySha256,
                    StringComparison.OrdinalIgnoreCase))==9,
            "original archive writer hashes exactly nine legitimate empty transfer placeholders");
        string destination=Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-"+Guid.NewGuid().ToString("N"));
        try
        {
            var sandboxReport=MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(
                archive,destination);
            Check(sandboxReport.SourceBatchId==verified.BatchId&&
                  sandboxReport.Maps.Length==10&&!sandboxReport.ProductionRestoreAllowed,
                "ten-map restore is byte-verified and scoped to an isolated TEMP sandbox");
            for(int i=0;i<9;i++)
            {
                string empty=Path.Combine(destination,"data","clusters",cluster,
                    "TransferFiles",hosts[0],"placeholder-"+i.ToString("D2"));
                Check(File.Exists(empty)&&new FileInfo(empty).Length==0,
                    "TEMP restore retains verified empty transfer placeholder "+i);
            }
            // Restore staging for each REAL host identity from the same
            // verified existing TEMP restore: no alternate archive format.
            // The common cluster transfer tree is included byte-for-byte in
            // BOTH offline inspection bundles; it is NOT a new Store writer.
            string[] stageRoots={Path.Combine(sandbox,"host-restore-stage-hp"),
                Path.Combine(sandbox,"host-restore-stage-ryzen")};
            // Unrelated extra hosts/maps/clusters are allowed in the CURRENT
            // unlimited Registry. The archive remains a complete 2-host
            // subset of that registry and every included ID must still match.
            var expanded=new MultiHostBackupOwnershipSnapshot(ownership.LocalHostId,
                ownership.Hosts.Concat(new[]{
                    new MultiHostBackupHostOwnership("unrelated-third-host",
                        "Independent Third Host",false,true)}).ToArray(),
                ownership.Clusters.Concat(new[]{
                    new MultiHostBackupClusterOwnership("unrelated-cluster",
                        "Another legitimate cluster")}).ToArray(),
                ownership.Maps.Concat(new[]{
                    new MultiHostBackupMapOwnership(Guid.NewGuid(),
                        "unrelated-third-host","Another ASA map","unrelated-cluster")
                }).ToArray());
            for(int side=0;side<2;side++)
            {
                Directory.CreateDirectory(stageRoots[side]);
                var plan=MultiHostHostRestoreStaging.Inspect(destination,hosts[side],
                    stageRoots[side],expanded);
                Check(plan.Maps==5&&plan.Clusters==1&&plan.Components>20&&
                      plan.IdentitySha256.Length==64,
                    "full host Restore preflight binds exact 5-map owner and shared Store/transfer copy");
                var receipt=MultiHostHostRestoreStaging.Stage(plan,expanded);
                string hostStage=Path.Combine(stageRoots[side],plan.StageDirectoryName);
                var validated=MultiHostHostRestoreStaging.Verify(hostStage);
                Check(receipt.BatchId==catalog.BatchId&&
                      validated.Components.Length==plan.Components&&
                      !validated.ProductionRestoreAllowed&&
                      validated.Components.Count(x=>x.Kind==
                          MultiHostBackupComponentKind.ClusterTransferFile&&
                          x.Length==0)==9,
                    "each host stage includes all nine SHA-256 verified zero-byte cluster transfer entries");
                string transferStage=Path.Combine(hostStage,"data","clusters",cluster,
                    "TransferFiles",hosts[0],"tribute.bin");
                Check(File.ReadAllBytes(transferStage).SequenceEqual(
                    File.ReadAllBytes(transfer)),
                    "host-specific bundle includes original shared tribute transfer bytes");
                bool refusedOverwrite=false;
                try{MultiHostHostRestoreStaging.Inspect(destination,hosts[side],
                    stageRoots[side],expanded);}
                catch(IOException){refusedOverwrite=true;}
                Check(refusedOverwrite,"existing host stage cannot be overwritten or silently reimported");
                var stagedMap=mapRecords.First(m=>m.HostId==hosts[side]);
                string stageWorld=Path.Combine(hostStage,"data","hosts",hosts[side],
                    "maps",stagedMap.MapId.ToString("N"),"Saved","world.ark");
                Check(File.ReadAllBytes(stageWorld).SequenceEqual(
                    sourceHashes["maps/"+stagedMap.MapId.ToString("N")+"/Saved/world.ark"]),
                    "real host world bytes preserve original identity and restore mapping");
            }

            foreach(var kv in sourceHashes)
            {
                string[] parts=kv.Key.Split('/');
                Guid map=Guid.Parse(parts[1]);
                var owner=mapRecords.Single(m=>m.MapId==map).HostId;
                string relative=string.Join('/',parts.Skip(2));
                string restored=Path.Combine(destination,"data","hosts",
                    owner,"maps",map.ToString("N"),
                    relative.Replace('/',Path.DirectorySeparatorChar));
                Check(File.Exists(restored)&&
                      File.ReadAllBytes(restored).SequenceEqual(kv.Value),
                    "real map world/profile/tribe/INI/plugin bytes survive archive and restore: "+relative);
            }
            foreach(var rel in new[]{"characters.arkprofile","inventory.bin","tribute.bin","dinos.bin"})
            {
                string restored=Path.Combine(destination,"data","clusters",
                    cluster,"TransferFiles",hosts[0],rel);
                string original=Path.Combine(source,"clusters",cluster,"Transfer",
                    hosts[0],rel);
                Check(File.ReadAllBytes(restored).SequenceEqual(File.ReadAllBytes(original)),
                    "real shared transfer data survives unchanged, including "+rel);
            }
            var zero=Path.Combine(destination,"data","hosts",hosts[0],
                "maps",mapRecords[0].MapId.ToString("N"),
                "Saved","character-0.arkprofile");
            Check(new FileInfo(zero).Length==0,
                "original zero-byte arkprofile remains exactly zero after restore");
        }
        finally
        {
            if(Directory.Exists(destination))Directory.Delete(destination,true);
        }
        // Exercise the same real v1 engine interrupted during a full TWO
        // host, ten-map archive. Recovery retains every previous partial byte
        // and uses the ORIGINAL archive writer to rebuild from offline copies.
        string recoveryRoot=Path.Combine(sandbox,"ten-map-recovery-tests");
        Directory.CreateDirectory(recoveryRoot);
        string recoveryTarget=Path.Combine(recoveryRoot,
            "Untamed-MultiHost-20261109-200000-"+catalog.BatchId.ToString("N"));
        bool failedMidCopy=false;
        try
        {
            MultiHostClusterBackupArchive.BuildFromCatalog(catalogPath,
                recoveryTarget,ownership,afterCopiedForTesting:index=>{
                    if(index==3)throw new IOException("Synthetic Manager crash");
                });
        }
        catch(IOException){failedMidCopy=true;}
        Check(failedMidCopy&&Directory.Exists(recoveryTarget+".incomplete"),
            "real ten-map two-host archive failure retains an incomplete checkpoint");
        var review=MultiHostOfflineArchiveRecovery.Inspect(
            recoveryTarget+".incomplete",catalogPath,ownership);
        Check(review.BatchId==catalog.BatchId&&review.Components==catalog.Items.Length,
            "recovery checkpoint and complete original catalog carry exactly matching batch identity");
        byte[] catalogueBefore=File.ReadAllBytes(catalogPath);
        File.AppendAllText(catalogPath," ");
        bool changedCatalogueDenied=false;
        try{MultiHostOfflineArchiveRecovery.RetainInterrupted(review,ownership);}
        catch(IOException){changedCatalogueDenied=true;}
        Check(changedCatalogueDenied&&Directory.Exists(review.Stage),
            "catalog altered after recovery preview rejects without moving the old archive stage");
        File.WriteAllBytes(catalogPath,catalogueBefore);
        using(var lease=MultiHostOfflineArchiveRecovery.AcquireLease(review.Target))
        {
            bool doubleWriterDenied=false;
            try
            {
                using var other=MultiHostOfflineArchiveRecovery.AcquireLease(review.Target);
            }
            catch(IOException){doubleWriterDenied=true;}
            Check(doubleWriterDenied,
                "two archive/recovery Manager processes cannot share the same output owner");
            string retained=MultiHostOfflineArchiveRecovery.RetainInterrupted(review,ownership);
            Check(Directory.Exists(Path.Combine(recoveryRoot,retained))&&
                  !Directory.Exists(review.Stage),
                "all interrupted two-host Store/transfer copies are retained, not deleted");
            var finished=MultiHostClusterBackupArchive.BuildFromCatalog(
                catalogPath,recoveryTarget,ownership);
            Check(finished.BatchId==catalog.BatchId&&
                  finished.Hosts.Length==2&&finished.Maps.Length==10,
                "normal v1 writer rebuilds complete ten-map two-host archive after explicit recovery");
        }
        string recoveryTemp=Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-"+Guid.NewGuid().ToString("N"));
        try
        {
            MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(recoveryTarget,recoveryTemp);
            string restoredTribute=Path.Combine(recoveryTemp,"data","clusters",
                cluster,"TransferFiles",hosts[0],"tribute.bin");
            Check(File.ReadAllBytes(restoredTribute).SequenceEqual(File.ReadAllBytes(transfer)),
                "recovered shared tribute payload restores exactly in TEMP, no original data touched");
        }
        finally
        {
            if(Directory.Exists(recoveryTemp))Directory.Delete(recoveryTemp,true);
        }

        // Negative zero-length cases: a real payload with an extension,
        // important Store metadata, world, tribe or INI cannot be silently
        // reclassified as a valid empty transfer placeholder.
        string[] requiredPayloads={
            "clusters/"+cluster+"/Transfer/"+hosts[0]+"/tribute.bin",
            "clusters/"+cluster+"/Transfer/"+hosts[0]+"/snapshot.json",
            "clusters/"+cluster+"/Transfer/"+hosts[0]+"/.arkproftemp",
            "clusters/"+cluster+"/Transfer/"+hosts[0]+"/.arktributetribe",
            "clusters/"+cluster+"/Transfer/"+hosts[0]+"/.formertribeownerlog",
            "maps/"+mapRecords[0].MapId.ToString("N")+"/Saved/world.ark",
            "maps/"+mapRecords[0].MapId.ToString("N")+"/Saved/tribe-0.arktribe",
            "maps/"+mapRecords[0].MapId.ToString("N")+"/WindowsServer/GameUserSettings.ini"
        };
        foreach(string relative in requiredPayloads)
        {
            string full=Path.Combine(source,relative.Replace('/',
                Path.DirectorySeparatorChar));
            byte[] original=File.ReadAllBytes(full);
            bool zeroRejected=false;
            try
            {
                File.WriteAllBytes(full,Array.Empty<byte>());
                MultiHostClusterBackupArchive.InspectCatalog(catalogPath,ownership);
            }
            catch(InvalidDataException){zeroRejected=true;}
            finally{File.WriteAllBytes(full,original);}
            Check(zeroRejected,
                "zero-byte world/tribe/INI/typed transfer or Store manifest must fail: "+relative);
        }
        string missingPlaceholder=Path.Combine(source,"clusters",cluster,
            "Transfer",hosts[0],"placeholder-00");
        string movedPlaceholder=missingPlaceholder+".moved";
        bool missingRejected=false;
        File.Move(missingPlaceholder,movedPlaceholder);
        try{MultiHostClusterBackupArchive.InspectCatalog(catalogPath,ownership);}
        catch(InvalidDataException){missingRejected=true;}
        finally{File.Move(movedPlaceholder,missingPlaceholder);}
        Check(missingRejected,
            "missing or renamed original zero-byte transfer entry fails inventory validation");

        // Once catalog exists, never overwrite or transparently re-import a
        // possibly stale copied source. Rebuilding needs a separate folder.
        bool denied=false;
        try{MultiHostOfflineCatalogAssembler.Inspect(source,ownership);}
        catch(IOException){denied=true;}
        Check(denied,"generated offline catalog cannot be silently overwritten");
    }
}
