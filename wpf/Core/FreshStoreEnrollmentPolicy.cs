using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

/// <summary>
/// Enrollment of an *unused* locally installed ASA SteamCMD map into a
/// versioned Obsidian cluster. Never migrates an existing world and NEVER
/// writes an original Legacy ACTIVE/DONE journal, session or transfer file.
/// Transfer metadata is taken only from the already approved ClusterRegistry.
/// Multi-host transfer identity requires a separate peer-backed certificate
/// and is deliberately not inferred from one machine's UNC path.
/// </summary>
public static class FreshStoreEnrollmentPolicy
{
    public sealed record Plan(Guid MapId,string ClusterId,string TransferDirectory,
        string Arguments,string InstalledBuild,long RegistryRevision,
        string TransferFileId,
        string Fingerprint,string Evidence);

    public static bool IsEnrolled(LocalMap map)=>
        map!=null&&!string.IsNullOrWhiteSpace(map.FreshStoreClusterId);

    private static void Deny(string code)=>throw new InvalidDataException(code);

    private static string SafeTransfer(string transfer)
    {
        if(string.IsNullOrWhiteSpace(transfer)||transfer.Length>500||
           !Path.IsPathFullyQualified(transfer)||
           transfer.StartsWith(@"\\?\",StringComparison.Ordinal)||
           transfer.StartsWith(@"\\.\",StringComparison.Ordinal)||
           transfer.Any(c=>(char.IsWhiteSpace(c)&&c!=' ')||
               char.IsControl(c)||c is '"' or '<' or '>' or
               '|' or '?' or '*' or '\0')||
           transfer.Split('\\','/').Any(x=>x is "." or ".."))
            Deny("UM-FRESH-STORE-TRANSFER-PATH-UNSAFE");
        string path=Path.GetFullPath(transfer);
        if(!Directory.Exists(path))
            Deny("UM-FRESH-STORE-TRANSFER-PATH-NOT-FOUND");
        // Read-only Windows file-ID identity check. This proves the local
        // path exists, NOT that an independently paired peer sees the same
        // directory. Cross-host clusters remain blocked below.
        if(!StorePathIdentity.CompareDirectories(path,path).SameFileSystemObject)
            Deny("UM-FRESH-STORE-TRANSFER-IDENTITY-UNVERIFIED");
        return path;
    }

    internal static string ValidateBaseArguments(LocalMap map,string arguments)
    {
        // Require the existing native initial-setup grammar rather than
        // accepting arbitrary WebView-supplied process command lines.
        if(string.IsNullOrWhiteSpace(arguments)||arguments.Length>7800||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(arguments)||
           arguments.Contains('\r')||arguments.Contains('\n'))
            Deny("UM-FRESH-STORE-BASE-ARGS-UNSAFE");
        var session=Regex.Match(arguments,
            "\\?SessionName=\"(?<name>[^\"\\r\\n]{1,75})\"",
            RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(!session.Success)
            Deny("UM-FRESH-STORE-NATIVE-SETUP-REQUIRED");
        // Official map names remain pinned to the verified Steam level.
        // For a manually imported custom map, recover only the strict
        // single-token level saved by native first-setup (#118). Rebuild
        // the entire expected command line; never trust arbitrary flags.
        string baseline;
        try
        {
            baseline=AsaSteamMapCatalog.LaunchArguments(map.Name,
                session.Groups["name"].Value,map.GamePort,map.QueryPort);
        }
        catch(InvalidDataException)
        {
            const string marker="?listen?SessionName=";
            int separator=arguments.IndexOf(marker,StringComparison.Ordinal);
            if(separator is <1 or >80)
                Deny("UM-FRESH-STORE-CUSTOM-LEVEL-NATIVE-SETUP-REQUIRED");
            string level=AsaSteamMapCatalog.ValidateCustomLevel(
                arguments[..separator]);
            baseline=AsaSteamMapCatalog.LaunchArgumentsForLevel(level,
                session.Groups["name"].Value,map.GamePort,map.QueryPort);
        }
        if(!arguments.StartsWith(baseline,StringComparison.Ordinal))
            Deny("UM-FRESH-STORE-NATIVE-SETUP-REQUIRED");
        string suffix=arguments[baseline.Length..];
        if(suffix.Length>0)
        {
            // Keep only the two existing ModStateController-owned switches.
            // Never forward arbitrary renderer-supplied process flags or an
            // unrecognized option by treating it as a valid mod extension.
            var tokens=Regex.Matches(suffix,
                @"\G -(?<kind>mods|passivemods)=(?<ids>[0-9]{5,12}(?:,[0-9]{5,12}){0,499})",
                RegexOptions.CultureInvariant|RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100));
            if(tokens.Count is <1 or >2||
               tokens.Cast<Match>().Sum(x=>x.Length)!=suffix.Length||
               tokens.Cast<Match>().Select(x=>x.Groups["kind"].Value)
                   .Distinct(StringComparer.OrdinalIgnoreCase).Count()!=tokens.Count)
                Deny("UM-FRESH-STORE-UNVERIFIED-LAUNCH-EXTENSION");
            // Existing native Mod Center owns the ordering and passive subset.
            // Reuse its strict ID/duplicate/disabled validation verbatim.
            _=ModStateController.Read(map);
        }
        return arguments;
    }

    private static RegisteredCluster ExistingCluster(ClusterService cluster,
        LocalMap map,string clusterId)
    {
        if(string.IsNullOrWhiteSpace(clusterId))
            Deny("UM-FRESH-STORE-CLUSTER-NOT-SELECTED");
        string normalized=ClusterRegistrySchema.NormalizeClusterId(clusterId);
        if(OriginalStoreRuntimeIdentity.IsOriginalClusterId(normalized))
            Deny("UM-FRESH-STORE-LEGACY-CLUSTER-RESERVED");
        var snapshot=cluster.Registry.Snapshot();
        // Imported historical Store clusters may have a source-pinned ID
        // different from the conventional "\u004BovaCluster" label. A fresh
        // Store writer must never enroll through this generic new-cluster
        // path when ANY member is historical.
        if(snapshot.ClusterRegistry.MapAssignments.Any(a=>
            a.ClusterId.Equals(normalized,StringComparison.OrdinalIgnoreCase) &&
            cluster.Config.LocalMaps.Any(m=>m.Id==a.MapId &&
                (m.HistoricalStoreProtected ||
                 !string.IsNullOrWhiteSpace(m.HistoricalArchiveSha256)))))
            Deny("UM-FRESH-STORE-HISTORICAL-CLUSTER-RESERVED");
        var entry=snapshot.ClusterRegistry.Clusters.SingleOrDefault(x=>
            x.ClusterId.Equals(normalized,StringComparison.Ordinal));
        if(entry==null)Deny("UM-FRESH-STORE-CLUSTER-NOT-REGISTERED");
        var matches=snapshot.ClusterRegistry.MapAssignments.Where(x=>
            x.MapId==map.Id).ToArray();
        if(matches.Length!=1||!matches[0].ClusterId.Equals(normalized,
            StringComparison.Ordinal))
            Deny("UM-FRESH-STORE-MAP-MEMBERSHIP-REQUIRED");
        string owner=ClusterRegistrySchema.NormalizeHostId(cluster.Config.LocalHostId);
        if(!snapshot.HostRegistry.Maps.Any(x=>
            x.MapId==map.Id&&x.LocalConfigurationPresent&&
            x.HostId.Equals(owner,StringComparison.OrdinalIgnoreCase)))
            Deny("UM-FRESH-STORE-MAP-NOT-LOCAL-OWNER");
        var members=snapshot.ClusterRegistry.MapAssignments.Where(x=>
            x.ClusterId.Equals(normalized,StringComparison.Ordinal)).Select(x=>x.MapId)
            .ToHashSet();
        if(snapshot.HostRegistry.Maps.Any(x=>members.Contains(x.MapId)&&
            !x.HostId.Equals(owner,StringComparison.OrdinalIgnoreCase)))
            Deny("UM-FRESH-STORE-REMOTE-TRANSFER-NOT-CERTIFIED");
        return entry!;
    }

    public static Plan Prepare(ClusterService cluster,LocalMap map,string clusterId)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(map);
        if(!cluster.Config.LocalMaps.Contains(map)||map.Id==Guid.Empty||
           map.HistoricalStoreProtected||IsEnrolled(map)||
           LegacyCompatibleWorldBackup.HasLiveOrHistoricalStore(map)||
           !string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           map.Maintenance||map.AutoStart||
           map.LastProcess!=null||map.LastLoader!=null||
           map.StartWithAsaApi||
           !string.IsNullOrWhiteSpace(map.ApiLoaderArguments))
            Deny("UM-FRESH-STORE-UNUSED-LOCAL-MAP-ONLY");
        if(!AsaReadyProbe.IsRegisteredServerBinary(map)||
           !FreshAsaInstallationSafety.IsProvenFresh(cluster,map)||
           LegacyCompatibleWorldBackup.InstallationRecoveryRequired(map))
            Deny("UM-FRESH-STORE-NEW-STEAM-INSTALL-REQUIRED");
        // The existing one-time local RCON setup must be completed BEFORE
        // enrollment. After enrollment, the fresh-only INI bootstrap route
        // deliberately refuses Store profiles. This prevents an irreversible
        // profile which can never be started or stopped safely.
        if(map.RconPort is <1024 or >65535 ||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected)||
           !AsaRconConfigurationEvidence.Matches(map))
            Deny("UM-FRESH-STORE-RCON-SETUP-REQUIRED-BEFORE-ENROLLMENT");
        string build=AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath)
            ??throw new InvalidDataException("UM-FRESH-STORE-STEAM-BUILD-MISSING");
        if(build.Length==0)Deny("UM-FRESH-STORE-STEAM-BUILD-MISSING");
        var state=cluster.Check(map);
        if(state.Running||!state.ConfirmedStopped||
           state.Identity!=null||state.LoaderIdentity!=null)
            Deny("UM-FRESH-STORE-ASA-NOT-PROVEN-STOPPED");
        var registered=ExistingCluster(cluster,map,clusterId);
        string transfer=SafeTransfer(registered.TransferDirectory);
        string transferId=StorePathIdentity.LocalDirectoryFingerprint(transfer);
        var root=Path.GetFullPath(map.InstallationPath);
        if(cluster.Config.LocalMaps.Any(x=>x.Id!=map.Id&&
           !string.IsNullOrWhiteSpace(x.InstallationPath)&&
           Path.GetFullPath(x.InstallationPath).Equals(root,
               StringComparison.OrdinalIgnoreCase)))
            Deny("UM-FRESH-STORE-INSTALL-OWNERSHIP-CONFLICT");
        // Refuse a previously used world, tribute, player or tribe save even
        // when the requested map's .ark is missing or a 0 KB placeholder.
        foreach(string folder in new[]{
            Path.Combine(root,"ShooterGame","Saved","SavedArks"),
            Path.Combine(root,"ShooterGame","Saved","SavedArksLocal")
        })
        {
            if(!Directory.Exists(folder))continue;
            var info=new DirectoryInfo(folder);
            if((info.Attributes&FileAttributes.ReparsePoint)!=0||
               Directory.EnumerateFileSystemEntries(folder,"*",
                   SearchOption.AllDirectories).Any())
                Deny("UM-FRESH-STORE-EXISTING-SAVE-OR-REPARSE");
        }
        string baseArgs=ValidateBaseArguments(map,map.Arguments);
        string normalized=ClusterRegistrySchema.NormalizeClusterId(clusterId);
        // The ASA command-line format supports quoted Windows paths.
        // Ordinary spaces are allowed; quotes and control characters are
        // refused above so the renderer cannot inject additional flags.
        string argPath=transfer.Contains(' ')?"\""+transfer+"\"":transfer;
        string args=baseArgs+" -clusterid="+normalized+
            " -ClusterDirOverride="+argPath+" -usestore";
        if(args.Length>8192)
            Deny("UM-FRESH-STORE-ARGUMENTS-TOO-LONG");
        var registry=cluster.Registry.Snapshot();
        string material=JsonSerializer.Serialize(map)+"\n"+registry.Revision+
            "\n"+normalized+"\n"+transfer+"\n"+transferId+
            "\n"+args+"\n"+build;
        string fingerprint=Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return new(map.Id,normalized,transfer,args,build,
            registry.Revision,transferId,fingerprint,
            "NEW_EMPTY_STORE_V1_METADATA_ONLY");
    }

    /// <summary>
    /// Recheck pinned local metadata before Start/Stop/SaveWorld. No
    /// historical map, changed cluster assignment or cross-host alias
    /// can silently become a new Store writer.
    /// </summary>
    public static void DemandEnrolled(ClusterService cluster,LocalMap map)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(map);
        if(!cluster.Config.LocalMaps.Contains(map)||!IsEnrolled(map)||
           map.HistoricalStoreProtected||
           !string.IsNullOrWhiteSpace(map.HistoricalMapKey)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot))
            Deny("UM-FRESH-STORE-NOT-OWNED-OR-HISTORICAL");
        string clusterId=ClusterRegistrySchema.NormalizeClusterId(
            map.FreshStoreClusterId);
        var registered=ExistingCluster(cluster,map,clusterId);
        string transfer=SafeTransfer(registered.TransferDirectory);
        if(!string.Equals(transfer,map.FreshStoreTransferDirectory,
            StringComparison.OrdinalIgnoreCase))
            Deny("UM-FRESH-STORE-TRANSFER-REGISTRY-CHANGED");
        if(string.IsNullOrWhiteSpace(map.FreshStoreTransferFileId)||
           map.FreshStoreTransferFileId.Length!=64||
           !string.Equals(StorePathIdentity.LocalDirectoryFingerprint(transfer),
               map.FreshStoreTransferFileId,StringComparison.OrdinalIgnoreCase))
            Deny("UM-FRESH-STORE-TRANSFER-FILE-ID-CHANGED");
        string argPath=transfer.Contains(' ')?"\""+transfer+"\"":transfer;
        string suffix=" -clusterid="+clusterId+
            " -ClusterDirOverride="+argPath+" -usestore";
        string args=map.Arguments;
        if(!args.EndsWith(suffix,StringComparison.Ordinal)||
           AsaStoreArgumentSwitches.ContainsConversion(args))
            Deny("UM-FRESH-STORE-CONVERSION-OR-PROFILE-CHANGED");
        ValidateBaseArguments(map,args[..^suffix.Length]);
        if(!AsaReadyProbe.IsRegisteredServerBinary(map)||
           LegacyCompatibleWorldBackup.InstallationRecoveryRequired(map))
            Deny("UM-FRESH-STORE-LOCAL-EXE-OR-RECOVERY-INVALID");
        var root=Path.GetFullPath(map.InstallationPath);
        if(cluster.Config.LocalMaps.Any(x=>x.Id!=map.Id&&
           !string.IsNullOrWhiteSpace(x.InstallationPath)&&
           Path.GetFullPath(x.InstallationPath).Equals(root,
               StringComparison.OrdinalIgnoreCase)))
            Deny("UM-FRESH-STORE-INSTALL-OWNERSHIP-CONFLICT");
    }
}
