using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;
public sealed class DesktopModRepairBridge
{
    private readonly ClusterService cluster;
    private readonly Dictionary<Guid,Approval> approvals=new();
    private readonly object gate=new();
    private sealed record Approval(Guid Token,Guid MapId,string ModId,string Profile,LocalModRepair.Folder[] Folders,DateTimeOffset Expires);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public DesktopModRepairBridge(ClusterService source)=>cluster=source;
    private LocalMap Owned(Guid id,string modId,bool stopped)
    {
        var map=cluster.Config.LocalMaps.Single(m=>m.Id==id);
        if(map.ObservedReadOnlyImported||!ModStateController.Read(map).EnabledOrder.Contains(modId))
            throw new InvalidOperationException("UM-MOD-REPAIR-NOT-OWNED-OR-ENABLED");
        _=UntamedOperationCoordinator.ResourceKey(map.InstallationPath);
        if(stopped)
        {
            if(map.Maintenance||UntamedWorldBackup.InstallationRecoveryRequired(map))
                throw new InvalidOperationException("UM-MOD-REPAIR-MAINTENANCE");
            string root=Path.GetFullPath(map.InstallationPath).TrimEnd(Path.DirectorySeparatorChar);
            foreach(var peer in cluster.Config.LocalMaps.Where(m=>m.Id!=map.Id&&!string.IsNullOrWhiteSpace(m.InstallationPath)))
            {
                string other=Path.GetFullPath(peer.InstallationPath).TrimEnd(Path.DirectorySeparatorChar);
                if(other.Equals(root,StringComparison.OrdinalIgnoreCase)||other.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
                    root.StartsWith(other+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("UM-MOD-REPAIR-SHARED-INSTALL");
            }
            var state=map.HistoricalStoreProtected?HistoricalLocalConfigGuard.DemandStopped(cluster,map):cluster.Check(map);
            if(!state.ConfirmedStopped||state.Running||state.Identity!=null||state.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-MOD-REPAIR-STOP-REQUIRED");
        }
        return map;
    }
    public Task<string> HandleAsync(string raw)
    {
        // Native filesystem enumeration and process checks run off the UI thread.
        return Task.Run(()=>{
            string? id=null;
            string Error(string code)=>JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
            string Ok(object data)=>JsonSerializer.Serialize(new{id,ok=true,data},Json);
            try{
                if(raw.Length is <2 or >4096)return Error("UM-MOD-REPAIR-REQUEST");
                using var doc=JsonDocument.Parse(raw);var a=doc.RootElement;
                id=a.GetProperty("id").GetGuid().ToString("D");
                string? method=a.GetProperty("method").GetString();
                lock(gate){
                    foreach(var expired in approvals.Where(x=>x.Value.Expires<DateTimeOffset.UtcNow).Select(x=>x.Key).ToArray())approvals.Remove(expired);
                    if(method=="mods.localRepairApply"){
                        Guid token=a.GetProperty("token").GetGuid();
                        if(!a.GetProperty("confirm").GetBoolean()||!approvals.Remove(token,out var approved))return Error("UM-MOD-REPAIR-APPROVAL");
                        var map=Owned(approved.MapId,approved.ModId,true);
                        void Verify(){
                            var now=Owned(approved.MapId,approved.ModId,true);
                            if(!ReferenceEquals(now,map)||JsonSerializer.Serialize(now)!=approved.Profile)
                                throw new InvalidOperationException("UM-MOD-REPAIR-PROFILE-CHANGED");
                        }
                        int moved=LocalModRepair.Quarantine(map,approved.ModId,approved.Folders,Verify,cluster.Operations);
                        return Ok(new{mapId=map.Id,modId=approved.ModId,quarantined=moved,remainingFolders=0,serverStarted=false,downloadVerified=false});
                    }
                    if(method is not ("mods.localRepairPreview" or "mods.localRepairStatus"))return Error("UM-MOD-REPAIR-METHOD");
                    Guid mapId=a.GetProperty("mapId").GetGuid();
                    string project=a.GetProperty("modId").GetString()??"";
                    var selected=Owned(mapId,project,method=="mods.localRepairPreview");
                    var folders=LocalModRepair.Inspect(selected.InstallationPath,project);
                    var names=folders.Select(x=>Path.GetFileName(x.Relative)).ToArray();
                    if(method=="mods.localRepairStatus")return Ok(new{mapId,modId=project,folders=names,diskOnly=true,runtimeVerified=false});
                    foreach(var old in approvals.Where(x=>x.Value.MapId==mapId).Select(x=>x.Key).ToArray())approvals.Remove(old);
                    if(approvals.Count>=32)return Error("UM-MOD-REPAIR-PREVIEW-CAPACITY");
                    Guid ticket=Guid.NewGuid();
                    approvals[ticket]=new(ticket,mapId,project,JsonSerializer.Serialize(selected),folders,DateTimeOffset.UtcNow.AddMinutes(3));
                    return Ok(new{token=ticket,mapId,modId=project,folders=names,diskOnly=true,runtimeVerified=false});
                }
            }catch(Exception ex){
                string code=ex.Message;
                return Error(System.Text.RegularExpressions.Regex.IsMatch(code,@"\AUM-[A-Z0-9-]{1,100}\z")?code:"UM-MOD-REPAIR-UNAVAILABLE");
            }
        });
    }
}
