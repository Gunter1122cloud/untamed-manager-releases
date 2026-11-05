using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// IMPORT AN EXISTING HISTORICAL STORE COPY WITHOUT WRITER AUTHORITY.
/// The verified 39-file historical ZIP contains original identities, map
/// roots, UDP ports and mod IDs, but NOT the complete runtime command line
/// (session name, RCON, cluster params, loader args). Therefore registration
/// is intentionally quarantined. It never starts, edits, converts, deletes,
/// restores or rewrites Kova/ASA/Store files.
/// </summary>
public sealed class DesktopHistoricalStoreImportBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly Func<LocalMap,ProcessCheck> inspect;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Dictionary<Guid,Prepared> pending=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Prepared(Guid Token,string Archive,string SourceHash,
        Guid MapId,string MapKey,string LocalName,string Root,string ServerExe,
        int GamePort,int QueryPort,string[] Enabled,string[] Passive,
        DateTimeOffset ExpiresUtc);

    public DesktopHistoricalStoreImportBridge(ClusterService service,
        Func<LocalMap,ProcessCheck>? processInspector=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        inspect=processInspector??(map=>new WindowsProcessInspector().InspectFamily(map));
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Fail(null,"UM-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !GuidField(data,"id",out var requestId) ||
               !data.TryGetProperty("method",out var methodProp) ||
               methodProp.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-INVALID-MESSAGE");
            id=requestId.ToString("D");
            string? method=methodProp.GetString();
            if(method is not ("historical.list" or "historical.preview" or
                "historical.register" or "historical.pairedEvidence" or
                "historical.launchEvidence"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                foreach(var expired in pending.Where(x=>x.Value.ExpiresUtc<DateTimeOffset.UtcNow)
                    .Select(x=>x.Key).ToArray())pending.Remove(expired);

                if(method=="historical.launchEvidence")
                {
                    if(!TextField(data,"archive",520,out var launchArchive) ||
                       !GuidField(data,"mapId",out var launchMapId) ||
                       !data.TryGetProperty("command",out var launchProp) ||
                       launchProp.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-HISTORICAL-LAUNCH-INVALID-REQUEST");
                    string command=launchProp.GetString()??"";
                    if(command.Length is < 1 or > 3072)
                        return Fail(id,"UM-HISTORICAL-LAUNCH-INVALID-REQUEST");
                    launchArchive=CanonicalArchive(launchArchive);
                    string archiveHash=Digest(launchArchive);
                    var historical=HistoricalLaunchProfileReader.Inspect(launchArchive);
                    var selected=historical.Maps.SingleOrDefault(x=>x.Id==launchMapId);
                    if(selected==null || !selected.ExistingStoreActive)
                        return Fail(id,"UM-HISTORICAL-MAP-NOT-FOUND");
                    var (_,launchActive,launchPassive)=ReadVerifiedLaunchRow(launchArchive,launchMapId);
                    var evidence=HistoricalLaunchEvidenceInspector.Inspect(
                        command,selected.MapId,selected.GamePort,selected.QueryPort,launchActive,launchPassive);
                    if(Digest(launchArchive)!=archiveHash)
                        return Fail(id,"UM-HISTORICAL-ARCHIVE-CHANGED");
                    return Ok(id,evidence);
                }

                if(method=="historical.pairedEvidence")
                {
                    if(!TextField(data,"coordinationArchive",520,out var coord) ||
                       !TextField(data,"localMetadataArchive",520,out var local))
                        return Fail(id,"UM-HISTORICAL-PAIR-INVALID-REQUEST");
                    coord=CanonicalArchive(coord);
                    local=CanonicalArchive(local);
                    if(string.Equals(coord,local,StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-HISTORICAL-PAIR-INVALID-REQUEST");
                    string firstHash=Digest(coord),secondHash=Digest(local);
                    var result=ClusterVaultArchiveInspector.InspectPairedCopies(coord,local);
                    if(firstHash!=Digest(coord) || secondHash!=Digest(local))
                        return Fail(id,"UM-HISTORICAL-ARCHIVE-CHANGED");
                    return Ok(id,new {
                        files=result.VerifiedFiles,maps=result.Maps,peers=result.Peers,
                        sessionConsistent=result.SessionConsistent,
                        configHashesVerified=result.ConfigHashesVerified,
                        activeStoreRecords=result.LocalHostMapRecordsActive,
                        completeTwoHostEvidence=result.CompleteTwoHostEvidence,
                        historicalConsistent=result.HistoricalConsistent,
                        findings=result.Findings.Select(x=>x.Code.ToString()).Distinct().ToArray(),
                        hosts=result.Hosts.Select(x=>new{
                            number=x.Number,maps=x.Maps,localEvidence=x.LocalEvidence,
                            sessionMatches=x.SessionMatches,
                            configHashMatches=x.ConfigHashMatches,
                            lockMatches=x.LockMatches,activeMaps=x.ActiveMaps
                        }).ToArray(),
                        productionUseAllowed=false,
                        evidence="PAIRED_COPIES_OFFLINE_READ_ONLY_NO_WRITER_CERTIFICATION"
                    });
                }

                if(method=="historical.list")
                {
                    if(!TextField(data,"archive",520,out var archive))
                        return Fail(id,"UM-HISTORICAL-INVALID-ARCHIVE");
                    archive=CanonicalArchive(archive);
                    var verified=HistoricalLaunchProfileReader.Inspect(archive);
                    return Ok(id,new{
                        maps=verified.Maps.Select(m=>new{
                            mapId=m.Id,mapKey=m.MapId,gamePort=m.GamePort,
                            queryPort=m.QueryPort,enabledMods=m.EnabledMods,
                            passiveMods=m.PassiveMods
                        }).ToArray(),
                        sourceVerified=true,completeLaunchArguments=false,
                        liveStoreVerified=false,allowServerStart=false,
                        evidence="HISTORICAL_LOCAL_ARCHIVE_READ_ONLY"
                    });
                }

                if(method=="historical.preview")
                {
                    if(!TextField(data,"archive",520,out var archive) ||
                       !TextField(data,"root",520,out var requestedRoot) ||
                       !GuidField(data,"mapId",out var mapId))
                        return Fail(id,"UM-HISTORICAL-INVALID-REQUEST");
                    archive=CanonicalArchive(archive);
                    string originalHash=Digest(archive);
                    string root=DesktopLocalMapRegistryBridge.ValidateFixedLocalRoot(requestedRoot);
                    var candidates=HistoricalLaunchProfileReader.Inspect(archive);
                    var selected=candidates.Maps.SingleOrDefault(m=>m.Id==mapId);
                    if(selected==null || !selected.ExistingStoreActive)
                        return Fail(id,"UM-HISTORICAL-MAP-NOT-FOUND");

                    // The historical map root must be EXACTLY the local
                    // installation being imported. No silent relocation.
                    var (originalRoot,previewEnabled,previewPassive)=ReadVerifiedLaunchRow(archive,mapId);
                    if(!string.Equals(Path.GetFullPath(originalRoot).TrimEnd('\\'),
                        root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-HISTORICAL-ROOT-MISMATCH");
                    if(previewEnabled.Length!=selected.EnabledMods ||
                       previewPassive.Length!=selected.PassiveMods)
                        return Fail(id,"UM-HISTORICAL-INVALID-MODS");
                    var previewInstall=LocalAsaInstallLocator.Probe(root);
                    if(!previewInstall.HasServerExecutable ||
                       string.IsNullOrWhiteSpace(previewInstall.ServerExecutable))
                        return Fail(id,"UM-ASA-EXE-MISSING");

                    string name=MapName(selected.MapId);
                    CheckDuplicate(mapId,name,root);
                    var observation=Observed(selected,root,previewInstall.ServerExecutable);
                    if(observation.Running || !observation.ConfirmedStopped)
                        return Fail(id,"UM-HISTORICAL-STOP-REQUIRED");

                    // Read-only inspection can never validate cross-host
                    // writer ownership or recover the original full args.
                    if(Digest(archive)!=originalHash)
                        return Fail(id,"UM-HISTORICAL-ARCHIVE-CHANGED");
                    if(pending.Count>=8)pending.Clear();
                    var token=Guid.NewGuid();
                    pending[token]=new(token,archive,originalHash,mapId,
                        selected.MapId,name,root,previewInstall.ServerExecutable,
                        selected.GamePort,selected.QueryPort,previewEnabled,previewPassive,
                        DateTimeOffset.UtcNow.AddMinutes(7));
                    return Ok(id,new{
                        token,mapId,mapKey=selected.MapId,name,
                        gamePort=selected.GamePort,queryPort=selected.QueryPort,
                        enabledMods=previewEnabled.Length,passiveMods=previewPassive.Length,
                        folderName=Path.GetFileName(root),
                        historicalActive=true,originalMapIdPreserved=true,
                        fullLaunchArgumentsVerified=false,peerWriterVerified=false,
                        serverStartAllowed=false,
                        evidence="HISTORICAL_STORE_V1_QUARANTINE_PREVIEW"
                    });
                }

                if(!GuidField(data,"token",out var tokenId) ||
                   !pending.TryGetValue(tokenId,out var staged))
                    return Fail(id,"UM-HISTORICAL-PREVIEW-EXPIRED");
                if(!BoolField(data,"confirm",out bool confirmed)||!confirmed)
                    return Fail(id,"UM-HISTORICAL-EXPLICIT-CONFIRMATION-REQUIRED");
                if(DateTimeOffset.UtcNow>staged.ExpiresUtc)
                {
                    pending.Remove(tokenId);
                    return Fail(id,"UM-HISTORICAL-PREVIEW-EXPIRED");
                }
                string rootFinal=DesktopLocalMapRegistryBridge.ValidateFixedLocalRoot(staged.Root);
                string archiveFinal=CanonicalArchive(staged.Archive);
                if(Digest(archiveFinal)!=staged.SourceHash)
                    return Fail(id,"UM-HISTORICAL-ARCHIVE-CHANGED");
                var rechecked=HistoricalLaunchProfileReader.Inspect(archiveFinal);
                var expected=rechecked.Maps.SingleOrDefault(x=>x.Id==staged.MapId);
                var (rootFromArchive,active,passive)=ReadVerifiedLaunchRow(archiveFinal,staged.MapId);
                if(expected==null || !expected.ExistingStoreActive ||
                   expected.MapId!=staged.MapKey ||
                   expected.GamePort!=staged.GamePort || expected.QueryPort!=staged.QueryPort ||
                   !Path.GetFullPath(rootFromArchive).TrimEnd('\\')
                       .Equals(rootFinal.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase) ||
                   !active.SequenceEqual(staged.Enabled,StringComparer.Ordinal) ||
                   !passive.SequenceEqual(staged.Passive,StringComparer.Ordinal))
                    return Fail(id,"UM-HISTORICAL-SOURCE-CONFLICT");

                var install=LocalAsaInstallLocator.Probe(rootFinal);
                if(!install.HasServerExecutable ||
                   !string.Equals(install.ServerExecutable,staged.ServerExe,
                       StringComparison.OrdinalIgnoreCase))
                    return Fail(id,"UM-HISTORICAL-INSTALL-CHANGED");
                CheckDuplicate(staged.MapId,staged.LocalName,rootFinal);
                var check=Observed(expected,rootFinal,staged.ServerExe);
                if(check.Running || !check.ConfirmedStopped)
                    return Fail(id,"UM-HISTORICAL-STOP-REQUIRED");

                var map=new LocalMap{
                    Id=staged.MapId,Name=staged.LocalName,
                    InstallationPath=rootFinal,ExecutablePath=staged.ServerExe,
                    GamePort=staged.GamePort,QueryPort=staged.QueryPort,
                    Arguments="", // full historical launch command NOT supplied by ZIP
                    HistoricalStoreProtected=true,
                    HistoricalArchiveSha256=staged.SourceHash,
                    HistoricalMapKey=staged.MapKey,
                    HistoricalEnabledMods=staged.Enabled.ToList(),
                    HistoricalPassiveMods=staged.Passive.ToList(),
                    StartWithAsaApi=false,AutoStart=false,MonitorRcon=false,
                    RconPort=0,ApiLoaderArguments=""
                };
                cluster.AddMap(map); // ONLY Manager's own atomic ConfigStore CAS.
                pending.Remove(tokenId);
                return Ok(id,new{
                    mapId=map.Id,mapName=map.Name,
                    registered=true,storeProtected=true,
                    requiresOriginalLaunchVerification=true,
                    serverStarted=false,worldFilesChanged=false,
                    evidence="HISTORICAL_STORE_V1_REGISTERED_QUARANTINED"
                });
            }
            finally {gate.Release();}
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or
            ArgumentException or UnauthorizedAccessException or
            InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException or
            System.Security.Cryptography.CryptographicException)
        {
            // Never echo private paths, usernames, Kova IDs or ZIP content to UI.
            return Fail(id,"UM-HISTORICAL-VERIFICATION-FAILED");
        }
    }

    private static string MapName(string key)=>key switch
    {
        "TheIsland_WP"=>"The Island",
        "TheCenter_WP"=>"The Center",
        "ScorchedEarth_WP"=>"Scorched Earth",
        "Aberration_WP"=>"Aberration",
        "Extinction_WP"=>"Extinction",
        "Genesis_WP"=>"Genesis Part 1",
        "Valguero_WP"=>"Valguero",
        "Ragnarok_WP"=>"Ragnarok",
        "Astraeos_WP"=>"Astraeos",
        "LostColony_WP"=>"Lost Colony",
        _=>LocalMapNameRules.Normalize(key)
    };

    private ProcessCheck Observed(HistoricalLaunchProfile selected,string root,string exe)
    {
        var map=new LocalMap{Id=selected.Id,Name=selected.MapId,
            InstallationPath=root,ExecutablePath=exe,Arguments=""};
        string loader=Path.Combine(root,"ShooterGame","Binaries","Win64","AsaApiLoader.exe");
        if(File.Exists(loader))map.ApiLoaderPath=loader;
        return inspect(map);
    }

    private void CheckDuplicate(Guid id,string name,string root)
    {
        if(cluster.Config.LocalMaps.Any(m=>m.Id==id ||
            m.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Already registered");
        foreach(var current in cluster.Config.LocalMaps)
        {
            if(string.IsNullOrWhiteSpace(current.InstallationPath))continue;
            string other=Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.InstallationPath));
            if(root.Equals(other,StringComparison.OrdinalIgnoreCase) ||
               root.StartsWith(other+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
               other.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Overlapping installation root");
        }
    }

    private static string CanonicalArchive(string supplied)
    {
        if(!supplied.EndsWith(".zip",StringComparison.OrdinalIgnoreCase) ||
           !Path.IsPathFullyQualified(supplied) ||
           supplied.StartsWith(@"\\",StringComparison.Ordinal) ||
           supplied.StartsWith("//",StringComparison.Ordinal))
            throw new InvalidDataException("An explicit local ZIP copy is required");
        string full=Path.GetFullPath(supplied);
        if(!File.Exists(full) || new FileInfo(full).Length is < 200 or > 50_000_000)
            throw new InvalidDataException("Missing or oversized historical ZIP");
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Historical ZIP must be on a fixed local disk");
        for(DirectoryInfo? node=new(Path.GetDirectoryName(full)!);node!=null;node=node.Parent)
            if(node.Exists && (node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Reparse-linked ZIP parent forbidden");
        if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked ZIP forbidden");
        return full;
    }

    private static string Digest(string archive)=>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));

    private static (string,string[],string[]) ReadVerifiedLaunchRow(string archive,Guid id)
    {
        // HistoricalLaunchProfileReader.Inspect must have validated the full
        // manifest and the matching Store record before we read these values.
        using var fs=new FileStream(archive,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var zip=new ZipArchive(fs,ZipArchiveMode.Read);
        var entries=zip.Entries.Where(x=>x.FullName.Replace('\\','/')
            .Equals("Kova/launch-profiles-v1.json",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(entries.Length!=1||entries[0].Length is < 20 or > 500_000)
            throw new InvalidDataException("Historical launch entry missing or ambiguous");
        using var stream=entries[0].Open();
        using var doc=JsonDocument.Parse(stream,new JsonDocumentOptions{MaxDepth=30});
        var rows=doc.RootElement.GetProperty("rows").EnumerateArray()
            .Where(x=>x.TryGetProperty("id",out var e) &&
                Guid.TryParse(e.GetString(),out var found)&&found==id).ToArray();
        if(rows.Length!=1)throw new InvalidDataException("Historical map ID ambiguous");
        var row=rows[0];
        string root=row.GetProperty("root").GetString()??"";
        var launch=row.GetProperty("launch");
        string[] Parse(string key)=>launch.GetProperty(key).EnumerateArray()
            .Select(x=>x.ValueKind switch{
                JsonValueKind.Number when x.TryGetInt64(out long i)=>i.ToString(),
                JsonValueKind.String=>x.GetString()??"",
                _=>throw new InvalidDataException("Invalid mod identifier")
            }).ToArray();
        return (root,Parse("mods"),Parse("passiveMods"));
    }
    private static bool GuidField(JsonElement obj,string key,out Guid value)
    {
        value=Guid.Empty;
        return obj.TryGetProperty(key,out var item) &&
            item.ValueKind==JsonValueKind.String &&
            Guid.TryParse(item.GetString(),out value) && value!=Guid.Empty;
    }
    private static bool TextField(JsonElement obj,string key,int max,out string text)
    {
        text="";
        if(!obj.TryGetProperty(key,out var item) ||
           item.ValueKind!=JsonValueKind.String)return false;
        text=item.GetString()?.Trim()??"";
        return text.Length is >0 and <=520 && text.Length<=max &&
            !text.Any(char.IsControl);
    }
    private static bool BoolField(JsonElement obj,string key,out bool value)
    {
        value=false;
        if(!obj.TryGetProperty(key,out var item) ||
           item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value=item.GetBoolean();
        return true;
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>gate.Dispose();
}
