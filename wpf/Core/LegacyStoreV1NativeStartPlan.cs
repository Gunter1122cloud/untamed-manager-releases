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
    string ApiCacheState="UM-API-CACHE-NOT-APPLICABLE");

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
        var original=LegacyStoreV1LiveReadGate.InspectOriginalActive(map,LocalOriginal);
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
        var plan=LegacyStoreV1ActiveLaunch.Build(new(
            map.HistoricalMapKey,map.GamePort,map.QueryPort,
            map.HistoricalEnabledMods,map.HistoricalPassiveMods,
            VaultProtocol.ClusterId,original.VerifiedOriginalSharedPath,
            "active",Encoding.UTF8.GetString(ini)));
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
        string evidence=string.Join("\n",map.Id.ToString("D"),
            Path.GetFullPath(map.InstallationPath),Path.GetFullPath(map.ExecutablePath),
            map.HistoricalArchiveSha256,original.SourceStateSha256,
            originalIniSha,args,launchMode,map.RconPort.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        string fingerprint=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(evidence)));
        return new(args,fingerprint,map.GamePort,map.QueryPort,map.RconPort,
            map.StartWithAsaApi?
                "ORIGINAL_STORE_V1_API_LOADER_NATIVE_PREFLIGHT_NOT_RUNTIME_CERTIFIED":
                "ORIGINAL_TWO_PEER_ACTIVE_DONE_AND_EXACT_ASA_RCON_NATIVE_START_PREPARED",
            map.StartWithAsaApi,cacheState);
    }
}
