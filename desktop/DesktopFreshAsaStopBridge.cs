using System;
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
        public string State="queued",Step="preflight";
        public bool ConfirmedStopped,SaveCompleteProven,PreExitSaveEvidenceObserved;
        public DateTimeOffset ChangedUtc=DateTimeOffset.UtcNow;
    }
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim serial=new(1,1);
    private readonly object sync=new();
    private readonly Func<int,int,bool> ownsPort;
    private readonly Func<Guid,ProcessIdentity,Action<string>,Task<AsaGracefulStopReceipt>> stop;
    private Approval? pending;
    private Job? job;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopFreshAsaStopBridge(ClusterService service,
        Func<int,int,bool>? owner=null,
        Func<Guid,ProcessIdentity,Task<AsaGracefulStopReceipt>>? execute=null)
    {
        cluster=service??throw new ArgumentNullException(nameof(service));
        ownsPort=owner??RconPortOwnership.IsOwnedListener;
        stop=execute==null
            ?(map,identity,stage)=>AsaGracefulStop.StopAsync(
                cluster,map,identity,TimeSpan.FromMinutes(2),evidenceStage:stage)
            :(map,identity,_)=>execute(map,identity);
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
           (map.Arguments+" "+map.ApiLoaderArguments).Contains("store",
               StringComparison.OrdinalIgnoreCase)))||
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
            task.State=state;task.Step=step;
            task.ChangedUtc=DateTimeOffset.UtcNow;
            if(receipt!=null)
            {
                task.ConfirmedStopped=receipt.ConfirmedStopped;
                task.SaveCompleteProven=receipt.SaveCompleteProven;
                task.PreExitSaveEvidenceObserved=receipt.PreExitSaveLogAndWorldObserved;
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
                "servers.gracefulStopApply" or "servers.gracefulStopStatus"))
                return Error(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="servers.gracefulStopStatus")
                {
                    if(!GuidProperty(request,"jobId",out var jobId)||
                       job==null||job.JobId!=jobId)
                        return Error(id,"UM-ASA-STOP-JOB-NOT-FOUND");
                    return Ok(id,Snapshot(job));
                }
                if(job!=null)
                {
                    lock(sync)
                        if(job.State is "queued" or "running")
                            return Error(id,"UM-ASA-STOP-BUSY");
                }
                if(method=="servers.gracefulStopPreview")
                {
                    if(!GuidProperty(request,"mapId",out var mapId))
                        return Error(id,"UM-ASA-STOP-MAP");
                    var owned=Owned(mapId);
                    if(owned==null)return Error(id,"UM-MAP-NOT-OWNED");
                    var process=Require(owned);
                    pending=new(Guid.NewGuid(),owned.Id,process.Pid,
                        process.StartedUtc,owned.InstallationPath,owned.RconPort,
                        DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pending.Token,mapId=owned.Id,mapName=owned.Name,
                        pid=process.Pid,usesSaveWorld=true,usesDoExit=true,
                        killPermitted=false,autoRestart=false,
                        evidence="REAL_LOCAL_GRACEFUL_STOP_PREVIEW_NO_DISPATCH"
                    });
                }
                if(!GuidProperty(request,"token",out var token)||pending==null||
                   pending.Token!=token||
                   DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(3))
                    return Error(id,"UM-ASA-STOP-TOKEN-EXPIRED");
                if(!request.TryGetProperty("confirm",out var yes)||
                   yes.ValueKind!=JsonValueKind.True)
                    return Error(id,"UM-ASA-STOP-APPROVAL");
                var approved=pending;
                pending=null; // one shot even if stop or confirmation fails
                var map=Owned(approved.MapId);
                if(map==null || map.InstallationPath!=approved.Root ||
                   map.RconPort!=approved.RconPort)
                    return Error(id,"UM-ASA-STOP-CHANGED");
                var current=Require(map);
                if(current.Pid!=approved.Pid||
                   current.StartedUtc!=approved.StartedUtc)
                    return Error(id,"UM-ASA-STOP-PROCESS-CHANGED");
                var item=new Job();
                job=item;
                _=Task.Run(async()=>{
                    Change(item,"running","SaveWorld → új ASA SaveComplete + stabil .ark → DoExit");
                    try
                    {
                        var receipt=await stop(map.Id,current,
                            stage=>Change(item,"running",stage));
                        if(!receipt.ConfirmedStopped||receipt.MapId!=map.Id||
                           receipt.Pid!=approved.Pid)
                            throw new InvalidDataException("ASA graceful stop receipt invalid");
                        Change(item,"completed","Windows process verified stopped",receipt);
                    }
                    catch(Exception)
                    {
                        Change(item,"needsOperator","Stop interrupted; inspect process and save journal");
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
