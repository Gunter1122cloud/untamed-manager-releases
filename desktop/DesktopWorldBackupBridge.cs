using System;
using System.IO;
using System.Collections.Generic;
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
    private readonly DesktopScheduledStoppedBackupSupervisor? scheduled;
    private readonly SemaphoreSlim serial=new(1,1);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Ticket(Guid Token,Guid MapId,string Point,string Root,
        string Arguments,DateTimeOffset Until,bool Restore);
    private Ticket? pending;
    private sealed record OriginalBatchTicket(Guid Token,string FourFilesSha,
        string LaunchProfileSha,Guid[] MapIds,DateTimeOffset ExpiresUtc);
    private OriginalBatchTicket? originalBatchPending;
    private readonly Func<LegacyStoreV1OriginalLocalSelection> originalSelection;

    public DesktopWorldBackupBridge(ClusterService cluster,
        Func<LocalMap,ProcessCheck>? processInspector=null,
        DesktopScheduledStoppedBackupSupervisor? scheduledBackups=null,
        Func<LegacyStoreV1OriginalLocalSelection>? originalSelectionProvider=null)
    {
        this.cluster=cluster??throw new ArgumentNullException(nameof(cluster));
        // Historical Store-v1 imports deliberately retain empty Arguments.
        // The generic fresh-map family inspector can misidentify an old Kova
        // process as stopped. Use the dedicated original ASA/Loader observer
        // for every manual backup preflight and immediately before copy.
        inspect=processInspector??(map=>map.HistoricalStoreProtected?
            cluster.ObserveLocalProcessesReadOnly(map):
            new WindowsProcessInspector().InspectFamily(map));
        scheduled=scheduledBackups;
        originalSelection=originalSelectionProvider??(()=>
            LegacyStoreV1OriginalImport.Inspect(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Kova")));
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
    private void UniqueWorldDirectory(LocalMap map)=>
        AsaSaveLayoutResolver.DemandUniqueOwner(map,cluster.Config.LocalMaps);
    // EXACT historical 5-map Kova ownership is a special legacy source.
    // The overall Manager still supports N hosts and arbitrary map counts.
    // This only aggregates existing per-map SHA-256 backup operations and
    // never touches the shared Kova Store-v1 writer or creates another engine.
    private (LegacyStoreV1OriginalLocalSelection Source,LocalMap[] Maps)
        RequireOriginalStoppedFive()
    {
        var source=originalSelection();
        if(source?.Maps==null||source.Maps.Length!=5||
           source.FourFilesSha256.Length!=64||
           source.LaunchProfileSha256.Length!=64||
           source.Maps.Select(x=>x.MapId).Distinct().Count()!=5)
            throw new InvalidDataException("UM-BACKUP-ORIGINAL-FIVE-SOURCE-MISSING");
        // A historical Kova import is a five-map selection, NOT a five-map
        // capacity limit for the Manager. Additional independent Store maps
        // must not hide or prevent backups of these five original GUIDs.
        var historical=source.Maps.Select(sourceMap=>
            cluster.Config.LocalMaps.SingleOrDefault(m=>
                m.HistoricalStoreProtected&&m.Id==sourceMap.MapId)
            ??throw new InvalidDataException(
                "UM-BACKUP-ORIGINAL-FIVE-IMPORTED-REQUIRED")).ToArray();
        var maps=new List<LocalMap>(5);
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var original in source.Maps)
        {
            LocalMap map=historical.SingleOrDefault(m=>m.Id==original.MapId)
                ??throw new InvalidDataException("UM-BACKUP-ORIGINAL-MAP-NOT-IMPORTED");
            if(!string.Equals(map.HistoricalMapKey,original.MapKey,
                    StringComparison.Ordinal)||
               !string.Equals(map.HistoricalArchiveSha256,source.FourFilesSha256,
                    StringComparison.Ordinal)||
               !string.Equals(Path.GetFullPath(map.InstallationPath),
                    Path.GetFullPath(original.OriginalInstallationRoot),
                    StringComparison.OrdinalIgnoreCase)||
               map.GamePort!=original.GamePort||map.QueryPort!=original.QueryPort||
               map.HistoricalEnabledMods?.SequenceEqual(original.EnabledMods,
                    StringComparer.Ordinal)!=true||
               map.HistoricalPassiveMods?.SequenceEqual(original.PassiveMods,
                    StringComparer.Ordinal)!=true)
                throw new InvalidDataException("UM-BACKUP-ORIGINAL-SOURCE-OR-PORTS-CHANGED");
            if(!roots.Add(Path.GetFullPath(map.InstallationPath)))
                throw new InvalidDataException("UM-BACKUP-ORIGINAL-DUPLICATE-ROOT");
            UniqueWorldDirectory(map);
            Stopped(map);
            if(KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
                throw new InvalidOperationException("UM-BACKUP-ORIGINAL-RESTORE-RECOVERY");
            string world=AsaSaveLayoutResolver.Resolve(map).WorldFile;
            if(!File.Exists(world)||new FileInfo(world).Length==0)
                throw new InvalidDataException("UM-BACKUP-ORIGINAL-WORLD-MISSING");
            maps.Add(map);
        }
        return (source,maps.ToArray());
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
                "backup.create" or "backup.restorePreview" or "backup.restore" or
                "backup.scheduleStatus" or "backup.scheduleCreate" or
                "backup.scheduleDisable" or
                "backup.originalBatchPreview" or "backup.originalBatchCreate"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="backup.originalBatchPreview")
                {
                    originalBatchPending=null;
                    var (original,maps)=RequireOriginalStoppedFive();
                    long worldBytes=0;
                    foreach(var map in maps)
                        worldBytes=checked(worldBytes+
                            new FileInfo(AsaSaveLayoutResolver.Resolve(map).WorldFile).Length);
                    var ticket=new OriginalBatchTicket(Guid.NewGuid(),
                        original.FourFilesSha256,original.LaunchProfileSha256,
                        maps.Select(x=>x.Id).ToArray(),DateTimeOffset.UtcNow.AddMinutes(5));
                    originalBatchPending=ticket;
                    return Ok(id,new{
                        token=ticket.Token,localMaps=5,worldBytes,
                        mapNames=maps.Select(x=>x.Name).ToArray(),
                        originalSourcePinned=true,allMapsConfirmedStopped=true,
                        requiresOperatorConfirmation=true,
                        originalKovaFilesWritten=false,serverStarted=false,
                        sharedTransferIncluded=false,otherHostIncluded=false,
                        evidence="EXISTING_SHA256_WORLD_ENGINE_ORIGINAL_FIVE_MAP_PREFLIGHT"
                    });
                }
                if(method=="backup.originalBatchCreate")
                {
                    if(!root.TryGetProperty("token",out var originalToken)||
                       originalToken.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(originalToken.GetString(),out var suppliedToken)||
                       !root.TryGetProperty("confirm",out var batchConsent)||
                       batchConsent.ValueKind!=JsonValueKind.True||
                       originalBatchPending?.Token!=suppliedToken)
                        return Fail(id,"UM-BACKUP-ORIGINAL-FIVE-APPROVAL-REQUIRED");
                    var approved=originalBatchPending!;
                    originalBatchPending=null; // consume on every attempt
                    if(approved.ExpiresUtc<DateTimeOffset.UtcNow)
                        return Fail(id,"UM-BACKUP-ORIGINAL-FIVE-APPROVAL-EXPIRED");
                    var (currentOriginal,maps)=RequireOriginalStoppedFive();
                    if(approved.FourFilesSha!=currentOriginal.FourFilesSha256||
                       approved.LaunchProfileSha!=currentOriginal.LaunchProfileSha256||
                       !approved.MapIds.SequenceEqual(maps.Select(m=>m.Id)))
                        return Fail(id,"UM-BACKUP-ORIGINAL-FIVE-SOURCE-CHANGED");
                    // Keep running actual existing Core.Create; never invent
                    // success from a detached job or mock receipt. Completed
                    // individual map packages remain durable if a later map
                    // fails, and no shared transfer/session bytes are touched.
                    var created=await Task.Run(()=>
                    {
                        var points=new List<(Guid MapId,string Name,
                            KovaCompatibleWorldBackup.Receipt Point)>();
                        foreach(var map in maps)
                        {
                            Stopped(map);
                            var point=KovaCompatibleWorldBackup.Create(
                                map,cluster.Store.Root,()=>inspect(map));
                            points.Add((map.Id,map.Name,point));
                        }
                        return points;
                    });
                    var (afterSource,afterMaps)=RequireOriginalStoppedFive();
                    if(afterSource.FourFilesSha256!=approved.FourFilesSha||
                       afterSource.LaunchProfileSha256!=approved.LaunchProfileSha||
                       !afterMaps.Select(m=>m.Id).SequenceEqual(approved.MapIds))
                        throw new IOException("UM-BACKUP-ORIGINAL-FIVE-SOURCE-CHANGED-AFTER-COPY");
                    return Ok(id,new{
                        localMaps=created.Count,
                        points=created.Select(x=>new{
                            mapId=x.MapId,mapName=x.Name,point=x.Point.Id,
                            files=x.Point.Files,bytes=x.Point.Bytes
                        }).ToArray(),
                        totalBytes=created.Sum(x=>x.Point.Bytes),
                        allFiveVerified=created.Count==5,
                        originalKovaFilesWritten=false,serverStarted=false,
                        sharedTransferIncluded=false,otherHostIncluded=false,
                        evidence="FIVE_VERIFIED_EXISTING_PER_MAP_WORLD_BACKUPS_NO_SHARED_QUORUM"
                    });
                }
                if(method is "backup.scheduleStatus" or "backup.scheduleCreate" or
                    "backup.scheduleDisable")
                {
                    if(scheduled==null)
                        return Fail(id,"UM-BACKUP-SCHEDULER-UNAVAILABLE");
                    if(!root.TryGetProperty("mapId",out var scheduledMap)||
                       scheduledMap.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(scheduledMap.GetString(),out var scheduledId)||
                       scheduledId==Guid.Empty)
                        return Fail(id,"UM-BACKUP-INVALID-MAP");
                    var owned=Owned(scheduledId);
                    if(method=="backup.scheduleCreate")
                    {
                        if(!root.TryGetProperty("confirm",out var approval)||
                           approval.ValueKind!=JsonValueKind.True||
                           !root.TryGetProperty("intervalMinutes",out var interval)||
                           interval.ValueKind!=JsonValueKind.Number||
                           !interval.TryGetInt32(out int minutes))
                            return Fail(id,"UM-BACKUP-EXPLICIT-APPROVAL-REQUIRED");
                        scheduled.Configure(owned.Id,minutes);
                    }
                    else if(method=="backup.scheduleDisable")
                    {
                        if(!root.TryGetProperty("confirm",out var approval)||
                           approval.ValueKind!=JsonValueKind.True)
                            return Fail(id,"UM-BACKUP-EXPLICIT-APPROVAL-REQUIRED");
                        scheduled.Disable(owned.Id);
                    }
                    var row=scheduled.Snapshot().SingleOrDefault(x=>x.MapId==owned.Id);
                    return Ok(id,new{
                        mapId=owned.Id,mapName=owned.Name,
                        enabled=row?.Enabled??false,
                        intervalMinutes=row?.IntervalMinutes,
                        nextDueUtc=row?.NextDueUtc,
                        lastAttemptUtc=row?.LastAttemptUtc,
                        lastStatus=row?.LastStatus??"not-configured",
                        lastPoint=row?.LastPoint,
                        runnerState=scheduled.RuntimeState,
                        storeProtected=KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(owned),
                        automaticRestoreAllowed=false,
                        automaticServerStopAllowed=false,
                        evidence="PERSISTED_OPT_IN_STOPPED_NONSTORE_WORLD_BACKUPS"
                    });
                }
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
                        recoveryRequired=KovaCompatibleWorldBackup.InstallationRecoveryRequired(map),
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
                    UniqueWorldDirectory(map);
                    Stopped(map);
                    if(KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
                        return Fail(id,"UM-BACKUP-RESTORE-RECOVERY-REQUIRED");
                    var key=KovaCompatibleWorldBackup.MapKey(map);
                    string point="";
                    if(method=="backup.restorePreview")
                    {
                        if(KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map))
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
                        string world=AsaSaveLayoutResolver.Resolve(map).WorldFile;
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
                UniqueWorldDirectory(current);
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
                        // The full on-disk backup digest is restored and the
                        // prior world retained, but only an actual ASA boot +
                        // client join proves gameplay/tribe/survivor recovery.
                        bytesRestoredAndHashesVerified=true,
                        worldRestoreVerified=false,
                        gameClientLoadVerified=false,
                        tribeOwnershipVerified=false,
                        characterRejoinVerified=false,
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
                    embeddedSurvivorCountUnknown=
                        KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(current),
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
