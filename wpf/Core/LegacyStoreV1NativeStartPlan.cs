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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Kova");

    internal static LegacyStoreV1NativeStartPlan Prepare(ClusterService cluster,
        LocalMap map,Guid? approvedRestartId=null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        // Stopped Kova imports cannot establish whether the previous process
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
        // The last SaveWorld-only RCON attempt can be uncertain even if
        // ASA has subsequently stopped. Do not launch across its durable
        // prepared/needsOperator receipt; the owner must review it first.
        var pendingSave=LegacyStoreV1SaveEvidenceJournal.Inspect(
            cluster.Store.Root,map.Id);
        if(pendingSave?.Phase is "prepared" or "needsOperator")
            throw new InvalidOperationException(
                "UM-ORIGINAL-SAVE-EVIDENCE-NEEDS-REVIEW");
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
        if(KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
            throw new InvalidOperationException("UM-STORE-NATIVE-BACKUP-RECOVERY");
        LocalMapPortPolicy.RequireNoConflicts(map,cluster.Config.LocalMaps);
        if(!AsaLocalPortPreflight.CanStart(map))
            throw new InvalidOperationException("UM-STORE-NATIVE-PORT-IN-USE");
        var stopped=LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(
            map,cluster.Config.LocalMaps);
        if(stopped.Running||!stopped.ConfirmedStopped||
           stopped.Identity!=null||stopped.LoaderIdentity!=null)
            throw new InvalidOperationException("UM-STORE-NATIVE-PROCESS-NOT-PROVEN-STOPPED");
        // The existing registered local source is sufficient for native ASA
        // launch. Peer migration status is not part of -usestore semantics.
        var source=LegacyStoreV1PinnedSource.ResolveRuntimeIdentity(
            cluster.Store.Root,LocalOriginal,map);
        return PreparePinnedRuntime(cluster,map,source);
    }

    // Registered local-source native ASA startup. This does NOT
    // probe Kova runtime, old ACTIVE/DONE peer markers or remote host uptime.
    // Transfer readiness is separately verified by travel/cluster operations,
    // not a blanket prerequisite for a local, explicitly approved Start.
    private static LegacyStoreV1NativeStartPlan PreparePinnedRuntime(
        ClusterService cluster,LocalMap map,NativeStoreIdentity source)
    {
        var original=source;
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
