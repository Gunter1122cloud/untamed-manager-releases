using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using UntamedManager.Core;

public static class AsaApiLaunchPolicyTests
{
    private static void Check(bool ok,string label)
    {
        if(!ok)throw new Exception("API launch mode regression: "+label);
        Console.WriteLine("PASS api-launch "+label);
    }
    private static bool Refused(Action action)
    {
        try{action();return false;}
        catch(Exception e) when(e is InvalidDataException or InvalidOperationException
            or IOException or UnauthorizedAccessException or ArgumentException){return true;}
    }
    public static void Run(string root,string fake)
    {
        var lab=Path.Combine(root,"api-launch-policy-fixture");
        var bin=Path.Combine(lab,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(Path.Combine(bin,"ArkApi","Plugins"));
        byte[] pe=new byte[256];
        using(var ms=new MemoryStream(pe))
        using(var w=new BinaryWriter(ms))
        {
            w.Write((ushort)0x5A4D);ms.Position=0x3c;w.Write(0x40);
            ms.Position=0x40;w.Write(0x4550);w.Write((ushort)0x8664);
            ms.Position=0x58;w.Write((ushort)0x20b);
        }
        string loader=Path.Combine(bin,"AsaApiLoader.exe");
        string server=Path.Combine(bin,"ArkAscendedServer.exe");
        File.WriteAllBytes(loader,pe);
        File.WriteAllBytes(Path.Combine(bin,"ArkApi","AsaApi.dll"),pe);
        File.WriteAllBytes(server,pe);
        File.WriteAllText(Path.Combine(bin,"config.json"),"{}");
        const string arguments="TheIsland_WP?listen?SessionName=\"Test Map\"?Port=7777 -log -mods=912815,928548";
        var map=new LocalMap{Name="Offline API fixture",InstallationPath=lab,
            ExecutablePath=server,Arguments=arguments,
            ApiLoaderPath=loader,ApiLoaderArguments=arguments};
        var direct=AsaApiLaunchPolicy.Resolve(map);
        Check(!direct.UseApi&&direct.ExecutablePath==server&&direct.Arguments==arguments,
            "loader path alone never enables API launch");
        map.StartWithAsaApi=true;
        var selected=AsaApiLaunchPolicy.Resolve(map);
        Check(selected.UseApi&&selected.ExecutablePath==loader&&selected.Arguments==arguments,
            "explicit API mode selects official loader with byte-for-byte original parameters");
        var copy=JsonSerializer.Deserialize<LocalMap>(JsonSerializer.Serialize(map))!;
        Check(copy.StartWithAsaApi&&copy.ApiLoaderArguments==arguments,
            "per-map API opt-in round-trips through persistent configuration JSON");
        map.ApiLoaderArguments="-log";
        Check(Refused(()=>AsaApiLaunchPolicy.Resolve(map)),
            "loader arguments cannot silently discard the map launch parameters");
        map.ApiLoaderArguments=arguments;
        map.ApiLoaderPath=Path.Combine(lab,"different-loader.exe");
        Check(Refused(()=>AsaApiLaunchPolicy.Resolve(map)),
            "foreign loader location rejected before process start");
        map.ApiLoaderPath=loader;
        File.Delete(Path.Combine(bin,"config.json"));
        Check(Refused(()=>AsaApiLaunchPolicy.Resolve(map)),
            "API opt-in blocked if installation inspection is incomplete");
        map.StartWithAsaApi=false;
        var safe=AsaApiLaunchPolicy.Resolve(map);
        Check(!safe.UseApi&&safe.ExecutablePath==server,
            "direct selection does not depend on missing API config");
        var fakeMap=new LocalMap{Name="Synthetic fake",ExecutablePath=fake,
            InstallationPath=Path.GetDirectoryName(fake)!,StartWithAsaApi=true};
        Check(Refused(()=>AsaApiLaunchPolicy.Resolve(fakeMap)),
            "API mode cannot masquerade as a FakeAsa lab executable");
    }
}
