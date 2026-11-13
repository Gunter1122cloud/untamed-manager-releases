using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Existing LocalMap.Maintenance flag, persisted through ClusterService.ReplaceMap.
/// Explicit local profile change only; never invokes RCON, ASA, SteamCMD, LAN or Store.
/// </summary>
public sealed class DesktopMapMaintenanceBridge
{
    private readonly ClusterService cluster;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopMapMaintenanceBridge(ClusterService owner) =>
        cluster=owner??throw new ArgumentNullException(nameof(owner));

    private static bool Protected(LocalMap map) =>
        map.HistoricalStoreProtected || map.ObservedReadOnlyImported ||
        !string.IsNullOrWhiteSpace(map.StoreTestRoot) ||
        LegacyCompatibleWorldBackup.HasLiveOrHistoricalStore(map) ||
        LegacyCompatibleWorldBackup.InstallationRecoveryRequired(map);

    private bool CanEdit(LocalMap map)
    {
        if(Protected(map) || string.IsNullOrWhiteSpace(map.InstallationPath) ||
           !Path.IsPathFullyQualified(map.InstallationPath))
            return false;
        var check=cluster.Check(map);
        return check.ConfirmedStopped && !check.Running &&
            check.Identity==null && check.LoaderIdentity==null;
    }

    private static object Status(LocalMap map,bool permitted) => new {
        mapId=map.Id,mapName=map.Name,maintenance=map.Maintenance,
        permitted,storeProtected=Protected(map),
        requiresStopped=true,serverProcessModified=false,
        evidence="PERSISTED_LOCAL_MANAGER_MAINTENANCE_ONLY"
    };

    public Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)
                return Task.FromResult(Fail(id,"UM-MAP-MAINTENANCE-MESSAGE"));
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !data.TryGetProperty("id",out var rawId) ||
               rawId.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(rawId.GetString(),out var request) || request==Guid.Empty ||
               !data.TryGetProperty("method",out var rawMethod) ||
               rawMethod.ValueKind!=JsonValueKind.String ||
               !data.TryGetProperty("mapId",out var rawMap) ||
               rawMap.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(rawMap.GetString(),out var mapId) || mapId==Guid.Empty)
                return Task.FromResult(Fail(id,"UM-MAP-MAINTENANCE-MESSAGE"));
            id=request.ToString("D");
            var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId);
            if(map==null)return Task.FromResult(Fail(id,"UM-MAP-NOT-OWNED"));
            var method=rawMethod.GetString();
            if(method=="maps.maintenanceStatus")
                return Task.FromResult(Ok(id,Status(map,CanEdit(map))));
            if(method!="maps.maintenanceSet")
                return Task.FromResult(Fail(id,"UM-METHOD-DENIED"));
            if(!data.TryGetProperty("confirm",out var confirm) ||
               confirm.ValueKind!=JsonValueKind.True ||
               !data.TryGetProperty("maintenance",out var requested) ||
               requested.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Task.FromResult(Fail(id,"UM-MAP-MAINTENANCE-CONFIRM"));
            bool enabled=requested.GetBoolean();
            if(!CanEdit(map))
                return Task.FromResult(Fail(id,"UM-MAP-MAINTENANCE-STOPPED-NONSTORE-ONLY"));
            if(map.Maintenance==enabled)
                return Task.FromResult(Ok(id,Status(map,true)));

            // Share the same installation coordinator as the other native
            // maintenance actions; stage a clone before the existing CAS save.
            using(var lease=cluster.Operations.Acquire(
                map.InstallationPath,UntamedOperationKind.Configuration))
            {
                map=cluster.Config.LocalMaps.Single(x=>x.Id==mapId);
                if(!CanEdit(map))
                    return Task.FromResult(Fail(id,"UM-MAP-MAINTENANCE-CHANGED"));
                var copy=JsonSerializer.Deserialize<LocalMap>(
                    JsonSerializer.Serialize(map,Json),Json) ??
                    throw new InvalidDataException("UM-MAP-MAINTENANCE-COPY");
                copy.Maintenance=enabled;
                lease.MarkMutationStarted();
                cluster.ReplaceMap(map,copy);
                lease.Complete();
                return Task.FromResult(Ok(id,Status(copy,CanEdit(copy))));
            }
        }
        catch(Exception){return Task.FromResult(Fail(id,"UM-MAP-MAINTENANCE-NATIVE-DENIED"));}
    }
    private static string Ok(string? id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
}
