using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Inspects ten already sealed, local, offline world-copy packages after
/// complete SHA-256 archive verification. This is never a live cluster reader
/// and never authorizes a restore, Store writer or game-client travel.
/// </summary>
public static class EmbeddedStoreArchiveInspector
{
    public sealed record MapResult(string Host,Guid MapId,string Package,
        string Status,int ProfileCandidates,int TribeCandidates,
        int UnclassifiedCandidates,int SupersededEntries,
        bool IndexBoundsVerified,bool GameplayVerified,
        bool CharacterTravelVerified);

    public sealed record ArchiveReport(Guid Batch,int Maps,int IndexScannedMaps,
        int NeedsReviewMaps,bool ArchiveFilesSha256Verified,
        bool LiveConsistencyCertified,bool ProductionRestoreAllowed,
        bool CharacterTravelVerified,MapResult[] Results,string Evidence);

    private const string Evidence="OFFLINE_TEN_MAP_WORLD_STORE_INDEX_AUDIT_ONLY";

    public static ArchiveReport Inspect(string archive)
    {
        if(string.IsNullOrWhiteSpace(archive))
            throw new InvalidDataException("UM-ARCHIVE-STORE-COPY-REQUIRED");
        // Re-verifies all maps, world files, shared transfer data and optional
        // configuration files before inspecting any embedded profile index.
        AsaOfflinePackage package=OfflineAsaClusterArchive.Verify(archive);
        string root=Path.GetFullPath(archive).TrimEnd(Path.DirectorySeparatorChar);
        var entries=package.Maps.OrderBy(m=>m.Host,StringComparer.Ordinal)
            .ThenBy(m=>m.Map).Select(map =>
        {
            string folder=Path.GetFullPath(Path.Combine(root,map.PackageRelativePath));
            string data=Path.Combine(folder,"data");
            // Archive.Verify has already hashed all map files. Check only
            // the small signed map index again to avoid rehashing 100+ GiB
            // of world saves a second time during a single audit.
            string indexPath=Path.Combine(folder,"manifest.json");
            var indexBytes=File.ReadAllBytes(indexPath);
            if(indexBytes.Length>12_000_000 ||
               !Convert.ToHexString(SHA256.HashData(indexBytes))
                   .Equals(map.ManifestSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UM-ARCHIVE-STORE-INDEX-CHANGED");
            var manifest=JsonSerializer.Deserialize<BackupManifest>(indexBytes)
                ??throw new InvalidDataException("UM-ARCHIVE-STORE-MANIFEST-INVALID");
            if(manifest.MapId!=map.Map || manifest.Files==null)
                throw new InvalidDataException("UM-ARCHIVE-STORE-OWNER-INVALID");
            var worlds=manifest.Files.Where(f=>
                    f.Path.EndsWith(".ark",StringComparison.OrdinalIgnoreCase) &&
                    f.Size>0).ToArray();

            MapResult Unverified(string state)=>new(map.Host,map.Map,map.Name,state,
                0,0,0,0,false,false,false);

            if(worlds.Length==0)return Unverified("NO_WORLD_CANDIDATE");
            if(worlds.Length!=1)return Unverified("MULTIPLE_WORLD_CANDIDATES");
            string filename=Path.GetFullPath(Path.Combine(data,worlds[0].Path));
            if(!filename.StartsWith(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar)+
                Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UM-ARCHIVE-STORE-WORLD-PATH-UNSAFE");
            try
            {
                var index=EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(filename);
                return new MapResult(map.Host,map.Map,map.Name,index.State,
                    index.ProfileCandidates,index.TribeCandidates,
                    index.UnclassifiedCandidates,index.SupersededEntries,
                    index.IndexBoundsVerified,false,false);
            }
            catch(Exception ex) when(ex is SqliteException or InvalidDataException
                or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A non-SQLite world (or an unsupported format) cannot be
                // classified as survivor loss. Report an unverified index.
                return Unverified("EMBEDDED_INDEX_NOT_READABLE");
            }
        }).ToArray();

        int clean=entries.Count(x=>x.Status=="INDEX_SCANNED" && x.IndexBoundsVerified);
        // Even a parsed index never certifies survivor ownership or in-game
        // cross-server transfer. All statuses are read-only evidence only.
        return new(package.Batch,entries.Length,clean,entries.Length-clean,
            true,false,false,false,entries,Evidence);
    }
}
