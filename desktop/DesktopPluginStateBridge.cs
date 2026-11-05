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
/// Explicit two-stage local plugin enable/disable workflow. The original Core
/// PluginStateManager moves the complete directory with a verified backup;
/// this bridge never accepts filesystem paths and never grants remote control.
/// Historical Store-v1 maps cannot authorize plugin mutations.
/// </summary>
public sealed class DesktopPluginStateBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Dictionary<Guid,Pending> approvals=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Pending(Guid MapId,string Plugin,bool TargetEnabled,
        string OriginalInstallation,Guid[] InstallationMembers,DateTimeOffset ExpiresUtc);

    public DesktopPluginStateBridge(ClusterService service)=>
        cluster=service??throw new ArgumentNullException(nameof(service));

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Error(null,"UM-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var request=doc.RootElement;
            if(request.ValueKind!=JsonValueKind.Object ||
               !GuidField(request,"id",out var requestId) ||
               !request.TryGetProperty("method",out var methodValue) ||
               methodValue.ValueKind!=JsonValueKind.String)
                return Error(null,"UM-INVALID-MESSAGE");
            id=requestId.ToString("D");
            string? method=methodValue.GetString();
            if(method is not ("plugins.state" or "plugins.togglePreview" or "plugins.toggleApply"))
                return Error(id,"UM-METHOD-DENIED");

            await gate.WaitAsync();
            try
            {
                foreach(var expired in approvals.Where(p=>p.Value.ExpiresUtc<=DateTimeOffset.UtcNow)
                    .Select(p=>p.Key).ToArray())approvals.Remove(expired);

                if(method=="plugins.state")
                {
                    if(!GuidField(request,"mapId",out var mapId))
                        return Error(id,"UM-INVALID-MAP");
                    var map=Own(mapId);
                    if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                    var states=PluginStateManager.Inventory(map);
                    return Success(id,new{
                        mapId=map.Id,mapName=map.Name,
                        historicalProtected=map.HistoricalStoreProtected,
                        plugins=states.Select(p=>new{
                            name=p.Name,enabled=p.Enabled,runtimeLoadedVerified=false
                        }).ToArray(),
                        evidence="LOCAL_PLUGIN_DISK_STATE_ONLY"
                    });
                }

                if(method=="plugins.togglePreview")
                {
                    if(!GuidField(request,"mapId",out var mapId) ||
                       !TextField(request,"plugin",out var plugin) ||
                       !BoolField(request,"enabled",out var enabled))
                        return Error(id,"UM-PLUGIN-INVALID-REQUEST");
                    var map=Own(mapId);
                    if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                    if(map.HistoricalStoreProtected)return Error(id,"UM-HISTORICAL-STORE-LOCKED");
                    var readiness=PluginStateManager.InspectLocalToggle(
                        cluster,mapId,plugin,enabled);
                    if(!readiness.Allowed)
                        return Success(id,new{
                            allowed=false,token=(string?)null,plugin=readiness.Plugin,
                            targetEnabled=enabled,checkedLocalMaps=readiness.CheckedLocalMaps,
                            reason="A módosítás nem engedélyezett: ellenőrizd a helyi mapok leállított állapotát és a plugin aktuális állapotát.",
                            requiresRestart=true,productionRuntimeVerified=false,
                            evidence="LOCAL_PLUGIN_TOGGLE_PREFLIGHT_NO_WRITES"
                        });
                    if(approvals.Count>=8)approvals.Clear();
                    var token=Guid.NewGuid();
                    string sourceRoot=Path.GetFullPath(map.InstallationPath);
                    approvals[token]=new(mapId,readiness.Plugin,enabled,
                        sourceRoot,Members(sourceRoot),DateTimeOffset.UtcNow.AddMinutes(5));
                    return Success(id,new{
                        allowed=true,token,plugin=readiness.Plugin,
                        targetEnabled=enabled,checkedLocalMaps=readiness.CheckedLocalMaps,
                        reason="Minden megosztott telepítésű helyi map igazoltan leállítva.",
                        requiresRestart=true,productionRuntimeVerified=false,
                        evidence="LOCAL_PLUGIN_TOGGLE_PREFLIGHT_NO_WRITES"
                    });
                }

                if(!GuidField(request,"token",out var approvalId) ||
                   !approvals.TryGetValue(approvalId,out var saved))
                    return Error(id,"UM-PLUGIN-PREVIEW-EXPIRED");
                if(!request.TryGetProperty("confirm",out var confirmed) ||
                   confirmed.ValueKind!=JsonValueKind.True)
                    return Error(id,"UM-PLUGIN-EXPLICIT-CONFIRMATION-REQUIRED");

                // Consume the approval before touching the filesystem. An
                // ambiguous/failed attempt must be re-preflighted; no replay.
                approvals.Remove(approvalId);
                var owned=Own(saved.MapId);
                if(owned==null)return Error(id,"UM-MAP-NOT-OWNED");
                if(owned.HistoricalStoreProtected)
                    return Error(id,"UM-HISTORICAL-STORE-LOCKED");
                if(!string.Equals(Path.GetFullPath(owned.InstallationPath),
                    saved.OriginalInstallation,StringComparison.OrdinalIgnoreCase) ||
                   !Members(saved.OriginalInstallation).SequenceEqual(saved.InstallationMembers))
                    return Error(id,"UM-PLUGIN-STATE-CHANGED");
                if(DateTimeOffset.UtcNow>=saved.ExpiresUtc)
                    return Error(id,"UM-PLUGIN-PREVIEW-EXPIRED");
                var latest=PluginStateManager.InspectLocalToggle(
                    cluster,saved.MapId,saved.Plugin,saved.TargetEnabled);
                if(!latest.Allowed ||
                   !string.Equals(latest.Plugin,saved.Plugin,StringComparison.Ordinal))
                    return Error(id,"UM-PLUGIN-STATE-CHANGED");
                var result=PluginStateManager.ToggleLocalChecked(cluster,
                    saved.MapId,saved.Plugin,saved.TargetEnabled);
                return Success(id,new{
                    mapId=result.Map,plugin=result.Plugin,enabled=result.Enabled,
                    backupCreated=true,requiresRestart=result.RequiresRestart,
                    runtimeLoadedVerified=false,
                    evidence="VERIFIED_LOCAL_PLUGIN_DIRECTORY_TOGGLE"
                });
            }
            finally{gate.Release();}
        }
        catch(Exception)
        {
            // Do not echo local paths, plugin config contents, stored licenses
            // or raw process observations to the WebView2 renderer.
            return Error(id,"UM-PLUGIN-OPERATION-FAILED");
        }
    }

    private Guid[] Members(string installationRoot)=>
        cluster.Config.LocalMaps
            .Where(m=>!string.IsNullOrWhiteSpace(m.InstallationPath) &&
                string.Equals(Path.GetFullPath(m.InstallationPath),installationRoot,
                    StringComparison.OrdinalIgnoreCase))
            .Select(m=>m.Id).OrderBy(id=>id).ToArray();

    private LocalMap? Own(Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id);

    private static bool GuidField(JsonElement root,string key,out Guid value)
    {
        value=Guid.Empty;
        return root.TryGetProperty(key,out var field) &&
            field.ValueKind==JsonValueKind.String &&
            Guid.TryParse(field.GetString(),out value) && value!=Guid.Empty;
    }
    private static bool TextField(JsonElement root,string key,out string value)
    {
        value="";
        if(!root.TryGetProperty(key,out var field) ||
           field.ValueKind!=JsonValueKind.String)return false;
        value=field.GetString()?.Trim()??"";
        return value.Length is >0 and <=80 && char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(c=>
            char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    }
    private static bool BoolField(JsonElement root,string key,out bool value)
    {
        value=false;
        if(!root.TryGetProperty(key,out var field) ||
           field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value=field.GetBoolean();
        return true;
    }
    private static string Success(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>gate.Dispose();
}
