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
        var ownership=new MultiHostBackupOwnershipSnapshot(hosts[0],
            hostRecords,clusterRecords,mapRecords.ToArray());
        var draft=MultiHostOfflineCatalogAssembler.Inspect(source,ownership);
        Check(draft.Catalog.Hosts.Length==2&&draft.Catalog.Maps.Length==10&&
              draft.Catalog.Clusters.Length==1&&draft.FileCount>100,
            "all original two-host ten-map identities and transfer payloads are inventoried");
        Check(draft.Catalog.Items.Count(x=>x.Kind==
              MultiHostBackupComponentKind.ClusterTransferManifest)==1&&
              draft.Catalog.Items.Count(x=>x.Kind==
              MultiHostBackupComponentKind.ClusterTransferFile)>=6,
            "a single transfer manifest and character/inventory/tribute/dino payloads are retained");

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
        Check(verified.Hosts.Length==2&&verified.Maps.Length==10&&
              verified.Components.Any(x=>x.Kind==MultiHostBackupComponentKind.ClusterTransferFile),
            "existing SHA-256 archive engine builds complete two-host Store/transfer batch");
        string destination=Path.Combine(Path.GetTempPath(),
            "Untamed-MultiHost-Restore-"+Guid.NewGuid().ToString("N"));
        try
        {
            var sandboxReport=MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(
                archive,destination);
            Check(sandboxReport.SourceBatchId==verified.BatchId&&
                  sandboxReport.Maps.Length==10&&!sandboxReport.ProductionRestoreAllowed,
                "ten-map restore is byte-verified and scoped to an isolated TEMP sandbox");
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
        // Once catalog exists, never overwrite or transparently re-import a
        // possibly stale copied source. Rebuilding needs a separate folder.
        bool denied=false;
        try{MultiHostOfflineCatalogAssembler.Inspect(source,ownership);}
        catch(IOException){denied=true;}
        Check(denied,"generated offline catalog cannot be silently overwritten");
    }
}
