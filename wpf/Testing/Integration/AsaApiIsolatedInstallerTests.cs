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
    {try{action();return false;}catch(Exception e) when(e is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException){return true;}}
    public static async Task Run()
    {
        byte[] pe=new byte[256];using(var ms=new MemoryStream(pe))using(var w=new BinaryWriter(ms))
        {w.Write((ushort)0x5A4D);ms.Position=0x3c;w.Write(0x40);ms.Position=0x40;w.Write(0x4550);w.Write((ushort)0x8664);ms.Position=0x58;w.Write((ushort)0x20b);}
        using var bytes=new MemoryStream();
        using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))
            foreach(string name in new[]{"AsaApiLoader.exe","ArkApi/AsaApi.dll","config.json","ArkApi/Plugins/Protected/data.db"})
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
                    File.ReadAllBytes(Path.Combine(bin,"ArkApi","AsaApi.dll")).SequenceEqual(pe),
                    "actual PE64 file replacement with durable receipt, without claiming runtime load");
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
                File.ReadAllText(Path.Combine(plugin,"license.key"))=="protected license",
                "configuration, plugin database and license remain byte-identical in "+mode);
        }
        foreach(string mode in new[]{"ok","cancel","race","running","proxy","peer"})await Case(mode);
    }
}
