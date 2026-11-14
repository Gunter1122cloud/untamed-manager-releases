using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private sealed record MissingGameApproval(Guid Token,Guid MapId,string Profile,DateTimeOffset Expires);
    // A Game.ini setup wizard on another local ASA map must not invalidate
    // this map's approval. Only one one-shot ticket per map is allowed.
    private readonly Dictionary<Guid,MissingGameApproval> pendingGameByMap=new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class OpenDocument
    {
        public required Guid Token {get;init;}
        public required Guid MapId {get;init;}
        public required ConfigSnapshot Snapshot {get;set;}
        public required DateTimeOffset OpenedUtc {get;init;}
        public string? PreviewedContent {get;set;}
        public ConfigPreview? Preview {get;set;}
    }
    public DesktopIniBridge(ClusterService source)=>
        cluster=source??throw new ArgumentNullException(nameof(source));

    private (LocalMap Map,string Path) MissingGamePreflight(Guid mapId)
    {
        var map=Owned(mapId)??throw new UnauthorizedAccessException("UM-INI-MAP-NOT-OWNED");
        if(map.ObservedReadOnlyImported||map.HistoricalStoreProtected||
           map.Maintenance||FreshStoreEnrollmentPolicy.IsEnrolled(map)||
           !string.IsNullOrWhiteSpace(map.HistoricalMapKey)||
           !string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           !string.IsNullOrWhiteSpace(map.FreshStoreClusterId)||
           !string.IsNullOrWhiteSpace(map.FreshStoreTransferDirectory)||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.Arguments)||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.ApiLoaderArguments)||
           !AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new UnauthorizedAccessException("UM-INI-CREATE-FRESH-ONLY");
        var root=Path.GetFullPath(map.InstallationPath).TrimEnd(Path.DirectorySeparatorChar);
        if(root.StartsWith(@"\\",StringComparison.Ordinal)||
           new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-INI-CREATE-FIXED-DISK");
        var members=cluster.Config.LocalMaps.Where(m=>
            !string.IsNullOrWhiteSpace(m.InstallationPath)&&
            Path.GetFullPath(m.InstallationPath).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(root,StringComparison.OrdinalIgnoreCase));
        foreach(var member in members)
        {
            if(member.HistoricalStoreProtected||member.ObservedReadOnlyImported||
               FreshStoreEnrollmentPolicy.IsEnrolled(member)||
               !string.IsNullOrWhiteSpace(member.HistoricalMapKey)||
               !string.IsNullOrWhiteSpace(member.HistoricalArchiveSha256)||
               !string.IsNullOrWhiteSpace(member.StoreTestRoot)||
               !string.IsNullOrWhiteSpace(member.FreshStoreClusterId)||
               !string.IsNullOrWhiteSpace(member.FreshStoreTransferDirectory))
                throw new UnauthorizedAccessException("UM-INI-CREATE-SHARED-STORE");
            var state=cluster.Check(member);
            if(!state.ConfirmedStopped||state.Running||
               state.Identity!=null||state.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-INI-CREATE-ALL-MAPS-STOPPED");
        }
        // Diagnose a specific shared Store sibling first; still enforce
        // the full fresh-install/restore-journal gate before any mutation.
        if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map))
            throw new UnauthorizedAccessException("UM-INI-CREATE-FRESH-ONLY");
        string target=Path.Combine(root,"ShooterGame","Saved","Config",
            "WindowsServer","Game.ini");
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(target)!);
            dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-INI-CREATE-LINKED-PATH");
        if(File.Exists(target)||Directory.Exists(target))
            throw new InvalidOperationException("UM-INI-GAME-ALREADY-EXISTS");
        return (map,target);
    }

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
            if(method is not ("ini.open" or "ini.preview" or "ini.rebasePreview" or
                "ini.save" or "ini.close" or "ini.createGamePreview" or "ini.createGameApply"))
                return Fail(id,"UM-METHOD-DENIED");

            await gate.WaitAsync();
            try
            {
                foreach(var expired in opened.Where(x=>
                    DateTimeOffset.UtcNow-x.Value.OpenedUtc>TimeSpan.FromMinutes(12))
                    .Select(x=>x.Key).ToArray())opened.Remove(expired);
                foreach(var expired in pendingGameByMap.Where(x=>
                    x.Value.Expires<DateTimeOffset.UtcNow)
                    .Select(x=>x.Key).ToArray())pendingGameByMap.Remove(expired);

                if(method=="ini.createGamePreview")
                {
                    if(!GetGuid(data,"mapId",out var mapId))
                        return Fail(id,"UM-INI-CREATE-MAP");
                    pendingGameByMap.Remove(mapId);
                    var selected=MissingGamePreflight(mapId);
                    if(pendingGameByMap.Count>=25)pendingGameByMap.Clear();
                    var approval=new MissingGameApproval(Guid.NewGuid(),mapId,
                        JsonSerializer.Serialize(selected.Map),DateTimeOffset.UtcNow.AddMinutes(3));
                    pendingGameByMap[mapId]=approval;
                    return Ok(id,new{
                        token=approval.Token,mapId,file="Game.ini",
                        createsFile=false,requiresStoppedProcess=true,
                        originalStoreTouched=false,
                        evidence="FRESH_GAME_INI_CREATE_PREVIEW_NO_WRITE"
                    });
                }
                if(method=="ini.createGameApply")
                {
                    if(!GetGuid(data,"token",out var gameToken)||
                       !pendingGameByMap.Values.Any(p=>p.Token==gameToken)||
                       !data.TryGetProperty("confirm",out var consent)||
                       consent.ValueKind!=JsonValueKind.True)
                        return Fail(id,"UM-INI-CREATE-EXPLICIT-APPROVAL");
                    var approved=pendingGameByMap.Values.Single(p=>p.Token==gameToken);
                    pendingGameByMap.Remove(approved.MapId); // one shot, including failed writes
                    if(approved.Expires<DateTimeOffset.UtcNow)
                        return Fail(id,"UM-INI-CREATE-EXPIRED");
                    var candidate=MissingGamePreflight(approved.MapId);
                    if(JsonSerializer.Serialize(candidate.Map)!=approved.Profile)
                        return Fail(id,"UM-INI-CREATE-PROFILE-CHANGED");
                    using var lease=cluster.Operations.Acquire(candidate.Map.InstallationPath,
                        UntamedOperationKind.Configuration);
                    var rechecked=MissingGamePreflight(approved.MapId);
                    if(!ReferenceEquals(rechecked.Map,candidate.Map)||
                       rechecked.Path!=candidate.Path||
                       JsonSerializer.Serialize(rechecked.Map)!=approved.Profile)
                        return Fail(id,"UM-INI-CREATE-LEASE-CHANGED");
                    const string template="[/Script/ShooterGame.ShooterGameMode]\r\n";
                    byte[] bytes=Encoding.UTF8.GetBytes(template);
                    lease.MarkMutationStarted();
                    Directory.CreateDirectory(Path.GetDirectoryName(rechecked.Path)!);
                    // A race can replace a newly created directory with a
                    // reparse-point alias after the first preflight. Recheck
                    // every ancestor immediately before CreateNew.
                    for(var d=new DirectoryInfo(Path.GetDirectoryName(rechecked.Path)!);
                        d!=null;d=d.Parent)
                        if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                            throw new InvalidDataException("UM-INI-CREATE-LINK-CHANGED");
                    if(File.Exists(rechecked.Path)||Directory.Exists(rechecked.Path))
                        throw new IOException("UM-INI-CREATE-CONCURRENT-TARGET");
                    // Creating a file is opt-in and must NEVER overwrite a
                    // historical or previously edited Game.ini, even on race.
                    using(var output=new FileStream(rechecked.Path,FileMode.CreateNew,
                        FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
                    {output.Write(bytes);output.Flush(true);}
                    using var verify=File.OpenRead(rechecked.Path);
                    if(!CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(verify),SHA256.HashData(bytes)))
                        throw new IOException("UM-INI-CREATE-SHA-MISMATCH");
                    lease.Complete();
                    return Ok(id,new{
                        mapId=approved.MapId,file="Game.ini",created=true,
                        bytes=bytes.Length,originalStoreTouched=false,
                        evidence="FRESH_GAME_INI_DURABLE_CREATE_NEW_VERIFIED"
                    });
                }
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
                    if(map.ObservedReadOnlyImported)return Fail(id,"UM-LIVE-OBSERVED-MAP-READONLY");
                    var check=map.HistoricalStoreProtected?
                        HistoricalLocalConfigGuard.DemandStopped(cluster,map):cluster.Check(map);
                    if(!check.ConfirmedStopped || check.Running || map.Maintenance)
                        return Fail(id,"UM-INI-STOP-REQUIRED");

                    // A never-started SteamCMD installation legitimately has no Saved tree.
                    // Classify absence before SafeConfigEditor path checks, without creating
                    // anything or weakening its reparse/ETag/atomic-save protections.
                    if(string.IsNullOrWhiteSpace(map.InstallationPath) ||
                       !Path.IsPathFullyQualified(map.InstallationPath) ||
                       map.InstallationPath.StartsWith(@"\\",StringComparison.Ordinal) ||
                       !Directory.Exists(map.InstallationPath))
                        return Fail(id,"UM-INI-INSTALLATION-MISSING");
                    // Existing valid INI files remain readable even when the
                    // server executable is absent (historical offline config
                    // inspection). An editor read never grants ASA start rights.
                    bool executablePresent=!string.IsNullOrWhiteSpace(map.ExecutablePath) &&
                        Path.IsPathFullyQualified(map.ExecutablePath) &&
                        File.Exists(map.ExecutablePath);
                    string directory=Path.Combine(map.InstallationPath,"ShooterGame",
                        "Saved","Config","WindowsServer");
                    if(!Directory.Exists(directory))
                        return Fail(id,executablePresent?"UM-INI-CONFIG-DIRECTORY-MISSING":
                            "UM-INI-SERVER-EXE-MISSING");
                    var path=Path.Combine(directory,file);
                    if(!File.Exists(path))
                        return Fail(id,!executablePresent?"UM-INI-SERVER-EXE-MISSING":
                            file=="Game.ini"?"UM-INI-GAME-MISSING":"UM-INI-GUS-MISSING");
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
                if(owned.ObservedReadOnlyImported)return Fail(id,"UM-LIVE-OBSERVED-MAP-READONLY");

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
                if(method=="ini.rebasePreview")
                {
                    if(!data.TryGetProperty("content",out var rebasedField) ||
                       rebasedField.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-INI-INVALID-CONTENT");
                    string proposed=rebasedField.GetString()??"";
                    if(proposed.Length>2_000_000)
                        return Fail(id,"UM-INI-CONTENT-TOO-LARGE");
                    var observed=owned.HistoricalStoreProtected?
                        HistoricalLocalConfigGuard.DemandStopped(cluster,owned):
                        cluster.Check(owned);
                    if(!observed.ConfirmedStopped||observed.Running||
                       owned.Maintenance)
                        return Fail(id,"UM-INI-STOP-REQUIRED");
                    var latest=SafeConfigEditor.Read(openedFile.Snapshot.Path,
                        owned.InstallationPath,ConfigKind.GameIni,observed,
                        owned.Maintenance);
                    var merge=SafeConfigEditor.RebaseIni(
                        openedFile.Snapshot,proposed,latest);
                    // No file mutation, no implicit approval, no reuse of
                    // the old hash preview. Only the fresh baseline is kept.
                    openedFile.Snapshot=latest;
                    openedFile.PreviewedContent=null;
                    openedFile.Preview=null;
                    return Ok(id,new{
                        content=merge.Content,diskContent=latest.Text,
                        freshSha256=latest.Sha256,
                        mergedLines=merge.MergedLines,
                        evidence="THREE_WAY_INI_REBASE_PREVIEW_NO_WRITE"
                    });
                }
                if(openedFile.PreviewedContent==null || openedFile.Preview==null ||
                   !openedFile.Preview.Changed)
                    return Fail(id,"UM-INI-PREVIEW-REQUIRED");

                // The original file hash is rechecked; process is checked
                // before and immediately before File.Replace. No live writes.
                var state=owned.HistoricalStoreProtected?
                    HistoricalLocalConfigGuard.DemandStopped(cluster,owned):cluster.Check(owned);
                if(!state.ConfirmedStopped||state.Running||owned.Maintenance)
                    return Fail(id,"UM-INI-STOP-REQUIRED");
                string backup=SafeConfigEditor.Save(openedFile.Snapshot,
                    openedFile.PreviewedContent,owned.InstallationPath,
                    state,owned.Maintenance,()=>owned.HistoricalStoreProtected?
                        HistoricalLocalConfigGuard.DemandStopped(cluster,owned,underOwnOperation:true):cluster.Check(owned));
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
        catch(DirectoryNotFoundException){return Fail(id,"UM-INI-CONFIG-DIRECTORY-MISSING");}
        // Only return fixed, explicitly allowlisted native error codes.
        // Never propagate arbitrary exception text, paths or INI passwords.
        catch(Exception ex) when(ex.Message is
            "UM-INI-MAP-NOT-OWNED" or "UM-INI-CREATE-FRESH-ONLY" or
            "UM-INI-CREATE-SHARED-STORE" or "UM-INI-CREATE-FIXED-DISK" or
            "UM-INI-CREATE-ALL-MAPS-STOPPED" or
            "UM-INI-GAME-ALREADY-EXISTS" or
            "UM-INI-CREATE-LINKED-PATH" or "UM-INI-CREATE-LINK-CHANGED" or
            "UM-INI-CREATE-CONCURRENT-TARGET" or "UM-INI-CREATE-SHA-MISMATCH")
        {
            return Fail(id,ex.Message);
        }
        catch(UnauthorizedAccessException){return Fail(id,"UM-INI-ACCESS-DENIED");}
        catch(InvalidDataException){return Fail(id,"UM-INI-PATH-OR-CONTENT-UNSAFE");}
        catch(ConfigEtagConflictException){return Fail(id,"UM-INI-ETAG-MISMATCH");}
        catch(ConfigMergeConflictException){return Fail(id,"UM-INI-MERGE-CONFLICT");}
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
