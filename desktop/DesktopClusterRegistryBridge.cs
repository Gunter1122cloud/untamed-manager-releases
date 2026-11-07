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
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, PendingMembership> pending = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record PendingMembership(Guid Token, string ClusterId,
        Guid[] MapIds, long Revision, DateTimeOffset CreatedUtc);

    public DesktopClusterRegistryBridge(ClusterService owner) =>
        cluster = owner ?? throw new ArgumentNullException(nameof(owner));

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
                "cluster.membership.preview" or "cluster.membership.apply"))
                return Failure(id, "UM-METHOD-DENIED");

            await gate.WaitAsync();
            try
            {
                ExpirePreviews();
                if (method == "cluster.registry.snapshot")
                    return Success(id, cluster.Registry.Snapshot());
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
