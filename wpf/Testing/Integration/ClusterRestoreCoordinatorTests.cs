using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UntamedManager.Core;

public static class ClusterRestoreCoordinatorTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new Exception("Offline restore staging failed: "+reason);
        Console.WriteLine("PASS offline-restore "+reason);
    }
    private static bool Refused(Action work)
    {
        try{work();return false;}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {return true;}
    }

    public static void Run(string root)
    {
        string sourceRoot=Path.Combine(root,"restore-fixture-input");
        string packagesRoot=Path.Combine(root,"restore-fixture-packages");
        var maps=new List<OfflineMapBackupInput>();
        Guid sqlHost=Guid.NewGuid();
        for(int i=0;i<10;i++)
        {
            Guid id=Guid.NewGuid();
            string name="FixtureMap"+i;
            string source=Path.Combine(sourceRoot,name);
            string world=Path.Combine(source,"ShooterGame","Saved","SavedArks",name+".ark");
            Directory.CreateDirectory(Path.GetDirectoryName(world)!);
            File.WriteAllText(world,"synthetic map world and profile data "+i);
            string package=Path.Combine(packagesRoot,name);
            TestBackupPackage.Create(source,package,id,"Isolated owner-scoped map backup");
            maps.Add(new(i<5?"HP":"RYZEN",id,name,package));
        }
        var sql=new List<OfflineSqlBackupInput>();
        foreach(string database in OfflineSqlArchive.SupportedDatabases)
        {
            string source=Path.Combine(sourceRoot,"sql-"+database);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source,database+".sql"),
                "CREATE TABLE synthetic_"+database+" (id int);");
            string package=Path.Combine(packagesRoot,"sql-"+database);
            TestBackupPackage.Create(source,package,sqlHost,
                "Offline SQL fixture only: "+database+"; isolated acceptance data");
            sql.Add(new(database,sqlHost,package));
        }
        string cluster=Path.Combine(root,"source-10-map-cluster");
        var sourceArchive=OfflineClusterBackup.Build(new(maps.ToArray(),sql.ToArray()),cluster);
        Check(sourceArchive.Items.Length==13&&!sourceArchive.RealAsaConsistencyProven,
            "ten independently checked map packages and three SQL packages prepared");

        string stagingFailure=Path.Combine(Path.GetTempPath(),
            "Untamed-Isolated-Restore-"+Guid.NewGuid().ToString("N"));
        Check(Refused(()=>ClusterRestoreCoordinator.MaterializeToLab(
            cluster,stagingFailure,default,index=>{
                if(index==2)throw new IOException("simulated power loss after two files");
            })) &&
            Directory.Exists(stagingFailure+".incomplete") &&
            !Directory.Exists(stagingFailure),
            "interrupted real byte copy cannot publish a restore workspace");
        Check(Refused(()=>ClusterRestoreCoordinator.VerifyLab(stagingFailure+".incomplete")),
            "public restore verification rejects an unfinished staging directory");

        string destination=Path.Combine(Path.GetTempPath(),
            "Untamed-Isolated-Restore-"+Guid.NewGuid().ToString("N"));
        var restored=ClusterRestoreCoordinator.MaterializeToLab(cluster,destination);
        Check(restored.Maps.Length==10&&restored.Sql.Length==3&&
            !restored.ProductionRestoreAllowed,
            "all ten host-scoped maps and three SQL scripts materialized without production restore permission");
        string worldFile=Path.Combine(destination,"HP",maps[0].Map.ToString("N"),
            "ShooterGame","Saved","SavedArks",maps[0].Name+".ark");
        Check(File.ReadAllText(worldFile)=="synthetic map world and profile data 0",
            "restored map retains its owner and exact ASA-relative world path");
        Check(File.Exists(Path.Combine(destination,"SQL","arkshop.sql")),
            "SQL output is a verified file, never a live database import");
        Check(ClusterRestoreCoordinator.VerifyLab(destination).SourceBatch==sourceArchive.Batch,
            "independent disk verification succeeds only for an intact published workspace");
        string indexFile=Path.Combine(destination,"restore-lab-index.json");
        byte[] sealedIndex=File.ReadAllBytes(indexFile);
        File.AppendAllText(indexFile," ");
        Check(Refused(()=>ClusterRestoreCoordinator.VerifyLab(destination)),
            "index byte corruption fails sealed manifest SHA verification");
        File.WriteAllBytes(indexFile,sealedIndex);
        Check(ClusterRestoreCoordinator.VerifyLab(destination).SourceBatch==sourceArchive.Batch,
            "restoring exact index bytes reestablishes offline-only integrity");
        Check(Refused(()=>ClusterRestoreCoordinator.MaterializeToLab(cluster,destination)),
            "existing output is never silently replaced");

        File.AppendAllText(worldFile,"tampered");
        Check(Refused(()=>ClusterRestoreCoordinator.VerifyLab(destination)),
            "one altered profile/world file invalidates staged restore");

        string forbidden=Path.Combine(root,"not-in-third-machine-temp");
        Check(Refused(()=>ClusterRestoreCoordinator.MaterializeToLab(cluster,forbidden))&&
              !Directory.Exists(forbidden),
            "direct installation or arbitrary non-TEMP paths are blocked");
        Check(OfflineClusterBackup.Verify(cluster).Items.Length==13,
            "staging, interruption and tamper tests leave all original backup sources unchanged");
    }
}
