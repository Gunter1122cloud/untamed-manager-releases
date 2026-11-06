using System.IO.Compression;
using System.Text.Json;
using UntamedManager.Core;

public static class GuardedPluginUpdateTests
{
    static void Check(bool yes,string reason)
    {if(!yes)throw new Exception("Guarded plugin: "+reason);Console.WriteLine("PASS guarded-plugin "+reason);}
    public static async Task Run(string root)
    {
        string zipPath=Path.Combine(root,"guarded-plugin.zip");
        using(var zip=ZipFile.Open(zipPath,ZipArchiveMode.Create))
        {
            using(var output=new StreamWriter(zip.CreateEntry("SamplePlugin/SamplePlugin.dll").Open()))output.Write("new binary");
            using(var output=new StreamWriter(zip.CreateEntry("SamplePlugin/config.json").Open()))output.Write("must not overwrite");
        }
        foreach(string mode in new[]{"ok","shared-running","started-after-lease","started-before-swap"})
        {
            string folder=Path.Combine(root,"guarded-plugin-"+mode),install=Path.Combine(folder,"installation");
            string target=Path.Combine(install,"ShooterGame","Binaries","Win64","ArkApi","Plugins","SamplePlugin");
            Directory.CreateDirectory(target);string dll=Path.Combine(target,"SamplePlugin.dll"),config=Path.Combine(target,"config.json");
            File.WriteAllText(dll,"old binary");File.WriteAllText(config,"original config");
            File.WriteAllText(Path.Combine(target,"data.db"),"original data");
            var map=new LocalMap{Id=Guid.NewGuid(),Name="plugin owner",InstallationPath=install};
            var other=new LocalMap{Id=Guid.NewGuid(),Name="shared installation",InstallationPath=install};
            int calls=0;bool enableRace=true;
            await using var cluster=new ClusterService("plugin-guard",Path.Combine(folder,"profile"),local=>{
                int call=++calls;
                bool running=enableRace&&((mode=="shared-running"&&local.Id==other.Id)||
                    (mode=="started-after-lease"&&call>=2)||(mode=="started-before-swap"&&call>=3));
                return running?new(true,"running CI fixture",null):new(false,"stopped CI fixture",null,true);
            });
            cluster.Config.LocalMaps.Add(map);
            if(mode=="shared-running")cluster.Config.LocalMaps.Add(other);
            cluster.Save();
            string backups=Path.Combine(folder,"backups");
            if(mode=="ok")
            {
                var result=SafePluginZip.InstallGuarded(cluster,map.Id,zipPath,backups);
                Check(File.ReadAllText(dll)=="new binary"&&File.ReadAllText(config)=="original config",
                    "actual guarded update replaces DLL and ignores ZIP configuration");
                var transaction=JsonSerializer.Deserialize<DirectoryUpdateTransaction.State>(File.ReadAllText(
                    Path.Combine(result.PreviousBackup+".transaction","update-transaction.json")))!;
                Check(transaction.Phase=="committed","whole plugin snapshot has a committed durable journal");
                File.WriteAllText(config,"new local config");File.WriteAllText(Path.Combine(target,"data.db"),"new local data");
                SafePluginZip.RollbackGuarded(cluster,map.Id,"SamplePlugin",result.PreviousBackup!,backups);
                Check(File.ReadAllText(dll)=="old binary"&&File.ReadAllText(config)=="new local config"&&
                    File.ReadAllText(Path.Combine(target,"data.db"))=="new local data",
                    "guarded binary rollback retains current configuration and database bytes");
            }
            else
            {
                bool refused=false;try{SafePluginZip.InstallGuarded(cluster,map.Id,zipPath,backups);}
                catch(InvalidOperationException){refused=true;}
                enableRace=false;
                Check(refused&&File.ReadAllText(dll)=="old binary"&&File.ReadAllText(config)=="original config",
                    mode+" blocks binary mutation even when a prior stopped observation was valid");
            }
        }
    }
}
