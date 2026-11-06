using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using UntamedManager.Core;

public static class AsaApiIsolatedInstallerTests
{
    sealed class Handler(byte[] bytes):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken ct)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
    }
    static void Check(bool value,string label)
    {if(!value)throw new Exception("API install failed: "+label);Console.WriteLine("PASS api-install "+label);}
    static bool Refused(Action action)
    {try{action();return false;}catch(Exception e) when(e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException){return true;}}
    public static async Task Run()
    {
        byte[] pe=new byte[256];using(var ms=new MemoryStream(pe))using(var w=new BinaryWriter(ms))
        {w.Write((ushort)0x5A4D);ms.Position=0x3c;w.Write(0x40);ms.Position=0x40;w.Write(0x4550);w.Write((ushort)0x8664);ms.Position=0x58;w.Write((ushort)0x20b);}
        using var bytes=new MemoryStream();
        using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))
            foreach(string name in new[]{
                "AsaApiLoader.exe","ArkApi/AsaApi.dll",
                "libcrypto-3-x64.dll","libssl-3-x64.dll",
                "ArkApi/Plugins/Protected/Protected.dll",
                "config.json","ArkApi/Plugins/Protected/data.db"})
            {using var output=zip.CreateEntry(name).Open();output.Write(name.EndsWith(".exe")||name.EndsWith(".dll")?pe:new byte[]{77});}
        byte[] package=bytes.ToArray();string sha=Convert.ToHexString(SHA256.HashData(package));
        var asset=new AsaApiReleaseAsset("AsaApi-2.03.zip",package.Length,
            "https://github.com/ArkServerApi/AsaApi/releases/download/2.03/AsaApi-2.03.zip",sha);
        var release=new AsaApiReleaseInfo("2.03",DateTimeOffset.UtcNow,
            "https://github.com/ArkServerApi/AsaApi/releases/tag/2.03",new[]{asset},false,"synthetic");
        string archive=Path.Combine(Path.GetTempPath(),"Untamed-ApiLab-"+Guid.NewGuid().ToString("N"));
        using(var http=new HttpClient(new Handler(package)))await AsaApiReleaseStaging.StageAsync(http,release,asset,archive);

        async Task Case(string mode)
        {
            string root=Path.Combine(Path.GetTempPath(),"Untamed-ApiInstallLab-"+Guid.NewGuid().ToString("N"));
            string install=Path.Combine(root,"installation"),bin=Path.Combine(install,"ShooterGame","Binaries","Win64");
            string plugin=Path.Combine(bin,"ArkApi","Plugins","Protected");Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(bin,"config.json"),"{\"private\":true}");
            File.WriteAllText(Path.Combine(plugin,"data.db"),"protected database");
            File.WriteAllText(Path.Combine(plugin,"license.key"),"protected license");
            File.WriteAllText(Path.Combine(plugin,"Protected.dll"),"existing protected plugin");
            File.WriteAllText(Path.Combine(bin,"AsaApiLoader.exe"),"old loader");
            File.WriteAllText(Path.Combine(bin,"ArkApi","AsaApi.dll"),"old API");
            var map=new LocalMap{Id=Guid.NewGuid(),InstallationPath=install,Name="isolated API map",
                ExecutablePath=Path.Combine(bin,"ArkAscendedServer.exe")};
            bool running=mode=="running";
            await using var cluster=new ClusterService("api-install",Path.Combine(root,"profile"),
                _=>running?new(true,"CI running",null):new(false,"CI stopped",null,true));
            cluster.Config.LocalMaps.Add(map);cluster.Save();
            if(mode=="ok")
            {
                var result=AsaApiIsolatedInstaller.Install(cluster,map.Id,archive,release,asset);
                Check(result.FilesVerified&&!result.RuntimeVerified&&
                    File.ReadAllBytes(Path.Combine(bin,"AsaApiLoader.exe")).SequenceEqual(pe)&&
                    File.ReadAllBytes(Path.Combine(bin,"ArkApi","AsaApi.dll")).SequenceEqual(pe)&&
                    File.ReadAllBytes(Path.Combine(bin,"libcrypto-3-x64.dll")).SequenceEqual(pe)&&
                    File.ReadAllBytes(Path.Combine(bin,"libssl-3-x64.dll")).SequenceEqual(pe),
                    "core loader, API and known crypto dependencies installed with verified hashes");
                Check(result.BundledPluginsUntouched?.SequenceEqual(new[]{"Protected"})==true,
                    "bundled plugin recognized but excluded from automatic data overwrite");
                var state=JsonSerializer.Deserialize<DirectoryUpdateTransaction.State>(File.ReadAllText(
                    Path.Combine(result.Transaction,"rollback","update-transaction.json")))!;
                Check(state.Phase=="committed","full-tree rollback snapshot durably committed");
            }
            else if(mode=="cancel")
            {
                using var cancel=new CancellationTokenSource();
                Check(Refused(()=>AsaApiIsolatedInstaller.InstallCore(cluster,map.Id,archive,release,asset,
                    cancel.Token,_=>cancel.Cancel())),"interrupted binary replacement rejects success");
                Check(File.ReadAllText(Path.Combine(bin,"AsaApiLoader.exe"))=="old loader"&&
                    File.ReadAllText(Path.Combine(bin,"ArkApi","AsaApi.dll"))=="old API",
                    "actual whole-tree rollback restores both binaries after first-file cancellation");
                Check(cluster.Operations.Observe(install)!.Phase==UntamedOperationPhase.NeedsOperator,
                    "rollback does not silently clear interrupted operation journal");
            }
            else if(mode=="race")
            {
                Check(Refused(()=>AsaApiIsolatedInstaller.InstallCore(cluster,map.Id,archive,release,asset,
                    default,_=>running=true)),"newly running participant prevents further replacement and unsafe rollback");
                Check(Directory.GetFiles(Path.Combine(root,"transactions"),"update-transaction.json",
                    SearchOption.AllDirectories).Length==1,"interrupted snapshot retained for operator recovery");
                running=false;
                string transaction=Directory.GetDirectories(Path.Combine(root,"transactions")).Single();
                AsaApiIsolatedInstaller.RecoverSnapshot(cluster,map.Id,transaction);
                Check(File.ReadAllText(Path.Combine(bin,"AsaApiLoader.exe"))=="old loader",
                    "explicit stopped-map recovery restores interrupted update");
            }
            else
            {
                if(mode=="proxy")File.WriteAllText(Path.Combine(bin,"version.dll"),"unknown proxy");
                Guid owner=mode=="peer"?Guid.NewGuid():map.Id;
                Check(Refused(()=>AsaApiIsolatedInstaller.Install(cluster,owner,archive,release,asset)),
                    mode+" prevents update before binary mutation");
                Check(File.ReadAllText(Path.Combine(bin,"AsaApiLoader.exe"))=="old loader","blocked update preserves old loader");
            }
            Check(File.ReadAllText(Path.Combine(bin,"config.json"))=="{\"private\":true}"&&
                File.ReadAllText(Path.Combine(plugin,"data.db"))=="protected database"&&
                File.ReadAllText(Path.Combine(plugin,"license.key"))=="protected license"&&
                File.ReadAllText(Path.Combine(plugin,"Protected.dll"))=="existing protected plugin",
                "configuration, plugin database and license remain byte-identical in "+mode);
        }
        // The production installer uses its OWN path: unlike the isolated
        // full-tree fixture it selectively backs up ONLY verified API binaries,
        // preserves configs/player plugins, and never handles Store worlds.
        {
            string top=Path.Combine(Path.GetTempPath(),"Untamed-ApiOwned-"+Guid.NewGuid().ToString("N"));
            string install=Path.Combine(top,"official-owned-asa"),
                binary=Path.Combine(install,"ShooterGame","Binaries","Win64"),
                plugin=Path.Combine(binary,"ArkApi","Plugins","PlayerState");
            Directory.CreateDirectory(plugin);
            File.WriteAllBytes(Path.Combine(binary,"ArkAscendedServer.exe"),pe);
            string oldLoader=Path.Combine(binary,"AsaApiLoader.exe");
            File.WriteAllText(oldLoader,"SYNTHETIC OLD LOADER");
            string originalConfig=Path.Combine(binary,"config.json");
            File.WriteAllText(originalConfig,"{\"UserSettings\":\"PRESERVED\"}");
            string playerDb=Path.Combine(plugin,"players.db");
            File.WriteAllText(playerDb,"ORIGINAL PLAYER PLUGIN STATE");
            var owned=new LocalMap {
                Id=Guid.NewGuid(),Name="The Island",InstallationPath=install,
                ExecutablePath=Path.Combine(binary,"ArkAscendedServer.exe"),
                Arguments="TheIsland_WP?listen"
            };
            await using var host=new ClusterService("api-owned",Path.Combine(top,"profile"),
                _=>new ProcessCheck(false,"Synthetic stopped",null,true));
            host.AddMap(owned);
            var receipt=AsaApiOwnedInstaller.ApplyOwnedStopped(
                host,owned.Id,archive,release,asset,true);
            Check(receipt.ByteVerified&&receipt.HasProtectiveCopy&&
                  !receipt.LoaderRuntimeVerified&&!receipt.Restarted &&
                  receipt.Binaries==4 &&
                  File.ReadAllBytes(oldLoader).SequenceEqual(pe)&&
                  File.ReadAllBytes(Path.Combine(binary,"ArkApi","AsaApi.dll")).SequenceEqual(pe),
                "owned fresh stopped API CORE binaries updated with original targeted rollback");
            Check(File.ReadAllText(originalConfig)=="{\"UserSettings\":\"PRESERVED\"}"&&
                  File.ReadAllText(playerDb)=="ORIGINAL PLAYER PLUGIN STATE",
                "verified owned API CORE install never modifies config or player plugin database");
            var transaction=Path.Combine(host.Store.Root,"api-core-transactions",
                receipt.TransactionId);
            Check(File.Exists(Path.Combine(transaction,"complete"))&&
                  File.ReadAllText(Path.Combine(transaction,"previous","AsaApiLoader.exe"))==
                    "SYNTHETIC OLD LOADER",
                "old API binaries protected in durable transaction without copying world or game EXE");
            // A completed transaction supports a separately approved, byte-
            // verified rollback; never roll back operator changes over the top.
            string newApi=Path.Combine(binary,"ArkApi","AsaApi.dll");
            File.WriteAllText(newApi,"AN OPERATOR HAS MODIFIED THE CORE");
            Check(Refused(()=>AsaApiOwnedInstaller.RollbackCompletedOwnedStopped(
                host,owned.Id,receipt.TransactionId,true)),
                "changed installed CORE DLL blocks rollback before touching other files");
            Check(File.ReadAllText(oldLoader)!="SYNTHETIC OLD LOADER" &&
                File.ReadAllText(originalConfig)=="{\\"UserSettings\\":\\"PRESERVED\\"}",
                "refused rollback neither replaces installed loader nor edits config");
            File.WriteAllBytes(newApi,pe);
            Check(Refused(()=>AsaApiOwnedInstaller.RollbackCompletedOwnedStopped(
                host,owned.Id,receipt.TransactionId,false)),
                "rollback requires fresh explicit operator approval");
            var restored=AsaApiOwnedInstaller.RollbackCompletedOwnedStopped(
                host,owned.Id,receipt.TransactionId,true);
            Check(restored.ByteVerified&&!restored.Restarted&&
                restored.FilesRestored==1&&restored.FilesRemoved==3&&
                File.ReadAllText(oldLoader)=="SYNTHETIC OLD LOADER"&&
                !File.Exists(newApi)&&
                !File.Exists(Path.Combine(binary,"libcrypto-3-x64.dll"))&&
                !File.Exists(Path.Combine(binary,"libssl-3-x64.dll")),
                "explicit completed CORE rollback restores exact original loader and removes only new binaries");
            Check(File.Exists(Path.Combine(transaction,"rolled-back"))&&
                File.ReadAllText(originalConfig)=="{\\"UserSettings\\":\\"PRESERVED\\"}"&&
                File.ReadAllText(playerDb)=="ORIGINAL PLAYER PLUGIN STATE",
                "durable rollback receipt and untouched player plugins survive");
            Check(Refused(()=>AsaApiOwnedInstaller.RollbackCompletedOwnedStopped(
                host,owned.Id,receipt.TransactionId,true)),
                "rollback transaction cannot be replayed");
            owned.HistoricalStoreProtected=true;
            Check(Refused(()=>AsaApiOwnedInstaller.ApplyOwnedStopped(
                    host,owned.Id,archive,release,asset,true)),
                "historical Store-v1 map cannot enter owned API CORE mutation path");
        }
        foreach(string mode in new[]{"ok","cancel","race","running","proxy","peer"})await Case(mode);
    }
}