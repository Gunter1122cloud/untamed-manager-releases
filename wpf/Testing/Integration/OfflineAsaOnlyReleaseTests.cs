using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

public static class OfflineAsaOnlyReleaseTests
{
    private static void Check(bool condition,string description)
    {
        if(!condition)throw new Exception("ASA ONLY RELEASE TEST FAILED: "+description);
        Console.WriteLine("PASS ASA-only WPF: "+description);
    }

    public static void Run(string root)
    {
        var shipping=typeof(ClusterService).Assembly;
        foreach(string forbidden in new[]{
            "UntamedManager.Core.MariaDbCompatibility",
            "UntamedManager.Core.MariaDbDumpAdapter",
            "UntamedManager.Core.OfflineSqlArchive",
            "UntamedManager.Core.OfflineClusterBackup",
            "UntamedManager.Core.ClusterRestoreCoordinator"})
            Check(shipping.GetType(forbidden,false)==null,
                "native WPF shipping assembly excludes historical SQL type "+forbidden);
        var window=shipping.GetType("UntamedManager.MainWindow",true)!;
        Check(window.GetMethod("ShowSqlBackupControls",
            System.Reflection.BindingFlags.NonPublic|
            System.Reflection.BindingFlags.Instance)==null,
            "native WPF shipping window has no SQL backup/restore handler");
        string baseDir=Path.Combine(root,"asa-only-wpf-offline");
        string hp=Path.Combine(baseDir,"HP"),ryzen=Path.Combine(baseDir,"Ryzen");
        string transfer=Path.Combine(baseDir,"transfer-copy");
        Directory.CreateDirectory(hp);Directory.CreateDirectory(ryzen);
        Directory.CreateDirectory(Path.Combine(transfer,".kova-store-v1"));
        File.WriteAllText(Path.Combine(transfer,".kova-store-v1","operation.json"),
            "{\"schema\":1,\"source\":\"synthetic-test-only\"}");
        File.WriteAllBytes(Path.Combine(transfer,"zero-legacy.arkprofile"),
            Array.Empty<byte>());
        var maps=new Guid[10];
        for(int i=0;i<10;i++)
        {
            maps[i]=Guid.NewGuid();
            string src=Path.Combine(baseDir,"source-"+i);
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src,"World"+i+".ark"),
                "SYNTHETIC ASA TEST WORLD "+i);
            TestBackupPackage.Create(src,
                Path.Combine(i<5?hp:ryzen,"map-"+i),maps[i],
                "STOPPED TEST MAP ONLY");
        }
        string package=Path.Combine(baseDir,"asa-only-archive");
        var report=OfflineAsaClusterArchive.BuildFromFolders(
            hp,ryzen,transfer,package);
        Check(report.Schema==2 && report.Maps.Length==10 &&
              report.Maps.Count(m=>m.Host=="HP")==5 &&
              report.Maps.Count(m=>m.Host=="RYZEN")==5 &&
              report.Transfer.Length==2 &&
              !report.LiveConsistencyCertified &&
              !report.LiveStoreWriterCertified &&
              !report.RestoreToProductionAllowed,
            "ten map integrity copies and original Store metadata, never live certification");
        Check(OfflineAsaClusterArchive.Verify(package).Batch==report.Batch &&
              !Directory.Exists(Path.Combine(package,"SQL")) &&
              new FileInfo(Path.Combine(package,"TRANSFER",
                  "zero-legacy.arkprofile")).Length==0,
            "WPF ASA-only backup never requires SQL and preserves historical zero-byte file");
        string restore=Path.Combine(Path.GetTempPath(),
            "Untamed-AsaRestore-Lab-"+Guid.NewGuid().ToString("N"));
        var receipt=OfflineAsaRestoreLab.MaterializeToLab(package,restore);
        Check(receipt.SourceBatch==report.Batch &&
              receipt.MapFiles.Length==10 &&
              !receipt.ProductionRestoreAllowed &&
              OfflineAsaRestoreLab.VerifyLab(restore).SourceBatch==report.Batch,
            "Windows TEMP restore confirms ten world hashes without production writes");
        var changed=Path.Combine(restore,"HP",maps[0].ToString("N"),"World0.ark");
        File.AppendAllText(changed,"-CORRUPT");
        bool refused=false;
        try{OfflineAsaRestoreLab.VerifyLab(restore);}
        catch(InvalidDataException){refused=true;}
        Check(refused,"corrupted world after lab restore is detected");
        string transferMeta=Path.Combine(package,"TRANSFER",".kova-store-v1","operation.json");
        File.AppendAllText(transferMeta,"-CORRUPT");
        refused=false;
        try{OfflineAsaClusterArchive.Verify(package);}
        catch(InvalidDataException){refused=true;}
        Check(refused,"modified Store metadata invalidates archive");
    }
}
