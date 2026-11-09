using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Local-only, source-pinned discovery of the ORIGINAL five maps in a Kova
/// ACTIVE/DONE profile. No caller-chosen UNC path, Store writer or conversion.
/// The original four files are held read-only across both inspections.
/// </summary>
public sealed record LegacyStoreV1OriginalMap(Guid MapId,string MapKey,
    string OriginalInstallationRoot);
public sealed record LegacyStoreV1OriginalLocalSelection(string FourFilesSha256,
    LegacyStoreV1OriginalMap[] Maps);

public static class LegacyStoreV1OriginalImport
{
    private static readonly string[] Names={
        "cluster-v1.json","cluster-store-v1.json",
        "shared-store-device-v1.json","shared-store-lock-v1.json"
    };

    public static LegacyStoreV1OriginalLocalSelection Inspect(string localProfile)
    {
        string root=VaultReadSnapshot.LocalPath(localProfile,true);
        using var held=new VaultReadSnapshot();
        var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        foreach(string name in Names)
            files.Add(name,held.Read(Path.Combine(root,name),1_048_576));

        // The full original verifier checks source/session/lock/map bindings.
        // Requiring its exact four-file byte tag prevents a second independent
        // parser from silently accepting a different or rewritten source.
        var original=LegacyStoreV1LocalReader.Inspect(root);
        if(!original.LocalSnapshotReady||original.Maps.Length!=5)
            throw new InvalidDataException("UM-STORE-DIRECT-ORIGINAL-NOT-READY");
        string tag=Convert.ToHexString(SHA256.HashData(
            Encoding.ASCII.GetBytes("STORE_V1_ORIGINAL_FOUR_FILES:"+
                string.Join(":",Names.Select(n=>Convert.ToHexString(
                    SHA256.HashData(files[n])))))));
        if(!string.Equals(tag,original.OriginalFourFilesTag,StringComparison.Ordinal))
            throw new IOException("UM-STORE-DIRECT-ORIGINAL-METADATA-CHANGED");

        using var doc=VaultReadSnapshot.Json(files,"cluster-v1.json");
        if(!doc.RootElement.TryGetProperty("members",out var members) ||
           members.ValueKind!=JsonValueKind.Array||members.GetArrayLength()!=5)
            throw new InvalidDataException("UM-STORE-DIRECT-FIVE-MAPS-REQUIRED");
        var rows=new List<LegacyStoreV1OriginalMap>();
        var ids=new HashSet<Guid>();
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in members.EnumerateArray())
        {
            if(row.ValueKind!=JsonValueKind.Object ||
               !row.TryGetProperty("id",out var idJson) ||
               !row.TryGetProperty("map",out var nameJson) ||
               !row.TryGetProperty("root",out var rootJson) ||
               idJson.ValueKind!=JsonValueKind.String ||
               nameJson.ValueKind!=JsonValueKind.String ||
               rootJson.ValueKind!=JsonValueKind.String ||
               !Guid.TryParseExact(idJson.GetString(),"D",out var id) ||
               id==Guid.Empty||!ids.Add(id))
                throw new InvalidDataException("UM-STORE-DIRECT-MAP-ID-INVALID");
            string name=nameJson.GetString()??"";
            string installation=rootJson.GetString()??"";
            if(name.Length is <1 or >80 ||
               !name.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-' or '.') ||
               !names.Add(name) ||
               installation.Length is <3 or >1000 ||
               !Path.IsPathFullyQualified(installation) ||
               installation.StartsWith(@"\\",StringComparison.Ordinal) ||
               installation.Any(char.IsControl))
                throw new InvalidDataException("UM-STORE-DIRECT-LOCAL-ROOT-INVALID");
            rows.Add(new(id,name,installation));
        }
        // A second source check while all four read-only handles are held.
        var after=LegacyStoreV1LocalReader.Inspect(root);
        if(!after.LocalSnapshotReady ||
           !string.Equals(after.OriginalFourFilesTag,tag,StringComparison.Ordinal))
            throw new IOException("UM-STORE-DIRECT-ORIGINAL-METADATA-CHANGED");
        return new(tag,rows.ToArray());
    }
}
