using System;
using System.Collections.Generic;
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
    // Each local map owns its own one-shot CORE install/rollback approval.
    // An invalid preview or a different map must never revoke it.
    private readonly Dictionary<Guid,Approval> pendingByMap=new();
    private readonly Dictionary<Guid,RollbackApproval> rollbackByMap=new();
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
        if(map.Maintenance||map.HistoricalStoreProtected||
           !AsaReadyProbe.IsRegisteredServerBinary(map))
            return false;
        if(FreshStoreEnrollmentPolicy.IsEnrolled(map))
        {
            try { FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map); }
            catch(Exception ex) when(ex is InvalidDataException or
                InvalidOperationException or IOException or UnauthorizedAccessException)
            { return false; }
        }
        else if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map)||
                UntamedWorldBackup.HasLiveOrHistoricalStore(map))
            return false;
        var restart=new AsaManualRestartJournal(map.InstallationPath).Observe();
        if(restart!=null&&restart.Phase is not
            (AsaRestartPhase.Completed or AsaRestartPhase.OperatorReviewed))return false;
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
                foreach(var stale in pendingByMap.Where(x=>x.Value.Until<DateTimeOffset.UtcNow)
                    .Select(x=>x.Key).ToArray())pendingByMap.Remove(stale);
                foreach(var stale in rollbackByMap.Where(x=>x.Value.Until<DateTimeOffset.UtcNow)
                    .Select(x=>x.Key).ToArray())rollbackByMap.Remove(stale);
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
                    if(!pendingByMap.ContainsKey(map.Id)&&pendingByMap.Count>=25)
                        return Err(id,"UM-API-CORE-PREVIEW-CAPACITY");
                    var approvedPreview=new Approval(Guid.NewGuid(),map.Id,map.InstallationPath,
                        assetName,release.Tag,asset.OfficialSha256!,
                        DateTimeOffset.UtcNow.AddMinutes(5));
                    pendingByMap[map.Id]=approvedPreview;
                    rollbackByMap.Remove(map.Id);
                    return Ok(id,new{
                        token=approvedPreview.Token,mapId=map.Id,mapName=map.Name,
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
                    if(!rollbackByMap.ContainsKey(mapId)&&rollbackByMap.Count>=25)
                        return Err(id,"UM-API-CORE-ROLLBACK-CAPACITY");
                    var approvedRollback=new RollbackApproval(Guid.NewGuid(),mapId,
                        local.InstallationPath,transactionId,
                        DateTimeOffset.UtcNow.AddMinutes(5));
                    rollbackByMap[mapId]=approvedRollback;
                    pendingByMap.Remove(mapId);
                    return Ok(id,new {
                        token=approvedRollback.Token,mapId,mapName=local.Name,
                        transactionId,releaseTag=candidate.ReleaseTag,
                        binaries=candidate.Binaries,
                        approved=false,doesNotRestart=true,
                        evidence="STOPPED_OWNED_COMMITTED_ROLLBACK_REQUIRES_BYTE_REVALIDATION"
                    });
                }
                if(method=="api.coreRollbackApply")
                {
                    if(!obj.TryGetProperty("token",out var rollbackTokenProp)||
                       rollbackTokenProp.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(rollbackTokenProp.GetString(),out var rollbackToken)||
                       !obj.TryGetProperty("confirm",out var yes)||
                       yes.ValueKind!=JsonValueKind.True)
                        return Err(id,"UM-API-CORE-ROLLBACK-APPROVAL");
                    var rollbackApproval=rollbackByMap.Values.SingleOrDefault(x=>x.Token==rollbackToken);
                    if(rollbackApproval==null)
                        return Err(id,"UM-API-CORE-ROLLBACK-APPROVAL");
                    rollbackByMap.Remove(rollbackApproval.MapId); // one shot
                    if(rollbackApproval.Until<DateTimeOffset.UtcNow)
                        return Err(id,"UM-API-CORE-ROLLBACK-PREVIEW-EXPIRED");
                    var local=Local(rollbackApproval.MapId);
                    if(local==null||local.InstallationPath!=rollbackApproval.Root||!Eligible(local))
                        return Err(id,"UM-API-CORE-ROLLBACK-OWNERSHIP-CHANGED");
                    if(!AsaApiOwnedInstaller.ListCompletedRollbacks(cluster,local.Id)
                        .Any(x=>x.TransactionId==rollbackApproval.TransactionId))
                        return Err(id,"UM-API-CORE-ROLLBACK-TRANSACTION-CHANGED");
                    var rollbackReceipt=await Task.Run(()=>
                        AsaApiOwnedInstaller.RollbackCompletedOwnedStopped(
                            cluster,local.Id,rollbackApproval.TransactionId,true));
                    return Ok(id,new {
                        mapId=rollbackReceipt.MapId,transactionId=rollbackReceipt.TransactionId,
                        filesRestored=rollbackReceipt.FilesRestored,filesRemoved=rollbackReceipt.FilesRemoved,
                        bytesVerified=rollbackReceipt.ByteVerified,
                        serverRestarted=rollbackReceipt.Restarted,evidence=rollbackReceipt.Evidence
                    });
                }
                if(!obj.TryGetProperty("token",out var t)||
                   t.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(t.GetString(),out var token)||
                   !obj.TryGetProperty("confirm",out var confirm)||
                   confirm.ValueKind!=JsonValueKind.True)
                    return Err(id,"UM-API-CORE-EXPLICIT-APPROVAL-REQUIRED");
                var approved=pendingByMap.Values.SingleOrDefault(x=>x.Token==token);
                if(approved==null)
                    return Err(id,"UM-API-CORE-EXPLICIT-APPROVAL-REQUIRED");
                pendingByMap.Remove(approved.MapId); // one shot
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
