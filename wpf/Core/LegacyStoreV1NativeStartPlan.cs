using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Original ACTIVE/DONE two-host Store-v1 launcher preflight. Unlike the
/// new-map SteamCMD wizard, this uses ONLY the existing original metadata,
/// original map ownership, original cluster identity and original world.
/// Never creates a session, conversion, shared lock, or restore transaction.
/// This is a native plan; never return its UNC path or arguments to WebView.
/// </summary>
internal sealed record LegacyStoreV1NativeStartPlan(string Arguments,
    string Fingerprint,int GamePort,int QueryPort,int RconPort,
    string SourceEvidence,bool UseApi=false,
    string ApiCacheState="UM-API-CACHE-NOT-APPLICABLE",
    bool OriginalTwoPeersVerified=false);

internal static class LegacyStoreV1NativeStartPlanner
{
    private static readonly string LocalOriginal=Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"\u004Bova");

    internal static LegacyStoreV1NativeStartPlan Prepare(ClusterService cluster,
        LocalMap map,Guid? approvedRestartId=null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        // Stopped Legacy imports cannot establish whether the previous process
        // used AsaApiLoader. Fail BEFORE file, RCON, port or launch checks:
        // neither executable can be spawned on an unconfirmed default.
        if(map?.HistoricalStoreProtected==true&&
           map.HistoricalLaunchModeNeedsApproval)
            throw new InvalidDataException("UM-STORE-LAUNCH-MODE-APPROVAL-REQUIRED");
        if(map==null||!cluster.Config.LocalMaps.Contains(map)||
           map.Id==Guid.Empty||!map.HistoricalStoreProtected||
           map.Maintenance||
           !string.IsNullOrWhiteSpace(map.ApiLoaderArguments)||
           (map.StartWithAsaApi&&string.IsNullOrWhiteSpace(map.ApiLoaderPath))||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           !string.IsNullOrWhiteSpace(map.Arguments)||
           string.IsNullOrWhiteSpace(map.HistoricalMapKey)||
           map.HistoricalArchiveSha256?.Length!=64 ||
           map.HistoricalArchiveSha256.Any(c=>!Uri.IsHexDigit(c))||
           map.GamePort is <1024 or >65535||
           map.QueryPort is <1024 or >65535||
           map.RconPort is <1024 or >65535)
            throw new InvalidDataException("UM-STORE-NATIVE-ACTIVE-IMPORT-ONLY");
        if(!AsaReadyProbe.IsRegisteredServerBinary(map)||
           !AsaRconConfigurationEvidence.Matches(map))
            throw new InvalidDataException("UM-STORE-NATIVE-EXE-RCON-PARITY");
        var prior=new AsaManualRestartJournal(map.InstallationPath);
        if(approvedRestartId is Guid restartId)
        {
            var record=prior.Observe();
            if(record==null||record.Id!=restartId||record.MapId!=map.Id||
               record.Phase!=AsaRestartPhase.Starting)
                throw new InvalidOperationException("UM-ORIGINAL-RESTART-START-NOT-AUTHORIZED");
            prior.DemandAuthorizedStart(restartId);
        }
        else prior.DemandNoPending();
        if(LegacyCompatibleWorldBackup.InstallationRecoveryRequired(map))
            throw new InvalidOperationException("UM-STORE-NATIVE-BACKUP-RECOVERY");
        LocalMapPortPolicy.RequireNoConflicts(map,cluster.Config.LocalMaps);
        if(!AsaLocalPortPreflight.CanStart(map))
            throw new InvalidOperationException("UM-STORE-NATIVE-PORT-IN-USE");
        var stopped=LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(
            map,cluster.Config.LocalMaps);
        if(stopped.Running||!stopped.ConfirmedStopped||
           stopped.Identity!=null||stopped.LoaderIdentity!=null)
            throw new InvalidOperationException("UM-STORE-NATIVE-PROCESS-NOT-PROVEN-STOPPED");
        // Once the owner explicitly approved importing the verified source,
        // a Manager-owned immutable copy replaces the *local* Legacy profile
        // dependency. The obsolete Legacy ACTIVE/DONE markers are checked only
        // in the fallback legacy handoff, NOT for a pinned independent start.
        var sourceProfile=LegacyStoreV1PinnedSource.Resolve(
            cluster.Store.Root,LocalOriginal,map);
        if(!string.Equals(sourceProfile,LocalOriginal,
               StringComparison.OrdinalIgnoreCase))
            return PreparePinnedRuntime(cluster,map,sourceProfile);
        // Compatibility path for legacy profiles that have not yet been
        // explicitly imported/pinned. Historical Legacy ACTIVE/DONE applies
        // to this legacy handoff only, not to independent Obsidian startup.
        var original=LegacyStoreV1LiveReadGate.InspectOriginalActive(map,sourceProfile);
        if(!original.CompletedOriginalSourceGuard||!original.BothPeersDone||
           !original.SharedSessionMatches||!original.NoPendingConversion||
           !original.NoMutationMarker||!original.LocalMapBindingMatches||
           !original.HistoricalFilesUnchanged||original.DonePeers!=2||
           original.ActiveMaps!=10||
           string.IsNullOrWhiteSpace(original.VerifiedOriginalSharedPath)||
           original.SourceStateSha256.Length!=64)
            throw new InvalidOperationException("UM-STORE-NATIVE-ORIGINAL-TWO-HOST-NOT-READY");
        // Bound and pin the existing ASA INI bytes before source argument
        // creation. NEVER create, normalize or write the original INI.
        using var held=new VaultReadSnapshot();
        byte[] ini=held.Read(AsaInitialRconSettings.NewIniPath(map.InstallationPath),131072);
        if(!AsaRconConfigurationEvidence.Matches(map))
            throw new InvalidDataException("UM-STORE-NATIVE-RCON-CHANGED");
        // The in-memory Obsidian registration must still match the CURRENT
        // original Legacy launch-profiles-v1.json on this specific host.
        // The four-file Store hash and the launch profile hash are both
        // pinned; no alternative transfer directory is generated.
        var currentSource=LegacyStoreV1OriginalImport.Inspect(sourceProfile);
        var originalMap=LegacyStoreV1SourceLaunchParity.RequireOriginalMap(
            map,currentSource);
        var plan=LegacyStoreV1ActiveLaunch.Build(new(
            originalMap.MapKey,originalMap.GamePort,originalMap.QueryPort,
            originalMap.EnabledMods,originalMap.PassiveMods,
            VaultProtocol.ClusterId,original.VerifiedOriginalSharedPath,
            "active",LegacyStoreV1ActiveLaunch.DecodeOriginalIni(ini)));
        LegacyStoreV1SourceLaunchParity.RequireAsaLaunchArguments(
            map,currentSource,original.VerifiedOriginalSharedPath,
            LegacyStoreV1ActiveLaunch.DecodeOriginalIni(ini),plan.Arguments);
        if(plan.ConversionRequested||plan.StoreMode!="active"||
           plan.Arguments.Count(x=>x.Equals("-usestore",
               StringComparison.OrdinalIgnoreCase))!=1)
            throw new InvalidOperationException("UM-STORE-NATIVE-CONVERSION-FORBIDDEN");
        string args=string.Join(" ",plan.Arguments);
        if(args.Length>8192||args.Contains("-converttostore",
            StringComparison.OrdinalIgnoreCase)||
           args.Contains("-convertfromstore",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-STORE-NATIVE-ARGUMENTS-INVALID");
        // Historical imports keep Arguments and ApiLoaderArguments empty at
        // rest. Only a disposable native clone receives source-derived flags.
        // Explicit opt-in chooses the Loader; finding a Loader.exe on disk
        // never selects it implicitly.
        string launchMode="DIRECT_ASA";
        string cacheState="UM-API-CACHE-NOT-APPLICABLE";
        if(map.StartWithAsaApi)
        {
            var exact=JsonSerializer.Deserialize<LocalMap>(JsonSerializer.Serialize(map))
                ??throw new InvalidDataException("UM-STORE-LOADER-CLONE");
            exact.Arguments=args;
            exact.ApiLoaderArguments=args;
            var launch=AsaApiLaunchPolicy.Resolve(exact);
            if(!launch.UseApi||
               !string.Equals(Path.GetFullPath(launch.ExecutablePath),
                    Path.GetFullPath(map.ApiLoaderPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UM-STORE-LOADER-EXE-IDENTITY");
            // A changed Loader, API DLL or config invalidates a pending
            // approval before spawning. The cache/runtime module is NOT
            // thereby claimed compatible with the current ASA build.
            var inspection=AsaApiInspector.Inspect(map.InstallationPath);
            if(!inspection.Complete||inspection.Binaries.Any(x=>x.Sha256==null))
                throw new InvalidDataException("UM-STORE-LOADER-BINARIES-UNVERIFIED");
            string config=Path.Combine(map.InstallationPath,"ShooterGame",
                "Binaries","Win64","config.json");
            using var configRead=File.OpenRead(config);
            var cfgHash=Convert.ToHexString(SHA256.HashData(configRead));
            var localCache=AsaApiCacheProbe.Inspect(map.InstallationPath,map.ExecutablePath);
            cacheState=localCache.State;
            // Missing cache is not a reason to substitute another ASA build's
            // archive. Official AsaApi performs SHA-bound acquisition itself;
            // Process.Start never means that acquisition has completed.
            launchMode="ASA_API:"+localCache.ExecutableSha256+":"+
                string.Join(":",inspection.Binaries.Select(x=>x.Sha256))+
                ":"+cfgHash+":"+cacheState;
        }
        string originalIniSha=Convert.ToHexString(SHA256.HashData(ini));
        // Preserve source Game.ini transfer/upload/download rules exactly.
        // Hash the existing file's bytes along with GUS, without rewriting
        // either config or inventing another ASA transfer mechanism.
        string gameIniPath=Path.Combine(map.InstallationPath,"ShooterGame",
            "Saved","Config","WindowsServer","Game.ini");
        string gameIniSha="ABSENT";
        if(File.Exists(gameIniPath))
        {
            byte[] gameIni=held.Read(gameIniPath,4_000_000);
            gameIniSha=Convert.ToHexString(SHA256.HashData(gameIni));
        }
        string directBinarySha=AsaReadyProbe.ApprovedLaunchBinariesFingerprint(map);
        string evidence=string.Join("\n",map.Id.ToString("D"),
            Path.GetFullPath(map.InstallationPath),Path.GetFullPath(map.ExecutablePath),
            directBinarySha,map.HistoricalArchiveSha256,currentSource.LaunchProfileSha256,
            original.SourceStateSha256,originalIniSha,gameIniSha,
            args,launchMode,map.RconPort.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        string fingerprint=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(evidence)));
        return new(args,fingerprint,map.GamePort,map.QueryPort,map.RconPort,
            map.StartWithAsaApi?
                "ORIGINAL_STORE_V1_API_LOADER_NATIVE_PREFLIGHT_NOT_RUNTIME_CERTIFIED":
                "ORIGINAL_TWO_PEER_ACTIVE_DONE_AND_EXACT_ASA_RCON_NATIVE_START_PREPARED",
            map.StartWithAsaApi,cacheState,OriginalTwoPeersVerified:true);
    }

    // Manager-owned, original-source-pinned Store-v1 startup. This does NOT
    // probe Legacy runtime, old ACTIVE/DONE peer markers or remote host uptime.
    // Transfer readiness is separately verified by travel/cluster operations,
    // not a blanket prerequisite for a local, explicitly approved Start.
    private static LegacyStoreV1NativeStartPlan PreparePinnedRuntime(
        ClusterService cluster,LocalMap map,string pinnedProfile)
    {
        var source=LegacyStoreV1OriginalImport.Inspect(pinnedProfile);
        LegacyStoreV1PinnedSource.RequireImmutableIdentity(map,source);
        var original=LegacyStoreV1PinnedSource.ReadOriginalCluster(pinnedProfile);
        string clusterId=string.IsNullOrWhiteSpace(map.HistoricalRuntimeClusterId)?
            original.RuntimeClusterId:map.HistoricalRuntimeClusterId;
        string transfer=string.IsNullOrWhiteSpace(map.HistoricalRuntimeTransferDirectory)?
            original.TransferRoot:map.HistoricalRuntimeTransferDirectory;
        if(string.IsNullOrWhiteSpace(clusterId)||string.IsNullOrWhiteSpace(transfer)||
           (string.IsNullOrWhiteSpace(map.HistoricalRuntimeClusterId)!=
            string.IsNullOrWhiteSpace(map.HistoricalRuntimeTransferDirectory)))
            throw new InvalidDataException("UM-STORE-RUNTIME-CLUSTER-INCOMPLETE");

        var currentMods=ModStateController.Read(map);
        using var held=new VaultReadSnapshot();
        byte[] ini=held.Read(AsaInitialRconSettings.NewIniPath(map.InstallationPath),131072);
        if(!AsaRconConfigurationEvidence.Matches(map))
            throw new InvalidDataException("UM-STORE-RUNTIME-RCON-INI-MISMATCH");
        string iniText=LegacyStoreV1ActiveLaunch.DecodeOriginalIni(ini);
        var built=LegacyStoreV1ActiveLaunch.Build(new(
            map.HistoricalMapKey,map.GamePort,map.QueryPort,
            currentMods.EnabledOrder.ToArray(),currentMods.Passive.ToArray(),
            clusterId,transfer,"active",iniText,SourcePinnedClusterOnly:false));
        if(built.ConversionRequested||built.StoreMode!="active"||
           built.Arguments.Count(a=>a.Equals("-usestore",
               StringComparison.OrdinalIgnoreCase))!=1)
            throw new InvalidDataException("UM-STORE-RUNTIME-CONVERSION-DENIED");
        string args=string.Join(" ",built.Arguments);
        if(args.Length>8192)
            throw new InvalidDataException("UM-STORE-RUNTIME-ARGUMENTS-TOO-LONG");

        string launchMode="DIRECT_ASA";
        string cacheState="UM-API-CACHE-NOT-APPLICABLE";
        if(map.StartWithAsaApi)
        {
            var exact=JsonSerializer.Deserialize<LocalMap>(JsonSerializer.Serialize(map))
                ??throw new InvalidDataException("UM-STORE-LOADER-CLONE");
            exact.Arguments=args;
            exact.ApiLoaderArguments=args;
            var launch=AsaApiLaunchPolicy.Resolve(exact);
            if(!launch.UseApi||
               !string.Equals(Path.GetFullPath(launch.ExecutablePath),
                   Path.GetFullPath(map.ApiLoaderPath),
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UM-STORE-LOADER-EXE-IDENTITY");
            var inspection=AsaApiInspector.Inspect(map.InstallationPath);
            if(!inspection.Complete||inspection.Binaries.Any(x=>x.Sha256==null))
                throw new InvalidDataException("UM-STORE-LOADER-BINARIES-UNVERIFIED");
            using var configRead=File.OpenRead(Path.Combine(map.InstallationPath,
                "ShooterGame","Binaries","Win64","config.json"));
            string cfgHash=Convert.ToHexString(SHA256.HashData(configRead));
            var localCache=AsaApiCacheProbe.Inspect(map.InstallationPath,map.ExecutablePath);
            cacheState=localCache.State;
            launchMode="ASA_API:"+localCache.ExecutableSha256+":"+
                string.Join(":",inspection.Binaries.Select(x=>x.Sha256))+
                ":"+cfgHash+":"+cacheState;
        }
        string gameIniPath=Path.Combine(map.InstallationPath,
            "ShooterGame","Saved","Config","WindowsServer","Game.ini");
        string gameIniSha="ABSENT";
        if(File.Exists(gameIniPath))
            gameIniSha=Convert.ToHexString(SHA256.HashData(
                held.Read(gameIniPath,4_000_000)));
        string directBinarySha=AsaReadyProbe.ApprovedLaunchBinariesFingerprint(map);
        string evidence=string.Join("\n",map.Id.ToString("D"),
            Path.GetFullPath(map.InstallationPath),
            Path.GetFullPath(map.ExecutablePath),
            directBinarySha,source.FourFilesSha256,source.LaunchProfileSha256,
            Convert.ToHexString(SHA256.HashData(ini)),gameIniSha,
            args,launchMode,map.RconPort.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        string fingerprint=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(evidence)));
        return new(args,fingerprint,map.GamePort,map.QueryPort,map.RconPort,
            map.StartWithAsaApi?
                "OBSIDIAN_PINNED_API_LOADER_PREFLIGHT_NOT_RUNTIME_CERTIFIED":
                "OBSIDIAN_LOCAL_STORE_V1_START_SOURCE_PINNED_PEER_NOT_REQUIRED",
            map.StartWithAsaApi,cacheState);
    }
}
