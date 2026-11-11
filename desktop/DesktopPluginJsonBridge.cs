using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Native-only edit of a map's existing ArkApi/Plugins/{plugin}/config.json.
/// Supports preview, original encoding, ETag, atomically backed-up apply.
/// Never accepts an arbitrary renderer path or edits a live/Store-v1 server.
/// </summary>
public sealed class DesktopPluginJsonBridge : IDisposable
{
    private sealed class Open
    {
        public required Guid Token {get;init;}
        public required Guid MapId {get;init;}
        public required string Installation {get;init;}
        public required ConfigSnapshot Baseline {get;init;}
        public DateTimeOffset CreatedUtc=DateTimeOffset.UtcNow;
        public string? Edited;
        public ConfigPreview? Preview;
    }
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim gate=new(1,1);
    private Open? opened;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly Regex PluginName=new(
        @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,79}\z",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public DesktopPluginJsonBridge(ClusterService owner)=>
        cluster=owner??throw new ArgumentNullException(nameof(owner));

    private LocalMap RequireOwned(Guid mapId)
    {
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId)
            ??throw new UnauthorizedAccessException("UM-PLUGIN-JSON-NOT-LOCAL");
        if(map.ObservedReadOnlyImported||
           (!map.HistoricalStoreProtected&&UntamedWorldBackup.HasLiveOrHistoricalStore(map))||
           map.Maintenance)
            throw new UnauthorizedAccessException("UM-PLUGIN-JSON-STORE-PROTECTED");
        if(map.HistoricalStoreProtected)
            HistoricalLocalConfigGuard.DemandStopped(cluster,map);
        var journal=new AsaManualRestartJournal(map.InstallationPath);
        journal.DemandNoPending();
        foreach(var sibling in cluster.Config.LocalMaps.Where(m=>
            !string.IsNullOrWhiteSpace(m.InstallationPath) &&
            string.Equals(Path.GetFullPath(m.InstallationPath),
                Path.GetFullPath(map.InstallationPath),
                StringComparison.OrdinalIgnoreCase)))
        {
            var check=cluster.Check(sibling);
            if(check.Running||!check.ConfirmedStopped||
               check.Identity!=null||check.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-PLUGIN-JSON-STOP-REQUIRED");
        }
        return map;
    }
    private static string ConfigPath(LocalMap map,string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name is "." or ".." ||
           !PluginName.IsMatch(name))
            throw new InvalidDataException("UM-PLUGIN-JSON-INVALID-NAME");
        string root=Path.GetFullPath(map.InstallationPath);
        string plugin=Path.Combine(root,"ShooterGame","Binaries","Win64",
            "ArkApi","Plugins",name);
        string path=Path.GetFullPath(Path.Combine(plugin,"config.json"));
        if(!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+
             Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("UM-PLUGIN-JSON-PATH-ESCAPE");
        return path;
    }
    private static bool GuidField(JsonElement request,string property,out Guid value)
    {
        value=Guid.Empty;
        return request.TryGetProperty(property,out var item) &&
            item.ValueKind==JsonValueKind.String &&
            Guid.TryParse(item.GetString(),out value)&&value!=Guid.Empty;
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2_200_000)return Fail(null,"UM-PLUGIN-JSON-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var args=doc.RootElement;
            if(args.ValueKind!=JsonValueKind.Object ||
               !GuidField(args,"id",out var requestId) ||
               !args.TryGetProperty("method",out var methodValue) ||
               methodValue.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-PLUGIN-JSON-INVALID-MESSAGE");
            id=requestId.ToString("D");
            string? method=methodValue.GetString();
            if(method is not ("plugins.jsonOpen" or "plugins.jsonPreview" or
                "plugins.jsonSave" or "plugins.jsonClose"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="plugins.jsonOpen")
                {
                    opened=null;
                    if(!GuidField(args,"mapId",out var mapId)||
                       !args.TryGetProperty("plugin",out var plugin) ||
                       plugin.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-PLUGIN-JSON-INVALID-REQUEST");
                    var map=RequireOwned(mapId);
                    string name=plugin.GetString()??"";
                    string path=ConfigPath(map,name);
                    var original=SafeConfigEditor.Read(path,map.InstallationPath,
                        ConfigKind.PluginJson,cluster.Check(map));
                    opened=new Open {
                        Token=Guid.NewGuid(),MapId=map.Id,
                        Installation=map.InstallationPath,Baseline=original
                    };
                    return Ok(id,new{
                        token=opened.Token,mapId=map.Id,plugin=name,
                        content=original.Text,sha256=original.Sha256,
                        encoding=original.EncodingId,bytes=original.Bytes,
                        evidence="STOPPED_OWNED_PLUGIN_JSON_SNAPSHOT"
                    });
                }
                if(!GuidField(args,"token",out var token) ||
                   opened==null || opened.Token!=token ||
                   opened.CreatedUtc<DateTimeOffset.UtcNow-TimeSpan.FromMinutes(12))
                    return Fail(id,"UM-PLUGIN-JSON-SESSION-EXPIRED");
                var current=opened;
                var owning=RequireOwned(current.MapId);
                if(!string.Equals(owning.InstallationPath,current.Installation,
                    StringComparison.OrdinalIgnoreCase))
                    return Fail(id,"UM-PLUGIN-JSON-OWNER-CHANGED");
                if(method=="plugins.jsonClose")
                {
                    opened=null;
                    return Ok(id,new{closed=true});
                }
                if(method=="plugins.jsonPreview")
                {
                    current.Edited=null;current.Preview=null;
                    if(!args.TryGetProperty("content",out var content) ||
                       content.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-PLUGIN-JSON-CONTENT-REQUIRED");
                    string text=content.GetString()??"";
                    var preview=SafeConfigEditor.Preview(current.Baseline,text);
                    current.Edited=text;current.Preview=preview;
                    return Ok(id,new{
                        previewToken=current.Token,changed=preview.Changed,
                        oldSha256=preview.OldSha256,newSha256=preview.NewSha256,
                        oldLines=preview.OldLines,newLines=preview.NewLines,
                        evidence="VALIDATED_PLUGIN_JSON_PREVIEW_NO_WRITES"
                    });
                }
                if(current.Preview==null||current.Edited==null)
                    return Fail(id,"UM-PLUGIN-JSON-PREVIEW-REQUIRED");
                if(!args.TryGetProperty("confirm",out var confirmed) ||
                   confirmed.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-PLUGIN-JSON-APPROVAL-REQUIRED");
                opened=null; // No replay even if an external edit races the save.
                if(!current.Preview.Changed)
                    return Ok(id,new{saved=false,reason="UNCHANGED",backupCreated=false});
                string backup=SafeConfigEditor.Save(current.Baseline,current.Edited,
                    owning.InstallationPath,cluster.Check(owning),false,
                    ()=>cluster.Check(owning));
                return Ok(id,new{
                    saved=true,backupCreated=File.Exists(backup),
                    newSha256=current.Preview.NewSha256,
                    asaPluginReloaded=false,requiresRestart=true,
                    storeTouched=false,sqlUsed=false,
                    evidence="ATOMIC_STOPPED_LOCAL_PLUGIN_JSON_WITH_PROTECTIVE_BAK"
                });
            }
            finally{gate.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-PLUGIN-JSON-SAFETY-CHECK-FAILED");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>gate.Dispose();
}
