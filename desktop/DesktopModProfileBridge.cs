using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>Portable MOD-ONLY profile exchange. File paths are selected by
/// native Windows dialogs, never supplied through untrusted WebView2 messages.
/// Import mutates local Manager config only, after a single explicit approval.
/// </summary>
public sealed class DesktopModProfileBridge : IDisposable
{
    private sealed record Approval(Guid Token,string Scope,PortableModImportPlan Plan,DateTimeOffset At);
    private readonly ClusterService cluster;
    private readonly Func<string,string?> savePicker;
    private readonly Func<string?> openPicker;
    private readonly SemaphoreSlim serial=new(1,1);
    // Separate target sets retain independent short-lived import approvals.
    private readonly Dictionary<string,Approval> pendingByScope=new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public DesktopModProfileBridge(ClusterService owner,
        Func<string,string?>? nativeSave=null,Func<string?>? nativeOpen=null)
    {
        cluster=owner??throw new ArgumentNullException(nameof(owner));
        savePicker=nativeSave??PickSave;
        openPicker=nativeOpen??PickOpen;
    }
    private static string? PickSave(string label)
    {
        using var box=new SaveFileDialog{
            Title="Mentés: "+label+" - csak mod ID-k és sorrend",
            Filter="Untamed modprofil (*.json)|*.json",DefaultExt="json",
            AddExtension=true,OverwritePrompt=true
        };
        return box.ShowDialog()==DialogResult.OK?box.FileName:null;
    }
    private static string? PickOpen()
    {
        using var box=new OpenFileDialog{
            Title="Másik Manager modprofiljának kiválasztása",
            Filter="Untamed modprofil (*.json)|*.json",CheckFileExists=true,
            Multiselect=false
        };
        return box.ShowDialog()==DialogResult.OK?box.FileName:null;
    }
    private static string FixedExternal(string name,bool existing)
    {
        if(string.IsNullOrWhiteSpace(name)||!Path.IsPathFullyQualified(name)||
           name.StartsWith(@"\\",StringComparison.Ordinal)||
           !name.EndsWith(".json",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Portable profile must use a local .json file");
        string path=Path.GetFullPath(name);
        if(new DriveInfo(Path.GetPathRoot(path)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Portable profile requires a fixed local disk");
        for(var node=new DirectoryInfo(Path.GetDirectoryName(path)!);node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked profile folder not allowed");
        if(existing && (!File.Exists(path)||
           (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||
           new FileInfo(path).Length is <=0 or >100000))
            throw new InvalidDataException("Portable profile file missing/oversized/linked");
        return path;
    }
    private void RequireExternal(string path)
    {
        foreach(var map in cluster.Config.LocalMaps)
        {
            if(string.IsNullOrWhiteSpace(map.InstallationPath))continue;
            string root=Path.GetFullPath(map.InstallationPath)
                .TrimEnd(Path.DirectorySeparatorChar);
            if(path.StartsWith(root+Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    "Portable profile may not reside inside a real ASA installation");
        }
        string config=Path.GetFullPath(cluster.Store.Root)
            .TrimEnd(Path.DirectorySeparatorChar);
        if(path.StartsWith(config+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Portable profile cannot reside in Manager config");
    }
    private void DemandHistoricalTargetsStopped(PortableModImportPlan plan)
    {
        foreach(var target in plan.Targets)
        {
            var map=cluster.Config.LocalMaps.SingleOrDefault(
                m=>m.Id==target.MapId)??throw new UnauthorizedAccessException(
                    "UM-MODPROFILE-LOCAL-MAP-CHANGED");
            if(map.HistoricalStoreProtected)
                HistoricalLocalConfigGuard.DemandStopped(cluster,map);
        }
    }
    private static bool GuidField(JsonElement element,string key,out Guid value)
    {
        value=Guid.Empty;
        return element.TryGetProperty(key,out var input) &&
            input.ValueKind==JsonValueKind.String &&
            Guid.TryParse(input.GetString(),out value)&&value!=Guid.Empty;
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >5000)return Fail(null,"UM-MODPROFILE-INVALID-REQUEST");
            using var document=JsonDocument.Parse(raw);
            var data=document.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !GuidField(data,"id",out var requestId)||
               !data.TryGetProperty("method",out var route) ||
               route.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-MODPROFILE-INVALID-REQUEST");
            id=requestId.ToString("D");
            string? method=route.GetString();
            if(method is not ("mods.profileExport" or "mods.profileImportPreview" or
                "mods.profileImportApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                foreach(var expired in pendingByScope.Where(x=>
                    DateTimeOffset.UtcNow-x.Value.At>TimeSpan.FromMinutes(5))
                    .Select(x=>x.Key).ToArray())pendingByScope.Remove(expired);
                if(method=="mods.profileExport")
                {
                    if(!GuidField(data,"mapId",out var mapId))
                        return Fail(id,"UM-MODPROFILE-INVALID-MAP");
                    var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId)
                        ??throw new UnauthorizedAccessException("Unowned map");
                    if(map.Maintenance||map.ObservedReadOnlyImported||
                       (!map.HistoricalStoreProtected&&
                        UntamedWorldBackup.HasLiveOrHistoricalStore(map))||
                       (map.HistoricalStoreProtected&&(
                           string.IsNullOrWhiteSpace(map.HistoricalMapKey)||
                           map.HistoricalArchiveSha256?.Length!=64||
                           !map.HistoricalArchiveSha256.All(Uri.IsHexDigit)||
                           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
                           !string.IsNullOrWhiteSpace(map.Arguments)||
                           !string.IsNullOrWhiteSpace(map.ApiLoaderArguments))))
                        return Fail(id,"UM-MODPROFILE-SOURCE-NOT-OWNED");
                    var profile=PortableModProfileCodec.FromMap(map,map.Name);
                    var path=savePicker(map.Name);
                    if(string.IsNullOrWhiteSpace(path))
                        return Fail(id,"UM-MODPROFILE-CANCELLED");
                    path=FixedExternal(path,false);RequireExternal(path);
                    if(File.Exists(path))return Fail(id,"UM-MODPROFILE-ALREADY-EXISTS");
                    byte[] content=Encoding.UTF8.GetBytes(
                        PortableModProfileCodec.Serialize(profile));
                    if(content.Length>100000)throw new InvalidDataException("Oversized mod profile");
                    string tmp=path+".untamed-"+Guid.NewGuid().ToString("N")+".tmp";
                    try
                    {
                        using(var outFile=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {outFile.Write(content);outFile.Flush(true);}
                        File.Move(tmp,path,false);
                    }
                    finally{if(File.Exists(tmp))File.Delete(tmp);}
                    return Ok(id,new{exported=true,mapId=map.Id,
                        profileName=Path.GetFileName(path),enabled=profile.EnabledOrder.Length,
                        passive=profile.Passive.Length,disabled=profile.Disabled.Length,
                        sha256=profile.Sha256,remoteWritesAllowed=false,
                        storeTouched=false,sqlUsed=false,
                        evidence="MOD_IDS_ONLY_NATIVE_LOCAL_FILE_EXPORT"});
                }
                if(method=="mods.profileImportPreview")
                {
                    if(!data.TryGetProperty("targets",out var targets)||
                       targets.ValueKind!=JsonValueKind.Array||
                       targets.GetArrayLength() is <1 or >25||
                       !data.TryGetProperty("mode",out var modeField)||
                       modeField.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-MODPROFILE-INVALID-TARGETS");
                    var mode=modeField.GetString() switch{
                        "merge"=>LocalModCopyMode.Merge,
                        "replace"=>LocalModCopyMode.Replace,
                        _=>throw new InvalidDataException("Invalid profile mode")
                    };
                    var ids=targets.EnumerateArray().Select(x=>
                        x.ValueKind==JsonValueKind.String&&
                        Guid.TryParse(x.GetString(),out var guid)?guid:Guid.Empty).ToArray();
                    if(ids.Any(x=>x==Guid.Empty))return Fail(id,"UM-MODPROFILE-INVALID-TARGETS");
                    string? path=openPicker();
                    if(string.IsNullOrWhiteSpace(path))
                        return Fail(id,"UM-MODPROFILE-CANCELLED");
                    path=FixedExternal(path,true);RequireExternal(path);
                    string content=File.ReadAllText(path,Encoding.UTF8);
                    var profile=PortableModProfileCodec.Parse(content);
                    var plan=PortableModProfileImporter.Preview(cluster,profile,ids,mode);
                    DemandHistoricalTargetsStopped(plan);
                    string scope=string.Join("|",plan.Targets.Select(t=>
                        t.MapId.ToString("N")).OrderBy(x=>x,StringComparer.Ordinal));
                    if(!pendingByScope.ContainsKey(scope)&&pendingByScope.Count>=25)
                        return Fail(id,"UM-MODPROFILE-PREVIEW-CAPACITY");
                    var previewApproval=new Approval(Guid.NewGuid(),scope,plan,DateTimeOffset.UtcNow);
                    pendingByScope[scope]=previewApproval;
                    return Ok(id,new{token=previewApproval.Token,label=profile.Label,
                        sha256=profile.Sha256,mode=modeField.GetString(),
                        enabled=profile.EnabledOrder.Length,
                        passive=profile.Passive.Length,
                        disabled=profile.Disabled.Length,
                        targets=plan.Targets.Select(t=>new {
                            mapId=t.MapId,mapName=t.Name,changed=t.Changed,
                            before=new{active=t.Before.Active,passive=t.Before.Passive,
                                disabled=t.Before.Disabled},
                            after=new{active=t.After.Active,passive=t.After.Passive,
                                disabled=t.After.Disabled}
                        }).ToArray(),
                        operationCount=plan.Edits.Count,changed=plan.Changed,
                        productionLoadedVerified=false,remoteWritesAllowed=false,
                        storeTouched=false,sqlUsed=false,
                        evidence="PORTABLE_PROFILE_IMPORT_PREVIEW_NO_WRITES"});
                }
                if(!GuidField(data,"token",out var approval)||
                   !data.TryGetProperty("confirm",out var confirm)||
                   confirm.ValueKind!=JsonValueKind.True ||
                   !pendingByScope.Values.Any(p=>p.Token==approval))
                    return Fail(id,"UM-MODPROFILE-APPROVAL-REQUIRED");
                var approved=pendingByScope.Values.Single(p=>p.Token==approval);
                pendingByScope.Remove(approved.Scope); // One-shot for this exact target set.
                if(DateTimeOffset.UtcNow-approved.At>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-MODPROFILE-PREVIEW-EXPIRED");
                if(!approved.Plan.Changed)
                    return Fail(id,"UM-MODPROFILE-NOTHING-TO-APPLY");
                DemandHistoricalTargetsStopped(approved.Plan);
                var applied=PortableModProfileImporter.Apply(cluster,approved.Plan);
                return Ok(id,new{saved=true,
                    changedMaps=approved.Plan.Targets.Where(t=>t.Changed)
                        .Select(t=>t.MapId).ToArray(),
                    operationsApplied=applied.Count,requiresRestart=true,
                    productionLoadedVerified=false,remoteWritesAllowed=false,
                    storeTouched=false,sqlUsed=false,
                    evidence="LOCAL_ATOMIC_PROFILE_IMPORT_PENDING_RESTART"});
            }
            finally{serial.Release();}
        }
        catch(Exception){return Fail(id,"UM-MODPROFILE-SAFETY-CHECK-FAILED");}
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>serial.Dispose();
}
