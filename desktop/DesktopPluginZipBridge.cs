using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Real guarded local plugin ZIP installer through a native Windows picker.
/// React never provides a path or sees local credentials. The Core performs
/// ZIP validation, backup, atomic binary update and rollback safety.
/// </summary>
public sealed class DesktopPluginZipBridge : IDisposable
{
    private sealed record Pending(Guid Token,Guid MapId,string Installation,
        string Zip,string ZipSha256,string Plugin,string Profile,DateTimeOffset Created);
    private sealed record RollbackPending(Guid Token,Guid MapId,string Installation,
        string Backup,string Plugin,string Profile,int Binaries,DateTimeOffset Created);
    private readonly ClusterService cluster;
    private readonly Func<string?> pick;
    private readonly Func<string?> pickRollback;
    private readonly SemaphoreSlim gate=new(1,1);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private Pending? pending;
    private RollbackPending? pendingRollback;
    public DesktopPluginZipBridge(ClusterService service,Func<string?>? picker=null,
        Func<string?>? rollbackPicker=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        pick=picker??PickNativeZip;
        pickRollback=rollbackPicker??PickNativeRollback;
    }
    private static string? PickNativeRollback()
    {
        using var dialog=new FolderBrowserDialog{
            Description="Korábbi SHA-256 védett plugin ZIP mentés kiválasztása",
            ShowNewFolderButton=false
        };
        return dialog.ShowDialog()==DialogResult.OK?dialog.SelectedPath:null;
    }
    private static string? PickNativeZip()
    {
        using var dialog=new OpenFileDialog{
            Title="Untamed Kingdom – ASA API plugin ZIP kiválasztása",
            Filter="Plugin ZIP csomag (*.zip)|*.zip",
            CheckFileExists=true,Multiselect=false,RestoreDirectory=true
        };
        return dialog.ShowDialog()==DialogResult.OK?dialog.FileName:null;
    }
    private LocalMap? Get(Guid mapId)=>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
    private static string Digest(string zip)
    {
        string path=Path.GetFullPath(zip);
        if(!Path.IsPathFullyQualified(zip)||path.StartsWith(@"\\",StringComparison.Ordinal) ||
           !path.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-PLUGIN-ZIP-PATH");
        var info=new FileInfo(path);
        if(!info.Exists || info.Length is <64 or >268435456 ||
           (info.Attributes&FileAttributes.ReparsePoint)!=0 ||
           new DriveInfo(Path.GetPathRoot(path)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-PLUGIN-ZIP-FILE");
        for(DirectoryInfo? parent=info.Directory;parent!=null;parent=parent.Parent)
            if(parent.Exists&&(parent.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-PLUGIN-ZIP-LINK");
        using var stream=File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private ProcessCheck EnsureStopped(LocalMap map)
    {
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        if(map.ObservedReadOnlyImported||map.Maintenance||
           (!map.HistoricalStoreProtected&&LegacyCompatibleWorldBackup.HasLiveOrHistoricalStore(map)))
            throw new InvalidOperationException("UM-PLUGIN-STORE-PROTECTED");
        string root=Path.GetFullPath(map.InstallationPath);
        var siblings=cluster.Config.LocalMaps.Where(m=>
            !string.IsNullOrWhiteSpace(m.InstallationPath)&&
            string.Equals(Path.GetFullPath(m.InstallationPath),root,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if(siblings.Length==0)
            throw new InvalidOperationException("UM-PLUGIN-INSTALLATION-MISSING");
        ProcessCheck? selected=null;
        foreach(var sibling in siblings)
        {
            var generic=cluster.Check(sibling);
            if(sibling.ObservedReadOnlyImported||sibling.Maintenance||
               generic.Running||generic.Identity!=null||generic.LoaderIdentity!=null||
               (!sibling.HistoricalStoreProtected&&
                LegacyCompatibleWorldBackup.HasLiveOrHistoricalStore(sibling)))
                throw new InvalidOperationException("UM-PLUGIN-SHARED-INSTALLATION-ACTIVE");
            var process=sibling.HistoricalStoreProtected?
                HistoricalLocalConfigGuard.DemandStopped(cluster,sibling):generic;
            if(process.Running||!process.ConfirmedStopped||
               process.Identity!=null||process.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-PLUGIN-SHARED-INSTALLATION-ACTIVE");
            if(ReferenceEquals(sibling,map))selected=process;
        }
        return selected??throw new UnauthorizedAccessException("UM-PLUGIN-MAP-OWNERSHIP-CHANGED");
    }
    private string BackupRoot=>Path.Combine(cluster.Store.Root,"plugin-zip-backups");
    private static bool TryId(JsonElement root,string field,out Guid id)
    {
        id=Guid.Empty;
        return root.TryGetProperty(field,out var value) &&
               value.ValueKind==JsonValueKind.String &&
               Guid.TryParse(value.GetString(),out id)&&id!=Guid.Empty;
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)return Error(null,"UM-PLUGIN-ZIP-REQUEST");
            using var document=JsonDocument.Parse(raw);
            var args=document.RootElement;
            if(args.ValueKind!=JsonValueKind.Object||!TryId(args,"id",out var requestId)||
               !args.TryGetProperty("method",out var field)||
               field.ValueKind!=JsonValueKind.String)
                return Error(null,"UM-PLUGIN-ZIP-REQUEST");
            id=requestId.ToString("D");
            string? method=field.GetString();
            if(method is not ("plugins.zipSelect" or "plugins.zipApply" or
                "plugins.zipRollbackSelect" or "plugins.zipRollbackApply"))
                return Error(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="plugins.zipRollbackSelect")
                {
                    pendingRollback=null;
                    if(!TryId(args,"mapId",out var requestedMap))
                        return Error(id,"UM-PLUGIN-ZIP-ROLLBACK-MAP");
                    var owned=Get(requestedMap);
                    if(owned==null)return Error(id,"UM-MAP-NOT-OWNED");
                    var stopped=EnsureStopped(owned);
                    string? chosen=pickRollback();
                    if(string.IsNullOrWhiteSpace(chosen))
                        return Error(id,"UM-PLUGIN-ZIP-ROLLBACK-NOT-SELECTED");
                    string full=Path.GetFullPath(chosen);
                    string mapRoot=Path.GetFullPath(Path.Combine(
                        BackupRoot,owned.Id.ToString("N")));
                    if(!full.StartsWith(mapRoot+Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                        return Error(id,"UM-PLUGIN-ZIP-ROLLBACK-FOREIGN-BACKUP");
                    string[] parts=Path.GetRelativePath(mapRoot,full).Split(
                        Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
                    if(parts.Length!=2||string.IsNullOrWhiteSpace(parts[0])||
                       string.IsNullOrWhiteSpace(parts[1]))
                        return Error(id,"UM-PLUGIN-ZIP-ROLLBACK-BACKUP-IDENTITY");
                    string plugin=parts[0];
                    var preview=SafePluginZip.PreviewRollback(
                        owned,stopped,plugin,full,BackupRoot);
                    if(!preview.Allowed)
                        return Error(id,"UM-PLUGIN-ZIP-ROLLBACK-PREFLIGHT");
                    pendingRollback=new(Guid.NewGuid(),owned.Id,
                        owned.InstallationPath,full,plugin,
                        JsonSerializer.Serialize(owned),
                        preview.PreviousBinaryCount,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pendingRollback.Token,mapId=owned.Id,
                        mapName=owned.Name,plugin,
                        binaryCount=preview.PreviousBinaryCount,
                        backupName=parts[1],
                        evidence="VERIFIED_OWNED_PLUGIN_ZIP_ROLLBACK_PREVIEW_NO_WRITE"
                    });
                }
                if(method=="plugins.zipRollbackApply")
                {
                    if(!TryId(args,"token",out var requestedToken)||
                       pendingRollback==null||
                       requestedToken!=pendingRollback.Token||
                       DateTimeOffset.UtcNow-pendingRollback.Created>TimeSpan.FromMinutes(5))
                        return Error(id,"UM-PLUGIN-ZIP-ROLLBACK-TOKEN-EXPIRED");
                    if(!args.TryGetProperty("confirm",out var permission)||
                       permission.ValueKind!=JsonValueKind.True)
                        return Error(id,"UM-PLUGIN-ZIP-ROLLBACK-APPROVAL");
                    var rollbackApproved=pendingRollback;
                    pendingRollback=null;
                    var rollbackTarget=Get(rollbackApproved.MapId);
                    if(rollbackTarget==null||
                       !string.Equals(rollbackTarget.InstallationPath,
                           rollbackApproved.Installation,StringComparison.OrdinalIgnoreCase)||
                       JsonSerializer.Serialize(rollbackTarget)!=rollbackApproved.Profile)
                        return Error(id,"UM-PLUGIN-ZIP-ROLLBACK-MAP-CHANGED");
                    var stopped=EnsureStopped(rollbackTarget);
                    var validated=SafePluginZip.PreviewRollback(
                        rollbackTarget,stopped,rollbackApproved.Plugin,rollbackApproved.Backup,BackupRoot);
                    if(!validated.Allowed||
                       validated.PreviousBinaryCount!=rollbackApproved.Binaries)
                        return Error(id,"UM-PLUGIN-ZIP-ROLLBACK-CHANGED");
                    var receipt=SafePluginZip.RollbackGuarded(
                        cluster,rollbackTarget.Id,rollbackApproved.Plugin,rollbackApproved.Backup,BackupRoot,
                        rollbackTarget.HistoricalStoreProtected?
                            member=>HistoricalLocalConfigGuard.DemandStopped(
                                cluster,member,underOwnOperation:true):
                            null);
                    return Ok(id,new{
                        mapId=rollbackTarget.Id,plugin=receipt.PluginName,
                        restoredBinaries=receipt.RestoredBinaryCount,
                        protectiveBackupCreated=true,
                        requiresRestart=true,runtimeLoadedVerified=false,
                        evidence="VERIFIED_OWNED_PLUGIN_ZIP_ROLLBACK"
                    });
                }
                if(method=="plugins.zipSelect")
                {
                    pending=null;
                    if(!TryId(args,"mapId",out var mapId))
                        return Error(id,"UM-PLUGIN-ZIP-MAP");
                    var map=Get(mapId);
                    if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                    var stopped=EnsureStopped(map);
                    string? selected=pick();
                    if(string.IsNullOrWhiteSpace(selected))
                        return Error(id,"UM-PLUGIN-ZIP-NOT-SELECTED");
                    string full=Path.GetFullPath(selected);
                    if(full.StartsWith(Path.GetFullPath(map.InstallationPath)+
                        Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                        return Error(id,"UM-PLUGIN-ZIP-WITHIN-INSTALLATION");
                    string digest=Digest(full);
                    var preview=SafePluginZip.Preview(map,stopped,full,BackupRoot);
                    if(!preview.Ready)
                        return Error(id,"UM-PLUGIN-ZIP-PREFLIGHT-DENIED");
                    pending=new(Guid.NewGuid(),map.Id,map.InstallationPath,full,
                        digest,preview.PluginName,
                        JsonSerializer.Serialize(map),DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pending.Token,mapId=map.Id,mapName=map.Name,
                        archiveName=Path.GetFileName(full),plugin=preview.PluginName,
                        binaryCount=preview.BinaryCount,skippedFiles=preview.SkippedFiles,
                        previousExists=preview.PreviousPluginExists,
                        evidence="NATIVE_PLUGIN_ZIP_PREFLIGHT_NO_WRITE"});
                }
                if(!TryId(args,"token",out var token)||pending==null||
                   token!=pending.Token||DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(5))
                    return Error(id,"UM-PLUGIN-ZIP-TOKEN-EXPIRED");
                if(!args.TryGetProperty("confirm",out var consent)||
                   consent.ValueKind!=JsonValueKind.True)
                    return Error(id,"UM-PLUGIN-ZIP-APPROVAL");
                var approved=pending;pending=null;
                var target=Get(approved.MapId);
                if(target==null||!string.Equals(target.InstallationPath,
                       approved.Installation,StringComparison.OrdinalIgnoreCase)||
                   JsonSerializer.Serialize(target)!=approved.Profile)
                    return Error(id,"UM-PLUGIN-ZIP-MAP-CHANGED");
                EnsureStopped(target);
                if(Digest(approved.Zip)!=approved.ZipSha256)
                    return Error(id,"UM-PLUGIN-ZIP-CHANGED");
                var updated=SafePluginZip.InstallGuarded(cluster,target.Id,
                    approved.Zip,BackupRoot,
                    target.HistoricalStoreProtected?
                        sibling=>HistoricalLocalConfigGuard.DemandStopped(
                            cluster,sibling,underOwnOperation:true):
                        null);
                if(updated.PluginName!=approved.Plugin)
                    throw new InvalidDataException("UM-PLUGIN-ZIP-IDENTITY-CHANGED");
                return Ok(id,new{mapId=target.Id,plugin=updated.PluginName,
                    installedBinaries=updated.BinaryCount,backupCreated=
                        updated.PreviousBackup!=null,
                    requiresRestart=true,runtimeLoadedVerified=false,
                    evidence="VERIFIED_GUARDED_PLUGIN_ZIP_INSTALL"});
            }
            finally{gate.Release();}
        }
        catch(Exception){return Error(id,"UM-PLUGIN-ZIP-FAILED");}
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>gate.Dispose();
}
