using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// The original Kova five-map source is needed for initial evidence, not as a
/// permanent running dependency. On an explicitly approved native import,
/// copy the five immutable metadata/launch files into the Manager's OWN local
/// profile. No ASA world, shared Store/transfer, lock or Kova file is written.
/// ACTIVE/DONE belongs to the historical Kova conversion/handoff protocol,\n/// not to ordinary local ASA startup after the immutable source is pinned.
/// </summary>
public static class LegacyStoreV1PinnedSource
{
    private const string Folder = "original-store-v1-source";
    private const string ManifestName = "pin-v1.json";
    private static readonly string[] FileNames = {
        "cluster-v1.json","cluster-store-v1.json",
        "shared-store-device-v1.json","shared-store-lock-v1.json",
        "launch-profiles-v1.json"
    };
    private sealed record Pin(int Schema,string FourFileHash,string LaunchHash,
        string[] FileHashes,DateTimeOffset CapturedUtc);

    private static string DirectoryFor(string managerProfileRoot) =>
        Path.Combine(VaultReadSnapshot.LocalPath(managerProfileRoot,true),Folder);

    private static string SupplementalRoot(string managerProfileRoot,Guid id)
    {
        if(id==Guid.Empty)throw new InvalidDataException("UM-STORE-EXTRA-GUID");
        return VaultReadSnapshot.LocalPath(Path.Combine(
            VaultReadSnapshot.LocalPath(managerProfileRoot,true),
            "supplemental-store-v1",id.ToString("N")),true);
    }

    internal static LegacyStoreV1AdditionalMapEvidence RequireSupplemental(
        LegacyStoreV1OriginalLocalSelection source,Guid id)
    {
        if(source.Maps.Any(m=>m.MapId==id))
            throw new InvalidDataException("UM-STORE-EXTRA-ORIGINAL-GUID");
        var row=source.AdditionalMaps.SingleOrDefault(m=>m.MapId==id)
            ??throw new InvalidDataException("UM-STORE-EXTRA-SOURCE-MISSING");
        if(!row.Active)throw new InvalidDataException("UM-STORE-EXTRA-NOT-ACTIVE");
        if(!row.SessionMatches)throw new InvalidDataException("UM-STORE-EXTRA-SESSION-MISMATCH");
        if(!row.BackupIdentityPresent)
            throw new InvalidDataException("UM-STORE-EXTRA-BACKUP-IDENTITY-MISSING");
        if(row.LaunchState!="source-launch-matched"||row.GamePort==null||
           row.QueryPort==null||row.EnabledMods==null||row.PassiveMods==null)
            throw new InvalidDataException("UM-STORE-EXTRA-LAUNCH-UNVERIFIED");
        return row;
    }

    internal static void CaptureSupplemental(string source,string managerProfileRoot,
        Guid mapId,string fourSha,string launchSha)
    {
        var proof=LegacyStoreV1OriginalImport.Inspect(source);
        _=RequireSupplemental(proof,mapId);
        if(proof.FourFilesSha256!=fourSha||proof.LaunchProfileSha256!=launchSha)
            throw new IOException("UM-STORE-EXTRA-SOURCE-CHANGED");
        // Orphaned immutable pin after a crash has no profile or registry
        // authority. Exact retry reuses it; differing bytes never overwrite.
        Capture(source,SupplementalRoot(managerProfileRoot,mapId),fourSha,launchSha);
    }

    // Caller holds the Manager's config publication mutex from before the
    // fresh proof through RegisterSupplementalHistoricalMap. Never replace
    // a pin with any committed profile, ownership, or membership authority.
    internal static bool ReconcileUnregisteredSupplemental(string source,ClusterService cluster,
        Guid mapId,string fourSha,string launchSha)
    {
        string root=SupplementalRoot(cluster.Store.Root,mapId);
        string existing=DirectoryFor(root);
        // Read durable authority independently, NOT Store.Read() on the
        // active instance (that would reset its optimistic-write digest).
        var disk=new ConfigStore("supplemental-reconcile",cluster.Store.Root).Read();
        static bool References(ManagerConfig config,Guid id)=>
            config.LocalMaps.Any(x=>x.Id==id)||
            config.HostRegistry.Maps.Any(x=>x.MapId==id)||
            config.ClusterRegistry.MapAssignments.Any(x=>x.MapId==id);
        if(disk.LocalHostId!=cluster.Config.LocalHostId||
           References(disk,mapId)||References(cluster.Config,mapId))
            throw new InvalidDataException("UM-STORE-EXTRA-PIN-ALREADY-OWNED");

        if(!Directory.Exists(existing))
        {
            CaptureSupplemental(source,cluster.Store.Root,mapId,fourSha,launchSha);
            return false;
        }
        var old=VerifyPinned(existing);
        if(old.FourFileHash==fourSha&&old.LaunchHash==launchSha)return false;


        var prior=RequireSupplemental(LegacyStoreV1OriginalImport.Inspect(existing),mapId);
        var fresh=LegacyStoreV1OriginalImport.Inspect(source);
        var row=RequireSupplemental(fresh,mapId);
        if(fresh.FourFilesSha256!=fourSha||fresh.LaunchProfileSha256!=launchSha||
           prior.MapKey!=row.MapKey||
           !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(prior.OriginalInstallationRoot)),
               Path.TrimEndingDirectorySeparator(Path.GetFullPath(row.OriginalInstallationRoot)),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-STORE-EXTRA-ORPHAN-IDENTITY-CHANGED");
        // Retain the original immutable evidence, never delete/overwrite it.
        // Prepare and verify the replacement in a separate local folder
        // before any rename. If interrupted after the first rename, the
        // next approved import can capture into the now-empty canonical slot.
        string stage=Path.Combine(root,"reconciliation-stage-"+Guid.NewGuid().ToString("N"));
        string archive=Path.Combine(root,"orphan-archive-"+Guid.NewGuid().ToString("N"));
        Capture(source,stage,fourSha,launchSha);
        var prepared=DirectoryFor(stage);
        _=VerifyPinned(prepared);
        Directory.Move(existing,archive);
        Directory.Move(prepared,existing);
        _=VerifyPinned(existing);
        Directory.Delete(stage,false);
        return true;
    }

    private static Pin VerifyPinned(string directory)
    {
        string safe=VaultReadSnapshot.LocalPath(directory,true);
        using var held=new VaultReadSnapshot();
        byte[] manifest=held.Read(Path.Combine(safe,ManifestName),16_384);
        var pin=JsonSerializer.Deserialize<Pin>(manifest) ??
            throw new InvalidDataException("UM-STORE-PIN-MANIFEST-MISSING");
        if(pin.Schema!=1||pin.FourFileHash?.Length!=64||
           pin.LaunchHash?.Length!=64||pin.FileHashes?.Length!=FileNames.Length||
           pin.FileHashes.Any(x=>x==null||x.Length!=64||!x.All(Uri.IsHexDigit)))
            throw new InvalidDataException("UM-STORE-PIN-MANIFEST-INVALID");
        for(int i=0;i<FileNames.Length;i++)
        {
            byte[] bytes=held.Read(Path.Combine(safe,FileNames[i]),1_048_576);
            if(!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
                   pin.FileHashes[i],StringComparison.Ordinal))
                throw new InvalidDataException("UM-STORE-PIN-FILE-CHANGED");
        }
        var parsed=LegacyStoreV1OriginalImport.Inspect(safe);
        if(!string.Equals(parsed.FourFilesSha256,pin.FourFileHash,StringComparison.Ordinal) ||
           !string.Equals(parsed.LaunchProfileSha256,pin.LaunchHash,StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-PIN-SOURCE-PARITY");
        return pin;
    }

    /// <summary>
    /// For already-approved imports and read-only diagnostics. This validates
    /// the Manager copy, but only Resolve(map) binds it to a concrete map.
    /// </summary>
    public static string PreferredProfile(string managerProfileRoot,string originalKovaProfile)
    {
        string pinned=DirectoryFor(managerProfileRoot);
        if(!Directory.Exists(pinned))
        {
            if(File.Exists(pinned))
                throw new InvalidDataException("UM-STORE-PIN-NOT-A-DIRECTORY");
            return originalKovaProfile;
        }
        _=VerifyPinned(pinned);
        return pinned;
    }

    /// <summary>
    /// A corrupt Manager-owned pin NEVER falls back silently to Kova. Without
    /// a pin, earlier installations continue using their original Kova source.
    /// </summary>
    public static string Resolve(string managerProfileRoot,string originalKovaProfile,
        LocalMap map)
    {
        if(map.HistoricalSupplementalSource)
        {
            string supplemental=DirectoryFor(SupplementalRoot(managerProfileRoot,map.Id));
            _=VerifyPinned(supplemental);
            RequireImmutableIdentity(map,LegacyStoreV1OriginalImport.Inspect(supplemental));
            return supplemental;
        }
        string pinned=PreferredProfile(managerProfileRoot,originalKovaProfile);
        if(string.Equals(pinned,originalKovaProfile,StringComparison.OrdinalIgnoreCase))
            return originalKovaProfile;
        var proof=VerifyPinned(pinned);
        var selection=LegacyStoreV1OriginalImport.Inspect(pinned);
        if(!string.Equals(map.HistoricalArchiveSha256,proof.FourFileHash,
               StringComparison.Ordinal)&&
           !selection.SelectedHistoricalSubsetOfExpandedRoster)
            throw new InvalidDataException("UM-STORE-PIN-MAP-IDENTITY");
        RequireImmutableIdentity(map,selection);
        return pinned;
    }

    /// <summary>
    /// Is the complete approved original metadata pinned locally to this
    /// concrete map? Never consult the retired Kova ACTIVE/DONE peer markers.
    /// A corrupt, half-copied or wrong-map pin fails closed.
    /// </summary>
    public static bool HasApprovedLocalSource(string managerProfileRoot,LocalMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if(map.HistoricalSupplementalSource)
        {
            _=Resolve(managerProfileRoot,"",map);
            return true;
        }
        string pinned=DirectoryFor(managerProfileRoot);
        if(!Directory.Exists(pinned))
        {
            if(File.Exists(pinned))
                throw new InvalidDataException("UM-STORE-PIN-NOT-A-DIRECTORY");
            return false;
        }
        var proof=VerifyPinned(pinned);
        var selection=LegacyStoreV1OriginalImport.Inspect(pinned);
        if(!string.Equals(map.HistoricalArchiveSha256,proof.FourFileHash,
             StringComparison.Ordinal)&&
           !selection.SelectedHistoricalSubsetOfExpandedRoster)
            throw new InvalidDataException("UM-STORE-PIN-MAP-IDENTITY");
        RequireImmutableIdentity(map,selection);
        return true;
    }

    /// <summary>
    /// The imported GUID, map level, installation root and original source
    /// fingerprint stay immutable. Ports, enabled/passive mods and cluster
    /// selection are CURRENT Obsidian settings; comparing them with the
    /// historical launch profile would incorrectly reject approved edits.
    /// </summary>
    public static void RequireImmutableIdentity(LocalMap map,
        LegacyStoreV1OriginalLocalSelection source)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(source);
        if(!map.HistoricalStoreProtected||map.Id==Guid.Empty||
           map.ObservedReadOnlyImported||
           map.HistoricalArchiveSha256?.Length!=64||
           !map.HistoricalArchiveSha256.All(Uri.IsHexDigit)||
           (!string.Equals(map.HistoricalArchiveSha256,
               source.FourFilesSha256,StringComparison.Ordinal)&&
            !source.SelectedHistoricalSubsetOfExpandedRoster))
            throw new InvalidDataException("UM-STORE-PIN-OWNER-CHANGED");
        if(map.HistoricalSupplementalSource)
        {
            var row=RequireSupplemental(source,map.Id);
            if(map.HistoricalArchiveSha256!=source.FourFilesSha256||
               map.HistoricalMapKey!=row.MapKey||
               !string.Equals(Path.GetFullPath(map.InstallationPath),
                   Path.GetFullPath(row.OriginalInstallationRoot),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UM-STORE-EXTRA-PIN-OWNER-MISMATCH");
            return;
        }
        var original=source.Maps.SingleOrDefault(x=>x.MapId==map.Id);
        if(original==null||
           !string.Equals(map.HistoricalMapKey,original.MapKey,StringComparison.Ordinal)||
           !string.Equals(Path.GetFullPath(map.InstallationPath),
               Path.GetFullPath(original.OriginalInstallationRoot),
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-STORE-PIN-MAP-ROOT-CHANGED");
    }

    public sealed record OriginalCluster(string RuntimeClusterId,string TransferRoot);

    public static OriginalCluster ReadOriginalCluster(string profile)
    {
        string directory=VaultReadSnapshot.LocalPath(profile,true);
        using var held=new VaultReadSnapshot();
        using var doc=JsonDocument.Parse(held.Read(
            Path.Combine(directory,"cluster-v1.json"),1_048_576));
        var source=doc.RootElement;
        if(!source.TryGetProperty("id",out var id)||
           !source.TryGetProperty("path",out var path)||
           id.ValueKind!=JsonValueKind.String||
           path.ValueKind!=JsonValueKind.String||
           string.IsNullOrWhiteSpace(id.GetString())||
           string.IsNullOrWhiteSpace(path.GetString()))
            throw new InvalidDataException("UM-STORE-PIN-CLUSTER-METADATA");
        return new(id.GetString()!,path.GetString()!);
    }

    /// <summary>
    /// Unchanging native-operation source proof for LOCAL SaveWorld/Stop/RCON
    /// against an owned map, without requiring the other ASA host or the
    /// obsolete Kova ACTIVE/DONE coordination files. It is NOT a proof that
    /// cluster transfer is currently available.
    /// </summary>
    public static string RuntimeFingerprint(string managerProfileRoot,
        string originalKovaProfile,LocalMap map)
    {
        string selected=Resolve(managerProfileRoot,originalKovaProfile,map);
        if(string.Equals(selected,originalKovaProfile,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UM-STORE-PIN-NOT-YET-APPROVED");
        var source=LegacyStoreV1OriginalImport.Inspect(selected);
        var cluster=ReadOriginalCluster(selected);
        var mods=ModStateController.Read(map);
        string effectiveId=string.IsNullOrWhiteSpace(map.HistoricalRuntimeClusterId)
            ?cluster.RuntimeClusterId:map.HistoricalRuntimeClusterId;
        string effectivePath=string.IsNullOrWhiteSpace(map.HistoricalRuntimeTransferDirectory)
            ?cluster.TransferRoot:map.HistoricalRuntimeTransferDirectory;
        if(string.IsNullOrWhiteSpace(effectiveId)||
           string.IsNullOrWhiteSpace(effectivePath))
            throw new InvalidDataException("UM-STORE-RUNTIME-CLUSTER-INCOMPLETE");
        // Bind approval epochs to the exact *current* ASA configuration
        // bytes. Otherwise changing the INI between RCON/SaveWorld preview
        // and apply could reuse an old source SHA even when the running
        // server settings had changed. Never read shared Store/transfer.
        using var snapshot=new VaultReadSnapshot();
        string IniTag(string path,int limit)=>File.Exists(path)
            ?Convert.ToHexString(SHA256.HashData(snapshot.Read(path,limit)))
            :"ABSENT";
        string gus=IniTag(AsaInitialRconSettings.NewIniPath(map.InstallationPath),131072);
        string game=IniTag(Path.Combine(map.InstallationPath,
            "ShooterGame","Saved","Config","WindowsServer","Game.ini"),4_000_000);
        var evidence=string.Join("\n",
            source.FourFilesSha256,source.LaunchProfileSha256,
            map.Id.ToString("D"),map.HistoricalMapKey,
            Path.GetFullPath(map.InstallationPath),
            map.GamePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            map.QueryPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            map.RconPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            effectiveId,effectivePath,
            string.Join(",",mods.EnabledOrder),string.Join(",",mods.Passive),
            map.StartWithAsaApi?"API":"DIRECT",
            string.IsNullOrWhiteSpace(map.ExecutablePath)?"UNSET":
                Path.GetFullPath(map.ExecutablePath),
            !map.StartWithAsaApi?"DIRECT":
                string.IsNullOrWhiteSpace(map.ApiLoaderPath)?"UNSET":
                    Path.GetFullPath(map.ApiLoaderPath),
            map.RconPasswordProtected??"",gus,game);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
    }

    /// <summary>
    /// This is a Manager-profile-only mutation permitted solely from an
    /// explicitly approved import. The approved source hashes are consumed
    /// here; neither an untrusted WebView path nor a replacement cluster id
    /// can supply them.
    /// </summary>
    public static void Capture(string originalKovaProfile,string managerProfileRoot,
        string approvedFourFileHash,string approvedLaunchHash)
    {
        string source=VaultReadSnapshot.LocalPath(originalKovaProfile,true);
        string manager=VaultReadSnapshot.LocalPath(managerProfileRoot,true);
        string existingPin=DirectoryFor(manager);
        if(string.Equals(source,existingPin,StringComparison.OrdinalIgnoreCase))
        {
            var existing=VerifyPinned(existingPin);
            if(existing.FourFileHash!=approvedFourFileHash||
               existing.LaunchHash!=approvedLaunchHash)
                throw new InvalidDataException("UM-STORE-PIN-EXISTING-CONFLICT");
            return;
        }
        if(source.Equals(manager,StringComparison.OrdinalIgnoreCase)||
           manager.StartsWith(source+Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase)||
           source.StartsWith(manager+Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-STORE-PIN-SOURCE-OVERLAP");
        var original=LegacyStoreV1OriginalImport.Inspect(source);
        if(!string.Equals(original.FourFilesSha256,approvedFourFileHash,
               StringComparison.Ordinal)||
           !string.Equals(original.LaunchProfileSha256,approvedLaunchHash,
               StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-PIN-APPROVAL-CHANGED");

        Directory.CreateDirectory(manager);
        string final=DirectoryFor(manager);
        if(Directory.Exists(final))
        {
            var existing=VerifyPinned(final);
            if(existing.FourFileHash!=approvedFourFileHash||
               existing.LaunchHash!=approvedLaunchHash)
                throw new InvalidDataException("UM-STORE-PIN-EXISTING-CONFLICT");
            return;
        }
        string stage=Path.Combine(manager,Folder+".incomplete-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string[] hashes=new string[FileNames.Length];
            using(var held=new VaultReadSnapshot())
            {
                for(int i=0;i<FileNames.Length;i++)
                {
                    var bytes=held.Read(Path.Combine(source,FileNames[i]),1_048_576);
                    hashes[i]=Convert.ToHexString(SHA256.HashData(bytes));
                    using var output=new FileStream(Path.Combine(stage,FileNames[i]),
                        FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    output.Write(bytes);output.Flush(true);
                }
            }
            var again=LegacyStoreV1OriginalImport.Inspect(source);
            var copied=LegacyStoreV1OriginalImport.Inspect(stage);
            if(again.FourFilesSha256!=approvedFourFileHash||
               again.LaunchProfileSha256!=approvedLaunchHash||
               copied.FourFilesSha256!=approvedFourFileHash||
               copied.LaunchProfileSha256!=approvedLaunchHash)
                throw new IOException("UM-STORE-PIN-SOURCE-CHANGED-DURING-COPY");
            var pin=new Pin(1,approvedFourFileHash,approvedLaunchHash,
                hashes,DateTimeOffset.UtcNow);
            byte[] metadata=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pin));
            using(var output=new FileStream(Path.Combine(stage,ManifestName),
                FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { output.Write(metadata);output.Flush(true); }
            _=VerifyPinned(stage);
            Directory.Move(stage,final);
            _=VerifyPinned(final);
        }
        finally
        {
            // Only this randomly named Manager-owned staging folder can be
            // removed; original Kova/ASA/shared transfer files are untouched.
            if(Directory.Exists(stage))Directory.Delete(stage,true);
        }
    }
}
