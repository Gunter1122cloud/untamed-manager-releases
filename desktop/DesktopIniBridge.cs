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
/// Restrained native INI editor: only two canonical files under a registered
/// local ASA installation. Requires confirmed stopped process, verified ETag,
/// explicit preview and a second confirmed local-process check at save time.
/// </summary>
public sealed class DesktopIniBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly Dictionary<Guid,OpenDocument> opened = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class OpenDocument
    {
        public required Guid Token {get;init;}
        public required Guid MapId {get;init;}
        public required ConfigSnapshot Snapshot {get;init;}
        public required DateTimeOffset OpenedUtc {get;init;}
        public string? PreviewedContent {get;set;}
        public ConfigPreview? Preview {get;set;}
    }
    public DesktopIniBridge(ClusterService source)=>
        cluster=source??throw new ArgumentNullException(nameof(source));

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2_200_000)return Fail(null,"UM-INVALID-MESSAGE");
            using var parsed=JsonDocument.Parse(raw);
            var data=parsed.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !GetGuid(data,"id",out var requestId) ||
               !data.TryGetProperty("method",out var methodValue) ||
               methodValue.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-INVALID-MESSAGE");
            id=requestId.ToString("D");
            string? method=methodValue.GetString();
            if(method is not ("ini.open" or "ini.preview" or "ini.save" or "ini.close"))
                return Fail(id,"UM-METHOD-DENIED");

            await gate.WaitAsync();
            try
            {
                foreach(var expired in opened.Where(x=>
                    DateTimeOffset.UtcNow-x.Value.OpenedUtc>TimeSpan.FromMinutes(12))
                    .Select(x=>x.Key).ToArray())opened.Remove(expired);

                if(method=="ini.open")
                {
                    if(!GetGuid(data,"mapId",out var mapId) ||
                       !data.TryGetProperty("file",out var fileField) ||
                       fileField.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-INI-INVALID-REQUEST");
                    string? file=fileField.GetString();
                    if(file is not ("Game.ini" or "GameUserSettings.ini"))
                        return Fail(id,"UM-INI-INVALID-FILE");
                    var map=Owned(mapId);
                    if(map==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    var check=cluster.Check(map);
                    if(!check.ConfirmedStopped || check.Running || map.Maintenance)
                        return Fail(id,"UM-INI-STOP-REQUIRED");

                    var path=Path.Combine(map.InstallationPath,"ShooterGame","Saved",
                        "Config","WindowsServer",file);
                    var snapshot=SafeConfigEditor.Read(path,map.InstallationPath,
                        ConfigKind.GameIni,check,map.Maintenance);
                    foreach(var existing in opened.Where(x=>x.Value.MapId==mapId)
                        .Select(x=>x.Key).ToArray())opened.Remove(existing);
                    if(opened.Count>=5)return Fail(id,"UM-INI-TOO-MANY-DOCUMENTS");
                    var token=Guid.NewGuid();
                    opened[token]=new(){
                        Token=token,MapId=mapId,Snapshot=snapshot,
                        OpenedUtc=DateTimeOffset.UtcNow};
                    return Ok(id,new{
                        token,mapId,file,content=snapshot.Text,
                        encoding=snapshot.EncodingId,
                        sha256=snapshot.Sha256,bytes=snapshot.Bytes,
                        evidence="STOPPED_LOCAL_INI_SNAPSHOT"
                    });
                }
                if(!GetGuid(data,"token",out var tokenId) ||
                   !opened.TryGetValue(tokenId,out var openedFile))
                    return Fail(id,"UM-INI-DOCUMENT-EXPIRED");
                var owned=Owned(openedFile.MapId);
                if(owned==null)return Fail(id,"UM-MAP-NOT-OWNED");

                if(method=="ini.close")
                {
                    opened.Remove(tokenId);
                    return Ok(id,new{closed=true});
                }
                if(method=="ini.preview")
                {
                    if(!data.TryGetProperty("content",out var contentField) ||
                       contentField.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-INI-INVALID-CONTENT");
                    string text=contentField.GetString()??"";
                    if(text.Length>2_000_000)return Fail(id,"UM-INI-CONTENT-TOO-LARGE");
                    var preview=SafeConfigEditor.Preview(openedFile.Snapshot,text);
                    openedFile.PreviewedContent=text;
                    openedFile.Preview=preview;
                    return Ok(id,new{
                        previewToken=tokenId,
                        changed=preview.Changed,
                        oldLines=preview.OldLines,
                        newLines=preview.NewLines,
                        bytes=openedFile.Snapshot.Bytes,
                        originalSha256=preview.OldSha256,
                        nextSha256=preview.NewSha256,
                        evidence="HASHED_IN_MEMORY_PREVIEW"
                    });
                }
                if(openedFile.PreviewedContent==null || openedFile.Preview==null ||
                   !openedFile.Preview.Changed)
                    return Fail(id,"UM-INI-PREVIEW-REQUIRED");

                // The original file hash is rechecked; process is checked
                // before and immediately before File.Replace. No live writes.
                var state=cluster.Check(owned);
                if(!state.ConfirmedStopped||state.Running||owned.Maintenance)
                    return Fail(id,"UM-INI-STOP-REQUIRED");
                string backup=SafeConfigEditor.Save(openedFile.Snapshot,
                    openedFile.PreviewedContent,owned.InstallationPath,
                    state,owned.Maintenance,()=>cluster.Check(owned));
                opened.Remove(tokenId);
                return Ok(id,new{
                    saved=true,backupCreated=!string.IsNullOrWhiteSpace(backup),
                    sha256=openedFile.Preview.NewSha256,
                    evidence="ATOMIC_LOCAL_INI_SAVE_ETAG_VERIFIED"
                });
            }
            finally {gate.Release();}
        }
        catch(FileNotFoundException){return Fail(id,"UM-INI-NOT-FOUND");}
        catch(IOException){return Fail(id,"UM-INI-CONFLICT-OR-IO-ERROR");}
        catch(Exception)
        {
            // Never expose file contents, passwords, paths, or exception details.
            return Fail(id,"UM-INI-OPERATION-FAILED");
        }
    }
    private LocalMap? Owned(Guid id) =>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id);
    private static bool GetGuid(JsonElement data,string name,out Guid value)
    {
        value=Guid.Empty;
        return data.TryGetProperty(name,out var element) &&
            element.ValueKind==JsonValueKind.String &&
            Guid.TryParse(element.GetString(),out value) && value!=Guid.Empty;
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>gate.Dispose();
}
