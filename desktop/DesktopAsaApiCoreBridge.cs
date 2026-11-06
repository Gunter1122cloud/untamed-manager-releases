using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Only native C# accesses official GitHub ASA API release bytes. The WebView
/// may select a bounded publisher asset name and registered local map ID,
/// never a path, ZIP contents, arbitrary URL, DLL or executable argument.
/// Mutations require short-lived approval; no ASA process or Store starts.
/// </summary>
public sealed class DesktopAsaApiCoreBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,string Root,
        string AssetName,string Tag,string Sha256,DateTimeOffset Until);
    private sealed record RollbackApproval(Guid Token,Guid MapId,string Root,
        string TransactionId,DateTimeOffset Until);
    private readonly ClusterService cluster;
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly SemaphoreSlim serial=new(1,1);
    private Approval? pending;
    private RollbackApproval? pendingRollback;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopAsaApiCoreBridge(ClusterService cluster,HttpClient? client=null)
    {
        this.cluster=cluster??throw new ArgumentNullException(nameof(cluster));
        ownsClient=client==null;
        http=client??new HttpClient{Timeout=TimeSpan.FromMinutes(3)};
    }
    private static string Err(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    private static string Ok(string? id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private LocalMap? Local(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id);
    private bool Eligible(LocalMap map)
    {
        if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map)||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||
           map.Maintenance||UntamedWorldBackup.HasLiveOrHistoricalStore(map))
            return false;
        if(new AsaManualRestartJournal(map.InstallationPath).Observe() is
            { Phase: not AsaRestartPhase.Completed })return false;
        return cluster.Config.LocalMaps.Where(m=>
            !string.IsNullOrWhiteSpace(m.InstallationPath)&&
            string.Equals(Path.GetFullPath(m.InstallationPath),
                Path.GetFullPath(map.InstallationPath),StringComparison.OrdinalIgnoreCase))
            .All(m=>{
                var p=cluster.ObserveLocalProcessesReadOnly(m);
                return p.ConfirmedStopped&&!p.Running&&
                    p.Identity==null&&p.LoaderIdentity==null;
            });
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2400)return Err(null,"UM-API-CORE-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var obj=doc.RootElement;
            if(obj.ValueKind!=JsonValueKind.Object||
               !obj.TryGetProperty("id",out var uid)||uid.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(uid.GetString(),out var guid)||guid==Guid.Empty||
               !obj.TryGetProperty("method",out var action)||
               action.ValueKind!=JsonValueKind.String)
                return Err(null,"UM-API-CORE-INVALID-MESSAGE");
            id=guid.ToString("D");
            var method=action.GetString();
            if(method is not ("api.coreRelease" or "api.corePreview" or "api.coreApply" or
                "api.coreRollbackCandidates" or "api.coreRollbackPreview" or "api.coreRollbackApply"))
                return Err(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="api.coreRelease")
                {
                    var latest=await AsaApiReleaseCatalog.FetchLatestAsync(http);
                    return Ok(id,new {
                        latest.Tag,latest.PublishedUtc,latest.HtmlUrl,
                        assets=latest.Assets.Where(x=>x.Name.EndsWith(".zip",
                            StringComparison.OrdinalIgnoreCase)).Select(x=>new{
                                x.Name,x.Bytes,
                                publisherDigestAvailable=x.OfficialSha256!=null
                            }).ToArray(),
                        installationStarted=false,
                        evidence="OFFICIAL_ASA_API_GITHUB_RELEASE_METADATA_ONLY"
                    });
                }
                if(method=="api.corePreview")
                {
                    pending=null;pendingRollback=null;
                    if(!obj.TryGetProperty("mapId",out var m)||
                       m.ValueKind!=JsonValueKind.String ||
                       !Guid.TryParse(m.GetString(),out var mapId)||
                       !obj.TryGetProperty("assetName",out var a)||
                       a.ValueKind!=JsonValueKind.String ||
                       a.GetString() is not string assetName ||
                       assetName.Length>180 || string.IsNullOrWhiteSpace(assetName))
                        return Err(id,"UM-API-CORE-INVALID-SELECTION");
                    var map=Local(mapId);
                    if(map==null || !Eligible(map))
                        return Err(id,"UM-API-CORE-NOT-STOPPED-NONSTORE");
                    var release=await AsaApiReleaseCatalog.FetchLatestAsync(http);
                    var asset=release.Assets.SingleOrDefault(x=>x.Name==assetName&&
                        x.OfficialSha256!=null&&
                        x.Name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase));
                    if(asset==null)return Err(id,"UM-API-CORE-UPSTREAM-DIGEST-MISSING");
                    pending=new Approval(Guid.NewGuid(),map.Id,map.InstallationPath,
                        assetName,release.Tag,asset.OfficialSha256!,
                        DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id,new{
                        token=pending.Token,mapId=map.Id,mapName=map.Name,
                        releaseTag=release.Tag,assetName,bytes=asset.Bytes,
                        officialSha256VerifiedInMetadata=true,
                        approved=false,serverStopped=true,storeProtected=false,
                        createsSelectiveRollback=true,
                        restartsServer=false,runtimeLoadCertified=false,
                        evidence="VERIFIED_UPSTREAM_RELEASE_PREFLIGHT_NO_WRITE"
                    });
                }
                if(method=="api.coreRollbackCandidates")
                {
                    if(!obj.TryGetProperty("mapId",out var mapArg)||
                       mapArg.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(mapArg.GetString(),out var mapId))
                        return Err(id,"UM-API-CORE-ROLLBACK-SELECTION");
                    var local=Local(mapId);
                    if(local==null||!Eligible(local))
                        return Err(id,"UM-API-CORE-ROLLBACK-NOT-STOPPED");
                    var candidates=AsaApiOwnedInstaller.ListCompletedRollbacks(cluster,mapId);
                    return Ok(id,new{candidates,restartsServer=false,
                        evidence="COMMITTED_OWNED_API_CORE_TRANSACTIONS_ONLY"});
                }
                if(method=="api.coreRollbackPreview")
                {
                    pending=null;pendingRollback=null;
                    if(!obj.TryGetProperty("mapId",out var mapArg)||
                       mapArg.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(mapArg.GetString(),out var mapId)||
                       !obj.TryGetProperty("transactionId",out var txArg)||
                       txArg.ValueKind!=JsonValueKind.String||
                       txArg.GetString() is not string transactionId||
                       transactionId.Length!=32)
                        return Err(id,"UM-API-CORE-ROLLBACK-SELECTION");
                    var local=Local(mapId);
                    if(local==null||!Eligible(local))
                        return Err(id,"UM-API-CORE-ROLLBACK-NOT-STOPPED");
                    var candidate=AsaApiOwnedInstaller.ListCompletedRollbacks(cluster,mapId)
                        .SingleOrDefault(x=>x.TransactionId==transactionId);
                    if(candidate==null)return Err(id,"UM-API-CORE-ROLLBACK-NOT-COMMITTED");
                    pendingRollback=new RollbackApproval(Guid.NewGuid(),mapId,
                        local.InstallationPath,transactionId,
                        DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id,new {
                        token=pendingRollback.Token,mapId,mapName=local.Name,
                        transactionId,releaseTag=candidate.ReleaseTag,
                        binaries=candidate.Binaries,
                        approved=false,doesNotRestart=true,
                        evidence="STOPPED_OWNED_COMMITTED_ROLLBACK_REQUIRES_BYTE_REVALIDATION"
                    });
                }
                if(method=="api.coreRollbackApply")
                {
                    if(!obj.TryGetProperty("token",out var t)||
                       t.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(t.GetString(),out var token)||
                       !obj.TryGetProperty("confirm",out var yes)||
                       yes.ValueKind!=JsonValueKind.True||
                       pendingRollback==null||pendingRollback.Token!=token)
                        return Err(id,"UM-API-CORE-ROLLBACK-APPROVAL");
                    var selected=pendingRollback;pendingRollback=null;
                    if(selected.Until<DateTimeOffset.UtcNow)
                        return Err(id,"UM-API-CORE-ROLLBACK-PREVIEW-EXPIRED");
                    var local=Local(selected.MapId);
                    if(local==null||local.InstallationPath!=selected.Root||!Eligible(local))
                        return Err(id,"UM-API-CORE-ROLLBACK-OWNERSHIP-CHANGED");
                    if(!AsaApiOwnedInstaller.ListCompletedRollbacks(cluster,local.Id)
                        .Any(x=>x.TransactionId==selected.TransactionId))
                        return Err(id,"UM-API-CORE-ROLLBACK-TRANSACTION-CHANGED");
                    var result=await Task.Run(()=>
                        AsaApiOwnedInstaller.RollbackCompletedOwnedStopped(
                            cluster,local.Id,selected.TransactionId,true));
                    return Ok(id,new {
                        mapId=result.MapId,transactionId=result.TransactionId,
                        filesRestored=result.FilesRestored,filesRemoved=result.FilesRemoved,
                        bytesVerified=result.ByteVerified,
                        serverRestarted=result.Restarted,evidence=result.Evidence
                    });
                }
                if(!obj.TryGetProperty("token",out var t)||
                   t.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(t.GetString(),out var token)||
                   !obj.TryGetProperty("confirm",out var confirm)||
                   confirm.ValueKind!=JsonValueKind.True||
                   pending==null||pending.Token!=token)
                    return Err(id,"UM-API-CORE-EXPLICIT-APPROVAL-REQUIRED");
                var approved=pending;pending=null;
                if(approved.Until<DateTimeOffset.UtcNow)
                    return Err(id,"UM-API-CORE-PREVIEW-EXPIRED");
                var owned=Local(approved.MapId);
                if(owned==null||owned.InstallationPath!=approved.Root||!Eligible(owned))
                    return Err(id,"UM-API-CORE-OWNERSHIP-CHANGED");
                var latestNow=await AsaApiReleaseCatalog.FetchLatestAsync(http);
                var selected=latestNow.Assets.SingleOrDefault(a=>a.Name==approved.AssetName);
                if(latestNow.Tag!=approved.Tag||selected==null ||
                   selected.OfficialSha256!=approved.Sha256)
                    return Err(id,"UM-API-CORE-UPSTREAM-CHANGED");
                string staged=Path.Combine(Path.GetTempPath(),
                    "Untamed-ApiLab-"+Guid.NewGuid().ToString("N"));
                var result=await AsaApiReleaseStaging.StageAsync(http,latestNow,selected,staged);
                if(result.OfficialSha256!=approved.Sha256)
                    return Err(id,"UM-API-CORE-UNEXPECTED-STAGED-HASH");
                var installed=await Task.Run(()=>AsaApiOwnedInstaller.ApplyOwnedStopped(
                    cluster,approved.MapId,staged,latestNow,selected,true));
                // The pinned rollback transaction is retained in the Manager
                // profile. Disposable release staging is not needed again.
                try{Directory.Delete(staged,true);}
                catch(IOException){/* optional staging cleanup never invalidates installed receipt */}
                catch(UnauthorizedAccessException){/* keep verified transaction result */}
                return Ok(id,new{
                    mapId=installed.MapId,releaseTag=installed.ReleaseTag,
                    transactionId=installed.TransactionId,binaries=installed.Binaries,
                    bytesVerified=installed.ByteVerified,
                    rollbackSnapshot=installed.HasProtectiveCopy,
                    runtimeLoadedVerified=installed.LoaderRuntimeVerified,
                    serverRestarted=installed.Restarted,
                    evidence=installed.Evidence
                });
            }
            finally{serial.Release();}
        }
        catch(Exception ex) when(ex is IOException or JsonException or
            InvalidOperationException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException or
            HttpRequestException or TaskCanceledException)
        {
            return Err(id,"UM-API-CORE-TRANSACTION-FAILED_REVIEW_JOURNAL");
        }
    }
    public void Dispose()
    {
        serial.Dispose();
        if(ownsClient)http.Dispose();
    }
}
