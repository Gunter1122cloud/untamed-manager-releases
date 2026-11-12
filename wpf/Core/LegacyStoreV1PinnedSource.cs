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
/// The live ACTIVE/DONE peer proof is still mandatory at every native Start.
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
        string pinned=PreferredProfile(managerProfileRoot,originalKovaProfile);
        if(string.Equals(pinned,originalKovaProfile,StringComparison.OrdinalIgnoreCase))
            return originalKovaProfile;
        var proof=VerifyPinned(pinned);
        if(!string.Equals(map.HistoricalArchiveSha256,proof.FourFileHash,
               StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-PIN-MAP-IDENTITY");
        LegacyStoreV1SourceLaunchParity.RequireOriginalMap(
            map,LegacyStoreV1OriginalImport.Inspect(pinned));
        return pinned;
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
