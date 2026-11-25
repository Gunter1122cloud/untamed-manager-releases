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
/// Operator-approved batch maintenance for ONLY locally owned, positively
/// STOPPED ASA installations, including original Store-v1 maps whose
/// source identity and stopped ASA/Loader process family are proved natively.
/// Delegates all writes to SteamStoppedMapUpdater.UpdateAllStoppedAsync/Core
/// queue, with a
/// single-use preview receipt and no auto-retry after interruption.
/// </summary>
public sealed class DesktopStoppedSteamBatchBridge : IDisposable
{
    private sealed record Candidate(Guid MapId,string Root,string Build);
    private sealed record Pending(Guid Token,Candidate[] Maps,string SteamExe,
        DateTimeOffset CreatedUtc,bool UseCache);

    private sealed class InlineProgress<T>(Action<T> report):IProgress<T>
    {
        public void Report(T value)=>report(value);
    }
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public string State="queued",Stage="preflight";
        public int Position,Total;
        public string ActiveMap="";
        public int CompletedInstallations;
        public string? ErrorCode;
        public readonly Dictionary<Guid,object> Results=new();
        public DateTimeOffset ChangedUtc=DateTimeOffset.UtcNow;
    }
    private readonly ClusterService cluster;
    private readonly bool isolatedExecution;
    private readonly Func<IReadOnlyList<LocalMap>,IProgress<SteamQueueProgress>,bool,
        Task<IReadOnlyList<SteamStoppedMapUpdater.Receipt>>> runBatch;
    private readonly Func<LocalMap,SteamUpdatePreview> plan;
    private readonly Func<LocalMap,bool,ProcessCheck> observeOriginal;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly object sync=new();
    private const long Reserve=10L*1024*1024*1024;
    private Pending? pending;
    private Job? job;
    internal bool RecoveryLaneBusy()=>RunningJob();
    private readonly HashSet<Guid> acceptedDirectRequests=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopStoppedSteamBatchBridge(ClusterService owner,
        Func<IReadOnlyList<LocalMap>,IProgress<SteamQueueProgress>,
            Task<IReadOnlyList<SteamStoppedMapUpdater.Receipt>>>? isolatedRunner=null,
        Func<LocalMap,SteamUpdatePreview>? isolatedPlanner=null,
        Func<LocalMap,bool,ProcessCheck>? isolatedOriginalObserver=null)
    {
        cluster=owner??throw new ArgumentNullException(nameof(owner));
        isolatedExecution=isolatedRunner!=null;
        // Production always uses the exact native stopped-family guard; an
        // injected process fixture is not a route exposed to WebView2.
        observeOriginal=isolatedOriginalObserver??((map,underOwnOperation)=>
            HistoricalLocalConfigGuard.DemandStopped(cluster,map,underOwnOperation));
        runBatch=isolatedRunner!=null?((maps,progress,useCache)=>{
            if(useCache)throw new InvalidOperationException("UM-STEAM-CACHE-ISOLATED-RUNNER");
            return isolatedRunner(maps,progress);
        }):((maps,progress,useCache)=>
            SteamStoppedMapUpdater.UpdateAllStoppedAsync(cluster,maps,
                cluster.Config.SteamCmdExecutable,Reserve,
                TimeSpan.FromHours(2),progress,
                originalStoppedObserver:member=>ObserveForSteam(member),
                originalStoppedObserverUnderLease:
                    member=>ObserveForSteam(member,true),useSharedPayloadCache:useCache));
        plan=isolatedPlanner??(map=>SteamUpdateDryRun.Plan(
            cluster.Config.SteamCmdExecutable,map.InstallationPath,
            cluster.Config.LocalMaps.ToArray(),
            member=>ObserveForSteam(member),Reserve));
    }

    private ProcessCheck ObserveForSteam(LocalMap map,bool underOwnOperation=false)=>
        map.HistoricalStoreProtected?
            observeOriginal(map,underOwnOperation):cluster.Check(map);

    private static string Ok(string? id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    private static bool GuidField(JsonElement doc,string key,out Guid value)
    {
        value=Guid.Empty;
        return doc.TryGetProperty(key,out var node) &&
            node.ValueKind==JsonValueKind.String &&
            Guid.TryParse(node.GetString(),out value)&&value!=Guid.Empty;
    }

    private bool BlockedJournal()
    {
        var current=new SteamSerialMaintenanceQueue(
            cluster.Store.Root,cluster.Config.LocalHostId).Observe();
        return SteamSerialMaintenanceQueue.BlocksNewWork(current);
    }
    private bool RunningJob()
    {
        lock(sync)return job!=null&&(job.State is "queued" or "running");
    }
    private object JobSnapshot(Job item)
    {
        lock(sync)return new{
            jobId=item.Id,state=item.State,stage=item.Stage,
            position=item.Position,total=item.Total,
            activeMapName=item.ActiveMap,
            completedInstallations=item.CompletedInstallations,errorCode=item.ErrorCode,
            results=item.Results.Values.ToArray(),
            changedUtc=item.ChangedUtc,
            serverStopRequested=false,remoteHostTouched=false,
            storeWriterTouched=false,automaticRetry=false
        };
    }
    private void Stage(Job item,string state,string stage,
        int position=0,string mapName="")
    {
        lock(sync)
        {
            // Progress<T> may post from another synchronization context
            // after the awaited runner returned. Never let a late event
            // turn a verified completion back into 'running'.
            if(item.State is "completed" or "needsOperator")return;
            item.State=state;item.Stage=stage;
            if(position>0)item.Position=position;
            if(mapName.Length>0)item.ActiveMap=mapName;
            item.ChangedUtc=DateTimeOffset.UtcNow;
        }
    }
    private (LocalMap[] Maps,Candidate[] Evidence,object[] Details) ValidateMaps(
        Guid[] requested)
    {
        if(requested.Length is <1 or >25 ||
           requested.Distinct().Count()!=requested.Length)
            throw new InvalidDataException("UM-STEAM-BATCH-INVALID-MAPS");
        if(BlockedJournal()||RunningJob())
            throw new InvalidOperationException("UM-STEAM-BATCH-BUSY-OR-JOURNAL");
        _=SteamCmdPreflight.Inspect(cluster.Config.SteamCmdExecutable);
        var chosen=new List<LocalMap>();
        foreach(var mapId in requested)
        {
            var local=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
            if(local==null)
                throw new UnauthorizedAccessException("UM-STEAM-BATCH-NOT-LOCAL");
            if(local.Maintenance||
               !AsaReadyProbe.IsRegisteredServerBinary(local))
                throw new UnauthorizedAccessException("UM-STEAM-BATCH-PROTECTED");
            if(local.HistoricalStoreProtected)
            {
                // Existing, approved offline Store profile, checked with
                // its own stopped-family Windows observer. No session/write.
                _=ObserveForSteam(local);
            }
            else if(FreshStoreEnrollmentPolicy.IsEnrolled(local))
            {
                // Exact local enrollment, never inferred from a UI toggle.
                FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,local);
            }
            else if(!string.IsNullOrWhiteSpace(local.HistoricalArchiveSha256) ||
                !string.IsNullOrWhiteSpace(local.HistoricalMapKey) ||
                !string.IsNullOrWhiteSpace(local.FreshStoreClusterId) ||
                !string.IsNullOrWhiteSpace(local.FreshStoreTransferDirectory) ||
                !string.IsNullOrWhiteSpace(local.StoreTestRoot) ||
                KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(local))
                throw new UnauthorizedAccessException("UM-STEAM-BATCH-PROTECTED");
            var process=ObserveForSteam(local);
            if(!process.ConfirmedStopped||process.Running||
               process.Identity!=null||process.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-STEAM-BATCH-NOT-STOPPED");
            chosen.Add(local);
        }
        // Core updates one shared installation once. Preview must use the
        // SAME grouping/order as Core, including the first map per directory.
        var unique=chosen.GroupBy(m=>Path.GetFullPath(m.InstallationPath)
                .TrimEnd(Path.DirectorySeparatorChar),StringComparer.OrdinalIgnoreCase)
            .Select(g=>g.First()).ToArray();
        // Reject nested/overlapping installation roots as a single
        // transaction: Core must never update a root while another selected
        // installation is a child of it, even if their map GUIDs differ.
        var roots=unique.Select(m=>Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(m.InstallationPath))).ToArray();
        for(int left=0;left<roots.Length;left++)
            for(int right=left+1;right<roots.Length;right++)
                if(roots[left].StartsWith(roots[right]+Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase) ||
                   roots[right].StartsWith(roots[left]+Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "UM-STEAM-BATCH-OVERLAPPING-INSTALLATIONS");
        var evidence=new List<Candidate>();
        var details=new List<object>();
        foreach(var map in unique)
        {
            // Keep the React batch preflight identical to the Core updater;
            // mixed Store or nested unselected ownership must fail before
            // an approval token is issued.
            SteamStoppedMapUpdater.DemandSafeLocalInstallation(
                cluster,map,member=>ObserveForSteam(member));
            string root=Path.GetFullPath(map.InstallationPath);
            foreach(var sibling in cluster.Config.LocalMaps.Where(m=>
                !string.IsNullOrWhiteSpace(m.InstallationPath) &&
                string.Equals(Path.GetFullPath(m.InstallationPath),
                    root,StringComparison.OrdinalIgnoreCase)))
            {
                var state=ObserveForSteam(sibling);
                if(state.Running||!state.ConfirmedStopped||
                   state.Identity!=null||state.LoaderIdentity!=null||
                   sibling.Maintenance||
                   sibling.HistoricalStoreProtected!=map.HistoricalStoreProtected||
                   (!sibling.HistoricalStoreProtected&&
                    !FreshStoreEnrollmentPolicy.IsEnrolled(sibling)&&(
                       !string.IsNullOrWhiteSpace(sibling.HistoricalMapKey)||
                       !string.IsNullOrWhiteSpace(sibling.HistoricalArchiveSha256)||
                       !string.IsNullOrWhiteSpace(sibling.FreshStoreClusterId)||
                       !string.IsNullOrWhiteSpace(sibling.FreshStoreTransferDirectory)||
                       !string.IsNullOrWhiteSpace(sibling.StoreTestRoot)||
                       KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(sibling))))
                    throw new InvalidOperationException(
                        "UM-STEAM-BATCH-SHARED-INSTALL-BLOCKED");
            }
            new AsaManualRestartJournal(root).DemandNoPending();
            var preview=plan(map);
            if(!preview.DryRunAllowed||
               string.IsNullOrWhiteSpace(preview.InstalledBuild)||
               !string.Equals(Path.GetFullPath(preview.InstallRoot),root,
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UM-STEAM-BATCH-PREFLIGHT");
            evidence.Add(new(map.Id,root,preview.InstalledBuild));
            details.Add(new{mapId=map.Id,mapName=map.Name,
                installedBuild=preview.InstalledBuild,
                sharedMapCount=preview.Maps.Length,
                availableBytes=preview.AvailableBytes,
                requiredReserveBytes=preview.EstimatedDownloadBytes});
        }
        return (unique,evidence.ToArray(),details.ToArray());
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3200)return Fail(null,"UM-STEAM-BATCH-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            if(data.ValueKind!=JsonValueKind.Object||
               !GuidField(data,"id",out var requestId) ||
               !data.TryGetProperty("method",out var node) ||
               node.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-STEAM-BATCH-MESSAGE");
            id=requestId.ToString("D");
            string? method=node.GetString();
            if(method is not ("steam.batchPreview" or "steam.batchApply" or "steam.batchStart" or
                "steam.batchStatus" or "steam.batchCurrent"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="steam.batchCurrent")
                {
                    var journal=new SteamSerialMaintenanceQueue(
                        cluster.Store.Root,cluster.Config.LocalHostId).Observe();
                    return Ok(id,new{
                        activeJob=job==null?null:JobSnapshot(job),
                        journalExists=journal!=null,
                        journalPhase=journal?.Phase.ToString(),
                        journalNextIndex=journal?.NextIndex,
                        journalTotal=journal?.MapIds.Length,
                        needsOperatorReview=SteamSerialMaintenanceQueue.BlocksNewWork(journal),
                        automaticRetry=false,remoteWritesAllowed=false
                    });
                }
                if(method=="steam.batchStatus")
                {
                    if(!GuidField(data,"jobId",out var lookup)||
                       job==null||job.Id!=lookup)
                        return Fail(id,"UM-STEAM-BATCH-JOB-MISSING");
                    return Ok(id,JobSnapshot(job));
                }
                bool direct=method=="steam.batchStart";
                Pending? saved=null;
                Guid[]? directIds=null;
                if(direct&&(acceptedDirectRequests.Contains(requestId)||acceptedDirectRequests.Count>=4096))
                    return Fail(id,"UM-STEAM-BATCH-REQUEST-CONSUMED");
                if(method=="steam.batchPreview"||direct)
                {
                    if(!data.TryGetProperty("mapIds",out var rawIds)||
                       rawIds.ValueKind!=JsonValueKind.Array||
                       rawIds.GetArrayLength() is <1 or >25)
                        return Fail(id,"UM-STEAM-BATCH-INVALID-MAPS");
                    var ids=new List<Guid>();
                    foreach(var element in rawIds.EnumerateArray())
                    {
                        if(element.ValueKind!=JsonValueKind.String ||
                           !Guid.TryParse(element.GetString(),out var mapId)||
                           mapId==Guid.Empty)
                            return Fail(id,"UM-STEAM-BATCH-INVALID-MAPS");
                        ids.Add(mapId);
                    }
                    var selected=ValidateMaps(ids.ToArray());
                    bool useCache=data.TryGetProperty("useSharedPayloadCache",out var cache)&&cache.ValueKind==JsonValueKind.True;
                    var token=Guid.NewGuid();
                    var proposal=new Pending(token,selected.Evidence,
                        cluster.Config.SteamCmdExecutable,DateTimeOffset.UtcNow,useCache);
                    if(direct)
                    {
                        directIds=ids.ToArray();
                        var roots=selected.Evidence.Select(x=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.Root))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if(cluster.Config.LocalMaps.Any(x=>!string.IsNullOrWhiteSpace(x.InstallationPath)&&
                           roots.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.InstallationPath)))&&!directIds.Contains(x.Id)))
                            return Fail(id,"UM-STEAM-BATCH-SELECT-ALL-SHARED-MAPS");
                        saved=proposal;
                    }
                    else
                    {
                    pending=proposal;
                    return Ok(id,new{
                        token,requestedMaps=ids.Count,
                        installations=selected.Evidence.Length,
                        useSharedPayloadCache=useCache,cacheMinimumStagingBytes=SteamSharedPayloadCache.MinimumStagingFreeBytes,
                        details=selected.Details,
                        requiresTypedApproval=true,
                        serverStopRequested=false,storeWriterTouched=false,
                        remoteHostTouched=false,
                        evidence="LOCAL_STOPPED_STEAM_SERIAL_BATCH_PREFLIGHT_NO_WRITES"
                    });
                    }
                }
                else
                {
                if(!GuidField(data,"token",out var approval)||
                   pending==null||pending.Token!=approval ||
                   DateTimeOffset.UtcNow-pending.CreatedUtc>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-STEAM-BATCH-TOKEN-EXPIRED");
                if(!data.TryGetProperty("confirm",out var confirmed) ||
                   confirmed.ValueKind!=JsonValueKind.True ||
                   !data.TryGetProperty("typedApproval",out var phrase) ||
                   phrase.ValueKind!=JsonValueKind.String ||
                   !string.Equals(phrase.GetString(),"HELYI SOROS STEAMCMD",
                       StringComparison.Ordinal))
                    return Fail(id,"UM-STEAM-BATCH-CONFIRMATION-REQUIRED");
                saved=pending;
                }
                if(saved==null||DateTimeOffset.UtcNow-saved.CreatedUtc>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-STEAM-BATCH-TOKEN-EXPIRED");
                pending=null; // one use only, even if later preflight fails
                if(!string.Equals(cluster.Config.SteamCmdExecutable,saved.SteamExe,
                    StringComparison.OrdinalIgnoreCase))
                    return Fail(id,"UM-STEAM-BATCH-CHANGED");
                var rechecked=ValidateMaps(directIds??saved.Maps.Select(x=>x.MapId).ToArray());
                if(directIds!=null)
                {
                    var roots=rechecked.Evidence.Select(x=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.Root))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if(cluster.Config.LocalMaps.Any(x=>!string.IsNullOrWhiteSpace(x.InstallationPath)&&
                       roots.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.InstallationPath)))&&!directIds.Contains(x.Id)))
                        return Fail(id,"UM-STEAM-BATCH-SELECT-ALL-SHARED-MAPS");
                }
                if(rechecked.Evidence.Length!=saved.Maps.Length ||
                   rechecked.Evidence.Where((x,i)=>
                       x.MapId!=saved.Maps[i].MapId ||
                       !string.Equals(x.Root,saved.Maps[i].Root,
                           StringComparison.OrdinalIgnoreCase) ||
                       !string.Equals(x.Build,saved.Maps[i].Build,
                           StringComparison.Ordinal)).Any())
                    return Fail(id,"UM-STEAM-BATCH-CHANGED");
                if(!isolatedExecution)
                {
                    try{SteamStoppedMapUpdater.DemandNoConcurrentSteamCmd();}
                    catch(IOException e){return Fail(id,e.Message is "UM-STEAM-EXTERNAL-PROCESS-BUSY" or "UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED"?e.Message:"UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED");}
                }
                if(direct)acceptedDirectRequests.Add(requestId);
                var task=new Job{Total=rechecked.Maps.Length};
                var events=new DiscordOperationEvents.NativeRun(cluster,task.Id,rechecked.Maps.Select(x=>x.Id).ToArray(),"Update");
                events.Emit("claimed","in-progress","NATIVE_STEAM_BATCH_ACCEPTED");
                job=task;
                _=Task.Run(async()=>
                {
                    Stage(task,"running","preflight");
                    events.Emit("started","in-progress","NATIVE_STEAM_BATCH_EXECUTION_STARTED");
                    void RetainReceipt(SteamStoppedMapUpdater.Receipt item)
                    {
                        var owner=rechecked.Maps.Single(m=>m.Id==item.MapId);
                        if(!item.ProtectedFilesIntact||!string.Equals(Path.GetFullPath(owner.InstallationPath),Path.GetFullPath(item.InstallRoot),StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("UM-STEAM-BATCH-RECEIPTS-INVALID");
                        lock(sync)
                        {
                            task.Results[item.MapId]=new{mapId=item.MapId,mapName=owner.Name,
                                oldBuild=item.OldBuild,newBuild=item.NewBuild,protectedFilesIntact=item.ProtectedFilesIntact};
                            task.CompletedInstallations=task.Results.Count;
                        }
                    }
                    var progress=new InlineProgress<SteamQueueProgress>(item=>{
                        if(item.VerifiedReceipt!=null)RetainReceipt(item.VerifiedReceipt);
                        Stage(task,"running",item.Stage,item.Position,item.MapName);
                    });
                    try
                    {
                        var receipt=await runBatch(rechecked.Maps,progress,saved.UseCache);
                        if(receipt.Count!=rechecked.Maps.Length ||
                           receipt.Any(x=>!x.ProtectedFilesIntact)||
                           !receipt.Select(x=>x.MapId).SequenceEqual(
                               rechecked.Maps.Select(m=>m.Id)))
                            throw new InvalidDataException(
                                "UM-STEAM-BATCH-RECEIPTS-INVALID");
                        foreach(var item in receipt)RetainReceipt(item);
                        Stage(task,"completed","verified");
                        events.Emit("terminal","verified-local","STEAM_BATCH_BUILDS_AND_PROTECTED_FILES_VERIFIED");
                    }
                    catch(Exception ex)
                    {
                        lock(sync)
                        {
                            task.ErrorCode=ex.Message is "UM-STEAM-EXTERNAL-PROCESS-BUSY" or "UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED" or "UM-STEAM-INSTALLED-BUILD-INVALID" or "UM-STEAM-INSTALLED-BUILD-CHANGED"?ex.Message:"UM-STEAM-BATCH-FAILED";
                            Stage(task,"needsOperator",task.Stage);
                        }
                        events.Emit("terminal","uncertain","STEAM_BATCH_NATIVE_QUEUE_REVIEW");
                    }
                });
                return Ok(id,new{
                    jobId=task.Id,started=true,
                    serverStopRequested=false,remoteHostTouched=false,
                    storeWriterTouched=false,automaticRetry=false,
                    evidence="EXPLICIT_LOCAL_STOPPED_SERIAL_STEAM_BATCH_STARTED"
                });
            }
            finally{gate.Release();}
        }
        catch(UnauthorizedAccessException){return Fail(id,"UM-STEAM-BATCH-PROTECTED-OR-REMOTE");}
        catch(InvalidDataException){return Fail(id,"UM-STEAM-BATCH-INVALID-PREFLIGHT");}
        catch(InvalidOperationException){return Fail(id,"UM-STEAM-BATCH-NOT-READY");}
        catch(Exception){return Fail(id,"UM-STEAM-BATCH-UNAVAILABLE");}
    }
    public void Dispose()=>gate.Dispose();
}
