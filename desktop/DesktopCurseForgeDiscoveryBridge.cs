using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Official CurseForge ASA metadata only. No mod downloading, automatic
/// installs, untrusted HTML, remote commands, or native arbitrary URLs.
/// API credential never leaves DPAPI-protected native configuration.
/// </summary>
public sealed class DesktopCurseForgeDiscoveryBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly CurseForgeCatalog catalog;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DesktopCurseForgeDiscoveryBridge(ClusterService cluster, HttpClient? http = null)
    {
        this.cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        client = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(22) };
        ownsClient = http is null;
        catalog = new CurseForgeCatalog(client);
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id = null;
        try
        {
            if (raw.Length is < 2 or > 4096) return Error(null, "UM-INVALID-MESSAGE");
            using var doc = JsonDocument.Parse(raw);
            var request = doc.RootElement;
            if (request.ValueKind != JsonValueKind.Object ||
                !request.TryGetProperty("id", out var idProp) ||
                idProp.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(idProp.GetString(), out var requestId) ||
                !request.TryGetProperty("method", out var methodProp) ||
                methodProp.ValueKind != JsonValueKind.String)
                return Error(null, "UM-INVALID-MESSAGE");
            id = requestId.ToString("D");
            string? method = methodProp.GetString();
            if (method is not ("curseforge.categories" or "curseforge.featured" or
                "curseforge.browse" or "curseforge.project" or "curseforge.files" or
                "curseforge.summary"))
                return Error(id, "UM-METHOD-DENIED");
            if (string.IsNullOrWhiteSpace(cluster.Config.CurseForgeApiKeyProtected))
                return Error(id, "UM-CURSEFORGE-NO-KEY");

            var page = 0;
            if (request.TryGetProperty("page", out var pageProp) &&
                (pageProp.ValueKind != JsonValueKind.Number ||
                 !pageProp.TryGetInt32(out page) || page is < 0 or > 19))
                return Error(id, "UM-INVALID-PAGE");

            var modId = 0;
            if (method is ("curseforge.project" or "curseforge.files" or "curseforge.summary") &&
                (!PositiveInt(request, "modId", out modId) || modId < 10000))
                return Error(id, "UM-INVALID-PROJECT");

            var term = "";
            var category = 0;
            var sort = 0;
            var descending = true;
            if (method == "curseforge.browse")
            {
                if (request.TryGetProperty("term", out var termProp))
                {
                    if (termProp.ValueKind != JsonValueKind.String)
                        return Error(id, "UM-INVALID-SEARCH");
                    term = (termProp.GetString() ?? "").Trim();
                }
                if (term.Length > 80 || (term.Length > 0 && term.Length < 2) ||
                    term.Any(char.IsControl))
                    return Error(id, "UM-INVALID-SEARCH");
                if (request.TryGetProperty("categoryId", out var categoryProp) &&
                    (categoryProp.ValueKind != JsonValueKind.Number ||
                     !categoryProp.TryGetInt32(out category) || category < 0))
                    return Error(id, "UM-INVALID-CATEGORY");
                if (request.TryGetProperty("sort", out var sortProp) &&
                    (sortProp.ValueKind != JsonValueKind.Number ||
                     !sortProp.TryGetInt32(out sort) ||
                     !Enum.IsDefined(typeof(CurseForgeSearchSortField), sort)))
                    return Error(id, "UM-INVALID-SORT");
                if (request.TryGetProperty("descending", out var descProp))
                {
                    if (descProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Error(id, "UM-INVALID-SORT");
                    descending = descProp.GetBoolean();
                }
            }

            // Never return the credential, even on failed provider requests.
            await gate.WaitAsync();
            try
            {
                string key = SecureStore.Unprotect(cluster.Config.CurseForgeApiKeyProtected);
                object response;
                switch (method)
                {
                    case "curseforge.categories":
                        var categories = await catalog.GetAsaCategoriesAsync(key);
                        response = new {
                            categories = categories.Select(c => new {
                                id = c.Id, name = c.Name, isClass = c.IsClass,
                                parentCategoryId = c.ParentCategoryId
                            }).ToArray(),
                            evidence = "OFFICIAL_ASA_CATEGORY_METADATA"
                        };
                        break;
                    case "curseforge.featured":
                        var featured = await catalog.GetAsaFeaturedAsync(key);
                        response = new {
                            gameId = featured.GameId,
                            featured = featured.Featured.Select(Summary).ToArray(),
                            popular = featured.Popular.Select(Summary).ToArray(),
                            recentlyUpdated = featured.RecentlyUpdated.Select(Summary).ToArray(),
                            evidence = "OFFICIAL_ASA_FEATURED_METADATA"
                        };
                        break;
                    case "curseforge.browse":
                        var found = await catalog.SearchAsaAsync(key,
                            new CurseForgeSearchOptions(term, CategoryId: category,
                                Sort: (CurseForgeSearchSortField)sort,
                                Descending: descending), page * 25);
                        response = new {
                            gameId = found.GameId, totalCount = found.TotalCount,
                            page, hasMore = found.HasMore,
                            mods = found.Mods.Select(Summary).ToArray(),
                            evidence = "OFFICIAL_CATALOG_METADATA_ONLY"
                        };
                        break;
                    case "curseforge.summary":
                        // Lightweight single-project metadata for names and
                        // thumbnails in the configured local Mod Center list.
                        // No file/description download and no mod mutation.
                        var summary=await catalog.GetAsaModAsync(key,modId);
                        response=new {
                            mod=Summary(summary),
                            evidence="OFFICIAL_ASA_PROJECT_SUMMARY_ONLY"
                        };
                        break;
                    case "curseforge.project":
                        var selected = await catalog.GetAsaModAsync(key, modId);
                        var description = await catalog.GetModDescriptionTextAsync(key, modId);
                        var files = await catalog.GetModFilesAsync(key, modId);
                        response = new {
                            mod = Summary(selected), description,
                            files = files.Files.Select(File).ToArray(),
                            filesTotal = files.Total, filesHasMore = files.HasMore,
                            evidence = "OFFICIAL_ASA_PROJECT_METADATA_ONLY"
                        };
                        break;
                    case "curseforge.files":
                        // Verify the project is actually assigned to ASA before
                        // returning file metadata for the requested project ID.
                        await catalog.GetAsaModAsync(key, modId);
                        var filePage = await catalog.GetModFilesAsync(key, modId, page * 25);
                        response = new {
                            modId, page, hasMore = filePage.HasMore,
                            totalCount = filePage.Total,
                            files = filePage.Files.Select(File).ToArray(),
                            evidence = "OFFICIAL_ASA_FILE_METADATA_ONLY"
                        };
                        break;
                    default:
                        return Error(id, "UM-METHOD-DENIED");
                }
                return JsonSerializer.Serialize(new { id, ok = true, data = response }, Json);
            }
            finally { gate.Release(); }
        }
        catch (Exception)
        {
            return Error(id, "UM-CURSEFORGE-REQUEST-FAILED");
        }
    }

    private static bool PositiveInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt32(out value) && value > 0;
    }

    private static object Summary(CurseForgeMod mod) => new {
        id = mod.Id, name = mod.Name, summary = mod.Summary,
        website = mod.Website, thumbnailUrl = mod.ThumbnailUrl,
        authors = mod.Authors, categories = mod.Categories,
        screenshots = mod.Screenshots, latestFile = mod.LatestFile,
        modifiedUtc = mod.ModifiedUtc, downloadCount = mod.DownloadCount,
        isAvailable = mod.IsAvailable
    };

    private static object File(CurseForgeFile file) => new {
        fileId = file.FileId, name = file.Name, published = file.Published,
        available = file.Available, sizeBytes = file.SizeBytes,
        gameVersions = file.GameVersions,
        dependencies = file.Relations.Select(r => new {
            modId = r.ModId, type = r.Type.ToString()
        }).ToArray(),
        distributionState = file.DistributionState
    };

    private static string Error(string? id, string code) =>
        JsonSerializer.Serialize(new { id, ok = false, error = code }, Json);

    public void Dispose()
    {
        gate.Dispose();
        if (ownsClient) client.Dispose();
    }
}
