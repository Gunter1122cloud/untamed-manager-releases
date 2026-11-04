using System.Text.Json;
using UntamedManager.Core;

public static class ManagedBackupRetentionTests
{
    static void Check(bool good,string reason)
    {if(!good)throw new Exception("Managed retention: "+reason);Console.WriteLine("PASS managed-retention "+reason);}
    static bool Refused(Action action)
    {try{action();return false;}catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException){return true;}}
    public static async Task Run(string root)
    {
        foreach(bool crash in new[]{false,true})
        {
            string test=Path.Combine(root,"managed-retention-"+(crash?"crash":"normal"));
            string install=Path.Combine(test,"installation");Directory.CreateDirectory(install);
            var map=new LocalMap{Id=Guid.NewGuid(),InstallationPath=install,Name="owned backup collection"};
            await using var cluster=new ClusterService("retention",Path.Combine(test,"profile"),_=>new(false,"CI stopped",null,true));
            cluster.Config.LocalMaps.Add(map);cluster.Save();
            string source=Path.Combine(test,"backup-source");Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source,"world.fixture"),"owned synthetic bytes");
            string managed=Path.Combine(cluster.Store.Root,"local-backups",map.Id.ToString("N"));
            var packages=new List<string>();
            for(int i=0;i<3;i++)
            {
                string point=Path.Combine(managed,"point-"+i);TestBackupPackage.Create(source,point,map.Id,"isolated local files");
                string manifest=Path.Combine(point,"manifest.json");var state=TestBackupPackage.Verify(point,map.Id);
                File.WriteAllText(manifest,JsonSerializer.Serialize(state with {CreatedUtc=DateTimeOffset.UtcNow.AddDays(i-3)}));
                packages.Add(point);
            }
            var rule=new RetentionRule(1,TimeSpan.FromDays(1),0);
            Check(Refused(()=>ManagedBackupRetention.Quarantine(cluster,map.Id,packages[2],rule)),
                "caller cannot forge eligibility for newest verified recovery point");
            if(crash)
            {
                Check(Refused(()=>ManagedBackupRetention.QuarantineCore(cluster,map.Id,packages[0],rule,
                    ()=>throw new IOException("CI interruption after atomic directory move"))),"interruption after move never publishes success");
                var pending=ManagedBackupRetention.InspectQuarantine(cluster,map.Id).Single();
                Check(pending.Status=="verified-quarantine"&&!Directory.Exists(packages[0])&&
                    cluster.Operations.Observe(install)!.Phase==UntamedOperationPhase.NeedsOperator,
                    "sealed intent finds intact point after crash and preserves pending operation");
                ManagedBackupRetention.RestoreToCatalog(cluster,map.Id,pending.Transaction);
                Check(TestBackupPackage.Verify(packages[0],map.Id).Files.Count==1&&
                    cluster.Operations.Observe(install)!.Phase==UntamedOperationPhase.NeedsOperator,
                    "explicit crash recovery returns the point to catalog without clearing original journal");
                continue;
            }
            ManagedBackupRetention.Protect(cluster,map.Id,packages[0],true);
            Check(Refused(()=>ManagedBackupRetention.Quarantine(cluster,map.Id,packages[0],rule)),
                "protected point cannot be moved even when retention count is exceeded");
            string corrupt=Path.Combine(managed,"newest-corrupt");TestBackupPackage.Create(source,corrupt,map.Id,"isolated local files");
            File.WriteAllText(Path.Combine(corrupt,"data","world.fixture"),"corrupted bytes");
            var preview=ManagedBackupRetention.Preview(cluster,map.Id,rule);
            Check(!preview.Items.Single(x=>x.Backup.Package==packages[2]).Eligible&&
                !preview.Items.Single(x=>x.Backup.Package==corrupt).Eligible,
                "a corrupt newer point cannot displace the last verified backup or become eligible itself");
            string external=Path.Combine(test,"external-copy");TestBackupPackage.Create(source,external,map.Id,"isolated local files");
            Check(Refused(()=>ManagedBackupRetention.Quarantine(cluster,map.Id,external,rule))&&Directory.Exists(external),
                "custom/outside folders are never moved by managed retention");
            var moved=ManagedBackupRetention.Quarantine(cluster,map.Id,packages[1],rule);
            Check(!Directory.Exists(packages[1])&&TestBackupPackage.Verify(moved.Package,map.Id).Files.Count==1&&
                File.ReadAllText(Path.Combine(source,"world.fixture"))=="owned synthetic bytes",
                "actual atomic quarantine move preserves package and leaves source files untouched");
            TestBackupPackage.Create(source,packages[1],map.Id,"replacement point must survive");
            Check(Refused(()=>ManagedBackupRetention.RestoreToCatalog(cluster,map.Id,moved.Transaction))&&Directory.Exists(moved.Package),
                "catalog restore refuses occupied original name without overwriting either point");
            Directory.Move(packages[1],Path.Combine(test,"occupied-point-preserved"));
            string data=Path.Combine(moved.Package,"data","world.fixture");File.WriteAllText(data,"tampered quarantine");
            Check(Refused(()=>ManagedBackupRetention.RestoreToCatalog(cluster,map.Id,moved.Transaction))&&!Directory.Exists(packages[1]),
                "corrupt quarantine cannot be restored into the catalog");
            File.WriteAllText(data,"owned synthetic bytes");
            string restored=ManagedBackupRetention.RestoreToCatalog(cluster,map.Id,moved.Transaction);
            Check(restored==packages[1]&&TestBackupPackage.Verify(restored,map.Id).Files.Count==1&&
                ManagedBackupRetention.InspectQuarantine(cluster,map.Id).Single().Status=="restored-to-catalog",
                "verified catalog restore moves original point back without touching the server installation");
            Check(Refused(()=>ManagedBackupRetention.RestoreToCatalog(cluster,map.Id,moved.Transaction)),
                "completed catalog restore cannot repeat");
        }
    }
}
