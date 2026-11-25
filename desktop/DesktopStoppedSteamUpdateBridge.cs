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
/// Real SteamCMD 2430930 stopped-map update. Work is delegated to the existing
/// verified Core backup/queue/Steam updater. No live stop, remote host restart,
/// Store conversion, SQL or arbitrary paths are accepted from the renderer.
/// </summary>
public sealed class DesktopStoppedSteamUpdateBridge : IDisposable
{
    private sealed record Preview(Guid Token,Guid MapId,string Root,string Build,
        string Steam,DateTimeOffset Created);
    private sealed record DiscoveredSteamCmd(Guid Token,Guid MapId,
        SteamCmdFileStatus File,string Source,DateTimeOffset ExpiresUtc);

    private sealed class InlineProgress<T>(Action<T> report):IProgress<T>
    {
        public void Report(T value)=>report(value);
    }
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public Guid MapId;
        public string State="queued",Stage="preflight";
        public string OldBuild="",NewBuild="";
        public bool ProtectedFilesIntact;
        public string? ErrorCode;
        public DateTimeOffset Changed=DateTimeOffset.UtcNow;
    }
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim mutex=new(1,1);
    private readonly object sync=new();
    private readonly Func<LocalMap,SteamUpdatePreview> planner;
    private readonly bool isolatedExecution;
    private readonly Func<LocalMap,IProgress<SteamMaintenanceStage>,Task<SteamStoppedMapUpdater.Receipt>> runner;
    private readonly Func<LocalMap,CancellationToken,Task<AsaSteamBuildCheck>> publishedBuild;
    private readonly Func<string?> pickSteamCmd;
    private readonly Func<CancellationToken,Task<string>> prepareSteamCmd;
    private readonly Func<Guid,IEnumerable<(string Path,string Source)>> candidatePathsForDiscovery;
    // Each local map has its own approval; SteamCMD execution stays serialized.
    private readonly System.Collections.Generic.Dictionary<Guid,Preview> pendingByMap=new();
    private readonly System.Collections.Generic.Dictionary<Guid,DiscoveredSteamCmd> discoveredByToken=new();
    private readonly System.Collections.Generic.Dictionary<Guid,Job> jobsById=new();
    private Job? job;
    internal bool RecoveryLaneBusy(){lock(sync)return job?.State is "queued" or "running";}
    private readonly System.Collections.Generic.HashSet<Guid> acceptedDirectRequests=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private const long Reserve=10L*1024*1024*1024;

    public DesktopStoppedSteamUpdateBridge(ClusterService owner,
        Func<LocalMap,SteamUpdatePreview>? plan=null,
        Func<LocalMap,IProgress<SteamMaintenanceStage>,Task<SteamStoppedMapUpdater.Receipt>>? execute=null,
        Func<LocalMap,CancellationToken,Task<AsaSteamBuildCheck>>? inspectPublished=null,
        Func<string?>? selectSteamCmd=null,
        Func<CancellationToken,Task<string>>? prepareOfficialSteamCmd=null,
        Func<Guid,IEnumerable<(string Path,string Source)>>? candidatePaths=null)
    {
        cluster=owner??throw new ArgumentNullException(nameof(owner));
        isolatedExecution=execute!=null;
        pickSteamCmd=selectSteamCmd??PickSteamCmd;
        prepareSteamCmd=prepareOfficialSteamCmd??PrepareOfficialSteamCmdAsync;
        candidatePathsForDiscovery=candidatePaths??(mapId=>
            DefaultSteamCmdCandidates(mapId,cluster.Config.LocalMaps,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        planner=plan??(map=>SteamUpdateDryRun.Plan(
            cluster.Config.SteamCmdExecutable,map.InstallationPath,
            cluster.Config.LocalMaps.ToArray(),
            member=>ObserveForSteam(member),Reserve));
        runner=execute??((map,progress)=>map.HistoricalStoreProtected?
            SteamStoppedMapUpdater.UpdateOriginalStoppedAsync(
                cluster,map,cluster.Config.SteamCmdExecutable,Reserve,
                TimeSpan.FromHours(2),
                member=>ObserveForSteam(member),progress,
                originalStoppedObserverUnderLease:
                    member=>ObserveForSteam(member,true)):
            SteamStoppedMapUpdater.UpdateAsync(
                cluster,map,cluster.Config.SteamCmdExecutable,Reserve,
                TimeSpan.FromHours(2),progress));
        publishedBuild=inspectPublished??((map,token)=>
            AsaSteamBuildCatalog.CheckAsync(cluster.Config.SteamCmdExecutable,
                map.InstallationPath,"public",TimeSpan.FromSeconds(90),token));
    }
    private static Task<string> PrepareOfficialSteamCmdAsync(CancellationToken ct)
    {
        // Same managed tool path and bounded Valve HTTPS / validated ZIP
        // implementation as the fresh-install wizard, without app_update.
        string target=Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "UntamedManager","Tools","SteamCMD","steamcmd.exe");
        return SteamFreshInstall.PrepareSteamCmdAsync(target,true,cancellationToken:ct);
    }
    private static string? PickSteamCmd()
    {
        using var dialog=new System.Windows.Forms.OpenFileDialog{
            Title="SteamCMD kiválasztása (csak Manager-beállítás)",
            Filter="SteamCMD (steamcmd.exe)|steamcmd.exe",
            CheckFileExists=true,Multiselect=false,RestoreDirectory=true};
        return dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK?dialog.FileName:null;
    }
    private static string? SteamCmdError(string path)
    {
        if(string.IsNullOrWhiteSpace(path))return "UM-STEAMCMD-NOT-CONFIGURED";
        try {SteamCmdPreflight.Inspect(path);return null;}
        catch(FileNotFoundException){return "UM-STEAMCMD-NOT-FOUND";}
        catch(InvalidDataException ex) when(ex.Message=="UM-STEAMCMD-NOT-WINDOWS-PE")
        {return "UM-STEAMCMD-NOT-WINDOWS-PE";}
        catch(Exception ex) when(ex is InvalidDataException or IOException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException or
            System.Security.SecurityException)
        {return "UM-STEAMCMD-INVALID";}
    }
    private ProcessCheck ObserveForSteam(LocalMap map,bool underOwnOperation=false)=>
        map.HistoricalStoreProtected?
            HistoricalLocalConfigGuard.DemandStopped(cluster,map,underOwnOperation):
            cluster.Check(map);

    private string? InstallationBlocker(LocalMap map)
    {
        // This is the same read-only owner proof used by the real updater,
        // but unlike the old bool wrapper it preserves a stable blocker code.
        try
        {
            if(map.HistoricalStoreProtected)ObserveForSteam(map);
            SteamStoppedMapUpdater.DemandSafeLocalInstallation(
                cluster,map,member=>ObserveForSteam(member));
            return null;
        }
        catch(Exception ex) when(ex is InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or IOException)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(ex.Message,
                @"\A(?:UM|UNTAMED)[-_][A-Z0-9_-]{3,110}\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100))
                ?ex.Message:"UM-STEAM-INSTALLATION-IDENTITY-UNVERIFIED";
        }
    }
    private bool Stopped(LocalMap map)=>InstallationBlocker(map)==null;
    private static bool PendingOperation(UntamedOperationPhase? phase)=>
        phase is UntamedOperationPhase.Prepared or UntamedOperationPhase.Mutating or
            UntamedOperationPhase.NeedsOperator;
    private static bool PendingRestart(AsaRestartPhase? phase)=>
        phase is AsaRestartPhase.Stopping or AsaRestartPhase.Starting or
            AsaRestartPhase.NeedsOperator;
    private string? LocalJournalBlocker(LocalMap map)
    {
        var snapshot=AsaLocalJournalReadOnly.Inspect(cluster.Operations,map.InstallationPath);
        if(snapshot.OperationUnreadable)return snapshot.OperationErrorCode??"UM-OPERATION-JOURNAL-READ-INVALID";
        if(PendingOperation(snapshot.Operation?.Phase))
            return "UM-STEAM-LOCAL-OPERATION-JOURNAL-NEEDS-REVIEW";
        if(snapshot.RestartUnreadable)return snapshot.RestartErrorCode??"UM-ASA-RESTART-JOURNAL-READ-INVALID";
        if(PendingRestart(snapshot.Restart?.Phase))
            return "UM-STEAM-LOCAL-RESTART-JOURNAL-NEEDS-REVIEW";
        return null;
    }
    private string? QueueJournalBlockerCode()
    {
        try
        {
            var journal=new SteamSerialMaintenanceQueue(
                cluster.Store.Root,cluster.Config.LocalHostId).Observe();
            return journal!=null && journal.Phase is
                (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress
                    or SteamQueuePhase.NeedsOperator)
                ?"UM-STEAM-QUEUE-JOURNAL-NEEDS-REVIEW":null;
        }
        catch{return "UM-STEAM-QUEUE-JOURNAL-UNREADABLE";}
    }
    private bool HasActiveSteamQueue()
    {
        try
        {
            var journal=new SteamSerialMaintenanceQueue(
                cluster.Store.Root,cluster.Config.LocalHostId).Observe();
            return journal!=null && journal.Phase is
                (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress);
        }
        catch{return true;}
    }
    private static string StoreBlockerCode(LocalMap map)
    {
        if(map.Maintenance)return "UM-STEAM-MAP-MAINTENANCE";
        if(map.ObservedReadOnlyImported)return "UM-STEAM-MAP-OBSERVATION-ONLY";
        try
        {
            if(!map.HistoricalStoreProtected&&!FreshStoreEnrollmentPolicy.IsEnrolled(map)&&
               (!string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)||
                !string.IsNullOrWhiteSpace(map.HistoricalMapKey)||
                !string.IsNullOrWhiteSpace(map.FreshStoreClusterId)||
                !string.IsNullOrWhiteSpace(map.FreshStoreTransferDirectory)||
                !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
                KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map)))
                return "UM-STEAM-SHARED-STORE-OR-RECOVERY-PROTECTED";
            if(KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
                return "UM-STEAM-RECOVERY-MUST-COMPLETE-FIRST";
            return "";
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {return "UM-STEAM-STORE-IDENTITY-UNVERIFIED";}
    }
    internal static IEnumerable<(string Path,string Source)> DefaultSteamCmdCandidates(
        Guid mapId,IEnumerable<LocalMap> maps,string roaming,string local,
        string programFilesX86,string programFiles)
    {
        foreach(var map in maps)
            if(map.Id==mapId)
                yield return(Path.Combine(roaming,"Kova","tools",map.Id.ToString("D"),
                    "SteamCMD","steamcmd.exe"),"Kova korábbi map-eszköze");
        yield return(Path.Combine(local,"UntamedManager","Tools","SteamCMD","steamcmd.exe"),
            "Obsidian Manager eszköztára");
        if(!string.IsNullOrWhiteSpace(programFilesX86))
            yield return(Path.Combine(programFilesX86,"Steam","steamcmd.exe"),
                "Steam telepítési könyvtár");
        if(!string.IsNullOrWhiteSpace(programFiles))
            yield return(Path.Combine(programFiles,"Steam","steamcmd.exe"),
                "Steam telepítési könyvtár");
        yield return(@"C:\SteamCMD\steamcmd.exe",@"C:\SteamCMD");
    }
    internal static bool SameSteamCmdCandidate(SteamCmdFileStatus before,
        SteamCmdFileStatus current)=>
        string.Equals(before.ExecutablePath,current.ExecutablePath,
            StringComparison.OrdinalIgnoreCase)&&
        before.LengthBytes==current.LengthBytes&&
        string.Equals(before.Sha256,current.Sha256,StringComparison.OrdinalIgnoreCase)&&
        before.LastModifiedUtc==current.LastModifiedUtc;
    private object DiscoverSteamCmd(Guid mapId)
    {
        var candidates=new List<object>();
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var candidate in candidatePathsForDiscovery(mapId))
        {
            string full;
            try{full=Path.GetFullPath(candidate.Path);}
            catch{continue;}
            if(!seen.Add(full)||!File.Exists(full))continue;
            try
            {
                var file=SteamCmdPreflight.Inspect(full);
                var token=Guid.NewGuid();
                discoveredByToken[token]=new(token,mapId,file,candidate.Source,
                    DateTimeOffset.UtcNow.AddMinutes(3));
                candidates.Add(new{
                    token,path=file.ExecutablePath,source=candidate.Source,
                    lengthBytes=file.LengthBytes,sha256=file.Sha256,
                    lastModifiedUtc=file.LastModifiedUtc
                });
            }
            catch(Exception ex) when(ex is InvalidDataException or IOException or
                UnauthorizedAccessException or ArgumentException or NotSupportedException or
                System.Security.SecurityException)
            { /* Invalid or linked candidates are deliberately not offered. */ }
        }
        return new{
            mapId,candidates=candidates.ToArray(),
            updateStarted=false,serverStopped=false,settingsOnly=true,
            requiresExplicitAcceptance=true,
            evidence="READ_ONLY_EXISTING_STEAMCMD_DISCOVERY_NO_EXECUTION"
        };
    }
    private static bool GuidField(JsonElement obj,string key,out Guid value)
    {
        value=Guid.Empty;
        return obj.TryGetProperty(key,out var prop)&&prop.ValueKind==JsonValueKind.String&&
            Guid.TryParse(prop.GetString(),out value)&&value!=Guid.Empty;
    }
    private void Stage(Job item,string state,string stage,
        SteamStoppedMapUpdater.Receipt? receipt=null)
    {
        lock(sync)
        {
            // Progress<T> posts asynchronously. A delayed Backup/VerifyBuild
            // callback must never turn a verified or failed job back into
            // "running" after the runner has reached a terminal state.
            if(item.State is "completed" or "needsOperator")return;
            item.State=state;item.Stage=stage;item.Changed=DateTimeOffset.UtcNow;
            if(receipt!=null)
            {
                item.OldBuild=receipt.OldBuild;
                item.NewBuild=receipt.NewBuild;
                item.ProtectedFilesIntact=receipt.ProtectedFilesIntact;
            }
        }
    }
    private object Snapshot(Job item)
    {
        lock(sync)return new{
            jobId=item.Id,mapId=item.MapId,state=item.State,stage=item.Stage,
            changedUtc=item.Changed,oldBuild=item.OldBuild,newBuild=item.NewBuild,
            protectedFilesIntact=item.ProtectedFilesIntact,errorCode=item.ErrorCode,
            restartPerformed=false,storeTouched=false
        };
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3000)return Fail(null,"UM-STEAM-MESSAGE");
            using var document=JsonDocument.Parse(raw);
            var data=document.RootElement;
            if(data.ValueKind!=JsonValueKind.Object||
               !GuidField(data,"id",out var requestId)||
               !data.TryGetProperty("method",out var field)||
               field.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-STEAM-MESSAGE");
            id=requestId.ToString("D");
            string? method=field.GetString();
            if(method is not ("steam.stoppedDiagnostics" or "steam.stoppedPreview" or
                "steam.stoppedApply" or "steam.stoppedStart" or "steam.stoppedStatus" or "steam.stoppedCurrent" or
                "steam.publishedBuild" or "steam.selectExecutable" or "steam.prepareExecutable" or
               "steam.discoverExecutables" or "steam.acceptDiscoveredExecutable"))
                return Fail(id,"UM-METHOD-DENIED");
            await mutex.WaitAsync();
            try
            {
                if(method is "steam.selectExecutable" or "steam.prepareExecutable")
                {
                    if(job!=null)
                    {
                        lock(sync)if(job.State is "queued" or "running")
                            return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    }
                    if(HasActiveSteamQueue())return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    // The renderer supplies no execute path; the operator selects an existing local file.
                    string? selected;
                    if(method=="steam.prepareExecutable")
                    {
                        // Existing valid settings remain unchanged. Otherwise
                        // prepare only the Manager-owned SteamCMD tool; never
                        // download or execute an ASA app_update.
                        if(SteamCmdError(cluster.Config.SteamCmdExecutable)==null)
                            selected=cluster.Config.SteamCmdExecutable;
                        else
                        {
                            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
                            try{selected=await prepareSteamCmd(deadline.Token);}
                            catch(Exception){return Fail(id,"UM-STEAMCMD-PREPARE-FAILED");}
                        }
                    }
                    else selected=pickSteamCmd();
                    if(selected==null)return Ok(id,new{cancelled=true,configured=false,updateStarted=false,serverStopped=false});
                    string? problem=SteamCmdError(selected);
                    if(problem!=null)return Fail(id,problem);
                    if(HasActiveSteamQueue())return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    string full=Path.GetFullPath(selected);
                    cluster.UpdateSettings(config=>config.SteamCmdExecutable=full);
                    pendingByMap.Clear(); // previously issued tokens bind the old executable
                    return Ok(id,new{cancelled=false,configured=true,updateStarted=false,serverStopped=false});
                }
                if(method=="steam.discoverExecutables")
                {
                    if(!GuidField(data,"mapId",out var discoverMapId))
                        return Fail(id,"UM-STEAM-MAP");
                    var discoverMap=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==discoverMapId);
                    if(discoverMap==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    foreach(var expired in discoveredByToken.Values
                        .Where(x=>x.ExpiresUtc<DateTimeOffset.UtcNow).Select(x=>x.Token).ToArray())
                        discoveredByToken.Remove(expired);
                    return Ok(id,DiscoverSteamCmd(discoverMapId));
                }
                if(method=="steam.acceptDiscoveredExecutable")
                {
                    if(job!=null)
                    {
                        lock(sync)if(job.State is "queued" or "running")
                            return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    }
                    if(HasActiveSteamQueue())return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    if(!GuidField(data,"token",out var candidateToken)||
                       !discoveredByToken.TryGetValue(candidateToken,out var candidate))
                        return Fail(id,"UM-STEAMCMD-DISCOVERY-TOKEN-EXPIRED");
                    discoveredByToken.Remove(candidateToken);
                    if(candidate.ExpiresUtc<DateTimeOffset.UtcNow||
                       !data.TryGetProperty("confirm",out var accept)||
                       accept.ValueKind!=JsonValueKind.True)
                        return Fail(id,"UM-STEAMCMD-DISCOVERY-APPROVAL-REQUIRED");
                    if(!GuidField(data,"mapId",out var acceptMapId)||
                       acceptMapId!=candidate.MapId||
                       !cluster.Config.LocalMaps.Any(x=>x.Id==candidate.MapId))
                        return Fail(id,"UM-STEAMCMD-DISCOVERY-MAP-CHANGED");
                    SteamCmdFileStatus current;
                    try{current=SteamCmdPreflight.Inspect(candidate.File.ExecutablePath);}
                    catch(Exception){return Fail(id,"UM-STEAMCMD-DISCOVERY-CANDIDATE-CHANGED");}
                    if(!SameSteamCmdCandidate(candidate.File,current))
                        return Fail(id,"UM-STEAMCMD-DISCOVERY-CANDIDATE-CHANGED");
                    cluster.UpdateSettings(config=>config.SteamCmdExecutable=current.ExecutablePath);
                    pendingByMap.Clear();
                    discoveredByToken.Clear();
                    return Ok(id,new{
                        configured=true,cancelled=false,
                        path=current.ExecutablePath,sha256=current.Sha256,
                        source=candidate.Source,updateStarted=false,serverStopped=false,
                        settingsOnly=true,
                        evidence="EXPLICITLY_ACCEPTED_EXISTING_STEAMCMD_SETTINGS_ONLY"
                    });
                }
                if(method=="steam.stoppedDiagnostics")
                {
                    if(!GuidField(data,"mapId",out var diagnosedId))
                        return Fail(id,"UM-STEAM-MAP");
                    var owned=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==diagnosedId);
                    if(owned==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    string root=Path.GetFullPath(owned.InstallationPath);
                    var related=cluster.Config.LocalMaps.Where(m=>
                        !string.IsNullOrWhiteSpace(m.InstallationPath)&&
                        string.Equals(Path.GetFullPath(m.InstallationPath),root,
                            StringComparison.OrdinalIgnoreCase)).ToArray();
                    var processes=related.Select(m=>{
                        ProcessCheck process;
                        try{process=ObserveForSteam(m);}
                        catch(Exception){process=new(false,
                            "Native ASA/Loader stopped proof unavailable",null,false);}
                        return new{mapName=m.Name,confirmedStopped=process.ConfirmedStopped,
                            running=process.Running,pid=process.Identity?.Pid,
                            loaderPid=process.LoaderIdentity?.Pid,reason=process.Status};
                    }).ToArray();
                    var overlaps=cluster.Config.LocalMaps.Where(m=>
                        !string.IsNullOrWhiteSpace(m.InstallationPath)&&
                        !string.Equals(Path.GetFullPath(m.InstallationPath),root,
                            StringComparison.OrdinalIgnoreCase)&&
                        (Path.GetFullPath(m.InstallationPath).StartsWith(
                            root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)||
                         root.StartsWith(Path.GetFullPath(m.InstallationPath).TrimEnd(
                            Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)))
                        .Select(m=>new{mapName=m.Name,installRoot=m.InstallationPath}).ToArray();
                    string steamExe=cluster.Config.SteamCmdExecutable;
                    string? steamCmdError=SteamCmdError(steamExe);
                    SteamCmdFileStatus? steamStatus=null;
                    if(steamCmdError==null)
                    {
                        try{steamStatus=SteamCmdPreflight.Inspect(steamExe);}
                        catch{steamCmdError="UM-STEAMCMD-INVALID";}
                    }
                    bool steamFound=steamCmdError==null&&steamStatus!=null;
                    string? externalSteamError=null;
                    if(!isolatedExecution)
                    {
                        try{SteamStoppedMapUpdater.DemandNoConcurrentSteamCmd();}
                        catch(IOException e){externalSteamError=e.Message is "UM-STEAM-EXTERNAL-PROCESS-BUSY" or "UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED"?e.Message:"UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED";}
                    }
                    long free=new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
                    bool stopped=processes.All(p=>p.confirmedStopped&&!p.running&&
                        p.pid==null&&p.loaderPid==null);
                    var blockers=new List<object>();
                    var journalBlockers=new List<object>();
                    var processBlockers=new List<object>();
                    var storeBlockers=new List<object>();
                    SteamQueueSnapshot? queue=null;
                    bool queueUnreadable=false;
                    try{queue=new SteamSerialMaintenanceQueue(
                        cluster.Store.Root,cluster.Config.LocalHostId).Observe();}
                    catch{queueUnreadable=true;}
                    bool queueJournalBlocked=queueUnreadable||queue!=null&&
                        queue.Phase is (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress or
                            SteamQueuePhase.NeedsOperator);
                    string? queueCode=queueUnreadable?"UM-STEAM-QUEUE-JOURNAL-UNREADABLE":
                        queueJournalBlocked?"UM-STEAM-QUEUE-JOURNAL-NEEDS-REVIEW":null;
                    if(queueCode!=null)
                    {
                        var item=new{category="queueJournal",code=queueCode,
                            phase=queue?.Phase.ToString(),
                            nextStep="Ellenőrizd és auditáld a korábbi SteamCMD-feladatot; új frissítést ne indíts vakon."};
                        journalBlockers.Add(item);blockers.Add(item);
                    }
                    var localJournal=AsaLocalJournalReadOnly.Inspect(
                        cluster.Operations,owned.InstallationPath);
                    string? operationCode=localJournal.OperationUnreadable?
                        localJournal.OperationErrorCode??"UM-OPERATION-JOURNAL-UNREADABLE":
                        PendingOperation(localJournal.Operation?.Phase)?
                            "UM-STEAM-LOCAL-OPERATION-JOURNAL-NEEDS-REVIEW":null;
                    string? restartCode=localJournal.RestartUnreadable?
                        localJournal.RestartErrorCode??"UM-ASA-RESTART-JOURNAL-UNREADABLE":
                        PendingRestart(localJournal.Restart?.Phase)?
                            "UM-STEAM-LOCAL-RESTART-JOURNAL-NEEDS-REVIEW":null;
                    if(operationCode!=null)
                    {
                        var item=new{category="operationJournal",code=operationCode,
                            phase=localJournal.Operation?.Phase.ToString(),
                            nextStep="Nyisd meg a map műveleti naplóját, ellenőrizd az eredményt, majd végezz jóváhagyott naplóegyeztetést."};
                        journalBlockers.Add(item);blockers.Add(item);
                    }
                    if(restartCode!=null)
                    {
                        var item=new{category="restartJournal",code=restartCode,
                            phase=localJournal.Restart?.Phase.ToString(),
                            nextStep="Ellenőrizd a régi és jelenlegi ASA/Loader PID-et; csak auditált naplórendezés után frissíts."};
                        journalBlockers.Add(item);blockers.Add(item);
                    }
                    bool localJournalBlocked=operationCode!=null||restartCode!=null;
                    bool journalBlocked=queueJournalBlocked||localJournalBlocked;
                    foreach(var process in processes)
                    {
                        if(process.confirmedStopped&&!process.running&&
                           process.pid==null&&process.loaderPid==null)continue;
                        string code=process.running?"UM-STEAM-MAP-STILL-RUNNING":
                            "UM-STEAM-MAP-STOP-NOT-VERIFIED";
                        var item=new{category="process",code,subject=process.mapName,
                            nextStep=process.running?
                                "Állítsd le szabályosan a mapot, majd várd meg az ASA és Loader teljes kilépését.":
                                "Ellenőrizd a Windows folyamatfigyelést és jogosultságokat; a leállás jelenleg nem bizonyított."};
                        processBlockers.Add(item);blockers.Add(item);
                    }
                    foreach(var sibling in related)
                    {
                        string code=StoreBlockerCode(sibling);
                        if(code.Length==0)continue;
                        var item=new{category="storeProtection",code,subject=sibling.Name,
                            nextStep=code=="UM-STEAM-RECOVERY-MUST-COMPLETE-FIRST"?
                                "Fejezd be vagy auditáld a mentés-helyreállítást.":
                                code=="UM-STEAM-MAP-MAINTENANCE"?
                                    "Várd meg a karbantartás végét.":
                                code=="UM-STEAM-MAP-OBSERVATION-ONLY"?
                                    "Ez a map csak megfigyelhető; válassz igazoltan kezelt helyi mapot.":
                                    "Ellenőrizd a Store/cluster tulajdonosi azonosítást; védett telepítésbe nem frissítünk."};
                        storeBlockers.Add(item);blockers.Add(item);
                    }
                    string? installationBlockerCode=InstallationBlocker(owned);
                    bool safeInstallation=installationBlockerCode==null;
                    if(installationBlockerCode!=null)
                    {
                        var item=new{category="installationIdentity",code=installationBlockerCode,
                            subject=owned.Name,
                            nextStep="Ellenőrizd a helyi telepítési útvonalat, a regisztrált map-tulajdonosokat és az egymásba ágyazott telepítéseket."};
                        blockers.Add(item);
                    }
                    foreach(var overlap in overlaps)
                    {
                        var item=new{category="installationOverlap",code="UM-STEAM-NESTED-INSTALL-OWNERSHIP",
                            subject=overlap.mapName,
                            nextStep="Válaszd szét az egymásba ágyazott telepítési gyökereket, vagy rendezd a regisztrációt."};
                        blockers.Add(item);
                    }
                    bool binaryRegistered=AsaReadyProbe.IsRegisteredServerBinary(owned);
                    if(!binaryRegistered)
                    {
                        var item=new{category="asaBinary",code="UM-STEAM-ASA-BINARY-INVALID",
                            subject=owned.Name,nextStep="Ellenőrizd a regisztrált ArkAscendedServer.exe útvonalat."};
                        blockers.Add(item);
                    }
                    if(steamCmdError!=null)
                    {
                        var item=new{category="steamcmd",code=steamCmdError,
                            subject="SteamCMD",
                            nextStep=steamCmdError=="UM-STEAMCMD-NOT-CONFIGURED"?
                                "Keress meglévő helyi steamcmd.exe példányt, majd külön jóváhagyással fogadd el, vagy válaszd ki kézzel.":
                                "Válassz ki egy érvényes, helyi steamcmd.exe fájlt, vagy készítsd elő a Valve hivatalos példányát."};
                        blockers.Add(item);
                    }
                    if(externalSteamError!=null)
                    {
                        var item=new{category="externalSteamProcess",code=externalSteamError,
                            subject="SteamCMD folyamat",
                            nextStep="Ellenőrizd a Feladatkezelőben a másik SteamCMD-folyamatot, és csak a befejezése után próbáld újra."};
                        blockers.Add(item);
                    }
                    long required=Reserve+Reserve/4;
                    if(free<required)
                    {
                        var item=new{category="diskSpace",code="UM-STEAM-DISK-RESERVE-INSUFFICIENT",
                            subject="Szabad tárhely",
                            nextStep="Szabadíts fel legalább "+Math.Ceiling(required/1073741824d)+" GiB helyet."};
                        blockers.Add(item);
                    }
                    return Ok(id,new{
                        mapId=owned.Id,mapName=owned.Name,installRoot=root,
                        installedBuild=AsaSteamBuildCatalog.InstalledBuild(root),
                        processes,overlaps,steamCmdError,
                        steamCmdPath=steamStatus?.ExecutablePath,
                        steamCmdConfiguredPath=string.IsNullOrWhiteSpace(steamExe)?null:steamExe,
                        steamCmdSha256=steamStatus?.Sha256,
                        steamCmdLastModifiedUtc=steamStatus?.LastModifiedUtc,
                        asaBinaryRegistered=binaryRegistered,
                        externalSteamError,steamCmdFound=steamFound,
                        steamCmdConfigured=!string.IsNullOrWhiteSpace(steamExe),
                        availableBytes=free,requiredBytes=required,
                        journalBlocked,queueJournalBlocked,localJournalBlocked,
                        queueJournalPhase=queue?.Phase.ToString(),
                        queueJournalBlockingCode=queueCode,
                        operationJournalPhase=localJournal.Operation?.Phase.ToString(),
                        operationJournalKind=localJournal.Operation?.Kind.ToString(),
                        operationJournalBlockingCode=operationCode,
                        restartJournalPhase=localJournal.Restart?.Phase.ToString(),
                        restartJournalBlockingCode=restartCode,
                        localJournalUnreadable=localJournal.AnyUnreadable,
                        journalBlockers=journalBlockers.ToArray(),
                        processBlockers=processBlockers.ToArray(),
                        storeBlockers=storeBlockers.ToArray(),
                        installationBlockerCode,
                        blockers=blockers.ToArray(),
                        historicalBlocked=storeBlockers.Count>0,
                        safeInstallation,
                        canPreview=blockers.Count==0&&binaryRegistered&&stopped&&
                            storeBlockers.Count==0&&safeInstallation&&steamFound&&
                            externalSteamError==null&&free>=required&&overlaps.Length==0,
                        noProcessAutoStop=true,remoteWrites=false,
                        evidence="REAL_STOPPED_MAP_STEAM_DIAGNOSTICS_READ_ONLY"
                    });
                }
                if(method=="steam.publishedBuild")
                {
                    if(!GuidField(data,"mapId",out var candidateId))
                        return Fail(id,"UM-STEAM-MAP");
                    var candidate=cluster.Config.LocalMaps.SingleOrDefault(
                        x=>x.Id==candidateId);
                    if(candidate==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    if(job!=null)
                    {
                        lock(sync)
                            if(job.State is "queued" or "running")
                                return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    }
                    if(QueueJournalBlockerCode() is { } queueBlocker)
                        return Fail(id,queueBlocker);
                    if(!AsaReadyProbe.IsRegisteredServerBinary(candidate))
                        return Fail(id,"UM-STEAM-ASA-BINARY-INVALID");
                    string? steamProblem=SteamCmdError(cluster.Config.SteamCmdExecutable);
                    if(steamProblem!=null)return Fail(id,steamProblem);
                    using var deadline=new CancellationTokenSource(
                        TimeSpan.FromSeconds(95));
                    var report=await publishedBuild(candidate,deadline.Token);
                    if(!string.Equals(Path.GetFullPath(report.InstallationRoot),
                           Path.GetFullPath(candidate.InstallationPath),
                           StringComparison.OrdinalIgnoreCase)||
                       report.Branch!="public")
                        return Fail(id,"UM-STEAM-PUBLISHED-RECEIPT-INVALID");
                    return Ok(id,new{
                        mapId=candidate.Id,mapName=candidate.Name,
                        installedBuild=report.InstalledBuild,
                        availableBuild=report.AvailableBuild,
                        state=report.State.ToString(),
                        checkedUtc=report.CheckedUtc,
                        explanation=report.Explanation,diagnosticCode=report.DiagnosticCode,
                        updateAvailable=report.State==AsaSteamBuildState.UpdateAvailable,
                        downloadStarted=false,serverStopped=false,
                        storeWriterTouched=false,remoteHostTouched=false,
                        evidence="STEAM_PUBLIC_BRANCH_APPINFO_READ_ONLY_NO_UPDATE"
                    });
                }
                if(method=="steam.stoppedCurrent")
                {
                    // Recovery is observational. Only the Core journal can
                    // establish a completed queue after a Manager UI restart.
                    var journal=new SteamSerialMaintenanceQueue(
                        cluster.Store.Root,cluster.Config.LocalHostId).Observe();
                    // WebView2 may reload or the operator may leave this
                    // tab while native SteamCMD remains active. Expose only
                    // this bridge's in-memory job receipt to REATTACH the
                    // renderer, never a capability to replay an update.
                    object? activeJob=null;
                    if(job!=null)
                    {
                        lock(sync)
                        {
                            if(job.State is "queued" or "running")
                                activeJob=Snapshot(job);
                        }
                    }
                    bool active=activeJob!=null;
                    if(journal==null)
                        return Ok(id,new{exists=false,needsOperatorReview=false,
                            autoResumeAllowed=false,activeJob});
                    bool needsReview=!active && journal.Phase is
                        (SteamQueuePhase.Prepared or SteamQueuePhase.MapInProgress
                            or SteamQueuePhase.NeedsOperator);
                    string? mapName=cluster.Config.LocalMaps.FirstOrDefault(
                        m=>m.Id==journal.ActiveMap)?.Name;
                    return Ok(id,new{
                        exists=true,queueId=journal.QueueId,
                        phase=journal.Phase.ToString(),
                        activeMapName=mapName,
                        nextIndex=journal.NextIndex,total=journal.MapIds.Length,
                        changedUtc=journal.ChangedUtc,
                        summary=journal.Summary,
                        needsOperatorReview=needsReview,autoResumeAllowed=false,
                        activeJob,
                        hostName=cluster.Config.LocalHostName,
                        evidence="DURABLE_MACHINE_LOCAL_STEAM_QUEUE_JOURNAL"
                    });
                }
                if(method=="steam.stoppedStatus")
                {
                    if(!GuidField(data,"jobId",out var jobId))
                        return Fail(id,"UM-STEAM-JOB-MISSING");
                    Job? requested;
                    lock(sync)jobsById.TryGetValue(jobId,out requested);
                    if(requested==null)return Fail(id,"UM-STEAM-JOB-MISSING");
                    return Ok(id,Snapshot(requested));
                }
                if(job!=null)
                {
                    lock(sync)
                    {
                        if(job.State is "queued" or "running")
                            return Fail(id,"UM-STEAM-UPDATE-IN-PROGRESS");
                    }
                }
                bool direct=method=="steam.stoppedStart";
                Preview? saved=null;
                if(direct&&(acceptedDirectRequests.Contains(requestId)||acceptedDirectRequests.Count>=4096))
                    return Fail(id,"UM-STEAM-DIRECT-REQUEST-CONSUMED");
                if(method=="steam.stoppedPreview"||direct)
                {
                    if(!GuidField(data,"mapId",out var mapId))
                        return Fail(id,"UM-STEAM-MAP");
                    var owned=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId);
                    if(owned==null)return Fail(id,"UM-MAP-NOT-OWNED");
                    string? localJournalCode=LocalJournalBlocker(owned);
                    if(localJournalCode!=null)return Fail(id,localJournalCode);
                    if(!Stopped(owned))return Fail(id,"UM-STEAM-STOPPED-ONLY");
                    if(QueueJournalBlockerCode() is { } queueBlocker)
                        return Fail(id,queueBlocker);
                    string? steamProblem=SteamCmdError(cluster.Config.SteamCmdExecutable);
                    if(steamProblem!=null)return Fail(id,steamProblem);
                    var steam=SteamCmdPreflight.Inspect(cluster.Config.SteamCmdExecutable);
                    var plan=planner(owned);
                    if(!plan.DryRunAllowed||string.IsNullOrWhiteSpace(plan.InstalledBuild)||
                       !string.Equals(Path.GetFullPath(plan.InstallRoot),
                            Path.GetFullPath(owned.InstallationPath),
                            StringComparison.OrdinalIgnoreCase))
                        return Fail(id,"UM-STEAM-PREFLIGHT-FAILED");
                    var proposed=new Preview(Guid.NewGuid(),owned.Id,owned.InstallationPath,
                        plan.InstalledBuild,steam.ExecutablePath,DateTimeOffset.UtcNow);
                    if(direct)
                    {
                        // A single explicit native request creates and consumes its own scoped preflight.
                        // Shared installation scope must have been displayed by the read-only diagnostics.
                        if(plan.Maps.Length>1&&(!data.TryGetProperty("affectedMaps",out var affected)||
                           affected.ValueKind!=JsonValueKind.Array||
                           !affected.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.String)||
                           !affected.EnumerateArray().Select(x=>x.GetString()).OrderBy(x=>x,StringComparer.Ordinal)
                               .SequenceEqual(plan.Maps.OrderBy(x=>x,StringComparer.Ordinal))))
                            return Fail(id,"UM-STEAM-SHARED-SCOPE-CHANGED");
                        saved=proposed;
                    }
                    else
                    {
                    pendingByMap[owned.Id]=proposed;
                    return Ok(id,new{
                        token=proposed.Token,mapName=owned.Name,mapId=owned.Id,
                        maps=plan.Maps,installedBuild=plan.InstalledBuild,
                        reserveBytes=plan.EstimatedDownloadBytes,
                        availableBytes=plan.AvailableBytes,
                        evidence="REAL_LOCAL_STOPPED_STEAM_PREFLIGHT_NO_WRITE"
                    });
                    }
                }
                else
                {
                if(!GuidField(data,"token",out var approval))
                    return Fail(id,"UM-STEAM-TOKEN-EXPIRED");
                saved=pendingByMap.Values.SingleOrDefault(p=>p.Token==approval);
                if(saved==null||DateTimeOffset.UtcNow-saved.Created>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-STEAM-TOKEN-EXPIRED");
                if(!data.TryGetProperty("confirm",out var confirm)||
                   confirm.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-STEAM-CONFIRMATION-REQUIRED");
                }
                if(saved==null||DateTimeOffset.UtcNow-saved.Created>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-STEAM-TOKEN-EXPIRED");
                // One-shot approval is consumed for its own map only.
                pendingByMap.Remove(saved.MapId);
                var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==saved.MapId);
                // This map was local at preview, but is no longer registered.
                // Consume the old approval as changed preflight, never dispatch.
                if(map==null)return Fail(id,"UM-STEAM-PREFLIGHT-CHANGED");
                string? applyJournalCode=LocalJournalBlocker(map);
                if(applyJournalCode!=null)return Fail(id,applyJournalCode);
                if(QueueJournalBlockerCode() is { } finalQueueBlocker)
                    return Fail(id,finalQueueBlocker);
                if(!Stopped(map)||
                   !string.Equals(map.InstallationPath,saved.Root,
                       StringComparison.OrdinalIgnoreCase)||
                   !string.Equals(cluster.Config.SteamCmdExecutable,saved.Steam,
                       StringComparison.OrdinalIgnoreCase)||
                   AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath)!=saved.Build)
                    return Fail(id,"UM-STEAM-PREFLIGHT-CHANGED");
                var fresh=planner(map);
                if(!fresh.DryRunAllowed||fresh.InstalledBuild!=saved.Build||
                   !string.Equals(Path.GetFullPath(fresh.InstallRoot),Path.GetFullPath(saved.Root),StringComparison.OrdinalIgnoreCase))
                    return Fail(id,"UM-STEAM-PREFLIGHT-CHANGED");
                if(direct&&fresh.Maps.Length>1&&(!data.TryGetProperty("affectedMaps",out var freshScope)||
                   freshScope.ValueKind!=JsonValueKind.Array||
                   !freshScope.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.String)||
                   !freshScope.EnumerateArray().Select(x=>x.GetString()).OrderBy(x=>x,StringComparer.Ordinal)
                       .SequenceEqual(fresh.Maps.OrderBy(x=>x,StringComparer.Ordinal))))
                    return Fail(id,"UM-STEAM-SHARED-SCOPE-CHANGED");
                if(!isolatedExecution)
                {
                    try{SteamStoppedMapUpdater.DemandNoConcurrentSteamCmd();}
                    catch(IOException e){return Fail(id,e.Message is "UM-STEAM-EXTERNAL-PROCESS-BUSY" or "UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED"?e.Message:"UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED");}
                }
                if(direct)acceptedDirectRequests.Add(requestId);
                var task=new Job{MapId=map.Id};
                var events=new DiscordOperationEvents.NativeRun(cluster,task.Id,new[]{map.Id},"Update");
                events.Emit("claimed","in-progress","NATIVE_STEAM_JOB_ACCEPTED");
                lock(sync)
                {
                    job=task;
                    if(jobsById.Count>=64)
                    {
                        var completed=jobsById.Values.Where(j=>
                            j.State is "completed" or "needsOperator")
                            .OrderBy(j=>j.Changed).Take(jobsById.Count-63).ToArray();
                        foreach(var old in completed)jobsById.Remove(old.Id);
                    }
                    jobsById[task.Id]=task;
                }
                _=Task.Run(async()=>
                {
                    Stage(task,"running","preflight");
                    events.Emit("started","in-progress","NATIVE_STEAM_EXECUTION_STARTED");
                    var progress=new InlineProgress<SteamMaintenanceStage>(value=>{
                        Stage(task,"running",value.ToString());
                        events.Emit("steam-"+value.ToString(),"in-progress","NATIVE_STAGE_OBSERVED");
                    });
                    try
                    {
                        var verified=await runner(map,progress);
                        if(verified.MapId!=map.Id||!verified.ProtectedFilesIntact)
                            throw new InvalidDataException("UM-STEAM-RECEIPT-INVALID");
                        Stage(task,"completed","verified",verified);
                        events.Emit("terminal","verified-local","STEAM_BUILD_AND_PROTECTED_FILES_VERIFIED");
                    }
                    catch(Exception ex)
                    {
                        lock(sync)
                        {
                            task.ErrorCode=ex.Message is "UM-STEAM-EXTERNAL-PROCESS-BUSY" or "UM-STEAM-EXTERNAL-PROCESS-UNVERIFIED" or "UM-STEAM-INSTALLED-BUILD-INVALID" or "UM-STEAM-INSTALLED-BUILD-CHANGED"?ex.Message:"UM-STEAM-UPDATE-FAILED";
                            Stage(task,"needsOperator",task.Stage);
                        }
                        events.Emit("terminal","uncertain","STEAM_UPDATE_FAILED_NATIVE_JOURNAL_REVIEW");
                    }
                });
                return Ok(id,new{jobId=task.Id,started=true,
                    restartPerformed=false,remoteHostTouched=false,
                    evidence="REAL_LOCAL_STOPPED_STEAM_UPDATE_STARTED"});
            }
            finally{mutex.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-STEAM-UPDATE-UNAVAILABLE");
        }
    }
    private static string Ok(string id,object value)=>
        JsonSerializer.Serialize(new{id,ok=true,data=value},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>mutex.Dispose();
}
