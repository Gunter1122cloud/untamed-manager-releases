using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// The original Legacy five-map source is needed for initial evidence, not as a
/// permanent running dependency. On an explicitly approved native import,
/// copy the five immutable metadata/launch files into the Manager's OWN local
/// profile. No ASA world, shared Store/transfer, lock or Legacy file is written.
/// ACTIVE/DONE belongs to the historical Legacy conversion/handoff protocol,\n/// not to ordinary local ASA startup after the immutable source is pinned.
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
    public static string PreferredProfile(string managerProfileRoot,string originalLegacyProfile)
    {
        string pinned=DirectoryFor(managerProfileRoot);
        if(!Directory.Exists(pinned))
        {
            if(File.Exists(pinned))
                throw new InvalidDataException("UM-STORE-PIN-NOT-A-DIRECTORY");
            return originalLegacyProfile;
        }
        _=VerifyPinned(pinned);
        return pinned;
    }

    /// <summary>
    /// A corrupt Manager-owned pin NEVER falls back silently to Legacy. Without
    /// a pin, earlier installations continue using their original Legacy source.
    /// </summary>
    public static string Resolve(string managerProfileRoot,string originalLegacyProfile,
        LocalMap map)
    {
        string pinned=PreferredProfile(managerProfileRoot,originalLegacyProfile);
        if(string.Equals(pinned,originalLegacyProfile,StringComparison.OrdinalIgnoreCase))
            return originalLegacyProfile;
        var proof=VerifyPinned(pinned);
        if(!string.Equals(map.HistoricalArchiveSha256,proof.FourFileHash,
               StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-PIN-MAP-IDENTITY");
        RequireImmutableIdentity(map,LegacyStoreV1OriginalImport.Inspect(pinned));
        return pinned;
    }

    /// <summary>
    /// Is the complete approved original metadata pinned locally to this
    /// concrete map? Never consult the retired Legacy ACTIVE/DONE peer markers.
    /// A corrupt, half-copied or wrong-map pin fails closed.
    /// </summary>
    public static bool HasApprovedLocalSource(string managerProfileRoot,LocalMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        string pinned=DirectoryFor(managerProfileRoot);
        if(!Directory.Exists(pinned))
        {
            if(File.Exists(pinned))
                throw new InvalidDataException("UM-STORE-PIN-NOT-A-DIRECTORY");
            return false;
        }
        var proof=VerifyPinned(pinned);
        if(!string.Equals(map.HistoricalArchiveSha256,proof.FourFileHash,
            StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-PIN-MAP-IDENTITY");
        RequireImmutableIdentity(map,LegacyStoreV1OriginalImport.Inspect(pinned));
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
           !string.Equals(map.HistoricalArchiveSha256,
               source.FourFilesSha256,StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-PIN-OWNER-CHANGED");
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
    /// obsolete Legacy ACTIVE/DONE coordination files. It is NOT a proof that
    /// cluster transfer is currently available.
    /// </summary>
    public static string RuntimeFingerprint(string managerProfileRoot,
        string originalLegacyProfile,LocalMap map)
    {
        string selected=Resolve(managerProfileRoot,originalLegacyProfile,map);
        if(string.Equals(selected,originalLegacyProfile,StringComparison.OrdinalIgnoreCase))
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
    public static void Capture(string originalLegacyProfile,string managerProfileRoot,
        string approvedFourFileHash,string approvedLaunchHash)
    {
        string source=VaultReadSnapshot.LocalPath(originalLegacyProfile,true);
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
            // removed; original Legacy/ASA/shared transfer files are untouched.
            if(Directory.Exists(stage))Directory.Delete(stage,true);
        }
    }
}
