using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Allowlisted WebView2 API for the versioned HostRegistry and ClusterRegistry.
/// Every mutating membership request is previewed and applied once by token.
/// This bridge only edits Manager profile metadata; it exposes no ASA, Store,
/// RCON, transfer-file or remote-host write operation.
/// </summary>
public sealed class DesktopClusterRegistryBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly string originalLegacyProfile;
    private readonly Func<string,string,bool> samePhysicalTransferDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, PendingMembership> pending = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record PendingMembership(Guid Token, string ClusterId,
        Guid[] MapIds, long Revision, DateTimeOffset CreatedUtc);

    public DesktopClusterRegistryBridge(ClusterService owner,
        string? originalLegacyProfile = null,
        Func<string,string,bool>? verifiedTransferIdentity = null)
    {
        cluster = owner ?? throw new ArgumentNullException(nameof(owner));
        this.originalLegacyProfile=originalLegacyProfile??Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Kova");
        // Test-only injection is used with disposable local fixtures. In the
        // shipped Windows host this calls the REAL filesystem identity probe.
        samePhysicalTransferDirectory=verifiedTransferIdentity??((a,b)=>
            StorePathIdentity.CompareDirectories(a,b).SameFileSystemObject);
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id = null;
        try
        {
            if (raw.Length is < 2 or > 1_500_000) return Failure(null, "UM-INVALID-MESSAGE");
            using var document = JsonDocument.Parse(raw);
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object || !GuidField(request, "id", out var requestId) ||
                !StringField(request, "method", 64, out var method))
                return Failure(null, "UM-INVALID-MESSAGE");
            id = requestId.ToString("D");
            if (method is not ("cluster.registry.snapshot" or "cluster.host.register" or
                "cluster.map.register" or "cluster.create" or "cluster.update" or
                "cluster.membership.preview" or "cluster.membership.apply" or
                "cluster.historicalExpansionInspect"))
                return Failure(id, "UM-METHOD-DENIED");

            await gate.WaitAsync();
            try
            {
                ExpirePreviews();
                if (method == "cluster.registry.snapshot")
                    return Success(id, cluster.Registry.Snapshot());
                if (method == "cluster.historicalExpansionInspect")
                {
                    if(!GuidField(request,"mapId",out var newMapId)||
                       !StringField(request,"clusterId",64,out var selectedId))
                        return Failure(id,"UM-EXPANSION-MAP-OR-CLUSTER-INVALID");
                    return Success(id,InspectHistoricalExpansion(newMapId,selectedId));
                }
                if (method == "cluster.host.register")
                {
                    if (!StringField(request, "hostId", 64, out var hostId) ||
                        !StringField(request, "hostName", 100, out var hostName))
                        return Failure(id, "UM-REGISTRY-HOST-INVALID");
                    return Success(id, cluster.Registry.RegisterHost(hostId, hostName));
                }
                if (method == "cluster.map.register")
                {
                    if (!StringField(request, "hostId", 64, out var hostId) ||
                        !GuidField(request, "mapId", out var mapId) ||
                        !StringField(request, "mapName", 100, out var mapName))
                        return Failure(id, "UM-REGISTRY-MAP-INVALID");
                    return Success(id, cluster.Registry.RegisterRemoteMap(hostId, mapId, mapName));
                }
                if (method is "cluster.create" or "cluster.update")
                {
                    if (!StringField(request, "clusterId", 64, out var clusterId) ||
                        !StringField(request, "name", 100, out var name) ||
                        !StringField(request, "transferDirectory", 520, out var transferDirectory) ||
                        !OptionalStringField(request, "backupDirectory", 520, out var backupDirectory))
                        return Failure(id, "UM-REGISTRY-CLUSTER-INVALID");
                    object result = method == "cluster.create"
                        ? cluster.Registry.CreateCluster(clusterId, name, transferDirectory, backupDirectory)
                        : cluster.Registry.UpdateCluster(clusterId, name, transferDirectory, backupDirectory);
                    return Success(id, result);
                }
                if (method == "cluster.membership.preview")
                {
                    if (!StringField(request, "clusterId", 64, out var clusterId) ||
                        !GuidArrayField(request, "mapIds", out var mapIds))
                        return Failure(id, "UM-REGISTRY-SELECTION-INVALID");
                    var preview = cluster.Registry.PreviewMapAssignments(clusterId, mapIds);
                    string? token = null;
                    if (preview.IsValid)
                    {
                        var tokenId = Guid.NewGuid();
                        token = tokenId.ToString("D");
                        pending[tokenId] = new(tokenId, preview.ClusterId,
                            preview.RequestedMapIds.ToArray(), preview.BaseRevision, DateTimeOffset.UtcNow);
                    }
                    return Success(id, new { token, preview });
                }
                if (method == "cluster.membership.apply")
                {
                    if (!GuidField(request, "token", out var token) ||
                        !BooleanField(request, "confirm", out var confirm) || !confirm)
                        return Failure(id, "UM-REGISTRY-CONFIRMATION-REQUIRED");
                    if (!pending.Remove(token, out var review))
                        return Failure(id, "UM-REGISTRY-PREVIEW-EXPIRED");
                    var receipt = cluster.Registry.ApplyMapAssignments(
                        review.ClusterId, review.MapIds, review.Revision);
                    return Success(id, receipt);
                }
                return Failure(id, "UM-METHOD-DENIED");
            }
            finally { gate.Release(); }
        }
        catch (UnauthorizedAccessException) { return Failure(id, "UM-REGISTRY-OWNERSHIP-DENIED"); }
        catch (KeyNotFoundException) { return Failure(id, "UM-REGISTRY-REFERENCE-NOT-FOUND"); }
        catch (InvalidDataException) { return Failure(id, "UM-REGISTRY-INVALID"); }
        catch (InvalidOperationException) { return Failure(id, "UM-REGISTRY-CONFLICT"); }
        catch (IOException) { return Failure(id, "UM-REGISTRY-CONFIG-CHANGED"); }
        catch (Exception) { return Failure(id, "UM-REGISTRY-FAILED"); }
    }

    /// <summary>
    /// Real, on-demand, read-only first step for a FUTURE map joining the
    /// ORIGINAL shared Kova cluster. This never assigns Store membership,
    /// changes launch flags, opens a Store writer or grants character travel.
    /// Only a separately implemented peer-verified transaction may do that.
    /// </summary>
    private object InspectHistoricalExpansion(Guid mapId,string chosenClusterId)
    {
        string selected=ClusterRegistrySchema.NormalizeClusterId(chosenClusterId);
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
        if(map==null)throw new KeyNotFoundException("UM-EXPANSION-NOT-LOCAL");
        var initial=cluster.Registry.Snapshot();
        var existing=initial.ClusterRegistry.Clusters.SingleOrDefault(c=>
            string.Equals(c.ClusterId,selected,StringComparison.OrdinalIgnoreCase));
        var warnings=new List<string>();
        bool originalSourceVerified=false,originalFivePreserved=false;
        bool sameTransferObject=false,sourceIdMatches=false;
        string sourceId="";
        string sourceTag="";
        if(existing==null)
            warnings.Add("UM-EXPANSION-CLUSTER-NOT-REGISTERED");
        if(initial.ClusterRegistry.MapAssignments.Any(a=>a.MapId==mapId))
            warnings.Add("UM-EXPANSION-MAP-ALREADY-ASSIGNED");
        if(map.HistoricalStoreProtected ||
           !string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256) ||
           !string.IsNullOrWhiteSpace(map.FreshStoreClusterId) ||
           KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map))
            warnings.Add("UM-EXPANSION-CANDIDATE-NOT-NEW");
        bool localSetupVerified=false;
        try
        {
            var process=cluster.Check(map);
            if(!process.ConfirmedStopped||process.Running||
               process.Identity!=null||process.LoaderIdentity!=null||
               map.AutoStart||map.Maintenance)
                warnings.Add("UM-EXPANSION-STOP-OR-MAINTENANCE");
            else if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map) ||
                    !AsaReadyProbe.IsRegisteredServerBinary(map) ||
                    map.GamePort is <1 or >65535 ||
                    map.QueryPort is <1 or >65535 ||
                    map.GamePort==map.QueryPort ||
                    string.IsNullOrWhiteSpace(map.Arguments))
                warnings.Add("UM-EXPANSION-FRESH-ASA-SETUP-REQUIRED");
            else
            {
                // Reuse the EXACT allowlisted native argument grammar
                // (including custom Unreal level, official-level pinning,
                // optional Mod Center flags and invalid token rejection).
                // Merely matching a launch prefix is NOT sufficient.
                _=AsaSaveLayoutResolver.MapToken(map);
                _=FreshStoreEnrollmentPolicy.ValidateBaseArguments(
                    map,map.Arguments);
                LocalMapPortPolicy.RequireNoConflicts(map,cluster.Config.LocalMaps);
                localSetupVerified=true;
                if(map.RconPort is <1024 or >65535 ||
                   string.IsNullOrWhiteSpace(map.RconPasswordProtected) ||
                   !AsaRconConfigurationEvidence.Matches(map))
                {
                    warnings.Add("UM-EXPANSION-RCON-SETUP-REQUIRED");
                    localSetupVerified=false;
                }
            }
            if(FreshAsaInstallationSafety.IsProvenFresh(cluster,map))
            {
                foreach(var folder in new[]{
                    Path.Combine(map.InstallationPath,"ShooterGame","Saved","SavedArks"),
                    Path.Combine(map.InstallationPath,"ShooterGame","Saved","SavedArksLocal")})
                    if(Directory.Exists(folder)&&
                       Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        warnings.Add("UM-EXPANSION-EXISTING-SAVE-REQUIRES-REVIEW");
                        localSetupVerified=false;
                        break;
                    }
            }
        }
        catch(Exception ex) when(ex is InvalidDataException or IOException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warnings.Add("UM-EXPANSION-LOCAL-SETUP-UNVERIFIED");
            localSetupVerified=false;
        }
        try
        {
            var before=LegacyStoreV1LocalReader.Inspect(originalLegacyProfile);
            if(!before.LocalSnapshotReady)
                warnings.Add("UM-EXPANSION-ORIGINAL-FIVE-NOT-READY");
            else
            {
                // Only the exact old local source files are read. The
                // source-provided ID/path must be preserved byte-identically;
                // the renderer never supplies or receives the UNC.
                using var held=new VaultReadSnapshot();
                byte[] sourceBytes=held.Read(Path.Combine(originalLegacyProfile,
                    "cluster-v1.json"),1_048_576);
                using var doc=JsonDocument.Parse(sourceBytes);
                var source=doc.RootElement;
                sourceId=VaultReadSnapshot.Text(source,"id");
                string sourcePath=VaultReadSnapshot.Text(source,"path");
                var members=source.GetProperty("members").EnumerateArray().ToArray();
                originalFivePreserved=members.Length==5&&members.All(m=>{
                    var rawId=VaultReadSnapshot.Text(m,"id");
                    var key=VaultReadSnapshot.Text(m,"map");
                    return Guid.TryParse(rawId,out var originalMapId)&&
                        cluster.Config.LocalMaps.Count(candidate=>
                            candidate.Id==originalMapId&&
                            candidate.HistoricalStoreProtected&&
                            string.Equals(candidate.HistoricalArchiveSha256,
                                before.OriginalFourFilesTag,StringComparison.OrdinalIgnoreCase)&&
                            string.Equals(candidate.HistoricalMapKey,key,
                                StringComparison.Ordinal))==1;
                });
                if(!originalFivePreserved)
                    warnings.Add("UM-EXPANSION-ORIGINAL-FIVE-NOT-IMPORTED");
                sourceIdMatches=string.Equals(sourceId,selected,
                    StringComparison.OrdinalIgnoreCase);
                if(!sourceIdMatches)
                    warnings.Add("UM-EXPANSION-SOURCE-CLUSTER-ID-DIFFERS");
                if(existing!=null&&sourceIdMatches)
                {
                    try
                    {
                        if(!sourcePath.StartsWith(@"\\",StringComparison.Ordinal) ||
                           !existing.TransferDirectory.StartsWith(@"\\",
                               StringComparison.Ordinal))
                            warnings.Add("UM-EXPANSION-UNC-SOURCE-REQUIRED");
                        else
                        {
                            sameTransferObject=samePhysicalTransferDirectory(
                                sourcePath,existing.TransferDirectory);
                            if(!sameTransferObject)
                                warnings.Add("UM-EXPANSION-TRANSFER-FILE-ID-DIFFERS");
                        }
                    }
                    catch(Exception ex) when(ex is IOException or
                        UnauthorizedAccessException or InvalidDataException or
                        ArgumentException or NotSupportedException)
                    {
                        warnings.Add("UM-EXPANSION-TRANSFER-FILE-ID-UNAVAILABLE");
                    }
                }
                var after=LegacyStoreV1LocalReader.Inspect(originalLegacyProfile);
                originalSourceVerified=after.LocalSnapshotReady&&
                    string.Equals(before.OriginalFourFilesTag,
                        after.OriginalFourFilesTag,StringComparison.Ordinal);
                if(!originalSourceVerified)
                    warnings.Add("UM-EXPANSION-ORIGINAL-SOURCE-CHANGED");
                else sourceTag=before.OriginalFourFilesTag;
            }
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or
            UnauthorizedAccessException or JsonException or
            ArgumentException or KeyNotFoundException or NotSupportedException)
        {
            warnings.Add("UM-EXPANSION-ORIGINAL-SOURCE-UNAVAILABLE");
        }
        if(cluster.Registry.Snapshot().Revision!=initial.Revision)
            warnings.Add("UM-EXPANSION-REGISTRY-CHANGED");
        // Deliberate fail-closed gate: local readiness is NOT permission to
        // enter original Kova Store. Peer-authenticated writer/session/lock
        // extension has not yet been certified, including A->B->A travel.
        return new{
            mapId,mapName=map.Name,requestedClusterId=selected,
            sourceClusterId=originalSourceVerified?sourceId:"",
            originalSourceVerified,originalFivePreserved,
            samePhysicalTransferDirectory=sameTransferObject,
            nativeLocalSetupVerified=localSetupVerified,
            localPreflightReady=warnings.Count==0,
            sourceEvidenceTag=sourceTag,
            blockers=warnings.Distinct(StringComparer.Ordinal).ToArray(),
            originalStoreFilesWritten=false,storeWriterTouched=false,
            originalTenMapIdentitiesChanged=false,
            remoteWriterSessionCertified=false,characterTravelVerified=false,
            newMapStoreEnrollmentAllowed=false,serverStarted=false,
            evidence="READ_ONLY_ORIGINAL_STORE_EXPANSION_PREFLIGHT_NO_WRITER_AUTHORITY"
        };
    }

    private void ExpirePreviews()
    {
        foreach (var token in pending.Where(x =>
            DateTimeOffset.UtcNow - x.Value.CreatedUtc > TimeSpan.FromMinutes(5))
            .Select(x => x.Key).ToArray()) pending.Remove(token);
        while (pending.Count > 64) pending.Remove(pending.Keys.First());
    }

    private static bool GuidField(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String &&
            Guid.TryParse(item.GetString(), out value) && value != Guid.Empty;
    }

    private static bool StringField(JsonElement root, string name, int max, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString()?.Trim() ?? "";
        return value.Length is > 0 && value.Length <= max && !value.Any(char.IsControl);
    }

    private static bool OptionalStringField(JsonElement root, string name, int max, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return true;
        if (item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString()?.Trim() ?? "";
        return value.Length <= max && !value.Any(char.IsControl);
    }

    private static bool GuidArrayField(JsonElement root, string name, out Guid[] values)
    {
        values = Array.Empty<Guid>();
        if (!root.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.Array) return false;
        var parsed = new List<Guid>();
        foreach (var entry in item.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(entry.GetString(), out var value) || value == Guid.Empty) return false;
            parsed.Add(value);
        }
        values = parsed.ToArray();
        return true;
    }

    private static bool BooleanField(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var item) ||
            item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = item.GetBoolean();
        return true;
    }

    private static string Success(string id, object data) =>
        JsonSerializer.Serialize(new { id, ok = true, data }, Json);
    private static string Failure(string? id, string code) =>
        JsonSerializer.Serialize(new { id, ok = false, error = code }, Json);

    public void Dispose() => gate.Dispose();
}
