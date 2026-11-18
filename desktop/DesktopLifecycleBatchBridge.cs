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

// A local rolling lifecycle queue. Existing native lifecycle bridges retain
// their installation leases, Store source proofs and one-shot approvals.
// A batch claim is written before invoking any side effect. After a crash
// an unfinished batch is review-only, never resumed or replayed.
public sealed class DesktopLifecycleBatchBridge : IDisposable
{
    public sealed record Target(Guid MapId,string MapName,string State,string Evidence,
        Guid? NativeJobId=null);
    public sealed record Receipt(Guid JobId,string Action,string State,DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc,Target[] Targets);
    private sealed record Plan(Guid MapId,string MapName,string Fingerprint,
        bool Historical,ProcessCheck Observation);
    private sealed record Approval(Guid Token,string Action,int DelaySeconds,
        DateTimeOffset Expires,Plan[] Plans);
    private readonly ClusterService cluster;
    private readonly Func<LocalMap,ProcessCheck> observe;
    private readonly Func<Guid,string,bool,CancellationToken,Task<(int Pid,Guid? JobId)>> execute;
    private readonly object sync=new();
    private readonly string root,journal,owner;
    private readonly CancellationTokenSource closing=new();
    private readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    private Approval? pending;
    private bool disposed;
    public DesktopLifecycleBatchBridge(ClusterService source,
        Func<string,Task<string>> freshStart,Func<string,Task<string>> freshStop,
        Func<string,Task<string>> freshRestart,Func<string,Task<string>> historicalStart,
        Func<string,Task<string>> historicalStop,Func<string,Task<string>> historicalRestart,
        Func<string,Task<string>> historicalReadOnly,
        Func<LocalMap,ProcessCheck>? isolatedObservation=null,
        Func<Guid,string,bool,CancellationToken,Task<(int Pid,Guid? JobId)>>? isolatedExecution=null)
    {
        cluster=source;
        root=Path.GetFullPath(cluster.Store.Root);
        if(root.StartsWith(@"\\",StringComparison.Ordinal)||
           new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-BATCH-LOCAL-PROFILE");
        journal=Path.Combine(root,"native-lifecycle-batch-v1.json");
        owner=Path.Combine(root,"native-lifecycle-batch-v1.lock");
        observe=isolatedObservation??(map=>{
            if(!map.HistoricalStoreProtected)return cluster.Check(map);
            var seen=cluster.ObserveLocalProcessesReadOnly(map);
            return !seen.Running&&!seen.ConfirmedStopped&&seen.Identity==null&&seen.LoaderIdentity==null?
                LegacyStoreV1ProcessObserver.ObserveStoppedForNativeStart(map,cluster.Config.LocalMaps):seen;
        });
        execute=isolatedExecution??(async(id,action,historical,ct)=>{
            var map=Owned(id);
            Func<string,Task<string>> invoke=action switch{
                "start"=>historical?historicalStart:freshStart,
                "stop"=>historical?historicalStop:freshStop,
                _=>historical?historicalRestart:freshRestart};
            string prefix=action switch{
                "start"=>historical?"historical.activeStart":"servers.freshStart",
                "stop"=>historical?"historical.gracefulStop":"servers.gracefulStop",
                _=>historical?"historical.restart":"servers.restart"};
            var preview=Decode(await invoke(Message(prefix+"Preview",new{mapId=id})));
            if(preview.GetProperty("mapId").GetGuid()!=id)
                throw new InvalidDataException("UM-BATCH-PREVIEW-MAP");
            ct.ThrowIfCancellationRequested();
            var applyTime=DateTimeOffset.UtcNow;
            var started=Decode(await invoke(Message(prefix+"Apply",new{
                token=preview.GetProperty("token").GetGuid(),confirm=true})));
            int pid=0; Guid? jobId=null; DateTimeOffset? exactStart=null;
            if(action=="start")
            {
                if(started.GetProperty("mapId").GetGuid()!=id||
                   !started.GetProperty("startRequested").GetBoolean())
                    throw new InvalidDataException("UM-BATCH-START-RECEIPT");
                pid=started.GetProperty("pid").GetInt32();
                exactStart=started.GetProperty("createdUtc").GetDateTimeOffset();
            }
            else
            {
                if(!started.GetProperty("started").GetBoolean())
                    throw new InvalidDataException("UM-BATCH-JOB-RECEIPT");
                jobId=started.GetProperty("jobId").GetGuid();
                var deadline=DateTimeOffset.UtcNow.AddMinutes(18);
                bool done=false;
                while(DateTimeOffset.UtcNow<deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    var status=Decode(await invoke(Message(prefix+"Status",new{jobId})));
                    if(status.GetProperty("jobId").GetGuid()!=jobId||
                       (historical&&status.GetProperty("mapId").GetGuid()!=id))
                        throw new InvalidDataException("UM-BATCH-JOB-IDENTITY");
                    string? state=status.GetProperty("state").GetString();
                    if(state is "needsOperator" or "notDispatched" or "reviewed" or "operatorReviewed")
                        throw new InvalidOperationException("UM-BATCH-NATIVE-JOB-NEEDS-REVIEW");
                    if(state=="completed")
                    {
                        done=true;
                        if(action=="stop")
                        {
                            if(!status.GetProperty("confirmedStopped").GetBoolean())
                                throw new InvalidOperationException("UM-BATCH-STOP-NOT-VERIFIED");
                            return (0,jobId);
                        }
                        pid=status.GetProperty("newPid").GetInt32();
                        if(historical)exactStart=status.GetProperty("newStartedUtc").GetDateTimeOffset();
                        break;
                    }
                    await Task.Delay(2000,ct);
                }
                if(!done)throw new TimeoutException("UM-BATCH-NATIVE-JOB-TIMEOUT");
            }
            if(pid<=0)throw new InvalidDataException("UM-BATCH-LAUNCH-PID");
            // Start and Restart wait for the newly launched family and RCON
            // readiness before another selected map may be started.
            var readyUntil=DateTimeOffset.UtcNow.AddMinutes(12);
            while(DateTimeOffset.UtcNow<readyUntil)
            {
                ct.ThrowIfCancellationRequested();
                var current=Owned(id);
                var seen=observe(current);
                var epoch=seen.Identity?.Pid==pid?seen.Identity:
                    seen.LoaderIdentity?.Pid==pid?seen.LoaderIdentity:null;
                if(seen.Running&&seen.Identity is {Pid:>0} child&&epoch!=null&&
                   (exactStart.HasValue?epoch.StartedUtc==exactStart.Value:
                       epoch.StartedUtc>=applyTime.AddSeconds(-2))&&
                   RconPortOwnership.IsOwnedListener(current.RconPort,child.Pid))
                {
                    try
                    {
                        bool ready;
                        if(historical)
                        {
                            var proof=LegacyStoreV1GracefulStop.Preview(cluster,id);
                            ready=proof.Process.Pid==child.Pid&&proof.Process.StartedUtc==child.StartedUtc&&
                                LegacyStoreV1GracefulStop.SaveOnlyStartupBlocker(proof)==null;
                            if(ready)
                            {
                                var response=Decode(await historicalReadOnly(Message("historical.rconPlayers",new{mapId=id})));
                                ready=response.GetProperty("mapId").GetGuid()==id&&
                                    response.GetProperty("readOnly").GetBoolean();
                            }
                        }
                        else
                        {
                            var response=Decode(await freshStart(Message("servers.freshReadiness",new{mapId=id})));
                            ready=response.GetProperty("mapId").GetGuid()==id&&
                                response.GetProperty("productionReady").GetBoolean();
                        }
                        var after=observe(Owned(id));
                        if(ready&&after.Running&&after.Identity?.Pid==child.Pid&&
                           after.Identity.StartedUtc==child.StartedUtc)return (child.Pid,jobId);
                    }
                    catch(Exception ex) when(ex is not OperationCanceledException){/* bounded read-only retry */}
                }
                await Task.Delay(5000,ct);
            }
            throw new TimeoutException("UM-BATCH-READINESS-TIMEOUT");
        });
    }
    private LocalMap Owned(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id)??
        throw new UnauthorizedAccessException("UM-BATCH-NOT-LOCAL");
    private static string Fingerprint(LocalMap map)=>Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(map))));
    private static bool SameEpoch(ProcessIdentity? a,ProcessIdentity? b)=>
        a==null?b==null:b!=null&&a.Pid==b.Pid&&a.StartedUtc==b.StartedUtc;
    private static bool SameState(ProcessCheck a,ProcessCheck b)=>
        a.Running==b.Running&&a.ConfirmedStopped==b.ConfirmedStopped&&
        SameEpoch(a.Identity,b.Identity)&&SameEpoch(a.LoaderIdentity,b.LoaderIdentity);
    private static bool Eligible(LocalMap map,ProcessCheck state,string action)=>
        !map.Maintenance&&!map.ObservedReadOnlyImported&&
        (action=="start"?state.ConfirmedStopped&&!state.Running&&state.Identity==null&&state.LoaderIdentity==null:
            state.Running&&!state.ConfirmedStopped&&state.Identity is {Pid:>0});
    private void NoLinks()
    {
        for(DirectoryInfo? d=new(root);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-BATCH-LINKED-PROFILE");
        foreach(string path in new[]{journal,owner})
            if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-BATCH-LINKED-JOURNAL");
    }
    private FileStream LockOwner()
    {
        NoLinks();Directory.CreateDirectory(root);
        return new FileStream(owner,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    private Receipt? Read()
    {
        NoLinks();if(!File.Exists(journal))return null;
        using var f=new FileStream(journal,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(f.Length is <2 or >1_000_000)throw new InvalidDataException("UM-BATCH-JOURNAL-SIZE");
        var receipt=JsonSerializer.Deserialize<Receipt>(f,json)??
            throw new InvalidDataException("UM-BATCH-JOURNAL");
        if(receipt.JobId==Guid.Empty||receipt.Targets==null||receipt.Targets.Length is <1 or >25||
           receipt.Targets.Select(t=>t.MapId).Distinct().Count()!=receipt.Targets.Length||
           receipt.Action is not ("start" or "stop" or "restart")||
           receipt.State is not ("running" or "completed" or "needsReview" or "reviewed"))
            throw new InvalidDataException("UM-BATCH-JOURNAL");
        return receipt;
    }
    private void Write(Receipt receipt)
    {
        NoLinks();
        string tmp=journal+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {JsonSerializer.Serialize(f,receipt,json);f.Flush(true);}
            File.Move(tmp,journal,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    // A running or uncertain durable batch keeps manager update on hold.
    internal bool CanManagerUpdate()
    {
        try{using var held=LockOwner();return Read()?.State is not ("running" or "needsReview");}
        catch{return false;}
    }
    private static string Message(string method,object fields)
    {
        var payload=new Dictionary<string,object?>{["id"]=Guid.NewGuid(),["method"]=method};
        foreach(var field in JsonSerializer.SerializeToElement(fields).EnumerateObject())
            payload[field.Name]=field.Value.Clone();
        return JsonSerializer.Serialize(payload);
    }
    private static JsonElement Decode(string raw)
    {
        using var document=JsonDocument.Parse(raw);
        var value=document.RootElement;
        if(!value.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(value.GetProperty("error").GetString()??"UM-BATCH-NATIVE-REJECTED");
        return value.GetProperty("data").Clone();
    }
    private string Ok(string? id,object? data)=>JsonSerializer.Serialize(new{id,ok=true,data},json);
    private string Fail(string? id,string? error)=>JsonSerializer.Serialize(new{id,ok=false,error},json);
    private static bool Confirm(JsonElement a)=>a.TryGetProperty("confirm",out var yes)&&yes.ValueKind==JsonValueKind.True;
    public Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >8192)throw new InvalidDataException("UM-BATCH-REQUEST");
            using var document=JsonDocument.Parse(raw);
            var a=document.RootElement;id=a.GetProperty("id").GetGuid().ToString("D");
            string? method=a.GetProperty("method").GetString();
            lock(sync)
            {
                if(disposed)return Task.FromResult(Fail(id,"UM-BATCH-CLOSED"));
                if(method=="operations.lifecycleBatchCurrent")
                {
                    var current=Read();
                    if(current?.State=="running")
                    {
                        try{using var stale=LockOwner();current=current with{State="needsReview"};}
                        catch(IOException){/* live owner or unavailable, do not replay */}
                    }
                    return Task.FromResult(Ok(id,new{receipt=current,remoteExecutionAllowed=false}));
                }
                if(method=="operations.lifecycleBatchReview")
                {
                    if(!Confirm(a))return Task.FromResult(Fail(id,"UM-BATCH-CONFIRM"));
                    using var held=LockOwner();
                    var current=Read();
                    if(current==null||current.JobId!=a.GetProperty("jobId").GetGuid()||
                       current.State is not ("running" or "needsReview"))
                        return Task.FromResult(Fail(id,"UM-BATCH-REVIEW-MISMATCH"));
                    var reviewed=current with{State="reviewed",UpdatedUtc=DateTimeOffset.UtcNow,
                        Targets=current.Targets.Select(t=>t.State=="dispatching"?
                            t with{State="unknown",Evidence="OPERATOR_REVIEWED_NO_REPLAY"}:
                            t.State=="queued"?t with{State="skipped",Evidence="NOT_DISPATCHED"}:t).ToArray()};
                    Write(reviewed);return Task.FromResult(Ok(id,reviewed));
                }
                if(method=="operations.lifecycleBatchPreview")
                {
                    using var held=LockOwner();
                    if(Read()?.State is "running" or "needsReview")
                        return Task.FromResult(Fail(id,"UM-BATCH-PRIOR-REVIEW"));
                    string action=a.GetProperty("action").GetString()??"";
                    int delay=a.GetProperty("delaySeconds").GetInt32();
                    var ids=a.GetProperty("mapIds").EnumerateArray().Select(x=>x.GetGuid()).ToArray();
                    if(action is not ("start" or "stop" or "restart")||delay is <0 or >300||
                       ids.Length is <1 or >25||ids.Distinct().Count()!=ids.Length)
                        return Task.FromResult(Fail(id,"UM-BATCH-SCOPE"));
                    var plans=ids.Select(mapId=>{
                        var map=Owned(mapId);var state=observe(map);
                        if(!Eligible(map,state,action))throw new InvalidOperationException("UM-BATCH-TARGET-STATE");
                        return new Plan(map.Id,map.Name,Fingerprint(map),map.HistoricalStoreProtected,state);
                    }).ToArray();
                    pending=new Approval(Guid.NewGuid(),action,delay,DateTimeOffset.UtcNow.AddMinutes(3),plans);
                    return Task.FromResult(Ok(id,new{token=pending.Token,action,delaySeconds=delay,
                        maps=plans.Select(t=>new{mapId=t.MapId,mapName=t.MapName}),remoteExecutionAllowed=false}));
                }
                if(method=="operations.lifecycleBatchApply")
                {
                    if(!Confirm(a)||pending==null||pending.Token!=a.GetProperty("token").GetGuid()||
                       pending.Expires<DateTimeOffset.UtcNow)
                        return Task.FromResult(Fail(id,"UM-BATCH-APPROVAL-REQUIRED"));
                    var approved=pending;pending=null;
                    var held=LockOwner();
                    try
                    {
                        var previous=Read();
                        if(previous?.State is "running" or "needsReview")throw new InvalidOperationException("UM-BATCH-PRIOR-REVIEW");
                        if(previous!=null)
                        {
                            var archive=Path.Combine(root,"native-lifecycle-batch-"+previous.JobId.ToString("N")+".json");
                            if(!File.Exists(archive))File.Copy(journal,archive,false);
                        }
                        foreach(var plan in approved.Plans)Recheck(plan,approved.Action);
                        var now=DateTimeOffset.UtcNow;
                        var receipt=new Receipt(Guid.NewGuid(),approved.Action,"running",now,now,
                            approved.Plans.Select(x=>new Target(x.MapId,x.MapName,"queued","NOT_DISPATCHED")).ToArray());
                        Write(receipt);
                        _=Task.Run(()=>RunAsync(receipt,approved,held,closing.Token));
                        return Task.FromResult(Ok(id,receipt));
                    }
                    catch{held.Dispose();throw;}
                }
                return Task.FromResult(Fail(id,"UM-METHOD-DENIED"));
            }
        }
        catch(Exception ex){return Task.FromResult(Fail(id,NativeLifecycleFailureCode.From(ex)));}
    }
    private void Recheck(Plan plan,string action)
    {
        var map=Owned(plan.MapId);var state=observe(map);
        if(Fingerprint(map)!=plan.Fingerprint||!Eligible(map,state,action)||!SameState(state,plan.Observation))
            throw new InvalidOperationException("UM-BATCH-TARGET-CHANGED");
    }
    private async Task RunAsync(Receipt initial,Approval approved,FileStream held,CancellationToken ct)
    {
        using(held)
        {
            var receipt=initial;
            void Set(int index,string state,string evidence,Guid? native=null)
            {
                var rows=receipt.Targets.ToArray();
                rows[index]=rows[index] with{State=state,Evidence=evidence,NativeJobId=native};
                receipt=receipt with{Targets=rows,UpdatedUtc=DateTimeOffset.UtcNow};Write(receipt);
            }
            try
            {
                for(int i=0;i<approved.Plans.Length;i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var plan=approved.Plans[i];
                    try{Recheck(plan,approved.Action);}
                    catch(Exception ex)
                    {
                        Set(i,"skipped",NativeLifecycleFailureCode.From(ex));
                        continue;
                    }
                    Set(i,"dispatching","DURABLE_CLAIM_NO_REPLAY");
                    try
                    {
                        var result=await execute(plan.MapId,approved.Action,plan.Historical,ct);
                        Set(i,"completed",approved.Action=="stop"?"NATIVE_STOP_RECEIPT":
                            "NEW_PID_AND_RCON_READY_GAME_CLIENT_UNVERIFIED",result.JobId);
                    }
                    catch(Exception ex)
                    {
                        Set(i,"unknown",NativeLifecycleFailureCode.From(ex));
                        for(int j=i+1;j<approved.Plans.Length;j++)Set(j,"skipped","HALTED_AFTER_UNCERTAIN_NATIVE_OPERATION");
                        receipt=receipt with{State="needsReview",UpdatedUtc=DateTimeOffset.UtcNow};Write(receipt);return;
                    }
                    if(i+1<approved.Plans.Length&&approved.DelaySeconds>0)
                        await Task.Delay(TimeSpan.FromSeconds(approved.DelaySeconds),ct);
                }
                receipt=receipt with{State="completed",UpdatedUtc=DateTimeOffset.UtcNow};Write(receipt);
            }
            catch{/* The disk claim survives any write failure or process exit. Never replay. */}
        }
    }
    public void Dispose(){lock(sync){disposed=true;pending=null;closing.Cancel();}}
}
