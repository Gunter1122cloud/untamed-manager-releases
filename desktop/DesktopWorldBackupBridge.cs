using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Real native per-map Kova-style backup and rollback restore. The WebView can
/// select only locally registered map/verified backup IDs; it cannot provide a
/// source/destination path, run an arbitrary file operation or touch the shared
/// Kova Store-v1 writer. Archive byte verification and OS process checks live
/// in Core; the renderer can never "confirm" an unsafe state away.
/// </summary>
public sealed class DesktopWorldBackupBridge
{
    private readonly ClusterService cluster;
    private readonly Func<LocalMap,ProcessCheck> inspect;
    private readonly SemaphoreSlim serial=new(1,1);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Ticket(Guid Token,Guid MapId,string Point,string Root,
        string Arguments,DateTimeOffset Until,bool Restore);
    private Ticket? pending;

    public DesktopWorldBackupBridge(ClusterService cluster,
        Func<LocalMap,ProcessCheck>? processInspector=null)
    {
        this.cluster=cluster??throw new ArgumentNullException(nameof(cluster));
        inspect=processInspector??(map=>new WindowsProcessInspector().InspectFamily(map));
    }
    private LocalMap Owned(Guid mapId)
    {
        if(mapId==Guid.Empty)throw new InvalidDataException("UM-BACKUP-INVALID-MAP");
        return cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId)??
            throw new UnauthorizedAccessException("UM-BACKUP-MAP-NOT-LOCAL");
    }
    private void Stopped(LocalMap map)
    {
        var state=inspect(map);
        if(!state.ConfirmedStopped||state.Running||
           state.Identity!=null||state.LoaderIdentity!=null)
            throw new InvalidOperationException("UM-BACKUP-CONFIRMED-STOP-REQUIRED");
    }
    private void UniqueInstallation(LocalMap map)
    {
        if(cluster.Config.LocalMaps.Count(m=>string.Equals(
            Path.GetFullPath(m.InstallationPath).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(map.InstallationPath).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))!=1)
            throw new InvalidOperationException("UM-BACKUP-SHARED-INSTALLATION-AMBIGUOUS");
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Fail(null,"UM-BACKUP-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||
               !root.TryGetProperty("id",out var rid)||rid.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(rid.GetString(),out var request)||request==Guid.Empty||
               !root.TryGetProperty("method",out var methodRaw)||
               methodRaw.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-BACKUP-INVALID-MESSAGE");
            id=request.ToString("D");
            string? method=methodRaw.GetString();
            if(method is not ("backup.history" or "backup.preview" or
                "backup.create" or "backup.restorePreview" or "backup.restore"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="backup.history")
                {
                    if(!root.TryGetProperty("mapId",out var m)||
                       m.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(m.GetString(),out var mid))
                        return Fail(id,"UM-BACKUP-INVALID-MAP");
                    var map=Owned(mid);
                    var points=await Task.Run(()=>KovaCompatibleWorldBackup.List(
                        map,cluster.Store.Root));
                    return Ok(id,new{
                        mapId=map.Id,mapName=map.Name,
                        points=points.Select(x=>new{
                            id=x.Id,createdUtc=x.CreatedUtc,kind=x.Kind,
                            files=x.Files,bytes=x.Bytes,characters=x.Characters,tribes=x.Tribes,
                            verified=true
                        }).ToArray(),
                        source="LOCAL_SAVEDARKS_MAP_FOLDER",
                        storeWriterTouched=false,
                        evidence="REAL_SHA256_BACKUP_PACKAGES"
                    });
                }
                if(method is "backup.preview" or "backup.restorePreview")
                {
                    pending=null;
                    if(!root.TryGetProperty("mapId",out var m)||
                       m.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(m.GetString(),out var mid))
                        return Fail(id,"UM-BACKUP-INVALID-MAP");
                    var map=Owned(mid);
                    UniqueInstallation(map);
                    Stopped(map);
                    var key=KovaCompatibleWorldBackup.MapKey(map);
                    string point="";
                    if(method=="backup.restorePreview")
                    {
                        if(map.HistoricalStoreProtected||
                           (map.Arguments+" "+map.ApiLoaderArguments).Contains(
                               "store",StringComparison.OrdinalIgnoreCase))
                            return Fail(id,"UM-BACKUP-STORE-V1-RESTORE-LOCKED");
                        if(!root.TryGetProperty("point",out var rawPoint)||
                           rawPoint.ValueKind!=JsonValueKind.String||
                           !Guid.TryParse(rawPoint.GetString(),out var parsedPoint)||
                           parsedPoint==Guid.Empty)
                            return Fail(id,"UM-BACKUP-INVALID-POINT");
                        point=parsedPoint.ToString("D");
                        await Task.Run(()=>KovaCompatibleWorldBackup.Verify(
                            map,cluster.Store.Root,point));
                    }
                    else
                    {
                        // Read the actual saved world file, not a dashboard
                        // state inferred from the presence of a process.
                        string world=Path.Combine(map.InstallationPath,"ShooterGame",
                            "Saved","SavedArks",key,key+".ark");
                        if(!File.Exists(world)||new FileInfo(world).Length==0)
                            return Fail(id,"UM-BACKUP-SAVED-WORLD-MISSING");
                    }
                    pending=new(Guid.NewGuid(),map.Id,point,map.InstallationPath,
                        map.Arguments,DateTimeOffset.UtcNow.AddMinutes(5),
                        method=="backup.restorePreview");
                    return Ok(id,new{
                        token=pending.Token,mapId=map.Id,mapName=map.Name,
                        mapKey=key,point=string.IsNullOrEmpty(point)?null:point,
                        confirmedStopped=true,
                        willCreateProtectiveBackup=method=="backup.restorePreview",
                        originalWorldRetained=method=="backup.restorePreview",
                        storeWriterTouched=false,
                        action=method=="backup.restorePreview"?"RESTORE":"BACKUP",
                        evidence="NATIVE_STOPPED_WORLD_BACKUP_PREFLIGHT"
                    });
                }
                // Both mutating operations require a short-lived, single-use
                // preview ticket plus explicit confirmation, never a path.
                if(!root.TryGetProperty("token",out var tokenRaw)||
                   tokenRaw.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(tokenRaw.GetString(),out var token)||
                   !root.TryGetProperty("confirm",out var confirmed)||
                   confirmed.ValueKind!=JsonValueKind.True||
                   pending==null||pending.Token!=token)
                    return Fail(id,"UM-BACKUP-EXPLICIT-APPROVAL-REQUIRED");
                Ticket ticket=pending;pending=null;
                if(ticket.Until<DateTimeOffset.UtcNow ||
                   ticket.Restore!=(method=="backup.restore"))
                    return Fail(id,"UM-BACKUP-PREVIEW-EXPIRED");
                var current=Owned(ticket.MapId);
                if(current.InstallationPath!=ticket.Root||
                   current.Arguments!=ticket.Arguments)
                    return Fail(id,"UM-BACKUP-MAP-CONFIG-CHANGED");
                UniqueInstallation(current);
                Stopped(current);
                if(ticket.Restore)
                {
                    if(!root.TryGetProperty("mapName",out var chosenName)||
                       chosenName.ValueKind!=JsonValueKind.String||
                       chosenName.GetString()!=current.Name)
                        return Fail(id,"UM-BACKUP-MAP-NAME-CONFIRMATION-REQUIRED");
                    var receipt=await Task.Run(()=>KovaCompatibleWorldBackup.Restore(
                        current,cluster.Store.Root,ticket.Point,()=>inspect(current)));
                    return Ok(id,new{
                        mapId=current.Id,mapName=current.Name,
                        point=receipt.Point,protectiveBackup=receipt.ProtectiveBackup,
                        restored=receipt.Restored,
                        oldWorldRetained=receipt.OldWorldRetained,
                        worldRestoreVerified=true,
                        storeWriterTouched=false,
                        evidence="VERIFIED_STOPPED_MAP_KOVA_STYLE_RESTORE_WITH_PROTECTIVE_COPY"
                    });
                }
                var created=await Task.Run(()=>KovaCompatibleWorldBackup.Create(
                    current,cluster.Store.Root,()=>inspect(current)));
                return Ok(id,new{
                    mapId=current.Id,mapName=current.Name,
                    point=created.Id,createdUtc=created.CreatedUtc,
                    files=created.Files,bytes=created.Bytes,
                    characters=created.Characters,tribes=created.Tribes,
                    backupVerified=true,
                    asaSaveCompleteProven=false,
                    storeWriterTouched=false,
                    evidence="REAL_VERIFIED_STOPPED_MAP_WORLD_BACKUP"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception e) when(e is IOException or JsonException or
            UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or ArgumentException or OverflowException)
        {
            return Fail(id,"UM-BACKUP-NATIVE-SAFETY-CHECK-FAILED");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
}
