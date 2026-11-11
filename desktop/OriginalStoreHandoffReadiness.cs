using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Pure read-only evidence classification. This must NEVER authorize a
/// historical Store writer, SteamCMD update, map launch, or manager handoff.
/// Production supplies the independently verified Kova source and WMI facts;
/// synthetic tests may call Evaluate without touching any files or processes.
/// </summary>
public static class OriginalStoreHandoffReadiness
{
    public const string RequiredClusterId = "KovaCluster";
    public const string RequiredTransferRoot = @"\\192.168.1.200\UKClusterRoot";

    public sealed record MapEvidence(
        Guid MapId, string MapName, string OriginalMapKey,
        bool ExactLocalRegistration, bool HostRegistryRegistration,
        bool ClusterAssignment, bool ProcessIdentityVerified,
        bool OriginalPortModRootMatches);

    public sealed record Report(
        bool ReadOnly, int OriginalSourceMapCount, bool ExactlyFiveOriginal,
        int ExactLocalRegistrations, int HostRegistryRegistrations,
        int ClusterAssignments, bool ClusterIdMatches,
        bool ConfiguredTransferRootMatches, bool VerifiedSourceRootMatches,
        bool NativeFiveProcessProof, int DirectAsaProcesses,
        int VerifiedLoaderAsaPairs, bool ClusterConfigurationCompliant,
        bool ExclusiveControllerHandoffVerified, bool KovaWatchdogDisabledVerified,
        bool ScheduledTasksReviewed, bool EmbeddedCharacterIntegrityVerified,
        bool GameClientRejoinVerified, bool RoundTripGameplayVerified,
        bool OriginalStoreSteamUpdateAuthorized,
        string[] Blockers, MapEvidence[] Maps, string Evidence);

    private static bool SameRoot(string? a, string? b)
    {
        if(string.IsNullOrWhiteSpace(a)||string.IsNullOrWhiteSpace(b))return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception ex) when(ex is ArgumentException or NotSupportedException
            or PathTooLongException)
        {return false;}
    }

    public static Report Evaluate(
        LegacyStoreV1OriginalLocalSelection source,
        IReadOnlyList<LocalMap> localMaps,
        ClusterRegistrySnapshot registry,
        bool nativeFiveProcessProof=false,
        string? verifiedOriginalTransferRoot=null,
        int directAsaProcesses=0,
        int verifiedLoaderAsaPairs=0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(localMaps);
        ArgumentNullException.ThrowIfNull(registry);
        var original=source.Maps??Array.Empty<LegacyStoreV1OriginalMap>();
        bool exactlyFive=original.Length==5&&
            original.All(m=>m.MapId!=Guid.Empty)&&
            original.Select(m=>m.MapId).Distinct().Count()==5;
        string localHost=registry.HostRegistry.LocalHostId;
        var configured=registry.ClusterRegistry.Clusters.SingleOrDefault(
            c=>string.Equals(c.ClusterId,RequiredClusterId,StringComparison.Ordinal));
        bool clusterIdMatches=configured!=null;
        bool configuredPathMatches=configured!=null&&string.Equals(
            configured.TransferDirectory,RequiredTransferRoot,StringComparison.Ordinal);
        // The source path is accepted only after the native two-peer/physical
        // verification. Registry text alone cannot prove the UNC Store identity.
        bool sourcePathMatches=nativeFiveProcessProof&&string.Equals(
            verifiedOriginalTransferRoot,RequiredTransferRoot,StringComparison.Ordinal);
        var rows=original.Select(sourceMap=>{
            var matches=localMaps.Where(m=>m.Id==sourceMap.MapId).ToArray();
            var local=matches.Length==1?matches[0]:null;
            bool parity=local!=null&&local.HistoricalStoreProtected&&
                string.Equals(local.HistoricalMapKey,sourceMap.MapKey,StringComparison.Ordinal)&&
                string.Equals(local.HistoricalArchiveSha256,source.FourFilesSha256,
                    StringComparison.Ordinal)&&
                SameRoot(local.InstallationPath,sourceMap.OriginalInstallationRoot)&&
                local.GamePort==sourceMap.GamePort&&
                local.QueryPort==sourceMap.QueryPort&&
                local.HistoricalEnabledMods.SequenceEqual(sourceMap.EnabledMods,
                    StringComparer.Ordinal)&&
                local.HistoricalPassiveMods.SequenceEqual(sourceMap.PassiveMods,
                    StringComparer.Ordinal);
            bool hostRegistered=parity&&registry.HostRegistry.Maps.Count(m=>
                m.MapId==sourceMap.MapId&&
                string.Equals(m.HostId,localHost,StringComparison.OrdinalIgnoreCase)&&
                m.LocalConfigurationPresent)==1;
            bool assigned=hostRegistered&&
                registry.ClusterRegistry.MapAssignments.Count(a=>
                    a.MapId==sourceMap.MapId&&string.Equals(
                        a.ClusterId,RequiredClusterId,StringComparison.Ordinal))==1;
            return new MapEvidence(sourceMap.MapId,
                local?.Name??sourceMap.MapKey,sourceMap.MapKey,
                parity,hostRegistered,assigned,nativeFiveProcessProof&&parity,parity);
        }).ToArray();
        int registered=rows.Count(m=>m.ExactLocalRegistration);
        int inHost=rows.Count(m=>m.HostRegistryRegistration);
        int assignedCount=rows.Count(m=>m.ClusterAssignment);
        bool nativeValid=nativeFiveProcessProof&&exactlyFive&&
            directAsaProcesses>=0&&verifiedLoaderAsaPairs>=0&&
            directAsaProcesses+verifiedLoaderAsaPairs==5;
        bool configCompliant=exactlyFive&&registered==5&&inHost==5&&
            assignedCount==5&&clusterIdMatches&&configuredPathMatches&&
            sourcePathMatches&&nativeValid;
        var blockers=new List<string>();
        if(!exactlyFive)blockers.Add("ORIGINAL_SOURCE_NOT_EXACTLY_FIVE");
        if(registered!=5)blockers.Add("ORIGINAL_LOCAL_REGISTRATION_INCOMPLETE");
        if(inHost!=5)blockers.Add("HOST_REGISTRY_NOT_EXACTLY_FIVE");
        if(assignedCount!=5)blockers.Add("ORIGINAL_CLUSTER_ASSIGNMENTS_INCOMPLETE");
        if(!clusterIdMatches||!configuredPathMatches)
            blockers.Add("KOVA_CLUSTER_ID_OR_TRANSFER_ROOT_MISMATCH");
        if(!sourcePathMatches)blockers.Add("ORIGINAL_SOURCE_TRANSFER_ROOT_NOT_PROVEN");
        if(!nativeValid)blockers.Add("FIVE_NATIVE_ASA_LOADER_PROCESS_IDENTITIES_NOT_PROVEN");
        // There is currently no authoritative independent proof that Kova's
        // watchdog, Windows services, scheduled tasks and autostart are inactive.
        // Never infer an exclusive handoff from green RUNNING badges or RCON.
        blockers.Add("EXCLUSIVE_KOVA_CONTROL_HANDOFF_NOT_PROVEN");
        blockers.Add("KOVA_WATCHDOG_AUTOSTART_SCHEDULE_NOT_AUDITED");
        blockers.Add("EMBEDDED_SURVIVOR_AND_GAMEPLAY_ROUNDTRIP_NOT_PROVEN");
        blockers.Add("HISTORICAL_STEAM_RECOVERY_PIPELINE_NOT_CERTIFIED");
        return new(true,original.Length,exactlyFive,registered,inHost,
            assignedCount,clusterIdMatches,configuredPathMatches,
            sourcePathMatches,nativeValid,
            nativeValid?directAsaProcesses:0,
            nativeValid?verifiedLoaderAsaPairs:0,
            configCompliant,
            ExclusiveControllerHandoffVerified:false,
            KovaWatchdogDisabledVerified:false,
            ScheduledTasksReviewed:false,
            EmbeddedCharacterIntegrityVerified:false,
            GameClientRejoinVerified:false,
            RoundTripGameplayVerified:false,
            OriginalStoreSteamUpdateAuthorized:false,
            blockers.ToArray(),rows,
            "ORIGINAL_FIVE_KOVA_SOURCE_AND_OBSIDIAN_REGISTRY_READ_ONLY_NOT_CONTROL");
    }
}
