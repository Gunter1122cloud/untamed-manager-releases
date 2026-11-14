using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private readonly string localLegacyDataDirectory;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Dictionary<Guid,Prepared> pending=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Prepared(Guid Token,string Archive,string SourceHash,
        Guid MapId,string MapKey,string LocalName,string Root,string ServerExe,
        int GamePort,int QueryPort,string[] Enabled,string[] Passive,
        DateTimeOffset ExpiresUtc);
    private sealed record OriginalPrepared(Guid Token,string FourFilesHash,
        string LaunchProfileHash,Guid MapId,string MapKey,string LocalName,
        string Root,string ServerExe,int GamePort,int QueryPort,
        string[] Enabled,string[] Passive,DateTimeOffset ExpiresUtc);
    private readonly Dictionary<Guid,OriginalPrepared> pendingOriginal=new();
    private sealed record OriginalBatchPrepared(Guid Token,string FourFilesHash,
        string LaunchProfileHash,Guid[] MapIds,DateTimeOffset ExpiresUtc);
    private OriginalBatchPrepared? pendingOriginalBatch;
    private sealed record OriginalLiveBatchPrepared(Guid Token,string FourFilesHash,
        string LaunchHash,string ProcessHash,string SharedStateHash,
        string RegistryHash,Guid[] MapIds,DateTimeOffset ExpiresUtc);
    private OriginalLiveBatchPrepared? pendingOriginalLiveBatch;
    private sealed record OriginalClusterPrepared(Guid Token,string FourFilesSha256,
        string SourceClusterId,string SourcePath,Guid[] OriginalMapIds,
        long RegistryRevision,DateTimeOffset ExpiresUtc);
    private OriginalClusterPrepared? pendingOriginalCluster;
    private sealed record SourcePinPrepared(Guid Token,string FourHash,
        string LaunchHash,Guid[] ApprovedLocalMapIds,DateTimeOffset ExpiresUtc);
    private SourcePinPrepared? pendingSourcePin;
    private sealed record RuntimeSettingsPrepared(Guid Token,Guid MapId,
        string MapSnapshot,string GusSha,int GamePort,int QueryPort,
        string ClusterId,string TransferRoot,DateTimeOffset ExpiresUtc);
    private RuntimeSettingsPrepared? pendingRuntimeSettings;

    public DesktopHistoricalStoreImportBridge(ClusterService service,
        Func<LocalMap,ProcessCheck>? processInspector=null,
        string? localLegacyDataDirectory=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        // An installed host can own multiple map profiles in one ASA binary
        // directory; identify each registered sibling instead of treating
        // its Windows process as an unknown conflict with the selected map.
        inspect=processInspector??(map=>new WindowsProcessInspector().InspectFamily(
            map,cluster.Config.LocalMaps));
        this.localLegacyDataDirectory=localLegacyDataDirectory??Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Kova");
    }

    // Import starts from the original Kova profile; after explicit approval,
    // the verified Manager pin is authoritative for subsequent local reads.
    // Every live shared peer/session check still runs against the real UNC.
    private string PreferredSource()=>LegacyStoreV1PinnedSource.PreferredProfile(
        cluster.Store.Root,localLegacyDataDirectory);

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        string? requestedMethod=null;
        try
        {
            // JSON escaping doubles backslashes in legitimate Windows launch paths.
            if(raw.Length is <2 or >8192)return Fail(null,"UM-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !GuidField(data,"id",out var requestId) ||
               !data.TryGetProperty("method",out var methodProp) ||
               methodProp.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-INVALID-MESSAGE");
            id=requestId.ToString("D");
            string? method=methodProp.GetString();
            requestedMethod=method;
            if(method is not ("historical.localStatus" or "historical.localParity" or "historical.list" or "historical.preview" or
                "historical.register" or "historical.pairedEvidence" or
                "historical.launchEvidence" or "historical.liveGuard" or
                "historical.originalList" or "historical.originalPreview" or
                "historical.originalRegister" or "historical.originalBatchPreview" or
                "historical.originalBatchRegister" or
                "historical.originalLiveBatchPreview" or
                "historical.originalLiveOperationBlockers" or
                "historical.operationJournalInventory" or
                "historical.originalLiveBatchApply" or
                "historical.originalClusterPreview" or
                "historical.originalClusterApply" or
                "historical.sourcePinPreview" or "historical.sourcePinApply" or
                "historical.runtimeSettingsStatus" or
                "historical.runtimeSettingsPreview" or "historical.runtimeSettingsApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                foreach(var expired in pending.Where(x=>x.Value.ExpiresUtc<DateTimeOffset.UtcNow)
                    .Select(x=>x.Key).ToArray())pending.Remove(expired);
                foreach(var expired in pendingOriginal.Where(x=>x.Value.ExpiresUtc<DateTimeOffset.UtcNow)
                    .Select(x=>x.Key).ToArray())pendingOriginal.Remove(expired);
                if(pendingOriginalBatch?.ExpiresUtc<DateTimeOffset.UtcNow)
                    pendingOriginalBatch=null;
                if(pendingOriginalLiveBatch?.ExpiresUtc<DateTimeOffset.UtcNow)
                    pendingOriginalLiveBatch=null;
                if(pendingOriginalCluster?.ExpiresUtc<DateTimeOffset.UtcNow)
                    pendingOriginalCluster=null;
                if(pendingSourcePin?.ExpiresUtc<DateTimeOffset.UtcNow)
                    pendingSourcePin=null;
                if(pendingRuntimeSettings?.ExpiresUtc<DateTimeOffset.UtcNow)
                    pendingRuntimeSettings=null;

                if(method=="historical.runtimeSettingsStatus")
                {
                    if(!GuidField(data,"mapId",out var mapId))
                        return Fail(id,"UM-STORE-RUNTIME-MAP-INVALID");
                    var localMap=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
                    if(localMap==null||!localMap.HistoricalStoreProtected||
                       localMap.ObservedReadOnlyImported)
                        return Fail(id,"UM-STORE-RUNTIME-LOCAL-MAP-REQUIRED");
                    string legacy=Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),"Kova");
                    string current=LegacyStoreV1PinnedSource.Resolve(
                        cluster.Store.Root,legacy,localMap);
                    if(string.Equals(current,legacy,StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-STORE-RUNTIME-SOURCE-PIN-REQUIRED");
                    var source=LegacyStoreV1PinnedSource.ReadOriginalCluster(current);
                    return Ok(id,new{
                        mapId=localMap.Id,mapName=localMap.Name,
                        gamePort=localMap.GamePort,queryPort=localMap.QueryPort,
                        clusterId=string.IsNullOrWhiteSpace(
                            localMap.HistoricalRuntimeClusterId)?
                            source.RuntimeClusterId:localMap.HistoricalRuntimeClusterId,
                        transferRoot=string.IsNullOrWhiteSpace(
                            localMap.HistoricalRuntimeTransferDirectory)?
                            source.TransferRoot:localMap.HistoricalRuntimeTransferDirectory,
                        originalSourcePinned=true,requiresStoppedForEdit=true,
                        peerRequiredForLocalStart=false,sharedTransferAccessVerified=false,
                        evidence="OBSIDIAN_LOCAL_RUNTIME_SETTINGS_READ_ONLY"
                    });
                }
                if(method is "historical.runtimeSettingsPreview" or
                    "historical.runtimeSettingsApply")
                {
                    // ONLY a source-pinned, locally owned map may edit its
                    // runtime ports/cluster without modifying the immutable
                    // original metadata or a live ASA/transfer file.
                    if(method=="historical.runtimeSettingsPreview")
                    {
                        pendingRuntimeSettings=null;
                        if(!GuidField(data,"mapId",out var mapId)||
                           !PortField(data,"gamePort",out var game)||
                           !PortField(data,"queryPort",out var query)||
                           game==query||
                           !TextField(data,"clusterId",80,out var clusterId)||
                           !TextField(data,"transferRoot",520,out var transferRoot))
                            return Fail(id,"UM-STORE-RUNTIME-SETTINGS-INVALID");
                        var runtimeMap=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
                        if(runtimeMap==null||!runtimeMap.HistoricalStoreProtected||
                           runtimeMap.ObservedReadOnlyImported)
                            return Fail(id,"UM-STORE-RUNTIME-LOCAL-MAP-REQUIRED");
                        string legacy=Path.Combine(Environment.GetFolderPath(
                            Environment.SpecialFolder.ApplicationData),"Kova");
                        string source=LegacyStoreV1PinnedSource.Resolve(
                            cluster.Store.Root,legacy,runtimeMap);
                        if(string.Equals(source,legacy,StringComparison.OrdinalIgnoreCase))
                            return Fail(id,"UM-STORE-RUNTIME-SOURCE-PIN-REQUIRED");
                        HistoricalLocalConfigGuard.DemandStopped(cluster,runtimeMap);
                        var candidate=JsonSerializer.Deserialize<LocalMap>(
                            JsonSerializer.Serialize(runtimeMap)) ??
                            throw new InvalidDataException("UM-STORE-RUNTIME-CLONE");
                        candidate.GamePort=game;candidate.QueryPort=query;
                        candidate.HistoricalRuntimeClusterId=clusterId;
                        candidate.HistoricalRuntimeTransferDirectory=transferRoot;
                        LocalMapPortPolicy.RequireNoConflicts(candidate,
                            cluster.Config.LocalMaps);
                        string ini=AsaInitialRconSettings.NewIniPath(runtimeMap.InstallationPath);
                        using var held=new VaultReadSnapshot();
                        byte[] iniBytes=held.Read(ini,131072);
                        // The existing editor is the ONLY way to modify an
                        // existing GUS. If it explicitly declares Port or
                        // QueryPort, it must match the new choice first.
                        var mods=ModStateController.Read(candidate);
                        _=LegacyStoreV1ActiveLaunch.Build(new(
                            candidate.HistoricalMapKey,game,query,
                            mods.EnabledOrder.ToArray(),mods.Passive.ToArray(),
                            clusterId,transferRoot,"active",
                            Encoding.UTF8.GetString(iniBytes),
                            SourcePinnedClusterOnly:false));
                        string gusSha=Convert.ToHexString(SHA256.HashData(iniBytes));
                        var token=Guid.NewGuid();
                        pendingRuntimeSettings=new(token,runtimeMap.Id,
                            JsonSerializer.Serialize(runtimeMap),gusSha,game,query,
                            clusterId,transferRoot,DateTimeOffset.UtcNow.AddMinutes(5));
                        return Ok(id,new{
                            token,mapId=runtimeMap.Id,gamePort=game,queryPort=query,
                            clusterId,transferRoot,sourcePinVerified=true,
                            noAutomaticTransfer=true,requiresStoppedProcess=true,
                            iniMustAlreadyMatchPorts=true,requiresApproval=true,
                            evidence="OBSIDIAN_LOCAL_RUNTIME_SETTINGS_PREVIEW"
                        });
                    }
                    if(!GuidField(data,"token",out var runtimeToken)||
                       !BoolField(data,"confirm",out var runtimeApproved)||
                       !runtimeApproved||
                       !TextField(data,"typedApproval",80,out var runtimePhrase)||
                       runtimePhrase!="OBSIDIAN MAP KONFIG"||
                       pendingRuntimeSettings?.Token!=runtimeToken)
                        return Fail(id,"UM-STORE-RUNTIME-EXPLICIT-APPROVAL");
                    var runtimeStage=pendingRuntimeSettings!;
                    pendingRuntimeSettings=null;
                    if(runtimeStage.ExpiresUtc<DateTimeOffset.UtcNow)
                        return Fail(id,"UM-STORE-RUNTIME-PREVIEW-EXPIRED");
                    var owned=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==runtimeStage.MapId);
                    if(owned==null||JsonSerializer.Serialize(owned)!=runtimeStage.MapSnapshot)
                        return Fail(id,"UM-STORE-RUNTIME-MAP-CHANGED");
                    HistoricalLocalConfigGuard.DemandStopped(cluster,owned);
                    using var operation=cluster.Operations.Acquire(
                        owned.InstallationPath,UntamedOperationKind.Configuration);
                    HistoricalLocalConfigGuard.DemandStopped(cluster,owned,
                        underOwnOperation:true);
                    if(JsonSerializer.Serialize(owned)!=runtimeStage.MapSnapshot)
                        return Fail(id,"UM-STORE-RUNTIME-MAP-CHANGED");
                    string original=LegacyStoreV1PinnedSource.Resolve(
                        cluster.Store.Root,Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),"Kova"),owned);
                    LegacyStoreV1PinnedSource.RequireImmutableIdentity(owned,
                        LegacyStoreV1OriginalImport.Inspect(original));
                    using(var held=new VaultReadSnapshot())
                    {
                        byte[] current=held.Read(AsaInitialRconSettings.NewIniPath(
                            owned.InstallationPath),131072);
                        if(Convert.ToHexString(SHA256.HashData(current))!=runtimeStage.GusSha)
                            return Fail(id,"UM-STORE-RUNTIME-INI-CHANGED");
                        var candidate=JsonSerializer.Deserialize<LocalMap>(
                            runtimeStage.MapSnapshot) ??
                            throw new InvalidDataException("UM-STORE-RUNTIME-CLONE");
                        candidate.GamePort=runtimeStage.GamePort;
                        candidate.QueryPort=runtimeStage.QueryPort;
                        candidate.HistoricalRuntimeClusterId=runtimeStage.ClusterId;
                        candidate.HistoricalRuntimeTransferDirectory=runtimeStage.TransferRoot;
                        LocalMapPortPolicy.RequireNoConflicts(candidate,
                            cluster.Config.LocalMaps);
                        var mods=ModStateController.Read(candidate);
                        _=LegacyStoreV1ActiveLaunch.Build(new(
                            candidate.HistoricalMapKey,candidate.GamePort,
                            candidate.QueryPort,mods.EnabledOrder.ToArray(),
                            mods.Passive.ToArray(),runtimeStage.ClusterId,runtimeStage.TransferRoot,
                            "active",Encoding.UTF8.GetString(current),
                            SourcePinnedClusterOnly:false));
                        operation.MarkMutationStarted();
                        cluster.ReplaceMap(owned,candidate);
                    }
                    operation.Complete();
                    return Ok(id,new{
                        mapId=runtimeStage.MapId,updated=true,
                        portAndClusterSettingsStoredInManager=true,
                        kovaFilesWritten=false,asaFilesWritten=false,
                        noServerStarted=true,noRconSent=true,
                        requiresManualAsaTransferVerification=true,
                        evidence="OBSIDIAN_APPROVED_OFFLINE_RUNTIME_SETTINGS_APPLIED"
                    });
                }
                if(method=="historical.sourcePinPreview")
                {
                    // Existing imported maps may predate Obsidian-owned source
                    // snapshots. Pin once, explicitly, without touching ASA,
                    // Kova or the shared transfer directory.
                    var current=LegacyStoreV1PinnedSource.PreferredProfile(
                        cluster.Store.Root,localLegacyDataDirectory);
                    if(!string.Equals(current,localLegacyDataDirectory,
                        StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-STORE-SOURCE-ALREADY-PINNED");
                    var source=LegacyStoreV1OriginalImport.Inspect(localLegacyDataDirectory);
                    var owned=cluster.Config.LocalMaps.Where(m=>
                        m.HistoricalStoreProtected && source.Maps.Any(o=>o.MapId==m.Id))
                        .ToArray();
                    if(owned.Length==0)
                        return Fail(id,"UM-STORE-SOURCE-NO-IMPORTED-LOCAL-MAPS");
                    foreach(var local in owned)
                        LegacyStoreV1PinnedSource.RequireImmutableIdentity(local,source);
                    var token=Guid.NewGuid();
                    pendingSourcePin=new(token,source.FourFilesSha256,
                        source.LaunchProfileSha256,owned.Select(m=>m.Id).ToArray(),
                        DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id,new{
                        token,verifiedLocalMaps=owned.Length,
                        requiresExplicitApproval=true,
                        originalMapIdentitiesPreserved=true,
                        sourceFilesWritten=false,serverStarted=false,
                        peerStatusRequired=false,
                        evidence="READ_ONLY_OBSIDIAN_SOURCE_PIN_PREVIEW"
                    });
                }
                if(method=="historical.sourcePinApply")
                {
                    if(!GuidField(data,"token",out var pinToken)||
                       !BoolField(data,"confirm",out var approved)||!approved||
                       !TextField(data,"typedApproval",80,out var phrase)||
                       phrase!="OBSIDIAN FORRÁS ÁTVÉTEL"||
                       pendingSourcePin?.Token!=pinToken)
                        return Fail(id,"UM-STORE-SOURCE-PIN-APPROVAL-REQUIRED");
                    var stage=pendingSourcePin!;
                    pendingSourcePin=null;
                    if(stage.ExpiresUtc<DateTimeOffset.UtcNow)
                        return Fail(id,"UM-STORE-SOURCE-PIN-EXPIRED");
                    var source=LegacyStoreV1OriginalImport.Inspect(localLegacyDataDirectory);
                    if(source.FourFilesSha256!=stage.FourHash||
                       source.LaunchProfileSha256!=stage.LaunchHash)
                        return Fail(id,"UM-STORE-SOURCE-PIN-CHANGED");
                    var current=cluster.Config.LocalMaps.Where(m=>
                        stage.ApprovedLocalMapIds.Contains(m.Id)).ToArray();
                    if(current.Length!=stage.ApprovedLocalMapIds.Length)
                        return Fail(id,"UM-STORE-SOURCE-PIN-MAP-REMOVED");
                    foreach(var sourceMap in current)
                        LegacyStoreV1PinnedSource.RequireImmutableIdentity(sourceMap,source);
                    LegacyStoreV1PinnedSource.Capture(localLegacyDataDirectory,
                        cluster.Store.Root,stage.FourHash,stage.LaunchHash);
                    return Ok(id,new{
                        pinned=true,localMaps=current.Length,
                        originalKovaRequiredAfterPin=false,
                        kovaFilesWritten=false,asaFilesWritten=false,
                        rconUsed=false,serverStarted=false,
                        sharedMarkersModified=false,
                        evidence="IMMUTABLE_SOURCE_PINNED_IN_OBSIDIAN_MANAGER_PROFILE"
                    });
                }

                if(method=="historical.originalClusterPreview")
                {
                    // Four source files are stable-handle read ONLY; no ASA,
                    // SMB writer, original Kova metadata or process mutation.
                    using var held=new VaultReadSnapshot();
                    var source=HistoricalClusterSource(held);
                    var registry=cluster.Registry.Snapshot();
                    var registered=registry.ClusterRegistry.Clusters.SingleOrDefault(c=>
                        c.ClusterId.Equals(source.ClusterId,StringComparison.OrdinalIgnoreCase));
                    int assigned=source.Members.Count(m=>
                        registry.ClusterRegistry.MapAssignments.Any(a=>
                            a.MapId==m.MapId&&a.ClusterId.Equals(source.ClusterId,
                                StringComparison.OrdinalIgnoreCase)));
                    bool sourcePinned=source.Members.All(m=>
                        cluster.Config.LocalMaps.Any(map=>
                            map.Id==m.MapId&&map.HistoricalStoreProtected&&
                            map.HistoricalArchiveSha256.Equals(source.Sha,StringComparison.OrdinalIgnoreCase)&&
                            string.Equals(map.HistoricalMapKey,m.MapKey,StringComparison.Ordinal)));
                    if(!sourcePinned)
                        return Fail(id,"UM-ORIGINAL-CLUSTER-LOCAL-FIVE-NOT-IMPORTED");
                    if(registered!=null && !string.Equals(
                        registered.TransferDirectory,source.TransferDirectory,
                        StringComparison.Ordinal))
                        return Fail(id,"UM-ORIGINAL-CLUSTER-TRANSFER-MISMATCH");
                    if(registry.ClusterRegistry.MapAssignments.Any(a=>
                        source.Members.Any(m=>m.MapId==a.MapId)&&
                        !a.ClusterId.Equals(source.ClusterId,StringComparison.OrdinalIgnoreCase)))
                        return Fail(id,"UM-ORIGINAL-CLUSTER-FOREIGN-ASSIGNMENT");
                    var token=Guid.NewGuid();
                    pendingOriginalCluster=new(token,source.Sha,source.ClusterId,
                        source.TransferDirectory,source.Members.Select(m=>m.MapId).ToArray(),
                        registry.Revision,DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id,new{
                        token,clusterId=source.ClusterId,
                        fiveOriginalMaps=source.Members.Length,
                        alreadyAssigned=assigned,missingAssignments=5-assigned,
                        registryRevision=registry.Revision,
                        sourceIdentityPreserved=true,originalStoreFilesWritten=false,
                        asaProcessTouched=false,writerSessionLockTouched=false,
                        requiresExactApproval=true,
                        evidence="ORIGINAL_FIVE_MAP_METADATA_BOOTSTRAP_PREVIEW_ONLY"
                    });
                }
                if(method=="historical.originalClusterApply")
                {
                    if(!GuidField(data,"token",out var token)||
                       !BoolField(data,"confirm",out var originalClusterApproved)||!originalClusterApproved||
                       !TextField(data,"typedApproval",60,out var approval)||
                       !string.Equals(approval,"EREDETI STORE REGISZTER",StringComparison.Ordinal)||
                       pendingOriginalCluster==null||
                       pendingOriginalCluster.Token!=token)
                        return Fail(id,"UM-ORIGINAL-CLUSTER-APPROVAL-REQUIRED");
                    var pendingStage=pendingOriginalCluster;
                    pendingOriginalCluster=null; // single-use, never replay
                    if(pendingStage.ExpiresUtc<DateTimeOffset.UtcNow)
                        return Fail(id,"UM-ORIGINAL-CLUSTER-PREVIEW-EXPIRED");
                    using var held=new VaultReadSnapshot();
                    var source=HistoricalClusterSource(held);
                    if(!string.Equals(source.Sha,pendingStage.FourFilesSha256,StringComparison.Ordinal)||
                       !string.Equals(source.ClusterId,pendingStage.SourceClusterId,StringComparison.Ordinal)||
                       !string.Equals(source.TransferDirectory,pendingStage.SourcePath,StringComparison.Ordinal)||
                       !source.Members.Select(m=>m.MapId).SequenceEqual(pendingStage.OriginalMapIds))
                        return Fail(id,"UM-ORIGINAL-CLUSTER-SOURCE-CHANGED");
                    var receipt=cluster.Registry.ImportHistoricalOriginalLocalMembership(
                        source.ClusterId,source.TransferDirectory,source.Sha,
                        source.Members,pendingStage.RegistryRevision);
                    var after=LegacyStoreV1LocalReader.Inspect(PreferredSource());
                    if(!after.LocalSnapshotReady||after.OriginalFourFilesTag!=source.Sha)
                        throw new IOException("UM-ORIGINAL-CLUSTER-SOURCE-CHANGED-DURING-COMMIT");
                    return Ok(id,new{
                        receipt.ClusterId,receipt.Revision,receipt.AddedCount,
                        receipt.RemovedCount,receipt.MovedCount,
                        originalFilesWritten=false,transferFilesWritten=false,
                        storeWriterTouched=false,serverStarted=false,
                        evidence="ORIGINAL_FIVE_MAP_MANAGER_REGISTRY_METADATA_ONLY"
                    });
                }

                if(method=="historical.operationJournalInventory")
                {
                    // Only local, bounded, registered-vs-unattributed journal
                    // metadata. No Kova source/ASA/PID/RCON/INI modifications.
                    LegacyStoreV1OriginalMap[]? trustedOriginal=null;
                    try
                    {
                        // Existing Kova source reader verifies all original
                        // five local IDs, launch roots and four immutable
                        // Store-v1 source hashes, without a writer.
                        trustedOriginal=LegacyStoreV1OriginalImport.Inspect(
                            PreferredSource()).Maps;
                    }
                    catch(Exception ex) when(ex is IOException or
                        UnauthorizedAccessException or ArgumentException or
                        JsonException)
                    {
                        // An absent original Kova source is not proof that an
                        // unmatched journal came from production or a test.
                        // Still return the local Manager inventory honestly.
                    }
                    return Ok(id,OperationJournalInventory.Inspect(
                        cluster.Config.LocalMaps,trustedOriginal));
                }
                if(method=="historical.originalLiveOperationBlockers")
                {
                    // This is a read-only diagnostic. It can locate the
                    // blocked installation even when another original map
                    // still lacks a Manager registration.
                    var (original,observed,_)=VerifyLiveOriginalFive();
                    var blocked=OriginalLiveOperationBlockers(observed.Maps);
                    return Ok(id,new{
                        mapCount=observed.Maps.Length,
                        blockedCount=blocked.Length,
                        blockedMaps=blocked,
                        allRecoveriesRequireFreshOwnerProof=true,
                        noAutomaticCommandRetry=true,
                        processStarted=false,processStopped=false,
                        evidence="ORIGINAL_FIVE_RUNNING_MAP_DURABLE_JOURNAL_BLOCKERS_READ_ONLY"
                    });
                }
                if(method=="historical.originalLiveBatchPreview")
                {
                    pendingOriginalLiveBatch=null;
                    var (original,observed,shared)=VerifyLiveOriginalFive();
                    // An existing NeedsOperator journal is NOT a source or
                    // process mismatch; surface it BEFORE offering a token
                    // that would inevitably fail at AcquireMany().
                    if(OriginalLiveOperationBlockers(observed.Maps).Length>0)
                        return Fail(id,"UM-ORIGINAL-LIVE-PENDING-LIFECYCLE-JOURNAL");
                    var preview=cluster.PreviewOriginalStoreLiveFive(observed.Maps);
                    var registryHash=OriginalLiveRegistryHash(original);
                    var token=Guid.NewGuid();
                    var expiresUtc=DateTimeOffset.UtcNow.AddMinutes(3);
                    pendingOriginalLiveBatch=new(token,original.FourFilesSha256,
                        original.LaunchProfileSha256,observed.ProcessSha256,shared,
                        registryHash,observed.Maps.Select(m=>m.Id).ToArray(),
                        expiresUtc);
                    return Ok(id,new {
                        token,expiresUtc,localMaps=observed.Maps.Length,
                        mapNames=observed.Maps.Select(m=>m.Name).ToArray(),
                        runningProcesses=observed.Maps.Length,
                        directProcesses=observed.DirectProcesses,
                        apiLoaderPairs=observed.ApiLoaderPairs,
                        alreadyRegistered=preview.Reattached,
                        newlyDiscovered=preview.Added,
                        sourceIdentityPreserved=true,
                        existingAsaProcessesUnaffected=true,
                        originalSessionLockWritten=false,
                        storeWriterAuthorizedByImport=false,
                        requiresExternalControllerStopped=true,
                        requiresTypedApproval=true,
                        evidence="ORIGINAL_ACTIVE_FIVE_RUNNING_ASA_NATIVE_IMPORT_PREFLIGHT"
                    });
                }
                if(method=="historical.originalLiveBatchApply")
                {
                    if(!GuidField(data,"token",out var token))
                        return Fail(id,"UM-ORIGINAL-LIVE-APPROVAL-INVALID");
                    if(pendingOriginalLiveBatch==null)
                        return Fail(id,"UM-ORIGINAL-LIVE-APPROVAL-EXPIRED");
                    if(pendingOriginalLiveBatch.Token!=token)
                        return Fail(id,"UM-ORIGINAL-LIVE-APPROVAL-STALE");
                    var approved=pendingOriginalLiveBatch;
                    pendingOriginalLiveBatch=null; // single use, including denial
                    if(approved.ExpiresUtc<DateTimeOffset.UtcNow)
                        return Fail(id,"UM-ORIGINAL-LIVE-APPROVAL-EXPIRED");
                    if(!BoolField(data,"confirm",out var confirm)||!confirm||
                       !BoolField(data,"externalControllerStopped",out var detached)||!detached||
                       !TextField(data,"typedApproval",60,out var phrase)||
                       phrase!="STORE-V1 FUTÓ ÁTVÉTEL")
                        return Fail(id,"UM-ORIGINAL-LIVE-EXPLICIT-APPROVAL-REQUIRED");
                    var (original,observed,shared)=VerifyLiveOriginalFive();
                    if(original.FourFilesSha256!=approved.FourFilesHash||
                       original.LaunchProfileSha256!=approved.LaunchHash||
                       observed.ProcessSha256!=approved.ProcessHash||
                       shared!=approved.SharedStateHash||
                       OriginalLiveRegistryHash(original)!=approved.RegistryHash||
                       !observed.Maps.Select(m=>m.Id).SequenceEqual(approved.MapIds))
                        return Fail(id,"UM-ORIGINAL-LIVE-SOURCE-OR-PROCESS-CHANGED");
                    if(OriginalLiveOperationBlockers(observed.Maps).Length>0)
                        return Fail(id,"UM-ORIGINAL-LIVE-PENDING-LIFECYCLE-JOURNAL");
                    // Lock all five installations in deterministic order.
                    // Under those locks the Core calls back for a SECOND
                    // complete original source + real Windows PID/parent
                    // inspection. Only the Manager registry is updated in
                    // a single ConfigStore CAS. All original Kova/ASA and
                    // two-host transfer bytes remain untouched.
                    LegacyStoreV1PinnedSource.Capture(PreferredSource(),
                        cluster.Store.Root,approved.FourFilesHash,approved.LaunchHash);
                    var outcome=cluster.ReconcileOriginalStoreLiveFive(
                        observed.Maps,()=>{
                            var (again,now,peer)=VerifyLiveOriginalFive();
                            return again.FourFilesSha256==approved.FourFilesHash&&
                                again.LaunchProfileSha256==approved.LaunchHash&&
                                now.ProcessSha256==approved.ProcessHash&&
                                peer==approved.SharedStateHash&&
                                OriginalLiveRegistryHash(again)==approved.RegistryHash&&
                                now.Maps.Select(m=>m.Id).SequenceEqual(approved.MapIds);
                        });
                    return Ok(id,new {
                        importedMaps=observed.Maps.Length,
                        addedMaps=outcome.Added,
                        reconnectedMaps=outcome.Reattached,
                        unchangedMaps=outcome.Unchanged,
                        mapIds=observed.Maps.Select(m=>m.Id).ToArray(),
                        apiLoaderPairs=observed.ApiLoaderPairs,
                        managerProfileOnly=true,
                        originalStoreFilesWritten=false,serverStarted=false,
                        serverStopped=false,storeWriterAuthorizedByImport=false,
                        requiresOriginalRconAttach=true,
                        evidence="ORIGINAL_ACTIVE_FIVE_RUNNING_MAPS_REGISTERED_NO_ASA_MUTATION"
                    });
                }

                if(method=="historical.originalBatchPreview")
                {
                    // Preflight EVERY not-yet-registered original batchMap before
                    // offering ONE consent token. No source or ASA mutation.
                    var source=LegacyStoreV1OriginalImport.Inspect(PreferredSource());
                    var remaining=source.Maps.Where(m=>!cluster.Config.LocalMaps
                        .Any(x=>x.Id==m.MapId)).ToArray();
                    if(remaining.Length==0)
                        return Fail(id,"UM-STORE-BATCH-ALREADY-REGISTERED");
                    string[] roots=remaining.Select(m=>
                        DesktopLocalMapRegistryBridge.ValidateFixedLocalRoot(
                            m.OriginalInstallationRoot)).ToArray();
                    for(int i=0;i<remaining.Length;i++)
                    {
                        var batchMap=remaining[i];
                        var root=roots[i];
                        if(roots.Where((_,j)=>j!=i).Any(other=>
                            root.Equals(other,StringComparison.OrdinalIgnoreCase)||
                            root.StartsWith(other+Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)||
                            other.StartsWith(root+Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)))
                            return Fail(id,"UM-STORE-BATCH-OVERLAPPING-ROOTS");
                        var name=MapName(batchMap.MapKey);
                        CheckDuplicate(batchMap.MapId,name,root);
                        var installation=LocalAsaInstallLocator.Probe(root);
                        if(!installation.HasServerExecutable||
                           string.IsNullOrWhiteSpace(installation.ServerExecutable))
                            return Fail(id,"UM-STORE-BATCH-ASA-EXE-MISSING");
                        var state=ObservedOriginal(batchMap.MapId,batchMap.MapKey,root,
                            installation.ServerExecutable);
                        if(state.Running||!state.ConfirmedStopped||
                           state.Identity!=null||state.LoaderIdentity!=null)
                            return Fail(id,"UM-STORE-BATCH-STOP-REQUIRED");
                    }
                    var token=Guid.NewGuid();
                    pendingOriginalBatch=new(token,source.FourFilesSha256,
                        source.LaunchProfileSha256,remaining.Select(m=>m.MapId).ToArray(),
                        DateTimeOffset.UtcNow.AddMinutes(7));
                    return Ok(id,new{
                        token,localMaps=remaining.Length,
                        mapNames=remaining.Select(m=>MapName(m.MapKey)).ToArray(),
                        sourceIdentityPreserved=true,originalFilesWritten=false,
                        processTouched=false,requiresExplicitApproval=true,
                        evidence="ORIGINAL_KOVA_ATOMIC_LOCAL_BATCH_PREVIEW"
                    });
                }
                if(method=="historical.originalBatchRegister")
                {
                    if(!GuidField(data,"token",out var token)||
                       !BoolField(data,"confirm",out var approved)||!approved||
                       pendingOriginalBatch==null||
                       pendingOriginalBatch.Token!=token)
                        return Fail(id,"UM-STORE-BATCH-APPROVAL-REQUIRED");
                    var batchStage=pendingOriginalBatch;
                    pendingOriginalBatch=null; // one-time token, even on error
                    if(batchStage.ExpiresUtc<DateTimeOffset.UtcNow)
                        return Fail(id,"UM-STORE-BATCH-APPROVAL-EXPIRED");
                    var source=LegacyStoreV1OriginalImport.Inspect(PreferredSource());
                    if(source.FourFilesSha256!=batchStage.FourFilesHash||
                       source.LaunchProfileSha256!=batchStage.LaunchProfileHash)
                        return Fail(id,"UM-STORE-BATCH-SOURCE-CHANGED");
                    var selected=source.Maps.Where(m=>batchStage.MapIds.Contains(m.MapId))
                        .ToArray();
                    if(selected.Length!=batchStage.MapIds.Length||
                       !selected.Select(m=>m.MapId).SequenceEqual(batchStage.MapIds))
                        return Fail(id,"UM-STORE-BATCH-SELECTION-CHANGED");
                    var additions=new List<LocalMap>();
                    var roots=new List<string>();
                    foreach(var batchMap in selected)
                    {
                        string root=DesktopLocalMapRegistryBridge.ValidateFixedLocalRoot(
                            batchMap.OriginalInstallationRoot);
                        if(roots.Any(other=>root.Equals(other,
                                StringComparison.OrdinalIgnoreCase)||
                            root.StartsWith(other+Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)||
                            other.StartsWith(root+Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)))
                            return Fail(id,"UM-STORE-BATCH-OVERLAPPING-ROOTS");
                        roots.Add(root);
                        string name=MapName(batchMap.MapKey);
                        CheckDuplicate(batchMap.MapId,name,root);
                        var installation=LocalAsaInstallLocator.Probe(root);
                        if(!installation.HasServerExecutable||
                           string.IsNullOrWhiteSpace(installation.ServerExecutable))
                            return Fail(id,"UM-STORE-BATCH-ASA-EXE-MISSING");
                        var state=ObservedOriginal(batchMap.MapId,batchMap.MapKey,root,
                            installation.ServerExecutable);
                        if(state.Running||!state.ConfirmedStopped||
                           state.Identity!=null||state.LoaderIdentity!=null)
                            return Fail(id,"UM-STORE-BATCH-STOP-REQUIRED");
                        additions.Add(new LocalMap{
                            Id=batchMap.MapId,Name=name,InstallationPath=root,
                            ExecutablePath=installation.ServerExecutable,
                            GamePort=batchMap.GamePort,QueryPort=batchMap.QueryPort,Arguments="",
                            HistoricalStoreProtected=true,
                            HistoricalArchiveSha256=source.FourFilesSha256,
                            HistoricalMapKey=batchMap.MapKey,
                            HistoricalEnabledMods=batchMap.EnabledMods.ToList(),
                            HistoricalPassiveMods=batchMap.PassiveMods.ToList(),
                            StartWithAsaApi=false,
                            HistoricalLaunchModeNeedsApproval=true,
                            AutoStart=false,MonitorRcon=false,
                            RconPort=0,ApiLoaderArguments=""
                        });
                    }
                    LegacyStoreV1PinnedSource.Capture(PreferredSource(),
                        cluster.Store.Root,batchStage.FourFilesHash,
                        batchStage.LaunchProfileHash);
                    cluster.AddMapsAtomically(additions);
                    return Ok(id,new{
                        importedMaps=additions.Count,
                        mapIds=additions.Select(m=>m.Id).ToArray(),
                        sourceIdentityPreserved=true,originalFilesWritten=false,
                        serverStarted=false,requiresOriginalRconAttach=true,
                        evidence="ORIGINAL_KOVA_ATOMIC_LOCAL_BATCH_REGISTERED"
                    });
                }

                if(method=="historical.originalList")
                {
                    var local=LegacyStoreV1OriginalImport.Inspect(PreferredSource());
                    return Ok(id,new{
                        sourceVerified=true,
                        originalFourFilesUnchanged=true,
                        maps=local.Maps.Select(m=>new{
                            mapId=m.MapId,mapKey=m.MapKey,name=MapName(m.MapKey),
                            folderName=Path.GetFileName(
                                Path.TrimEndingDirectorySeparator(m.OriginalInstallationRoot)),
                            gamePort=m.GamePort,queryPort=m.QueryPort,
                            enabledMods=m.EnabledMods.Length,
                            passiveMods=m.PassiveMods.Length,
                            registered=cluster.Config.LocalMaps.Any(x=>x.Id==m.MapId)
                        }).ToArray(),
                        evidence="ORIGINAL_KOVA_FIVE_LOCAL_MAPS_READ_ONLY"
                    });
                }
                if(method=="historical.originalPreview")
                {
                    if(!GuidField(data,"mapId",out var originalMapId)||
                       !PortField(data,"gamePort",out var originalGamePort)||
                       !PortField(data,"queryPort",out var originalQueryPort)||
                       originalGamePort==originalQueryPort)
                        return Fail(id,"UM-STORE-DIRECT-PORTS-INVALID");
                    var source=LegacyStoreV1OriginalImport.Inspect(PreferredSource());
                    var selected=source.Maps.SingleOrDefault(x=>x.MapId==originalMapId);
                    if(selected==null)return Fail(id,"UM-STORE-DIRECT-ORIGINAL-MAP-UNKNOWN");
                    if(originalGamePort!=selected.GamePort||
                       originalQueryPort!=selected.QueryPort)
                        return Fail(id,"UM-STORE-DIRECT-SOURCE-PORT-MISMATCH");
                    string root=DesktopLocalMapRegistryBridge.ValidateFixedLocalRoot(
                        selected.OriginalInstallationRoot);
                    var installation=LocalAsaInstallLocator.Probe(root);
                    if(!installation.HasServerExecutable||
                       string.IsNullOrWhiteSpace(installation.ServerExecutable))
                        return Fail(id,"UM-ASA-EXE-MISSING");
                    string name=MapName(selected.MapKey);
                    CheckDuplicate(selected.MapId,name,root);
                    var observed=ObservedOriginal(selected.MapId,selected.MapKey,root,
                        installation.ServerExecutable);
                    if(observed.Running||!observed.ConfirmedStopped||
                       observed.Identity!=null||observed.LoaderIdentity!=null)
                        return Fail(id,"UM-HISTORICAL-STOP-REQUIRED");
                    if(pendingOriginal.Count>=8)pendingOriginal.Clear();
                    var approval=Guid.NewGuid();
                    pendingOriginal[approval]=new(approval,source.FourFilesSha256,
                        source.LaunchProfileSha256,selected.MapId,selected.MapKey,name,root,
                        installation.ServerExecutable,originalGamePort,
                        originalQueryPort,selected.EnabledMods,selected.PassiveMods,
                        DateTimeOffset.UtcNow.AddMinutes(7));
                    return Ok(id,new{
                        token=approval,mapId=selected.MapId,mapName=name,
                        folderName=Path.GetFileName(root),
                        gamePort=originalGamePort,queryPort=originalQueryPort,
                        originalEnabledMods=selected.EnabledMods.Length,
                        originalPassiveMods=selected.PassiveMods.Length,
                        originalLaunchProfileVerified=true,
                        originalIdsPreserved=true,originalFilesWritten=false,
                        serverStarted=false,requiresOriginalRconAttach=true,
                        evidence="ORIGINAL_KOVA_DIRECT_IMPORT_PREVIEW"
                    });
                }
                if(method=="historical.originalRegister")
                {
                    if(!GuidField(data,"token",out var approval)||
                       !BoolField(data,"confirm",out var accept)||!accept||
                       !pendingOriginal.TryGetValue(approval,out var directApproval))
                        return Fail(id,"UM-STORE-DIRECT-EXPLICIT-APPROVAL-REQUIRED");
                    pendingOriginal.Remove(approval);
                    if(directApproval.ExpiresUtc<DateTimeOffset.UtcNow)
                        return Fail(id,"UM-STORE-DIRECT-PREVIEW-EXPIRED");
                    var source=LegacyStoreV1OriginalImport.Inspect(PreferredSource());
                    var selected=source.Maps.SingleOrDefault(x=>x.MapId==directApproval.MapId);
                    if(selected==null||source.FourFilesSha256!=directApproval.FourFilesHash||
                       source.LaunchProfileSha256!=directApproval.LaunchProfileHash||
                       selected.MapKey!=directApproval.MapKey||
                       selected.GamePort!=directApproval.GamePort||
                       selected.QueryPort!=directApproval.QueryPort||
                       !selected.EnabledMods.SequenceEqual(directApproval.Enabled,StringComparer.Ordinal)||
                       !selected.PassiveMods.SequenceEqual(directApproval.Passive,StringComparer.Ordinal))
                        return Fail(id,"UM-STORE-DIRECT-ORIGINAL-SOURCE-CHANGED");
                    string root=DesktopLocalMapRegistryBridge.ValidateFixedLocalRoot(
                        selected.OriginalInstallationRoot);
                    if(!root.Equals(directApproval.Root,StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-STORE-DIRECT-ORIGINAL-ROOT-CHANGED");
                    var installation=LocalAsaInstallLocator.Probe(root);
                    if(!installation.HasServerExecutable||
                       !string.Equals(installation.ServerExecutable,directApproval.ServerExe,
                           StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-STORE-DIRECT-INSTALL-CHANGED");
                    CheckDuplicate(directApproval.MapId,directApproval.LocalName,root);
                    var state=ObservedOriginal(directApproval.MapId,directApproval.MapKey,root,
                        directApproval.ServerExe);
                    if(state.Running||!state.ConfirmedStopped||
                       state.Identity!=null||state.LoaderIdentity!=null)
                        return Fail(id,"UM-HISTORICAL-STOP-REQUIRED");
                    // Register ONLY in Obsidian's own ConfigStore. No original
                    // Kova file, shared session, world or transfer byte changes.
                    LegacyStoreV1PinnedSource.Capture(PreferredSource(),
                        cluster.Store.Root,directApproval.FourFilesHash,
                        directApproval.LaunchProfileHash);
                    cluster.AddMap(new LocalMap{
                        Id=directApproval.MapId,Name=directApproval.LocalName,
                        InstallationPath=root,ExecutablePath=directApproval.ServerExe,
                        GamePort=directApproval.GamePort,QueryPort=directApproval.QueryPort,
                        Arguments="",HistoricalStoreProtected=true,
                        HistoricalArchiveSha256=directApproval.FourFilesHash,
                        HistoricalMapKey=directApproval.MapKey,
                        HistoricalEnabledMods=directApproval.Enabled.ToList(),
                        HistoricalPassiveMods=directApproval.Passive.ToList(),
                        StartWithAsaApi=false,
                        HistoricalLaunchModeNeedsApproval=true,
                        AutoStart=false,MonitorRcon=false,
                        RconPort=0,ApiLoaderArguments=""
                    });
                    return Ok(id,new{
                        mapId=directApproval.MapId,mapName=directApproval.LocalName,
                        registered=true,originalIdsPreserved=true,
                        originalFilesWritten=false,serverStarted=false,
                        requiresOriginalRconAttach=true,
                        originalLaunchProfileVerified=true,
                        requiresExactModOrderVerification=false,
                        evidence="ORIGINAL_KOVA_DIRECT_LOCAL_MAP_REGISTERED"
                    });
                }

                if(method=="historical.localParity")
                {
                    if(!TextField(data,"archive",520,out var parityArchive))
                        return Fail(id,"UM-STORE-PARITY-ARCHIVE-REQUIRED");
                    try
                    {
                        string archivePath=CanonicalArchive(parityArchive);
                        var parity=LegacyStoreV1ArchiveParity.Inspect(
                            archivePath,PreferredSource());
                        return Ok(id,new {
                            archiveVerified=parity.ArchiveVerified,
                            localStoreActive=parity.LocalStoreActive,
                            allFourExactlyMatch=parity.AllFourExactlyMatch,
                            files=parity.Files.Select(f=>new {
                                kind=f.Kind,exactlySame=f.ExactlySame
                            }).ToArray(),
                            liveTwoHostVerified=false,
                            characterTravelVerified=false,
                            productionUseAllowed=false,
                            evidence=parity.Evidence
                        });
                    }
                    catch(DirectoryNotFoundException)
                    {return Fail(id,"UM-STORE-PARITY-LOCAL-MISSING");}
                    catch(Exception)
                    {return Fail(id,"UM-STORE-PARITY-FAILED");}
                }

                if(method=="historical.liveGuard")
                {
                    // Explicit user-initiated read-only verification of the
                    // ORIGINAL Kova local metadata and the configured UNC
                    // coordination directory. No path can be supplied by UI.
                    if(!GuidField(data,"mapId",out var liveMapId))
                        return Fail(id,"UM-HISTORICAL-LIVE-MAP-REQUIRED");
                    var selected=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==liveMapId);
                    if(selected==null||!selected.HistoricalStoreProtected)
                        return Fail(id,"UM-HISTORICAL-LIVE-OWNED-STORE-ONLY");
                    try
                    {
                        var proof=await Task.Run(()=>
                            LegacyStoreV1LiveReadGate.InspectOriginalActive(
                                selected,PreferredSource()));
                        return Ok(id,new{
                            mapId=selected.Id,
                            originalActiveReady=proof.CompletedOriginalSourceGuard,
                            bothPeersDone=proof.BothPeersDone,
                            sharedSessionMatches=proof.SharedSessionMatches,
                            noPendingConversion=proof.NoPendingConversion,
                            noMutationMarker=proof.NoMutationMarker,
                            localMapBindingMatches=proof.LocalMapBindingMatches,
                            activeMaps=proof.ActiveMaps,donePeers=proof.DonePeers,
                            readOnly=true,serverStarted=false,
                            gameClientRejoinVerified=false,
                            productionLaunchEnabled=false,
                            evidence=proof.Evidence
                        });
                    }
                    catch(Exception)
                    {return Fail(id,"UM-HISTORICAL-LIVE-ORIGINAL-UNC-UNAVAILABLE-OR-INCONSISTENT");}
                }

                if(method=="historical.localStatus")
                {
                    // Only the local, explicitly configured legacy profile folder
                    // is inspected. No UI-supplied file path, no SMB or remote reads.
                    try
                    {
                        var proof=LegacyStoreV1LocalReader.Inspect(PreferredSource());
                        return Ok(id,new {
                            found=true,localFilesVerified=proof.LocalFilesVerified,
                            configHashVerified=proof.ClusterHashVerified,
                            localLockVerified=proof.LocalLockVerified,
                            allLocalMapsActive=proof.AllLocalMapsActive,
                            localSnapshotReady=proof.LocalSnapshotReady,
                            liveTwoHostVerified=false,characterTravelVerified=false,
                            maps=proof.Maps.Select(m=>new{
                                mapKey=m.MapKey,active=m.Active,
                                sessionMatches=m.SessionMatches,
                                rootMatches=m.RootMatches,selected=m.Selected,
                                backupIdentityPresent=m.BackupIdentityPresent
                            }).ToArray(),
                            productionUseAllowed=false,evidence=proof.Evidence
                        });
                    }
                    catch(DirectoryNotFoundException)
                    {return Fail(id,"UM-LOCAL-STORE-NOT-FOUND");}
                    catch(Exception)
                    {return Fail(id,"UM-LOCAL-STORE-VERIFICATION-FAILED");}
                }

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
                        historicalActiveProtocolShape=result.HistoricalActiveProtocolShape,
                        historicalStartBlockers=result.HistoricalStartBlockers,
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
                    // Archive-only registration is quarantined until the
                    // original direct-vs-ASA-API launch mode is approved.
                    // Never grant native offline edits simply because a
                    // Store metadata ZIP and empty WMI process list exist.
                    HistoricalLaunchModeNeedsApproval=true,
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
            // Allow only bounded, known machine-readable reasons. Raw native
            // messages can contain local paths, private Store fields or secrets.
            // A prior NeedsOperator lease is NOT a Kova source failure.
            if(ex.Message=="UNTAMED_OPERATION_NEEDS_OPERATOR")
                return Fail(id,"UM-ORIGINAL-LIVE-PENDING-LIFECYCLE-JOURNAL");
            if(ex.Message=="UNTAMED_OPERATION_BUSY")
                return Fail(id,"UM-ORIGINAL-LIVE-OTHER-MANAGER-OPERATION-IN-PROGRESS");
            if(ex.Message.Length is >=7 and <=112 &&
                System.Text.RegularExpressions.Regex.IsMatch(ex.Message,
                    @"\A(?:UM-[A-Z0-9_-]+|UNTAMED_[A-Z0-9_]+)\z",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)))
                return Fail(id,ex.Message);
            // A sanitized, operation-specific failure must replace the old
            // generic error. Never disclose exception text, file paths,
            // encrypted secrets or Store session bytes to the renderer.
            return Fail(id,requestedMethod switch{
                "historical.originalLiveBatchPreview"=>
                    "UM-ORIGINAL-LIVE-PREVIEW-UNEXPECTED-VERIFICATION",
                "historical.originalLiveBatchApply"=>
                    "UM-ORIGINAL-LIVE-APPLY-UNEXPECTED-VERIFICATION",
                "historical.originalLiveOperationBlockers"=>
                    "UM-ORIGINAL-LIVE-JOURNAL-INSPECTION-FAILED",
                "historical.operationJournalInventory"=>
                    "UM-OPERATION-JOURNAL-INVENTORY-UNAVAILABLE",
                _=>"UM-HISTORICAL-VERIFICATION-FAILED"
            });
        }
    }

    private sealed record OriginalLiveJournalBlocker(Guid MapId,
        string MapName,string Kind,string Phase,Guid OperationId,
        string BlockingCode,bool ExistingManagerProfile,
        bool SupportedReviewInMapDetails);

    private OriginalLiveJournalBlocker[] OriginalLiveOperationBlockers(
        IReadOnlyList<LocalMap> original)
    {
        var blocked=new List<OriginalLiveJournalBlocker>();
        foreach(var map in original)
        {
            // Every installation has TWO independent durable operation logs.
            // Previously only the common Launch journal was inspected:
            // a restart-only NeedsOperator could pass the five-map preview
            // and block the apply/next Stop with no actionable explanation.
            var journal=cluster.Operations.Observe(map.InstallationPath);
            var restart=new AsaManualRestartJournal(map.InstallationPath).Observe();
            var existing=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==map.Id);
            bool launchPending=journal?.Phase is (UntamedOperationPhase.Prepared or
                UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator);
            bool restartPending=restart?.Phase is (AsaRestartPhase.Stopping or
                AsaRestartPhase.Starting or AsaRestartPhase.NeedsOperator);
            bool sameOriginalPid=restart!=null&&restart.OldPid>0&&
                restart.OldPid==map.LastProcess?.Pid&&restart.NewPid==0&&
                restart.MapId==map.Id;
            if(launchPending)
            {
                bool supported=existing?.HistoricalStoreProtected==true &&
                    journal!.Kind==UntamedOperationKind.Launch &&
                    (!restartPending||sameOriginalPid&&restart?.Phase is
                        (AsaRestartPhase.Stopping or AsaRestartPhase.NeedsOperator));
                blocked.Add(new(map.Id,map.Name,journal!.Kind.ToString(),
                    journal.Phase.ToString(),journal.Operation,
                    supported?"UM-RECOVERY-ORIGINAL-LAUNCH-REVIEW-AVAILABLE":
                        "UM-ORIGINAL-LIVE-OTHER-JOURNAL-REQUIRES-REVIEW",
                    existing!=null,supported));
            }
            if(restartPending)
            {
                bool supported=existing?.HistoricalStoreProtected==true&&
                    sameOriginalPid&&restart!.Phase is
                        (AsaRestartPhase.Stopping or AsaRestartPhase.NeedsOperator)&&
                    (!launchPending||journal?.Kind==UntamedOperationKind.Launch);
                blocked.Add(new(map.Id,map.Name,"Restart-v1",
                    restart!.Phase.ToString(),restart.Id,
                    supported?(launchPending?
                        "UM-RECOVERY-PAIRED-LAUNCH-RESTART-REVIEW-AVAILABLE":
                        "UM-RECOVERY-RESTART-ONLY-REVIEW-AVAILABLE"):
                        "UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR",
                    existing!=null,supported));
            }
        }
        return blocked.ToArray();
    }

    private sealed record HistoricalClusterSourceData(string ClusterId,
        string TransferDirectory,string Sha,(Guid MapId,string MapKey)[] Members);

    private HistoricalClusterSourceData HistoricalClusterSource(VaultReadSnapshot held)
    {
        var reportBefore=LegacyStoreV1LocalReader.Inspect(PreferredSource());
        if(!reportBefore.LocalSnapshotReady || reportBefore.Maps.Length!=5)
            throw new InvalidDataException("UM-ORIGINAL-CLUSTER-SOURCE-NOT-READY");
        var bytes=held.Read(Path.Combine(PreferredSource(),"cluster-v1.json"),
            1_048_576);
        // Hold all FOUR originals open throughout preview/apply. If a writer
        // races the operation, Windows sharing rules reject its mutation.
        foreach(string name in new[]{"cluster-store-v1.json",
             "shared-store-device-v1.json","shared-store-lock-v1.json"})
            _=held.Read(Path.Combine(PreferredSource(),name),1_048_576);
        using var doc=JsonDocument.Parse(bytes);
        var root=doc.RootElement;
        string id=VaultReadSnapshot.Text(root,"id");
        string path=VaultReadSnapshot.Text(root,"path");
        var members=root.GetProperty("members").EnumerateArray().Select(m=>{
            if(!Guid.TryParseExact(VaultReadSnapshot.Text(m,"id"),"D",out var guid)||
               guid==Guid.Empty)throw new InvalidDataException(
                    "UM-ORIGINAL-CLUSTER-MEMBER-INVALID");
            return(MapId:guid,MapKey:VaultReadSnapshot.Text(m,"map"));
        }).ToArray();
        var after=LegacyStoreV1LocalReader.Inspect(PreferredSource());
        if(!after.LocalSnapshotReady||
           !string.Equals(reportBefore.OriginalFourFilesTag,
               after.OriginalFourFilesTag,StringComparison.Ordinal))
            throw new IOException("UM-ORIGINAL-CLUSTER-SOURCE-CHANGED");
        return new(id,path,after.OriginalFourFilesTag,members);
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

    private string OriginalLiveRegistryHash(
        LegacyStoreV1OriginalLocalSelection original)
    {
        // No raw configuration or DPAPI credential is exposed to WebView.
        // Any other Manager/editor changing one of these profiles between
        // the preview, apply and installation leases invalidates consent.
        var rows=original.Maps.Select(row=>{
            var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==row.MapId);
            return new{row.MapId,Profile=map==null?"<not-registered>":
                JsonSerializer.Serialize(map)};
        }).ToArray();
        return Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(rows))));
    }

    private (LegacyStoreV1OriginalLocalSelection Original,
        OriginalStoreLiveBatchEvidence.Result Observed,string SharedState)
        VerifyLiveOriginalFive()
    {
        var original=LegacyStoreV1OriginalImport.Inspect(PreferredSource());
        // Original five-map identity is mandatory, but a partly registered
        // Obsidian must be able to recover the OTHER maps without deleting
        // previously imported Kova profiles. Core validates any existing
        // owner against original GUID, root, map, exact mod order and ports.
        var observed=OriginalStoreLiveBatchEvidence.Classify(original,
            LocalRunningAsaDiscovery.ReadSamples());
        string? sharedSha=null;
        foreach(var map in observed.Maps)
        {
            if(!cluster.Config.LocalMaps.Any(m=>m.Id==map.Id))
                CheckDuplicate(map.Id,map.Name,
                    Path.GetFullPath(map.InstallationPath));
            var proof=LegacyStoreV1LiveReadGate.InspectOriginalActive(
                map,PreferredSource());
            if(!proof.CompletedOriginalSourceGuard||
               !proof.BothPeersDone||!proof.LocalActive||
               proof.ActiveMaps!=10||proof.DonePeers!=2||
               string.IsNullOrWhiteSpace(proof.SourceStateSha256)||
               (sharedSha!=null && sharedSha!=proof.SourceStateSha256))
                throw new InvalidDataException("UM-ORIGINAL-LIVE-PEER-SOURCE-UNVERIFIED");
            sharedSha=proof.SourceStateSha256;
            // Physical independent WMI process recheck; a fabricated
            // renderer PID and even synthetic Classify() result are never
            // sufficient to register a live original Store owner.
            var fresh=LegacyStoreV1ProcessObserver.Observe(map,observed.Maps);
            if(!fresh.Running||fresh.ConfirmedStopped||
               fresh.Identity is null||map.LastProcess is null||
               fresh.Identity.Pid!=map.LastProcess.Pid||
               fresh.Identity.StartedUtc!=map.LastProcess.StartedUtc||
               (map.StartWithAsaApi &&
                (fresh.LoaderIdentity is null||map.LastLoader is null||
                 fresh.LoaderIdentity.Pid!=map.LastLoader.Pid||
                 fresh.LoaderIdentity.StartedUtc!=map.LastLoader.StartedUtc))||
               (!map.StartWithAsaApi&&fresh.LoaderIdentity!=null))
                throw new InvalidDataException("UM-ORIGINAL-LIVE-PHYSICAL-IDENTITY-CHANGED");
        }
        // Enforce all five ownership invariants even in preview, not only
        // after approval. A foreign/reused GUID or modified INI/RCON profile
        // must remain a hard failure.
        _=cluster.PreviewOriginalStoreLiveFive(observed.Maps);
        var reread=LegacyStoreV1OriginalImport.Inspect(PreferredSource());
        if(reread.FourFilesSha256!=original.FourFilesSha256||
           reread.LaunchProfileSha256!=original.LaunchProfileSha256)
            throw new IOException("UM-ORIGINAL-LIVE-SOURCE-CHANGED");
        return(original,observed,sharedSha??throw new InvalidDataException(
            "UM-ORIGINAL-LIVE-PEER-SOURCE-UNVERIFIED"));
    }

    private ProcessCheck ObservedOriginal(Guid mapId,string mapKey,
        string root,string executable)
    {
        var map=new LocalMap{Id=mapId,Name=MapName(mapKey),
            HistoricalStoreProtected=true,HistoricalMapKey=mapKey,
            InstallationPath=root,ExecutablePath=executable,Arguments=""};
        string loader=Path.Combine(root,"ShooterGame","Binaries","Win64","AsaApiLoader.exe");
        if(File.Exists(loader))map.ApiLoaderPath=loader;
        return inspect(map);
    }

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
    private static bool PortField(JsonElement obj,string key,out int value)
    {
        value=0;
        return obj.TryGetProperty(key,out var item)&&
            item.ValueKind==JsonValueKind.Number &&
            item.TryGetInt32(out value) && value is >=1024 and <=65535;
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
