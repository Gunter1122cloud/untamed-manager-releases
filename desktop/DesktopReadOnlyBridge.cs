using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// New UI gets an explicit, limited read-only API. It does not expose file paths,
/// credentials, launch arguments, SQL writers, RCON dispatch or Store mutations.
/// UI readiness must NEVER be treated as production operation certification.
/// </summary>
public sealed class DesktopReadOnlyBridge : IAsyncDisposable
{
    private readonly ClusterService _cluster;
    internal ClusterService LocalCluster => _cluster;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CurseForgeCatalog _catalog;
    public DesktopReadOnlyBridge(string? profileRootOverride = null)
    {
        _cluster = new ClusterService(rootOverride: profileRootOverride);
        _catalog = new CurseForgeCatalog(_http);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // A peer may use "N" or "D" Guid formatting. Never compare string
    // representations when determining ownership/protection flags.
    public static bool MatchesPublishedMapId(Guid localMapId,string? publishedId)=>
        localMapId!=Guid.Empty && Guid.TryParse(publishedId,out var parsed) &&
        parsed==localMapId;

    public async Task<string> HandleAsync(string input)
    {
        string? id = null;
        try
        {
            if (input.Length is < 2 or > 4096) return Error(null, "UM-INVALID-MESSAGE");
            using var doc = JsonDocument.Parse(input);
            var request = doc.RootElement;
            if (request.ValueKind != JsonValueKind.Object ||
                !request.TryGetProperty("id", out var rawId) ||
                rawId.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(rawId.GetString(), out var parsedId) ||
                !request.TryGetProperty("method", out var rawMethod) ||
                rawMethod.ValueKind != JsonValueKind.String)
                return Error(null, "UM-INVALID-MESSAGE");

            id = parsedId.ToString("D");
            string? method = rawMethod.GetString();
            if (method is not ("dashboard.snapshot" or "mods.configured" or "curseforge.search" or "plugins.inventory" or "backups.summary" or "logs.read" or "steam.installations" or "servers.suggestPorts" or "mods.sharedInventory"))
                return Error(id, "UM-METHOD-DENIED");

            string? mapId = null;
            if (method is "mods.configured" or "plugins.inventory" or "logs.read")
            {
                if (!request.TryGetProperty("mapId", out var rawMap) ||
                    rawMap.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(rawMap.GetString(), out var parsedMap))
                    return Error(id, "UM-INVALID-MAP");
                mapId = parsedMap.ToString("D");
            }

            string? searchTerm = null;
            int searchIndex = 0;
            if (method == "curseforge.search")
            {
                if (!request.TryGetProperty("term", out var rawTerm) ||
                    rawTerm.ValueKind != JsonValueKind.String)
                    return Error(id, "UM-INVALID-SEARCH");
                searchTerm = (rawTerm.GetString() ?? "").Trim();
                if (searchTerm.Length is < 2 or > 80 ||
                    searchTerm.Any(char.IsControl))
                    return Error(id, "UM-INVALID-SEARCH");
                if (request.TryGetProperty("page", out var pageField))
                {
                    if (pageField.ValueKind != JsonValueKind.Number ||
                        !pageField.TryGetInt32(out int page) || page is < 0 or > 19)
                        return Error(id, "UM-INVALID-PAGE");
                    searchIndex = page * 25;
                }
                if (string.IsNullOrWhiteSpace(_cluster.Config.CurseForgeApiKeyProtected))
                    return Error(id, "UM-CURSEFORGE-NO-KEY");
            }

            await _serial.WaitAsync();
            try
            {
                object result = method switch
                {
                    "dashboard.snapshot" => await GetDashboardAsync(),
                    "mods.configured" => GetConfiguredMods(Guid.Parse(mapId!)),
                    "curseforge.search" => await SearchCurseForgeAsync(searchTerm!, searchIndex),
                    "plugins.inventory" => await Task.Run(() => GetLocalPluginInventory(Guid.Parse(mapId!))),
                    "backups.summary" => await Task.Run<object>(() => GetLocalBackupOverview()),
                    "logs.read" => await Task.Run<object>(() => GetLogTail(Guid.Parse(mapId!))),
                    "steam.installations" => await Task.Run<object>(() => GetInstallationInventory()),
                    "servers.suggestPorts" => GetSuggestedLocalPorts(),
                    "mods.sharedInventory" => await Task.Run<object>(() => DesktopSharedModInventory.Read(_cluster.Config.LocalMaps)),
                    _ => throw new InvalidOperationException("Disallowed bridge operation")
                };
                return JsonSerializer.Serialize(new { id, ok = true, data = result }, Json);
            }
            finally { _serial.Release(); }
        }
        catch (Exception)
        {
            // Never send exception messages, absolute paths, RCON credentials or
            // store metadata from the native host to the JavaScript surface.
            return Error(id, "UM-READ-UNAVAILABLE");
        }
    }


    // Read-only classification for the locally owned legacy Store map.
    // This never authorizes Start/Stop/SaveWorld: their exact original
    // ACTIVE/DONE, PID, source, RCON and journal preflights remain mandatory.
    internal static string ClassifyOriginalStoreLifecycle(LocalMap? map,
        bool running,bool journalBlocked,bool journalUnreadable,
        AsaRestartPhase? restartPhase=null)
    {
        if(map is not {HistoricalStoreProtected:true})return "protected";
        if(journalUnreadable||restartPhase==AsaRestartPhase.NeedsOperator)
            return "needsOperator";
        // Pending restart journal is not automatic recovery and never Start
        // authorization. The dedicated native panel provides its true status.
        if(restartPhase is AsaRestartPhase.Stopping or AsaRestartPhase.Starting)
            return "restartPending";
        if(journalBlocked)return "needsOperator";
        if(map.Maintenance)return "maintenance";
        if(running)return "running";
        if(map.RconPort is <1024 or >65535||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected))
            return "needsConfiguration";
        return "preflightRequired";
    }

    private string ReadOriginalStoreLifecycle(LocalMap? map,bool running)
    {
        if(map==null)return "protected";
        try
        {
            var journal=_cluster.Operations.Observe(map.InstallationPath);
            bool blocked=journal?.Phase is UntamedOperationPhase.Prepared or
                UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator;
            var restart=new AsaManualRestartJournal(map.InstallationPath).Observe();
            return ClassifyOriginalStoreLifecycle(map,running,blocked,false,
                restart?.Phase);
        }
        catch
        {
            // An unreadable durable operation journal must never display
            // a green/operable state or silently suggest a new mutation.
            return ClassifyOriginalStoreLifecycle(map,running,true,true);
        }
    }

    private async Task<object> GetDashboardAsync()
    {
        // Poll every explicitly configured, individually pinned LAN peer.
        // The Core throttles concurrent sockets; no remote commands, Store
        // writers, or production lifecycle calls are available here.
        await _cluster.PollPeersAsync();
        return await Task.Run<object>(() =>
        {
            var local = _cluster.LocalStatus();
            var resources = LocalHostResourceProbe.Read();
            var remote = _cluster.RemoteIsFresh ? _cluster.RemoteSnapshot : null;
            var remotes=_cluster.PeerObservations.Select(peer=>new{
                hostName=peer.HostName,hostId=peer.HostId,
                observedUtc=peer.Snapshot?.ObservedUtc,
                lastSuccessUtc=peer.LastSuccessUtc,
                fresh=peer.IsFresh,stateCode=peer.StateCode,
                maps=peer.Snapshot==null?Array.Empty<PublicMapStatus>():
                    peer.Snapshot.Maps.Select(m=>new PublicMapStatus{
                        Id=m.Id,Name=m.Name,Running=m.Running,Status=m.Status,
                        GamePort=m.GamePort,SteamBuildId=m.SteamBuildId,
                        SteamUpdateState=m.SteamUpdateState
                    }).ToArray()
            }).ToArray();
            return new
            {
                mode = "LIVE_READ_ONLY",
                observedUtc = DateTimeOffset.UtcNow,
                local = new {
                    hostName = local.HostName,
                    hostId = local.HostId,
                    maps = local.Maps.Select(m => {
                        // Return configuration flags only for a locally owned
                        // map. Never pass encrypted RCON secrets, Store roots,
                        // launch arguments or internal cluster identifiers
                        // to the WebView2 renderer.
                        var config = _cluster.Config.LocalMaps.FirstOrDefault(x =>
                            MatchesPublishedMapId(x.Id,m.Id));
                        bool protectedStore = config != null &&
                            KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(config);
                        bool historicalStore = config?.HistoricalStoreProtected==true;
                        return new {
                            id = m.Id, name = m.Name, running = m.Running,
                            status = m.Status, gamePort = m.GamePort,
                            steamBuildId = m.SteamBuildId, steamUpdateState = m.SteamUpdateState,
                            storeProtected = protectedStore,
                            freshStoreEnrolled = config != null && FreshStoreEnrollmentPolicy.IsEnrolled(config),
                            storeLifecycleState = historicalStore ?
                                ReadOriginalStoreLifecycle(config,m.Running) : null,
                            maintenance = config?.Maintenance ?? false,
                            rconConfigured = config != null && config.RconPort > 0 &&
                                !string.IsNullOrWhiteSpace(config.RconPasswordProtected)
                        };
                    }).ToArray(),
                    cpuPercent = resources.CpuPercent,
                    memoryUsedKiB = resources.UsedMemoryKiB,
                    memoryTotalKiB = resources.TotalMemoryKiB
                },
                remote = remote is null ? null : new {
                    hostName = remote.HostName, hostId = remote.HostId,
                    observedUtc = remote.ObservedUtc,
                    maps = remote.Maps.Select(m => new {
                        id = m.Id, name = m.Name, running = m.Running,
                        status = m.Status, gamePort = m.GamePort,
                        steamBuildId = m.SteamBuildId, steamUpdateState = m.SteamUpdateState
                    }).ToArray()
                },
                remotes,
                peerMessage = _cluster.RemoteMessage
            };
        });
    }

    private async Task<object> SearchCurseForgeAsync(string term, int index)
    {
        // The DPAPI protected secret stays in native memory. Only sanitized
        // official ASA catalog metadata is sent to the React renderer.
        string secret = SecureStore.Unprotect(_cluster.Config.CurseForgeApiKeyProtected);
        var found = await _catalog.SearchAsaAsync(secret, term, index);
        return new {
            gameId = found.GameId,
            totalCount = found.TotalCount,
            page = found.Index / 25,
            hasMore = found.HasMore,
            mods = found.Mods.Select(m => new {
                id = m.Id,
                name = m.Name,
                summary = m.Summary,
                website = m.Website,
                thumbnailUrl = m.ThumbnailUrl,
                authors = m.Authors,
                latestFile = m.LatestFile,
                modifiedUtc = m.ModifiedUtc,
                downloadCount = m.DownloadCount
            }).ToArray(),
            evidence = "OFFICIAL_CATALOG_METADATA_ONLY"
        };
    }

    private object GetLogTail(Guid mapId)
    {
        // No arbitrary path is accepted: resolve only the registered local
        // ASA log through Core's canonical-root, no-link and bounded-tail checks.
        var map = _cluster.Config.LocalMaps.SingleOrDefault(m => m.Id == mapId);
        if (map is null) throw new UnauthorizedAccessException("Not a locally owned map");
        var snapshot = LocalLogCenter.Read(map, maxBytes: 131072, maxLines: 300);
        return new
        {
            mapId = map.Id,
            mapName = map.Name,
            lastModifiedUtc = snapshot.LastModifiedUtc,
            totalBytes = snapshot.TotalBytes,
            truncated = snapshot.Truncated,
            lines = snapshot.Lines.Select(l => new {
                number = l.Number,
                level = l.Level.ToString().ToLowerInvariant(),
                content = l.Content
            }).ToArray(),
            evidence = "SANITIZED_LOCAL_LOG_TAIL_NOT_SERVER_HEALTH"
        };
    }

    private object GetInstallationInventory()
    {
        // Does not expose sensitive Windows paths or grant SteamCMD access.
        var installations = AsaInstallationInventory.Inspect(_cluster.Config.LocalMaps, _cluster.Check);
        return new
        {
            installations = installations.Select((i, index) => new {
                id = index,
                maps = i.LocalMaps,
                executablePresent = i.ServerExecutablePresent,
                installedSteamBuild = i.InstalledSteamBuild,
                updateBlocked = i.UpdateBlocked,
                reason = i.Reason
            }).ToArray(),
            evidence = "READ_ONLY_STEAM_INSTALLATION_PREFLIGHT"
        };
    }

    private object GetSuggestedLocalPorts()
    {
        // Profile-only suggestions; OS sockets and router are not probed.
        var suggested = LocalMapPortPolicy.Suggest(_cluster.Config.LocalMaps);
        return new {
            game = suggested.Game,
            query = suggested.Query,
            rcon = suggested.Rcon,
            evidence = "PROFILE_ONLY_NO_NETWORK_PROBE"
        };
    }

    private object GetLocalPluginInventory(Guid mapId)
    {
        var map = _cluster.Config.LocalMaps.SingleOrDefault(m => m.Id == mapId);
        if (map is null) throw new UnauthorizedAccessException("Not locally owned");
        var entries = LocalPluginInventory.Scan(map);
        return new
        {
            mapId = map.Id,
            mapName = map.Name,
            plugins = entries.Select(p => new {
                name = p.PluginName,
                protectedData = p.Protected,
                binaries = p.Binaries.Select(b => new {
                    name = b.Name, version = b.Version, bytes = b.Bytes
                }).ToArray()
            }).ToArray(),
            evidence = "READ_ONLY_LOCAL_FILE_INVENTORY"
        };
    }

    private object GetLocalBackupOverview()
    {
        var entries = LocalBackupOverview.Inspect(_cluster.Store.Root, _cluster.Config.LocalMaps);
        return new {
            maps = entries.Select(m => new {
                mapId = m.MapId, mapName = m.MapName,
                validPackages = m.ValidPackages,
                invalidPackages = m.InvalidPackages,
                verifiedBytes = m.VerifiedBytes,
                latestVerifiedUtc = m.LatestVerifiedUtc
            }).ToArray(),
            evidence = "FILE_INTEGRITY_ONLY_NOT_ASA_SAVE_COMPLETE"
        };
    }

    private object GetConfiguredMods(Guid mapId)
    {
        var map = _cluster.Config.LocalMaps.SingleOrDefault(m => m.Id == mapId);
        if (map is null) throw new UnauthorizedAccessException("Not locally owned");
        var configured = ModStateController.Read(map);
        return new {
            mapId = map.Id,
            mapName = map.Name,
            pending = configured.Pending,
            // Configured launch arguments are NOT proof of installed or loaded files.
            active = configured.Active,
            passive = configured.Passive,
            disabled = configured.Disabled,
            evidence = "CONFIGURED_ONLY"
        };
    }

    private static string Error(string? id, string code) =>
        JsonSerializer.Serialize(new { id, ok = false, error = code }, Json);

    public async ValueTask DisposeAsync()
    {
        await _cluster.DisposeAsync();
        _http.Dispose();
        _serial.Dispose();
    }
}
