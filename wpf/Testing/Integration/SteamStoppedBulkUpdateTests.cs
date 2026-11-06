using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class SteamStoppedBulkUpdateTests
{
    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException("STOPPED STEAM BULK: "+message);
        Console.WriteLine("PASS stopped Steam bulk: "+message);
    }
    private static LocalMap CreateMap(string root,string name,int game)
    {
        SteamStoppedMapUpdaterTests.Prepare(root);
        return new LocalMap{Id=Guid.NewGuid(),Name=name,
            InstallationPath=root,
            ExecutablePath=Path.Combine(root,"ShooterGame","Binaries","Win64",
                "ArkAscendedServer.exe"),
            GamePort=game,QueryPort=27015+(game-7777),RconPort=37015+(game-7777)};
    }
    private static ProcessCheck Stopped(LocalMap m)=>
        new(false,"CI confirmed stopped",null,true);
    public static async Task Run(string sandbox,string? fakeExe)
    {
        if(string.IsNullOrWhiteSpace(fakeExe)||!File.Exists(fakeExe))
            throw new InvalidDataException("Synthetic FakeSteamCMD.exe is missing");
        string root=Path.Combine(sandbox,"stopped-bulk-"+Guid.NewGuid().ToString("N"));
        var steam=Path.Combine(root,"tools","steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(steam)!);
        File.WriteAllText(steam,"CI placeholder");
        await using(var cluster=new ClusterService("bulk-local",root,Stopped))
        {
            var first=CreateMap(Path.Combine(root,"island"),"The Island",7777);
            var second=CreateMap(Path.Combine(root,"ragnarok"),"Ragnarok",7817);
            cluster.AddMap(first);
            cluster.AddMap(second);
            var sameRoot=new LocalMap{Id=Guid.NewGuid(),Name="The Center",
                InstallationPath=first.InstallationPath,
                ExecutablePath=first.ExecutablePath,
                GamePort=7837,QueryPort=27075,RconPort=37075};
            cluster.AddMap(sameRoot);
            var receipts=await SteamStoppedMapUpdater.RunAllFakeFixtureAsync(
                cluster,new[]{first,sameRoot,second},
                steam,fakeExe,1024L*1024*1024,TimeSpan.FromMinutes(2));
            Check(receipts.Count==2 &&
                receipts.Select(x=>x.InstallRoot).Distinct(
                    StringComparer.OrdinalIgnoreCase).Count()==2 &&
                receipts.All(x=>x.NewBuild=="123457"),
                "shared ASA install is updated once; distinct local installs run one by one");
            Check(cluster.Check(first).ConfirmedStopped &&
                cluster.Check(second).ConfirmedStopped &&
                new SteamSerialMaintenanceQueue(cluster.Store.Root,
                    cluster.Config.LocalHostId).Observe()?.Phase==SteamQueuePhase.Completed,
                "bulk update leaves maps stopped and marks local queue complete");
        }
        string failedRoot=Path.Combine(sandbox,
            "stopped-bulk-failure-"+Guid.NewGuid().ToString("N"));
        await using(var cluster=new ClusterService("bulk-fail",failedRoot,Stopped))
        {
            var first=CreateMap(Path.Combine(failedRoot,"first"),"The Island",7877);
            var second=CreateMap(Path.Combine(failedRoot,"second"),"Astraeos",7897);
            var third=CreateMap(Path.Combine(failedRoot,"third"),"Ragnarok",7917);
            // Corrupt the second precondition by removing its last world save
            // BEFORE any Steam updater runs. The queue must never reach third.
            Directory.Delete(Path.Combine(second.InstallationPath,
                "ShooterGame","Saved","SavedArks"),true);
            cluster.AddMap(first);
            cluster.AddMap(second);
            cluster.AddMap(third);
            bool stoppedOnFailure=false;
            try{await SteamStoppedMapUpdater.RunAllFakeFixtureAsync(
                cluster,new[]{first,second,third},
                steam,fakeExe,1024L*1024*1024,TimeSpan.FromMinutes(2));}
            catch(InvalidDataException){stoppedOnFailure=true;}
            var status=new SteamSerialMaintenanceQueue(cluster.Store.Root,
                cluster.Config.LocalHostId).Observe();
            Check(stoppedOnFailure && status?.Phase==SteamQueuePhase.NeedsOperator &&
                status.NextIndex==1 && status.ActiveMap==second.Id,
                "failed second backup stops local queue with persisted operator-review marker");
            Check(AsaSteamBuildCatalog.InstalledBuild(first.InstallationPath)=="123457" &&
                AsaSteamBuildCatalog.InstalledBuild(second.InstallationPath)=="123456" &&
                AsaSteamBuildCatalog.InstalledBuild(third.InstallationPath)=="123456",
                "no third installation is touched after one local map fails");
        }
    }
}
