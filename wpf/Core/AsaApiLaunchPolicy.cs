using System;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

public sealed record AsaApiLaunchPlan(bool UseApi,string ExecutablePath,
    string Arguments,string Evidence)
{
    internal string BinaryFingerprint {get;init;}="";
}

// Immutable launch selection. The persisted opt-in, not a nonempty legacy
// loader path, selects the executable. Production launches remain denied by
// ReleaseCapabilityGate until real ASA validation is available.
public static class AsaApiLaunchPolicy
{
    public static AsaApiLaunchPlan Resolve(LocalMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if(string.IsNullOrWhiteSpace(map.ExecutablePath) ||
           !Path.IsPathFullyQualified(map.ExecutablePath))
            throw new InvalidDataException("A helyi szerver EXE elérési útja hiányzik");
        string server=Path.GetFullPath(map.ExecutablePath);
        if(!map.StartWithAsaApi)
            return new(false,server,map.Arguments,
                "Direct server mode selected; legacy loader fields are not launch authorization");

        if(string.IsNullOrWhiteSpace(map.InstallationPath) ||
           !Path.IsPathFullyQualified(map.InstallationPath) ||
           string.IsNullOrWhiteSpace(map.ApiLoaderPath) ||
           !Path.IsPathFullyQualified(map.ApiLoaderPath))
            throw new InvalidDataException("Az ASA API-hoz teljes helyi telepítési és loaderútvonal szükséges");
        string root=Path.GetFullPath(map.InstallationPath);
        string binary=Path.Combine(root,"ShooterGame","Binaries","Win64");
        string expectedServer=Path.Combine(binary,"ArkAscendedServer.exe");
        string expectedLoader=Path.Combine(binary,"AsaApiLoader.exe");
        if(!string.Equals(server,expectedServer,StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(Path.GetFullPath(map.ApiLoaderPath),expectedLoader,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Az ASA API loader és a szerver EXE ugyanabban a helyi Win64 mappában legyen");
        if(!string.Equals(map.ApiLoaderArguments,map.Arguments,StringComparison.Ordinal))
            throw new InvalidDataException("ASA API módban a loader argumentumai pontosan egyezzenek a szerverével");
        if(string.IsNullOrWhiteSpace(map.Arguments))
            throw new InvalidDataException("Az ASA API indításához map/port indítási argumentumok szükségesek");
        if(!File.Exists(server))
            throw new FileNotFoundException("Az ASA szerver EXE hiányzik",server);
        // Port of Cerious AASM resolveServerLaunch (MIT, Jared Kirchgatter,
        // ark-server-paths.utils.ts:38-51): select the local executable,
        // not plugin/config diagnostic status. Retain Obsidian's explicit
        // per-map opt-in and existing PE64 binary integrity check.
        var binaries=AsaApiInspector.InspectLaunchBinaries(root);
        if(binaries.Any(x=>!x.Present||!x.ValidPe64||x.Sha256==null))
            throw new InvalidOperationException("Az ASA API Loader vagy Core DLL hiányzik, illetve érvénytelen");
        return new(true,expectedLoader,map.Arguments,
            "Verified local loader and core binaries; API initializes its own configuration and plugins")
            {BinaryFingerprint=string.Join(":",binaries.Select(x=>x.Sha256))};
    }
}
