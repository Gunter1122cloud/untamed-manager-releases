using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

/// <summary>
/// Isolated Windows fixture exercising actual native per-map mode persistence.
/// Builds synthetic PE64 headers in TEMP only; it never launches fake
/// executables, touches installed ASA, RCON, Legacy or network shares.
/// </summary>
internal static class LocalAsaApiModeTests
{
    private static void Check(bool yes,string description)
    {
        if(!yes)throw new InvalidOperationException(
            "LOCAL ASA API MODE REGRESSION FAILED: "+description);
        Console.WriteLine("PASS local per-map ASA API: "+description);
    }

    private static async Task<JsonElement> Call(
        DesktopLocalAsaApiModeBridge bridge,string method,object? args=null)
    {
        var dict=new System.Collections.Generic.Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method};
        if(args!=null)
        {
            var fields=JsonSerializer.SerializeToElement(args);
            foreach(var item in fields.EnumerateObject())
                dict[item.Name]=item.Value.Clone();
        }
        using var result=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(dict)));
        return result.RootElement.Clone();
    }

    private static void FakePe64(string path)
    {
        var data=new byte[512];
        data[0]=(byte)'M';data[1]=(byte)'Z';
        BitConverter.GetBytes(128).CopyTo(data,0x3c);
        BitConverter.GetBytes(0x4550u).CopyTo(data,0x80);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(data,0x84);
        BitConverter.GetBytes((ushort)0x20b).CopyTo(data,0x98);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path,data);
    }

    public static async Task Run(string testsRoot)
    {
        string root=Path.Combine(testsRoot,"local-api-mode-only");
        string folder=Path.Combine(root,"ShooterGame","Binaries","Win64");
        string exe=Path.Combine(folder,"ArkAscendedServer.exe");
        string loader=Path.Combine(folder,"AsaApiLoader.exe");
        string apiDll=Path.Combine(folder,"ArkApi","AsaApi.dll");
        FakePe64(exe);FakePe64(loader);FakePe64(apiDll);
        File.WriteAllText(Path.Combine(folder,"config.json"),"{}");
        Directory.CreateDirectory(Path.Combine(folder,"ArkApi","Plugins"));
        string ini=Path.Combine(root,"ShooterGame","Saved","Config",
            "WindowsServer","GameUserSettings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        const string protectedContent="[ServerSettings]\r\nRCONPort=37145\r\n";
        File.WriteAllText(ini,protectedContent);
        await using var cluster=new ClusterService("local-api-mode-test",
            Path.Combine(testsRoot,"local-api-mode-profile"),
            _=>new ProcessCheck(false,"Synthetic confirmed stopped",null,true));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="The Center",InstallationPath=root,
            ExecutablePath=exe,Arguments="TheCenter_WP?listen -log",
            GamePort=7807,QueryPort=27055
        };
        cluster.AddMap(map);
        using var handler=new DesktopLocalAsaApiModeBridge(cluster);
        var status=await Call(handler,"servers.apiModeStatus",new{mapId=map.Id});
        Check(status.GetProperty("ok").GetBoolean()&&
              status.GetProperty("data").GetProperty("canChangeMode").GetBoolean()&&
              !status.GetProperty("data").GetProperty("useApi").GetBoolean(),
            "ordinary stopped local map has real native editable launch preference");
        var preview=await Call(handler,"servers.apiModePreview",
            new{mapId=map.Id,useApi=true});
        Check(preview.GetProperty("ok").GetBoolean()&&
              preview.GetProperty("data").GetProperty("useApi").GetBoolean()&&
              !preview.GetProperty("data").GetProperty("startsServer").GetBoolean(),
            "API checkbox gets native approved preview without starting ASA");
        Check(!cluster.Config.LocalMaps.Single(m=>m.Id==map.Id).StartWithAsaApi,
            "unapproved checkbox does not modify persisted map setting");
        var token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var applied=await Call(handler,"servers.apiModeApply",new{token,confirm=true});
        Check(applied.GetProperty("ok").GetBoolean()&&
              applied.GetProperty("data").GetProperty("useApi").GetBoolean()&&
              cluster.Config.LocalMaps.Single(m=>m.Id==map.Id).StartWithAsaApi,
            "one-shot approval persists API Loader launch choice in local Manager");
        var registered=cluster.Config.LocalMaps.Single(m=>m.Id==map.Id);
        var plan=AsaApiLaunchPolicy.Resolve(registered);
        Check(plan.UseApi&&Path.GetFullPath(plan.ExecutablePath)==
              Path.GetFullPath(loader)&&
              registered.ApiLoaderArguments==registered.Arguments,
            "actual next start resolves exact same-install loader and same arguments");
        var replay=await Call(handler,"servers.apiModeApply",new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "approved mode token cannot be replayed");
        Check(File.ReadAllText(ini)==protectedContent,
            "native API checkbox never edits original GameUserSettings.ini");
        var off=await Call(handler,"servers.apiModePreview",
            new{mapId=map.Id,useApi=false});
        Check(off.GetProperty("ok").GetBoolean(),
            "same map supports opting out of API Loader");
        var direct=await Call(handler,"servers.apiModeApply",
            new{token=off.GetProperty("data").GetProperty("token").GetString(),confirm=true});
        Check(direct.GetProperty("ok").GetBoolean()&&
              !cluster.Config.LocalMaps.Single(m=>m.Id==map.Id).StartWithAsaApi,
            "opting out persists direct ASA mode without starting or stopping server");

        var observed=new LocalMap{
            Id=Guid.NewGuid(),Name="Observation only",
            InstallationPath=root,ExecutablePath=exe,
            Arguments="TheCenter_WP?listen -log",
            ObservedReadOnlyImported=true,GamePort=7857,QueryPort=27105
        };
        cluster.AddMap(observed);
        var blocked=await Call(handler,"servers.apiModeStatus",new{mapId=observed.Id});
        Check(!blocked.GetProperty("ok").GetBoolean(),
            "WMI observation-only map cannot silently change lifecycle ownership");
        blocked=await Call(handler,"servers.apiModePreview",
            new{mapId=observed.Id,useApi=true});
        Check(!blocked.GetProperty("ok").GetBoolean(),
            "observed-only map cannot create API launch approval");

        // A second map using the SAME installation and a synthetic running
        // process prevents a mode switch. It never spawns a real ASA child.
        await using var busyCluster=new ClusterService("local-api-busy-test",
            Path.Combine(testsRoot,"local-api-busy-profile"),
            m=>new ProcessCheck(true,"Synthetic running map",
                new ProcessIdentity{Pid=33001,
                    StartedUtc=DateTimeOffset.UtcNow,
                    ExecutablePath=exe,Arguments="synthetic"},false));
        var busy=new LocalMap{
            Id=Guid.NewGuid(),Name="Busy map",InstallationPath=root,
            ExecutablePath=exe,Arguments="TheCenter_WP?listen -log",
            GamePort=7877,QueryPort=27125
        };
        busyCluster.AddMap(busy);
        using var busyBridge=new DesktopLocalAsaApiModeBridge(busyCluster);
        var denied=await Call(busyBridge,"servers.apiModeStatus",new{mapId=busy.Id});
        Check(denied.GetProperty("ok").GetBoolean()&&
              !denied.GetProperty("data").GetProperty("canChangeMode").GetBoolean(),
            "running ASA blocks checkbox even if API binaries exist");
        denied=await Call(busyBridge,"servers.apiModePreview",
            new{mapId=busy.Id,useApi=true});
        Check(!denied.GetProperty("ok").GetBoolean(),
            "native API preview cannot bypass running ASA/Loader process gate");
    }
}
