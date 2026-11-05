using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

public static class LocalAsaWizardTests
{
    private static void Assert(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException("ASA WIZARD: "+reason);
        Console.WriteLine("PASS wizard "+reason);
    }

    public static void Run(string tempRoot)
    {
        var root=Path.Combine(tempRoot,"asa-wizard-probe");
        var binaries=Path.Combine(root,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binaries);
        string marker=Path.Combine(binaries,"ArkAscendedServer.exe");
        string loader=Path.Combine(binaries,"AsaApiLoader.exe");

        var notFound=LocalAsaInstallLocator.Probe(root);
        Assert(notFound.Root==Path.GetFullPath(root) &&
            !notFound.HasServerExecutable && !notFound.HasApiLoader,
            "empty local installation reports missing EXE without creating one");
        File.WriteAllText(marker,"FAKE FILE - NEVER EXECUTE");
        var found=LocalAsaInstallLocator.Probe(root);
        Assert(found.HasServerExecutable && !found.HasApiLoader &&
            found.ServerExecutable==marker,
            "detects only the exact chosen installation's ArkAscendedServer executable");
        File.WriteAllText(loader,"FAKE API FILE - NEVER EXECUTE");
        var withLoader=LocalAsaInstallLocator.Probe(root);
        Assert(withLoader.HasServerExecutable && withLoader.HasApiLoader &&
            withLoader.ApiLoaderExecutable==loader,
            "detects optional API loader without enabling loader launch");
        Assert(File.ReadAllText(marker)=="FAKE FILE - NEVER EXECUTE" &&
            File.ReadAllText(loader)=="FAKE API FILE - NEVER EXECUTE",
            "probing never modifies local install files");

        bool badRoot=false,missingRoot=false;
        try{LocalAsaInstallLocator.Probe(" ");}
        catch(InvalidDataException){badRoot=true;}
        try{LocalAsaInstallLocator.Probe(Path.Combine(tempRoot,"unknown-asa-root"));}
        catch(DirectoryNotFoundException){missingRoot=true;}
        Assert(badRoot && missingRoot,"blank and missing root are refused without scanning other disks");

        var owned=new LocalMap {Name="Island",GamePort=7777,QueryPort=27015,RconPort=37015};
        var another=new LocalMap {Name="Ragnarok",GamePort=7817,QueryPort=27055,RconPort=37055};
        var maps=new[]{owned,another};
        var candidate=new LocalMap {Name="Center",GamePort=7907,QueryPort=27235,RconPort=37125};
        Assert(LocalMapPortPolicy.Conflicts(candidate,maps).Count==0,
            "unique local UDP and RCON TCP ports are accepted");

        candidate.GamePort=27015;
        Assert(LocalMapPortPolicy.Conflicts(candidate,maps).Any(x=>x.Contains("Island")),
            "Game UDP collides with another map's Query UDP port");
        candidate.GamePort=7907;
        candidate.QueryPort=7777;
        Assert(LocalMapPortPolicy.Conflicts(candidate,maps).Any(x=>x.Contains("Island")),
            "Query UDP collides with another map's Game UDP port");
        candidate.QueryPort=27235;
        candidate.RconPort=37055;
        Assert(LocalMapPortPolicy.Conflicts(candidate,maps).Any(x=>x.Contains("Ragnarok")),
            "RCON TCP conflicts with other RCON TCP port");
        candidate.RconPort=7777;
        Assert(LocalMapPortPolicy.Conflicts(candidate,maps).Count==0,
            "TCP RCON may reuse a UDP game number since the protocols are different");

        candidate.QueryPort=7907;
        Assert(LocalMapPortPolicy.Conflicts(candidate,maps).Any(x=>x.Contains("Game")),
            "same-map Game UDP and Query UDP cannot bind one socket port");
        candidate.QueryPort=27235;
        candidate.GamePort=7817;
        bool rejected=false;
        try{LocalMapPortPolicy.RequireNoConflicts(candidate,maps);}
        catch(InvalidDataException){rejected=true;}
        Assert(rejected,"conflicting local map cannot be staged by the wizard");
        candidate.GamePort=7907;
        candidate.Id=owned.Id;
        candidate.QueryPort=27015;
        candidate.RconPort=37015;
        Assert(LocalMapPortPolicy.Conflicts(candidate,maps).Count==0,
            "editing a registered map excludes only its own ID from collision checks");
        Assert(LocalMapPortPolicy.Conflicts(new LocalMap {
            Name="Legacy profile",GamePort=0,QueryPort=0,RconPort=0
        },maps).Count==0,
            "legacy profiles with unconfigured ports remain inspectable");
    }
}
