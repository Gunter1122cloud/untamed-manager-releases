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
/// Two-stage real SaveWorld/DoExit for an owned, non-Store ASA process.
/// The native RCON process and its listener are reverified before every
/// dispatch. No arbitrary command, force kill, Store writer or restart is
/// exposed to JavaScript. A long-running shutdown runs off the UI thread.
/// </summary>
public sealed class DesktopFreshAsaStopBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,int Pid,
        DateTimeOffset StartedUtc,string Root,int RconPort,DateTimeOffset Created);
    private sealed class Job
    {
        public Guid JobId=Guid.NewGuid();
        public Guid MapId;
        public string State="queued",Step="preflight";
        public bool ConfirmedStopped,SaveCompleteProven,PreExitSaveEvidenceObserved,PreExitWorldMetadataObserved;
        public DateTimeOffset ChangedUtc=DateTimeOffset.UtcNow;
    }
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim serial=new(1,1);
    private readonly object sync=new();
    private readonly Func<int,int,bool> ownsPort;
    private readonly Func<Guid,ProcessIdentity,Action<string>,Task<AsaGracefulStopReceipt>> stop;
    // One pending one-time approval per map; another map's preflight
    // must not silently invalidate a still-current local approval.
    private readonly Dictionary<Guid,Approval> approvalsByMap=new();
    // Retain a separate Stop receipt for every registered local ASA map.
    private readonly Dictionary<Guid,Job> jobsByMap=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private bool HasActive(Guid mapId)
    {
        lock(sync)return jobsByMap.TryGetValue(mapId,out var existing)&&
            existing.State is ("queued" or "running");
    }

    public DesktopFreshAsaStopBridge(ClusterService service,
        Func<int,int,bool>? owner=null,
        Func<Guid,ProcessIdentity,Task<AsaGracefulStopReceipt>>? execute=null)
        : this(service,owner,execute,null) {}

    // Isolated native regression seam; not exposed to WebView2 dispatch.
    // Production retains the original source-controlled RCON/ASA callback.
    internal DesktopFreshAsaStopBridge(ClusterService service,
        Func<int,int,bool>? owner,
        Func<Guid,ProcessIdentity,Task<AsaGracefulStopReceipt>>? execute,
        Func<Guid,ProcessIdentity,Action<string>,Task<AsaGracefulStopReceipt>>? progressRunner)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        ownsPort=owner??RconPortOwnership.IsOwnedListener;
        stop=progressRunner??(execute==null
            ?(map,identity,stage)=>AsaGracefulStop.StopAsync(
                cluster,map,identity,TimeSpan.FromMinutes(2),evidenceStage:stage)
            :(map,identity,_)=>execute(map,identity));
    }
    private LocalMap? Owned(Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(map=>map.Id==id);
    private static bool GuidProperty(JsonElement item,string name,out Guid id)
    {
        id=Guid.Empty;
        return item.TryGetProperty(name,out var value)&&
            value.ValueKind==JsonValueKind.String&&
            Guid.TryParse(value.GetString(),out id)&&id!=Guid.Empty;
    }
    private static bool Same(ProcessIdentity a,ProcessIdentity b)=>
        a.Pid==b.Pid&&a.StartedUtc==b.StartedUtc&&
        string.Equals(a.ExecutablePath,b.ExecutablePath,
            StringComparison.OrdinalIgnoreCase)&&a.Arguments==b.Arguments;
    private ProcessIdentity Require(LocalMap map)
    {
        bool newStore=FreshStoreEnrollmentPolicy.IsEnrolled(map);
        if((!newStore&&(KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot)||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.Arguments)||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.ApiLoaderArguments)))||
           map.Maintenance||!AsaReadyProbe.IsRegisteredServerBinary(map)||
           map.RconHost!="127.0.0.1"||
           map.RconPort is <1024 or >65535 ||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected))
            throw new InvalidOperationException("Local non-Store RCON configuration required");
        if(newStore)FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);
        else FreshAsaInstallationSafety.Demand(cluster,map);
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        var state=cluster.Check(map);
        if(!state.Running||state.ConfirmedStopped||
           state.Identity is not {Pid:>0} identity||
           !ownsPort(map.RconPort,identity.Pid))
            throw new InvalidOperationException("ASA process or RCON listener unverified");
        return identity;
    }
    private void Change(Job task,string state,string step,
        AsaGracefulStopReceipt? receipt=null)
    {
        lock(sync)
        {
            // A late native progress callback must not reopen a completed
            // Stop or conceal an interrupted/uncertain shutdown.
            if(task.State is "completed" or "needsOperator")return;
            task.State=state;task.Step=step;
            task.ChangedUtc=DateTimeOffset.UtcNow;
            if(receipt!=null)
            {
                task.ConfirmedStopped=receipt.ConfirmedStopped;
                task.SaveCompleteProven=receipt.SaveCompleteProven;
                task.PreExitSaveEvidenceObserved=receipt.PreExitSaveLogAndWorldObserved;
                task.PreExitWorldMetadataObserved=receipt.PreExitWorldMetadataObserved;
            }
        }
    }
    private object Snapshot(Job task)
    {
        lock(sync)return new{
            jobId=task.JobId,state=task.State,step=task.Step,
            changedUtc=task.ChangedUtc,
            confirmedStopped=task.ConfirmedStopped,
            saveCompleteProven=task.SaveCompleteProven,
            preExitSaveEvidenceObserved=task.PreExitSaveEvidenceObserved,
            preExitWorldMetadataObserved=task.PreExitWorldMetadataObserved,
            finalDoExitSaveCertified=false,gameClientRejoinVerified=false,
            killPerformed=false,restartPerformed=false,storeTouched=false
        };
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >3000)return Error(null,"UM-ASA-STOP-REQUEST");
            using var document=JsonDocument.Parse(raw);
            var request=document.RootElement;
            if(request.ValueKind!=JsonValueKind.Object||
               !GuidProperty(request,"id",out var requestId)||
               !request.TryGetProperty("method",out var methodElement)||
               methodElement.ValueKind!=JsonValueKind.String)
                return Error(null,"UM-ASA-STOP-REQUEST");
            id=requestId.ToString("D");
            var method=methodElement.GetString();
            if(method is not ("servers.gracefulStopPreview" or
                "servers.gracefulStopApply" or "servers.gracefulStopStatus" or
                "servers.gracefulStopCurrent"))
                return Error(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                // Read-only job reconciliation survives a WebView navigation.
                // It never queues a second Stop or touches the server.
                if(method=="servers.gracefulStopCurrent")
                {
                    if(!GuidProperty(request,"mapId",out var mapId))
                        return Error(id,"UM-ASA-STOP-MAP");
                    if(Owned(mapId)==null)return Error(id,"UM-MAP-NOT-OWNED");
                    lock(sync)
                    {
                        if(!jobsByMap.TryGetValue(mapId,out var tracked))
                            return Ok(id,new{
                                mapId,jobId=(Guid?)null,state=(string?)null,active=false,
                                evidence="LOCAL_ASA_STOP_IN_MEMORY_JOB_OBSERVATION_NO_DISPATCH"
                            });
                        return Ok(id,new{
                            mapId,jobId=(Guid?)tracked.JobId,state=tracked.State,
                            active=tracked.State is "queued" or "running",
                            evidence="LOCAL_ASA_STOP_IN_MEMORY_JOB_OBSERVATION_NO_DISPATCH"
                        });
                    }
                }
                if(method=="servers.gracefulStopStatus")
                {
                    if(!GuidProperty(request,"jobId",out var jobId))
                        return Error(id,"UM-ASA-STOP-JOB-NOT-FOUND");
                    Job? tracked;
                    lock(sync)tracked=jobsByMap.Values.FirstOrDefault(
                        item=>item.JobId==jobId);
                    if(tracked==null)return Error(id,"UM-ASA-STOP-JOB-NOT-FOUND");
                    return Ok(id,Snapshot(tracked));
                }
                if(method=="servers.gracefulStopPreview")
                {
                    if(!GuidProperty(request,"mapId",out var mapId))
                        return Error(id,"UM-ASA-STOP-MAP");
                    var owned=Owned(mapId);
                    if(owned==null)return Error(id,"UM-MAP-NOT-OWNED");
                    if(HasActive(mapId))return Error(id,"UM-ASA-STOP-BUSY");
                    var process=Require(owned);
                    var permit=new Approval(Guid.NewGuid(),owned.Id,process.Pid,
                        process.StartedUtc,owned.InstallationPath,owned.RconPort,
                        DateTimeOffset.UtcNow);
                    approvalsByMap[owned.Id]=permit;
                    return Ok(id,new{
                        token=permit.Token,mapId=owned.Id,mapName=owned.Name,
                        pid=process.Pid,usesSaveWorld=true,usesDoExit=true,
                        killPermitted=false,autoRestart=false,
                        evidence="REAL_LOCAL_GRACEFUL_STOP_PREVIEW_NO_DISPATCH"
                    });
                }
                if(!GuidProperty(request,"token",out var token))
                    return Error(id,"UM-ASA-STOP-TOKEN-EXPIRED");
                var approved=approvalsByMap.Values.SingleOrDefault(
                    item=>item.Token==token);
                if(approved==null||
                   DateTimeOffset.UtcNow-approved.Created>TimeSpan.FromMinutes(3))
                    return Error(id,"UM-ASA-STOP-TOKEN-EXPIRED");
                if(!request.TryGetProperty("confirm",out var yes)||
                   yes.ValueKind!=JsonValueKind.True)
                    return Error(id,"UM-ASA-STOP-APPROVAL");
                approvalsByMap.Remove(approved.MapId); // one shot per map
                var map=Owned(approved.MapId);
                if(map==null || map.InstallationPath!=approved.Root ||
                   map.RconPort!=approved.RconPort)
                    return Error(id,"UM-ASA-STOP-CHANGED");
                if(HasActive(map.Id))return Error(id,"UM-ASA-STOP-BUSY");
                var current=Require(map);
                if(current.Pid!=approved.Pid||
                   current.StartedUtc!=approved.StartedUtc)
                    return Error(id,"UM-ASA-STOP-PROCESS-CHANGED");
                var item=new Job{MapId=map.Id};
                lock(sync)jobsByMap[map.Id]=item;
                _=Task.Run(async()=>{
                    Change(item,"running","SaveWorld → új stabil .ark / naplóellenőrzés → DoExit");
                    try
                    {
                        var receipt=await stop(map.Id,current,
                            stage=>Change(item,"running",stage));
                        if(!receipt.ConfirmedStopped||receipt.MapId!=map.Id||
                           receipt.Pid!=approved.Pid)
                            throw new InvalidDataException("ASA graceful stop receipt invalid");
                        Change(item,"completed","Windows process verified stopped",receipt);
                    }
                    catch(Exception ex)
                    {
                        Change(item,"needsOperator",
                            "Stop interrupted ["+NativeLifecycleFailureCode.From(ex)+
                            "]; inspect process and save journal");
                    }
                });
                return Ok(id,new{jobId=item.JobId,started=true,
                    killPerformed=false,autoRestart=false,
                    evidence="REAL_LOCAL_GRACEFUL_STOP_JOB_QUEUED"});
            }
            finally{serial.Release();}
        }
        catch(Exception){return Error(id,"UM-ASA-STOP-FAILED");}
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>serial.Dispose();
}
