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
    private sealed record SupplementalPlan(LocalMap Map,string Source,string FourSha,
        string LaunchSha,string Fingerprint,long RegistryRevision,bool Running,string ClusterId,
        string TransferRoot,string InventoryHash,SupplementalMapContentProbe.Evidence Content);
    private sealed record SupplementalApproval(Guid Token,Guid MapId,bool StoppedUseApi,
        string Fingerprint,DateTimeOffset ExpiresUtc);
    private readonly Dictionary<Guid,SupplementalApproval> supplementalApprovals=new();
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
    // One bounded approval per source-pinned map; invalid previews preserve prior tokens.
    private readonly Dictionary<Guid,RuntimeSettingsPrepared> runtimeSettingsByMap=new();

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
                "historical.supplementalPreview" or "historical.supplementalRegister" or "historical.supplementalStart" or
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
                foreach(var expired in runtimeSettingsByMap.Where(x=>
                    x.Value.ExpiresUtc<DateTimeOffset.UtcNow)
                    .Select(x=>x.Key).ToArray())runtimeSettingsByMap.Remove(expired);

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
                            LegacyStoreV1ActiveLaunch.DecodeOriginalIni(iniBytes),
                            SourcePinnedClusterOnly:false));
                        string gusSha=Convert.ToHexString(SHA256.HashData(iniBytes));
                        if(!runtimeSettingsByMap.ContainsKey(runtimeMap.Id)&&
                           runtimeSettingsByMap.Count>=25)
                            return Fail(id,"UM-STORE-RUNTIME-PREVIEW-CAPACITY");
                        var token=Guid.NewGuid();
                        runtimeSettingsByMap[runtimeMap.Id]=new(token,runtimeMap.Id,
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
                       !runtimeSettingsByMap.Values.Any(x=>x.Token==runtimeToken))
                        return Fail(id,"UM-STORE-RUNTIME-EXPLICIT-APPROVAL");
                    var runtimeStage=runtimeSettingsByMap.Values.Single(x=>x.Token==runtimeToken);
                    runtimeSettingsByMap.Remove(runtimeStage.MapId); // one-shot only after explicit consent
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
                            "active",LegacyStoreV1ActiveLaunch.DecodeOriginalIni(current),
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
                    // Only a *successful* re-preview supersedes an earlier
                    // valid five-map takeover approval. A failed WMI/UNC or
                    // journal check must not revoke an unrelated consent.
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
                    if(approved.ExpiresUtc<DateTimeOffset.UtcNow)
                    {
                        pendingOriginalLiveBatch=null;
                        return Fail(id,"UM-ORIGINAL-LIVE-APPROVAL-EXPIRED");
                    }
                    if(!BoolField(data,"confirm",out var confirm)||!confirm||
                       !BoolField(data,"externalControllerStopped",out var detached)||!detached||
                       !TextField(data,"typedApproval",60,out var phrase)||
                       phrase!="STORE-V1 FUTÓ ÁTVÉTEL")
                        return Fail(id,"UM-ORIGINAL-LIVE-EXPLICIT-APPROVAL-REQUIRED");
                    // Consume only after explicit owner approval; never
                    // replay any failed or ambiguous physical takeover.
                    pendingOriginalLiveBatch=null;
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
                            bool sourceUnchanged=
                                again.FourFilesSha256==approved.FourFilesHash&&
                                again.LaunchProfileSha256==approved.LaunchHash&&
                                now.ProcessSha256==approved.ProcessHash&&
                                peer==approved.SharedStateHash&&
                                OriginalLiveRegistryHash(again)==approved.RegistryHash&&
                                now.Maps.Select(m=>m.Id).SequenceEqual(approved.MapIds);
                            if(!sourceUnchanged)return false;
                            // The generic Prepared journal belongs to THIS
                            // AcquireMany operation. Check only independent
                            // SaveWorld intents and distinguish their failure
                            // from a real source/parent/PID mismatch.
                            DemandNoPendingOriginalSaveEvidence(now.Maps);
                            return true;
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

                if(method=="historical.supplementalPreview")
                {
                    if(!GuidField(data,"mapId",out var extraId)||
                       !BoolField(data,"stoppedUseApi",out var stoppedApi))
                        return Fail(id,"UM-STORE-EXTRA-REQUEST");
                    // Repeat clicks never allocate another GUID or duplicate a
                    // successfully imported profile. No operation on existing maps.
                    var existing=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==extraId);
                    if(existing!=null)
                    {
                        if(!existing.HistoricalSupplementalSource||
                           !LegacyStoreV1PinnedSource.HasApprovedLocalSource(cluster.Store.Root,existing))
                            return Fail(id,"UM-STORE-EXTRA-GUID-OR-NAME-CONFLICT");
                        return Ok(id,new{alreadyRegistered=true,mapId=extraId,mapName=existing.Name});
                    }
                    var plan=await Task.Run(()=>PrepareSupplemental(extraId,stoppedApi));
                    foreach(var stale in supplementalApprovals.Where(x=>x.Value.ExpiresUtc<=DateTimeOffset.UtcNow)
                        .Select(x=>x.Key).ToArray())supplementalApprovals.Remove(stale);
                    if(supplementalApprovals.Count>=32&&!supplementalApprovals.ContainsKey(extraId))
                        return Fail(id,"UM-STORE-EXTRA-PREVIEW-CAPACITY");
                    var token=Guid.NewGuid();
                    supplementalApprovals[extraId]=new(token,extraId,stoppedApi,plan.Fingerprint,
                        DateTimeOffset.UtcNow.AddMinutes(3));
                    return Ok(id,new{token,mapId=extraId,mapName=plan.Map.Name,mapKey=plan.Map.HistoricalMapKey,
                        gamePort=plan.Map.GamePort,queryPort=plan.Map.QueryPort,rconPort=plan.Map.RconPort,
                        mods=plan.Map.HistoricalEnabledMods,passiveMods=plan.Map.HistoricalPassiveMods,
                        running=plan.Running,useApi=plan.Map.StartWithAsaApi,clusterId=plan.ClusterId,
                        alreadyRegistered=false,addsClusterMembership=true,changesOriginalFive=false,
                        modContentInstallationVerified=false,modContentState=plan.Content.State,
                        presentModProjects=plan.Content.PresentProjects,unverifiedModProjects=plan.Content.UnverifiedProjects,
                        levelManifestProjects=plan.Content.LevelManifestProjects,serverProcessTouched=false});
                }
                if(method is "historical.supplementalRegister" or "historical.supplementalStart")
                {
                    SupplementalApproval approval;
                    if(method=="historical.supplementalStart")
                    {
                        if(!GuidField(data,"mapId",out var requestedExtra)||!BoolField(data,"stoppedUseApi",out var requestedApi))
                            return Fail(id,"UM-STORE-EXTRA-REQUEST");
                        var existing=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==requestedExtra);
                        if(existing!=null)
                        {
                            if(!existing.HistoricalSupplementalSource||
                               !LegacyStoreV1PinnedSource.HasApprovedLocalSource(cluster.Store.Root,existing)||
                               !cluster.Registry.Snapshot().ClusterRegistry.MapAssignments.Any(x=>x.MapId==requestedExtra))
                                return Fail(id,"UM-STORE-EXTRA-GUID-OR-NAME-CONFLICT");
                            return Ok(id,new{alreadyRegistered=true,mapId=existing.Id,mapName=existing.Name,
                                registered=true,processCommandIssued=false,scheduleChanged=false});
                        }
                        var initial=await Task.Run(()=>PrepareSupplemental(requestedExtra,requestedApi));
                        approval=new(Guid.NewGuid(),requestedExtra,requestedApi,initial.Fingerprint,DateTimeOffset.UtcNow.AddMinutes(3));
                        supplementalApprovals.Remove(requestedExtra);
                    }
                    else
                    {
                        if(!GuidField(data,"token",out var extraToken)||
                           !BoolField(data,"confirm",out var yes)||!yes)
                            return Fail(id,"UM-STORE-EXTRA-APPROVAL");
                        var stored=supplementalApprovals.Values.SingleOrDefault(x=>x.Token==extraToken);
                        if(stored==null)return Fail(id,"UM-STORE-EXTRA-PREVIEW-EXPIRED");
                        supplementalApprovals.Remove(stored.MapId);
                        approval=stored;
                    }
                    if(approval.ExpiresUtc<=DateTimeOffset.UtcNow)
                        return Fail(id,"UM-STORE-EXTRA-PREVIEW-EXPIRED");
                    var result=await Task.Run(()=>{
                        var before=PrepareSupplemental(approval.MapId,approval.StoppedUseApi);
                        if(before.Fingerprint!=approval.Fingerprint)
                            throw new InvalidDataException("UM-STORE-EXTRA-SOURCE-OR-PROCESS-CHANGED");
                        // This operation writes only the Manager profile; no
                        // live installation mutation journal is needed. A
                        // crash releases the process-scoped semaphore, and
                        // retry reuses the immutable pin or committed GUID.
                        using var registrationGate=new Mutex(false,
                            @"Global\Untamed-SupplementalImport-"+UntamedOperationCoordinator.ResourceKey(before.Map.InstallationPath));
                        bool acquired;
                        try{acquired=registrationGate.WaitOne(0);}
                        catch(AbandonedMutexException){acquired=true;}
                        if(!acquired)throw new IOException("UM-STORE-EXTRA-REGISTRATION-BUSY");
                        try
                        {
                        // Serialize with every ConfigStore.Save, including another
                        // Manager process. The synchronous worker owns both mutexes.
                        string configKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                            Path.GetFullPath(cluster.Store.FilePath).ToUpperInvariant())));
                        using var configGate=new Mutex(false,@"Global\Untamed-Config-"+configKey);
                        bool configAcquired;
                        try{configAcquired=configGate.WaitOne(0);}catch(AbandonedMutexException){configAcquired=true;}
                        if(!configAcquired)throw new IOException("UM-STORE-EXTRA-REGISTRATION-BUSY");
                        try
                        {
                        var current=PrepareSupplemental(approval.MapId,approval.StoppedUseApi);
                        if(current.Fingerprint!=approval.Fingerprint)
                            throw new InvalidDataException("UM-STORE-EXTRA-SOURCE-OR-PROCESS-CHANGED");
                        // Publish an immutable per-map pin first. A crash here
                        // leaves no profile or membership, and the exact next
                        // import may reuse the same verified pin.
                        bool orphanPinReconciled=LegacyStoreV1PinnedSource.ReconcileUnregisteredSupplemental(current.Source,cluster,
                            current.Map.Id,current.FourSha,current.LaunchSha);
                        var final=PrepareSupplemental(approval.MapId,approval.StoppedUseApi);
                        if(final.Fingerprint!=approval.Fingerprint)
                            throw new InvalidDataException("UM-STORE-EXTRA-SOURCE-OR-PROCESS-CHANGED");
                        final.Map.HistoricalSupplementalImportedUtc=DateTimeOffset.UtcNow;
                        // Single atomic config publication: no half-member
                        // or half-profile after an interrupted write.
                        cluster.RegisterSupplementalHistoricalMap(final.Map,final.ClusterId,
                            final.TransferRoot,final.RegistryRevision);
                        bool unchanged=false;
                        try{unchanged=SupplementalInventoryHash(LocalRunningAsaDiscovery.ReadSamples())==final.InventoryHash;}
                        catch(Exception){/* registration remains committed, report unknown */ }
                        return new{mapId=final.Map.Id,mapName=final.Map.Name,registered=true,
                            alreadyRegistered=false,running=final.Running,originalGuidPreserved=true,
                            modContentState=final.Content.State,presentModProjects=final.Content.PresentProjects,
                            unverifiedModProjects=final.Content.UnverifiedProjects,levelManifestProjects=final.Content.LevelManifestProjects,
                            processInventoryUnchanged=unchanged,processCommandIssued=false,
                            originalStoreTouched=false,scheduleChanged=false,orphanPinReconciled,
                            requiresLocalAcceptance=!unchanged};
                        }
                        finally{configGate.ReleaseMutex();}
                        }
                        finally{registrationGate.ReleaseMutex();}
                    });
                    return Ok(id,result);
                }
                if(method=="historical.originalList")
                {
                    var local=LegacyStoreV1OriginalImport.Inspect(PreferredSource());
                    var supplemental=local.AdditionalMaps;
                    // A pinned five-map source stays immutable. A later local
                    // legacy roster may be inspected for supplemental maps only
                    // if every original GUID/level/root is still identical.
                    string additionalSource="pinned-or-current";
                    if(Directory.Exists(localLegacyDataDirectory))
                    {
                        try
                        {
                            var current=LegacyStoreV1OriginalImport.Inspect(localLegacyDataDirectory);
                            bool sameOwners=current.Maps.Length==local.Maps.Length&&
                                local.Maps.All(m=>current.Maps.Any(x=>x.MapId==m.MapId&&
                                    x.MapKey==m.MapKey&&string.Equals(x.OriginalInstallationRoot,
                                        m.OriginalInstallationRoot,StringComparison.OrdinalIgnoreCase)));
                            if(sameOwners)
                            {supplemental=current.AdditionalMaps;additionalSource="current-local-roster";}
                            else additionalSource="current-roster-original-owners-conflict";
                        }
                        catch(IOException){additionalSource="current-local-roster-unavailable";}
                        catch(InvalidDataException){additionalSource="current-local-roster-unverified";}
                        catch(UnauthorizedAccessException){additionalSource="current-local-roster-unavailable";}
                    }
                    return Ok(id,new{
                        sourceVerified=true,
                        originalFourFilesUnchanged=true,
                        additionalSource,
                        additionalMaps=supplemental.Select(m=>new{
                            mapId=m.MapId,mapKey=m.MapKey,
                            active=m.Active,sessionMatches=m.SessionMatches,
                            sessionBinding=m.SessionBinding,
                            independentActivationMetadataComplete=m.IndependentActivationMetadataComplete,
                            backupIdentityPresent=m.BackupIdentityPresent,
                            gamePort=m.GamePort,queryPort=m.QueryPort,
                            enabledMods=m.EnabledMods?.Length,passiveMods=m.PassiveMods?.Length,
                            launchState=m.LaunchState,
                            selectedHistoricalOwner=false,storeWriterAuthorized=false,
                            registered=cluster.Config.LocalMaps.Any(x=>x.Id==m.MapId)
                        }).ToArray(),
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
                    // Cap applies to independent local maps, not previews.
                    // Refuse a new map without revoking earlier owner approvals.
                    if(pendingOriginal.Count>=8&&
                       !pendingOriginal.Values.Any(x=>x.MapId==selected.MapId))
                        return Fail(id,"UM-STORE-DIRECT-PREVIEW-CAPACITY");
                    foreach(var previous in pendingOriginal.Where(x=>
                        x.Value.MapId==selected.MapId).Select(x=>x.Key).ToArray())
                        pendingOriginal.Remove(previous);
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
                    if(pending.Count>=8&&
                       !pending.Values.Any(x=>x.MapId==mapId))
                        return Fail(id,"UM-HISTORICAL-PREVIEW-CAPACITY");
                    foreach(var previous in pending.Where(x=>x.Value.MapId==mapId)
                        .Select(x=>x.Key).ToArray())pending.Remove(previous);
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
            if(requestedMethod is "historical.supplementalPreview" or "historical.supplementalRegister" or "historical.supplementalStart")
                return Fail(id,"UM-STORE-EXTRA-PREFLIGHT-FAILED");
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

    private bool HasPendingOriginalSaveEvidence(IReadOnlyList<LocalMap> maps)
    {
        // SaveWorld-only persists intent before its RCON worker acquires
        // the standard installation lease. Check only that independent
        // receipt here; scanning a generic Prepared lease during takeover
        // would incorrectly reject the current takeover's OWN journal.
        foreach(var map in maps)
        {
            try
            {
                var save=LegacyStoreV1SaveEvidenceJournal.Inspect(
                    cluster.Store.Root,map.Id);
                if(save?.Phase is "prepared" or "needsOperator")return true;
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or
                UnauthorizedAccessException or JsonException)
            {
                // A corrupted or interrupted receipt cannot be treated as
                // completed merely because its current phase is unreadable.
                return true;
            }
        }
        return false;
    }

    private void DemandNoPendingOriginalSaveEvidence(
        IReadOnlyList<LocalMap> maps)
    {
        if(HasPendingOriginalSaveEvidence(maps))
            throw new InvalidOperationException(
                "UM-ORIGINAL-LIVE-PENDING-LIFECYCLE-JOURNAL");
    }

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
            // SaveWorld-only has a separate durable intent saved before
            // asynchronous RCON begins. The generic journal may still
            // report Completed while this receipt is unresolved.
            LegacyStoreV1SaveEvidence? save=null;
            bool unreadableSave=false;
            try
            {
                save=LegacyStoreV1SaveEvidenceJournal.Inspect(
                    cluster.Store.Root,map.Id);
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or
                UnauthorizedAccessException or JsonException)
            {
                // Return an exact affected map without trusting a malformed
                // receipt or exposing its contents/path to the WebView.
                unreadableSave=true;
            }
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
            if(unreadableSave)
                blocked.Add(new(map.Id,map.Name,"SaveWorld-v1",
                    "Unreadable",Guid.Empty,"UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE",
                    existing!=null,false));
            if(save?.Phase is "prepared" or "needsOperator")
            {
                bool supported=existing?.HistoricalStoreProtected==true;
                blocked.Add(new(map.Id,map.Name,"SaveWorld-v1",
                    save.Phase,save.OperationId,
                    "UM-ORIGINAL-SAVE-EVIDENCE-NEEDS-REVIEW",
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
        // Import only the original five lock-selected maps. Extra current
        // local event maps have been validated by the read-only Core reader,
        // but do not become members of the original Store registration.
        var selected=reportBefore.Maps.ToDictionary(x=>x.MapId);
        var members=root.GetProperty("members").EnumerateArray().Select(m=>{
            if(!Guid.TryParseExact(VaultReadSnapshot.Text(m,"id"),"D",out var guid)||
               guid==Guid.Empty)throw new InvalidDataException(
                    "UM-ORIGINAL-CLUSTER-MEMBER-INVALID");
            return(MapId:guid,MapKey:VaultReadSnapshot.Text(m,"map"));
        }).Where(m=>selected.ContainsKey(m.MapId)).ToArray();
        if(members.Length!=5||members.Any(m=>
            !string.Equals(m.MapKey,selected[m.MapId].MapKey,
                StringComparison.Ordinal)))
            throw new InvalidDataException(
                "UM-ORIGINAL-CLUSTER-SELECTED-MEMBER-CHANGED");
        var after=LegacyStoreV1LocalReader.Inspect(PreferredSource());
        if(!after.LocalSnapshotReady||
           !string.Equals(reportBefore.OriginalFourFilesTag,
               after.OriginalFourFilesTag,StringComparison.Ordinal))
            throw new IOException("UM-ORIGINAL-CLUSTER-SOURCE-CHANGED");
        return new(id,path,after.OriginalFourFilesTag,members);
    }

    private static string SupplementalHash(string material)=>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    private static string SupplementalInventoryHash(IReadOnlyList<LocalRunningAsaDiscovery.Sample> samples)
    {
        if(samples.Any(x=>x.Pid<=0||x.StartedUtc is not {Year:>=2020}||
            string.IsNullOrWhiteSpace(x.ExecutablePath))||
           samples.Select(x=>x.Pid).Distinct().Count()!=samples.Count)
            throw new InvalidDataException("UM-STORE-EXTRA-PROCESS-INVENTORY-UNVERIFIED");
        return SupplementalHash(JsonSerializer.Serialize(samples.OrderBy(x=>x.Pid)
            .Select(x=>new{x.Pid,x.ParentPid,x.StartedUtc,x.ExecutablePath,x.Name})));
    }

    private SupplementalPlan PrepareSupplemental(Guid id,bool stoppedUseApi)
    {
        if(cluster.Config.LocalMaps.Any(x=>x.Id==id))
            throw new InvalidDataException("UM-STORE-EXTRA-GUID-OR-NAME-CONFLICT");
        // The current local roster can expand independently of the old pin.
        // If the original controller is removed, a committed per-map pin
        // handles runtime; an unimported map needs its actual source.
        string sourcePath=Directory.Exists(localLegacyDataDirectory)?
            localLegacyDataDirectory:PreferredSource();
        var source=LegacyStoreV1OriginalImport.Inspect(sourcePath);
        var supplemental=LegacyStoreV1PinnedSource.RequireSupplemental(source,id);
        var original=LegacyStoreV1OriginalImport.Inspect(PreferredSource());
        if(!original.Maps.All(m=>source.Maps.Any(x=>x.MapId==m.MapId&&
            x.MapKey==m.MapKey&&string.Equals(Path.GetFullPath(x.OriginalInstallationRoot),
                Path.GetFullPath(m.OriginalInstallationRoot),StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("UM-STORE-EXTRA-ORIGINAL-ROSTER-MISMATCH");
        var oldReport=LegacyStoreV1LocalReader.Inspect(PreferredSource());
        var currentReport=LegacyStoreV1LocalReader.Inspect(sourcePath);
        if(oldReport.SessionTag!=currentReport.SessionTag||
           oldReport.DeviceTag!=currentReport.DeviceTag||
           oldReport.SharedPathTag!=currentReport.SharedPathTag)
            throw new InvalidDataException("UM-STORE-EXTRA-SESSION-MISMATCH");
        var originalCluster=LegacyStoreV1PinnedSource.ReadOriginalCluster(sourcePath);
        var registry=cluster.Registry.Snapshot();
        if(registry.HostRegistry.Maps.Any(x=>x.MapId==id)||
           registry.ClusterRegistry.MapAssignments.Any(x=>x.MapId==id))
            throw new InvalidDataException("UM-STORE-EXTRA-GUID-OR-NAME-CONFLICT");
        var registered=registry.ClusterRegistry.Clusters.SingleOrDefault(x=>
            x.ClusterId.Equals(originalCluster.RuntimeClusterId,StringComparison.OrdinalIgnoreCase));
        if(registered==null||!string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered.TransferDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalCluster.TransferRoot)),
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-STORE-EXTRA-CLUSTER-MISMATCH");
        // Verify existing shared original session through a real historical
        // anchor, never promote the supplemental GUID to the selected five.
        var anchor=source.Maps.FirstOrDefault(x=>cluster.Config.LocalMaps.Any(m=>
            m.Id==x.MapId&&m.HistoricalStoreProtected&&!m.HistoricalSupplementalSource))
            ??throw new InvalidDataException("UM-STORE-EXTRA-ORIGINAL-ANCHOR-MISSING");
        var anchorMap=cluster.Config.LocalMaps.Single(m=>m.Id==anchor.MapId);
        var shared=LegacyStoreV1LiveReadGate.InspectOriginalActive(anchorMap,sourcePath,supplementalMapId:id);
        if(!shared.CompletedOriginalSourceGuard||!shared.SharedSessionMatches||
           !shared.NoMutationMarker||!shared.NoPendingConversion||!shared.BothPeersDone||
           !shared.LocalMapBindingMatches||!shared.HistoricalFilesUnchanged)
            throw new InvalidDataException("UM-STORE-EXTRA-SHARED-SESSION-UNVERIFIED");
        string transferId=StorePathIdentity.LocalDirectoryFingerprint(originalCluster.TransferRoot);
        string root=DesktopLocalMapRegistryBridge.ValidateFixedLocalRoot(supplemental.OriginalInstallationRoot);
        string Normalize(string path)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        bool Overlap(string a,string b)
        {
            a=Normalize(a);b=Normalize(b);
            return a.Equals(b,StringComparison.OrdinalIgnoreCase)||
                a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
                b.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        }
        if(cluster.Config.LocalMaps.Any(m=>!string.IsNullOrWhiteSpace(m.InstallationPath)&&
            Overlap(root,m.InstallationPath))||
           source.Maps.Any(m=>Overlap(root,m.OriginalInstallationRoot))||
           source.AdditionalMaps.Any(m=>m.MapId!=id&&Overlap(root,m.OriginalInstallationRoot))||
           Overlap(root,cluster.Store.Root))
            throw new InvalidDataException("UM-STORE-EXTRA-ROOT-CONFLICT");
        var installation=LocalAsaInstallLocator.Probe(root);
        if(!installation.HasServerExecutable||string.IsNullOrWhiteSpace(installation.ServerExecutable))
            throw new InvalidDataException("UM-STORE-EXTRA-ASA-EXE-MISSING");
        string name=MapName(supplemental.MapKey);
        if(cluster.Config.LocalMaps.Any(m=>m.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))
            name=LocalMapNameRules.Normalize(name+" "+id.ToString("N")[..8]);
        if(cluster.Config.LocalMaps.Any(m=>m.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UM-STORE-EXTRA-GUID-OR-NAME-CONFLICT");
        var map=new LocalMap{
            Id=id,Name=name,InstallationPath=root,ExecutablePath=installation.ServerExecutable,
            GamePort=supplemental.GamePort!.Value,QueryPort=supplemental.QueryPort!.Value,
            HistoricalStoreProtected=true,HistoricalSupplementalSource=true,
            HistoricalArchiveSha256=source.FourFilesSha256,HistoricalMapKey=supplemental.MapKey,
            HistoricalEnabledMods=supplemental.EnabledMods!.ToList(),
            HistoricalPassiveMods=supplemental.PassiveMods!.ToList(),
            HistoricalRuntimeClusterId=originalCluster.RuntimeClusterId,
            HistoricalRuntimeTransferDirectory=originalCluster.TransferRoot,
            AutoStart=false,MonitorRcon=false,Arguments="",ApiLoaderArguments=""
        };
        var iniPath=AsaInitialRconSettings.NewIniPath(root);
        using var held=new VaultReadSnapshot();
        var ini=held.Read(iniPath,131072);
        var rcon=DesktopHistoricalRconAttachBridge.ReadExistingOriginalRcon(map);
        map.RconPort=rcon.Port;map.RconHost="127.0.0.1";
        map.RconPasswordProtected=SecureStore.Protect(rcon.Password);
        if(!AsaRconConfigurationEvidence.Matches(map))
            throw new InvalidDataException("UM-STORE-EXTRA-RCON-INI-MISMATCH");
        if(LocalMapPortPolicy.Conflicts(map,cluster.Config.LocalMaps).Count>0)
            throw new InvalidDataException("UM-STORE-EXTRA-PORT-CONFLICT");
        var operation=cluster.Operations.Observe(root);
        if(operation!=null&&operation.Phase is not
            (UntamedOperationPhase.Completed or UntamedOperationPhase.CancelledBeforeMutation or
             UntamedOperationPhase.OperatorReviewed))
            throw new InvalidDataException("UM-STORE-EXTRA-INSTALLATION-OPERATION-PENDING");
        var launch=LegacyStoreV1ActiveLaunch.Build(new(map.HistoricalMapKey,map.GamePort,
            map.QueryPort,map.HistoricalEnabledMods,map.HistoricalPassiveMods,
            originalCluster.RuntimeClusterId,originalCluster.TransferRoot,"active",
            LegacyStoreV1ActiveLaunch.DecodeOriginalIni(ini),SourcePinnedClusterOnly:false));
        string loader=Path.Combine(Path.GetDirectoryName(map.ExecutablePath)!,"AsaApiLoader.exe");
        var samples=LocalRunningAsaDiscovery.ReadSamples();
        string inventory=SupplementalInventoryHash(samples);
        var children=samples.Where(x=>string.Equals(x.ExecutablePath,map.ExecutablePath,StringComparison.OrdinalIgnoreCase)).ToArray();
        var parents=samples.Where(x=>string.Equals(x.ExecutablePath,loader,StringComparison.OrdinalIgnoreCase)).ToArray();
        bool running=children.Length>0||parents.Length>0;
        if(running)
        {
            if(children.Length!=1||parents.Length>1)
                throw new InvalidDataException("UM-STORE-EXTRA-PID-AMBIGUOUS");
            var child=children[0];
            map.StartWithAsaApi=parents.Length==1;
            if(map.StartWithAsaApi)
            {
                var parent=parents[0];
                bool exactParent=LegacyStoreV1ProcessObserver.MatchesOriginalActiveCommand(
                    parent.CommandLine,loader,map);
                bool exactChild=LegacyStoreV1ProcessObserver.MatchesOriginalActiveCommand(
                    child.CommandLine,map.ExecutablePath,map)||
                    LegacyStoreV1ProcessObserver.MatchesOriginalApiLoaderChild(
                        child.CommandLine,map.ExecutablePath,child.Pid,child.ParentPid,
                        child.StartedUtc!.Value,parent.CommandLine,loader,parent.Pid,parent.StartedUtc!.Value,map);
                if(!exactParent||!exactChild||child.ParentPid!=parent.Pid||
                   parent.StartedUtc>child.StartedUtc)
                    throw new InvalidDataException("UM-STORE-EXTRA-LOADER-PID-OR-LAUNCH-MISMATCH");
                map.ApiLoaderPath=loader;
            }
            else if(!LegacyStoreV1ProcessObserver.MatchesOriginalActiveCommand(
                child.CommandLine,map.ExecutablePath,map))
                throw new InvalidDataException("UM-STORE-EXTRA-PID-OR-LAUNCH-MISMATCH");
            var observed=LegacyStoreV1ProcessObserver.Observe(map,cluster.Config.LocalMaps);
            if(!observed.Running||observed.Identity?.Pid!=child.Pid||
               observed.Identity.StartedUtc!=child.StartedUtc||
               observed.LiveModOrder==null||observed.LivePassiveModOrder==null||
               !observed.LiveModOrder.SequenceEqual(map.HistoricalEnabledMods)||
               !observed.LivePassiveModOrder.SequenceEqual(map.HistoricalPassiveMods)||
               (parents.Length==1&&(observed.LoaderIdentity?.Pid!=parents[0].Pid||
                   observed.LoaderIdentity.StartedUtc!=parents[0].StartedUtc)))
                throw new InvalidDataException("UM-STORE-EXTRA-PID-OR-LAUNCH-MISMATCH");
            if(!RconPortOwnership.IsOwnedListener(map.RconPort,child.Pid)||
               !RconPortOwnership.OwnsUdpPort(map.GamePort,child.Pid)||
               !RconPortOwnership.OwnsUdpPort(map.QueryPort,child.Pid))
                throw new InvalidDataException("UM-STORE-EXTRA-PORT-OWNER-MISMATCH");
            map.LastProcess=observed.Identity;map.LastLoader=observed.LoaderIdentity;
        }
        else
        {
            var stopped=LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(map,cluster.Config.LocalMaps);
            if(!stopped.ConfirmedStopped||stopped.Running||stopped.Identity!=null||stopped.LoaderIdentity!=null)
                throw new InvalidDataException("UM-STORE-EXTRA-STOPPED-UNVERIFIED");
            if(!AsaLocalPortPreflight.CanStart(map))
                throw new InvalidDataException("UM-STORE-EXTRA-PORT-BUSY");
            map.StartWithAsaApi=stoppedUseApi;
            if(stoppedUseApi)map.ApiLoaderPath=loader;
        }
        if(map.StartWithAsaApi)
        {
            var copy=JsonSerializer.Deserialize<LocalMap>(JsonSerializer.Serialize(map))!;
            copy.Arguments=string.Join(" ",launch.Arguments);copy.ApiLoaderArguments=copy.Arguments;
            try{_=AsaApiLaunchPolicy.Resolve(copy);}
            catch(Exception){throw new InvalidDataException("UM-STORE-EXTRA-API-INSTALLATION-UNVERIFIED");}
        }
        var content=SupplementalMapContentProbe.Inspect(root,map.HistoricalMapKey,map.HistoricalEnabledMods);
        var after=LegacyStoreV1OriginalImport.Inspect(sourcePath);
        if(after.FourFilesSha256!=source.FourFilesSha256||after.LaunchProfileSha256!=source.LaunchProfileSha256||
           SupplementalInventoryHash(LocalRunningAsaDiscovery.ReadSamples())!=inventory)
            throw new IOException("UM-STORE-EXTRA-SOURCE-OR-PROCESS-CHANGED");
        string fingerprint=SupplementalHash(JsonSerializer.Serialize(new{
            id,source.FourFilesSha256,source.LaunchProfileSha256,registry.Revision,
            root,transferId,shared.SourceStateSha256,inventory,iniSha=Convert.ToHexString(SHA256.HashData(ini)),
            launch=launch.Arguments,map.Name,map.RconPort,map.StartWithAsaApi,contentFingerprint=content.Fingerprint,
            commands=children.Concat(parents).OrderBy(p=>p.Pid).Select(p=>p.CommandLine)}));
        return new(map,sourcePath,source.FourFilesSha256,source.LaunchProfileSha256,
            fingerprint,registry.Revision,running,originalCluster.RuntimeClusterId,
            originalCluster.TransferRoot,inventory,content);
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
        "Asa_N_Chill_Hub"=>"Event",
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
