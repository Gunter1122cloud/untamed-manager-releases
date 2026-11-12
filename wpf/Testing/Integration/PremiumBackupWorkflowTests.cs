using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UntamedManager.Core;

// All fixtures reside in the integration runner's isolated TEMP root.
public static class PremiumBackupWorkflowTests
{
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException("BACKUP CENTER TEST: "+reason);
        Console.WriteLine("PASS backup-center "+reason);
    }
    public static Task Run(string root)
    {
        string fixture=Path.Combine(root,"premium-backup-center-fixture");
        string install=Path.Combine(fixture,"mock-map");
        string saved=Path.Combine(install,"ShooterGame","Saved");
        string game=Path.Combine(saved,"SavedArksLocal","TheIsland.ark");
        string ini=Path.Combine(saved,"Config","WindowsServer","Game.ini");
        string plugin=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "ArkApi","Plugins","SamplePlugin","config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
        File.WriteAllText(game,"offline synthetic world data, not an actual ARK save");
        File.WriteAllText(ini,"[ServerSettings]\r\nXPMultiplier=2\r\n");
        File.WriteAllText(plugin,"{\"offline_fixture\":true}");
        var map=new LocalMap {
            Id=Guid.NewGuid(),Name="Backup center offline fixture",InstallationPath=install
        };
        var stopped=new ProcessCheck(false,"confirmed stopped by synthetic inspector",null,true);
        var running=new ProcessCheck(true,"synthetic running",null);
        var ambiguous=new ProcessCheck(false,"ambiguous",null,false);
        string catalog=Path.Combine(fixture,"backup-catalog",map.Id.ToString("N"));
        string target=Path.Combine(catalog,"20261105-001-unique");
        var scope=LocalBackupScope.WorldAndProfiles|LocalBackupScope.Ini|LocalBackupScope.Plugins;

        var preview=LocalBackupWorkflow.Preview(map,stopped,target,scope);
        Check(preview.MapId==map.Id&&preview.Scope==scope&&preview.Files==3 &&
              preview.SelectedBytes>0&&preview.ReserveBytes>=64*1024*1024 &&
              !Directory.Exists(catalog)&&!Directory.Exists(target),
            "read-only preflight counts exactly selected files and does not create destination directories");
        Check(File.ReadAllText(game).StartsWith("offline synthetic world data") &&
              File.ReadAllText(plugin)=="{\"offline_fixture\":true}",
            "read-only preflight leaves all source and plugin data byte-identical");
        bool deniedRunning=false,deniedUnknown=false,deniedInside=false;
        bool deniedNone=false,deniedMissing=false;
        try{LocalBackupWorkflow.Preview(map,running,target,scope);}
        catch(InvalidOperationException){deniedRunning=true;}
        try{LocalBackupWorkflow.Preview(map,ambiguous,target,scope);}
        catch(InvalidOperationException){deniedUnknown=true;}
        try{LocalBackupWorkflow.Preview(map,stopped,Path.Combine(install,"backups","pkg"),scope);}
        catch(InvalidDataException){deniedInside=true;}
        try{LocalBackupWorkflow.Preview(map,stopped,target,LocalBackupScope.None);}
        catch(InvalidDataException){deniedNone=true;}
        try{LocalBackupWorkflow.Preview(map,stopped,target,
            LocalBackupScope.WorldAndProfiles|(LocalBackupScope)16);}
        catch(InvalidDataException){deniedMissing=true;}
        Check(deniedRunning&&deniedUnknown&&deniedInside&&deniedNone&&deniedMissing &&
              !Directory.Exists(catalog),
            "unsafe or unknown process, overlapping destination and invalid scopes fail before I/O");

        // Protect the original character/tribe profile from an accidentally
        // reported complete backup. Historical -usestore is special:
        // empty legacy loose files must be preserved, not modified.
        string emptyProfile=Path.Combine(Path.GetDirectoryName(game)!,
            "76561198000000001.arkprofile");
        File.WriteAllBytes(emptyProfile,Array.Empty<byte>());
        bool looseRejected=false;
        try{LocalBackupWorkflow.Preview(map,stopped,target,scope);}
        catch(InvalidDataException e) when(e.Message.Contains("UM-BACKUP-ZERO-BYTE-CRITICAL-SAVE"))
        {looseRejected=true;}
        Check(looseRejected&&!Directory.Exists(target),
            "0-byte loose character file rejects normal stopped-map backup");
        map.Arguments="-usestore";
        var legacyPreview=LocalBackupWorkflow.Preview(map,stopped,target,scope);
        Check(legacyPreview.Files==4&&new FileInfo(emptyProfile).Length==0,
            "original Store mode keeps empty historical loose profile bytes unchanged");
        map.Arguments="";
        File.Delete(emptyProfile);
        string emptyWorld=Path.Combine(Path.GetDirectoryName(game)!,"EmptyOtherMap.ark");
        File.WriteAllBytes(emptyWorld,Array.Empty<byte>());
        map.Arguments="-usestore";
        bool worldRejected=false;
        try{LocalBackupWorkflow.Preview(map,stopped,target,scope);}
        catch(InvalidDataException e) when(e.Message.Contains("UM-BACKUP-ZERO-BYTE-CRITICAL-SAVE"))
        {worldRejected=true;}
        map.Arguments="";
        File.Delete(emptyWorld);
        Check(worldRejected&&!Directory.Exists(target),
            "0-byte ASA world is always rejected, including historical Store mode");

        var backup=LocalBackupWorkflow.Create(map,stopped,target,scope);
        var verified=TestBackupPackage.Verify(target,map.Id);
        Check(backup.PackageDirectory==target&&backup.Manifest.Files.Count==3 &&
              verified.Files.Count==3&&File.Exists(Path.Combine(target,"complete")),
            "confirmed stopped local backup saves and verifies exactly three synthetic files");
        // Simulate an external ASA process taking ownership after bytes
        // were copied but before final commit. The incomplete staging folder
        // must never be published as a successful file backup.
        string racing=Path.Combine(catalog,"race-after-copy");
        int reads=0;
        bool raceRejected=false;
        try
        {
            LocalBackupWorkflow.Create(map,stopped,racing,scope,
                checkCurrent:()=>++reads>=3?running:stopped);
        }
        catch(InvalidOperationException ex) when(ex.Message.Contains(
            "UM-BACKUP-CONFIRMED-STOPPED-REQUIRED"))
        {raceRejected=true;}
        var afterRace=new UntamedOperationCoordinator().Observe(install);
        Check(raceRejected&&reads>=3&&
              !Directory.Exists(racing)&&
              Directory.Exists(racing+".incomplete")&&
              afterRace?.Kind==UntamedOperationKind.Backup&&
              afterRace.Phase==UntamedOperationPhase.CancelledBeforeMutation,
            "ASA startup during staged copy refuses success without a stranded NeedsOperator lease");
        Check(File.ReadAllText(game)=="offline synthetic world data, not an actual ARK save",
            "racing process observer never modifies the original world file");
        var catalogItems=LocalBackupCatalog.Inspect(catalog,map.Id,10);
        Check(catalogItems.Count==2&&catalogItems.Count(x=>x.Valid)==1&&
              catalogItems.Any(x=>!x.Valid&&
                  x.Status.StartsWith("Incomplete backup",StringComparison.Ordinal))&&
              catalogItems.Single(x=>x.Valid).TotalBytes==preview.SelectedBytes,
            "Backup Center lists interrupted staging as invalid and keeps only the verified package restorable");
        bool targetCollision=false,createCollision=false;
        try{LocalBackupWorkflow.Preview(map,stopped,target,scope);}
        catch(IOException){targetCollision=true;}
        try{LocalBackupWorkflow.Create(map,stopped,target,scope);}
        catch(IOException){createCollision=true;}
        Check(targetCollision&&createCollision,
            "preflight and execution both refuse overwriting a completed backup");
        bool forbiddenRestore=false;
        try{TestBackupPackage.Verify(target,Guid.NewGuid());}
        catch(InvalidDataException){forbiddenRestore=true;}
        Check(forbiddenRestore,"backup cannot be verified as another map's package");

        File.AppendAllText(game,"-changed-after-backup");
        Check(TestBackupPackage.Verify(target,map.Id).Files.Count==3 &&
              File.ReadAllText(game).EndsWith("changed-after-backup"),
            "backup verification reads only the package and does not rewrite source world saves");
        return Task.CompletedTask;
    }
}
