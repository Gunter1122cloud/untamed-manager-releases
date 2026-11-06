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
        string Zip,string ZipSha256,string Plugin,DateTimeOffset Created);
    private readonly ClusterService cluster;
    private readonly Func<string?> pick;
    private readonly SemaphoreSlim gate=new(1,1);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private Pending? pending;
    public DesktopPluginZipBridge(ClusterService service,Func<string?>? picker=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        pick=picker??PickNativeZip;
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
    private void EnsureStopped(LocalMap map)
    {
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        if(map.HistoricalStoreProtected||map.Maintenance||
           KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map))
            throw new InvalidOperationException("UM-PLUGIN-STORE-PROTECTED");
        string root=Path.GetFullPath(map.InstallationPath);
        var siblings=cluster.Config.LocalMaps.Where(m=>
            !string.IsNullOrWhiteSpace(m.InstallationPath)&&
            string.Equals(Path.GetFullPath(m.InstallationPath),root,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if(siblings.Length==0)
            throw new InvalidOperationException("UM-PLUGIN-INSTALLATION-MISSING");
        foreach(var sibling in siblings)
        {
            var process=cluster.Check(sibling);
            if(sibling.HistoricalStoreProtected||sibling.Maintenance||
               process.Running||!process.ConfirmedStopped||
               process.Identity!=null||process.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-PLUGIN-SHARED-INSTALLATION-ACTIVE");
        }
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
            if(method is not ("plugins.zipSelect" or "plugins.zipApply"))
                return Error(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="plugins.zipSelect")
                {
                    pending=null;
                    if(!TryId(args,"mapId",out var mapId))
                        return Error(id,"UM-PLUGIN-ZIP-MAP");
                    var map=Get(mapId);
                    if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                    EnsureStopped(map);
                    string? selected=pick();
                    if(string.IsNullOrWhiteSpace(selected))
                        return Error(id,"UM-PLUGIN-ZIP-NOT-SELECTED");
                    string full=Path.GetFullPath(selected);
                    if(full.StartsWith(Path.GetFullPath(map.InstallationPath)+
                        Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                        return Error(id,"UM-PLUGIN-ZIP-WITHIN-INSTALLATION");
                    string digest=Digest(full);
                    var preview=SafePluginZip.Preview(map,cluster.Check(map),full,BackupRoot);
                    if(!preview.Ready)
                        return Error(id,"UM-PLUGIN-ZIP-PREFLIGHT-DENIED");
                    pending=new(Guid.NewGuid(),map.Id,map.InstallationPath,full,
                        digest,preview.PluginName,DateTimeOffset.UtcNow);
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
                       approved.Installation,StringComparison.OrdinalIgnoreCase))
                    return Error(id,"UM-PLUGIN-ZIP-MAP-CHANGED");
                EnsureStopped(target);
                if(Digest(approved.Zip)!=approved.ZipSha256)
                    return Error(id,"UM-PLUGIN-ZIP-CHANGED");
                var updated=SafePluginZip.InstallGuarded(cluster,target.Id,
                    approved.Zip,BackupRoot);
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
