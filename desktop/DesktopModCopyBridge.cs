using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Multi-map Copy To / Merge / Replace. Only Manager launch configuration on
/// explicitly owned local maps can change, via one atomic Core operation.
/// Imported historical Store-v1 maps can edit their dedicated Manager mod
/// lists only while exact native Windows process-family checks prove stopped;
/// remote host profiles and the shared Store writer are never writable.
/// </summary>
public sealed class DesktopModCopyBridge : IDisposable
{
    private sealed record Approval(Guid Token,LocalModCopyPlan Plan,DateTimeOffset ExpiresUtc);
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim serial=new(1,1);
    private Approval? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public DesktopModCopyBridge(ClusterService source)=>
        cluster=source??throw new ArgumentNullException(nameof(source));

    private void DemandHistoricalTargetsStopped(LocalModCopyPlan plan)
    {
        foreach(var target in plan.Targets)
        {
            var local=cluster.Config.LocalMaps.SingleOrDefault(
                m=>m.Id==target.MapId)??throw new UnauthorizedAccessException(
                    "UM-MOD-COPY-LOCAL-MAP-CHANGED");
            if(local.HistoricalStoreProtected)
                HistoricalLocalConfigGuard.DemandStopped(cluster,local);
        }
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >8192)return Fail(null,"UM-MOD-COPY-INVALID-REQUEST");
            using var document=JsonDocument.Parse(raw);
            var data=document.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !data.TryGetProperty("id",out var requestId) ||
               requestId.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(requestId.GetString(),out var parsedId) ||
               parsedId==Guid.Empty ||
               !data.TryGetProperty("method",out var methodElement) ||
               methodElement.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-MOD-COPY-INVALID-REQUEST");
            id=parsedId.ToString("D");
            string? method=methodElement.GetString();
            if(method is not ("mods.copyPreview" or "mods.copyApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="mods.copyPreview")
                {
                    pending=null;
                    if(!data.TryGetProperty("sourceMapId",out var source) ||
                       source.ValueKind!=JsonValueKind.String ||
                       !Guid.TryParse(source.GetString(),out var sourceId) ||
                       sourceId==Guid.Empty ||
                       !data.TryGetProperty("targets",out var targets) ||
                       targets.ValueKind!=JsonValueKind.Array ||
                       targets.GetArrayLength() is <1 or >25 ||
                       !data.TryGetProperty("mode",out var copyMode) ||
                       copyMode.ValueKind!=JsonValueKind.String)
                        return Fail(id,"UM-MOD-COPY-INVALID-REQUEST");
                    var mode=copyMode.GetString() switch {
                        "merge"=>LocalModCopyMode.Merge,
                        "replace"=>LocalModCopyMode.Replace,
                        _=>throw new InvalidOperationException("Invalid mod copy mode")
                    };
                    var ids=new List<Guid>();
                    foreach(var element in targets.EnumerateArray())
                    {
                        if(element.ValueKind!=JsonValueKind.String ||
                           !Guid.TryParse(element.GetString(),out var target) ||
                           target==Guid.Empty)
                            return Fail(id,"UM-MOD-COPY-INVALID-TARGET");
                        ids.Add(target);
                    }
                    var plan=LocalModCopyPlanner.Preview(cluster,sourceId,ids,mode);
                    DemandHistoricalTargetsStopped(plan);
                    var token=Guid.NewGuid();
                    pending=new(token,plan,DateTimeOffset.UtcNow.AddMinutes(5));
                    return Ok(id,new {
                        token,sourceMapId=sourceId,mode=copyMode.GetString(),
                        targets=plan.Targets.Select(t=>new {
                            mapId=t.MapId,mapName=t.MapName,changed=t.Changed,
                            before=new {
                                active=t.Before.Active,passive=t.Before.Passive,
                                disabled=t.Before.Disabled
                            },
                            after=new {
                                active=t.After.Active,passive=t.After.Passive,
                                disabled=t.After.Disabled
                            }
                        }).ToArray(),
                        operationCount=plan.Edits.Count,
                        changed=plan.Changed,requiresRestart=plan.Changed,
                        remoteWritesAllowed=false,storeWriterTouched=false,
                        evidence="LOCAL_MOD_COPY_PREVIEW_NO_WRITES"
                    });
                }
                if(!data.TryGetProperty("token",out var supplied) ||
                   supplied.ValueKind!=JsonValueKind.String ||
                   !Guid.TryParse(supplied.GetString(),out var tokenId) ||
                   !data.TryGetProperty("confirm",out var confirmed) ||
                   confirmed.ValueKind!=JsonValueKind.True ||
                   pending==null || pending.Token!=tokenId)
                    return Fail(id,"UM-MOD-COPY-APPROVAL-REQUIRED");
                var approved=pending;
                pending=null; // No replay, including failed CAS or interrupted writes.
                if(approved.ExpiresUtc<DateTimeOffset.UtcNow)
                    return Fail(id,"UM-MOD-COPY-PREVIEW-EXPIRED");
                if(!approved.Plan.Changed)
                    return Fail(id,"UM-MOD-COPY-NOTHING-TO-APPLY");
                DemandHistoricalTargetsStopped(approved.Plan);
                var applied=await Task.Run(()=>
                    LocalModCopyPlanner.Apply(cluster,approved.Plan));
                return Ok(id,new {
                    saved=true,sourceMapId=approved.Plan.SourceMapId,
                    mode=approved.Plan.Mode.ToString().ToLowerInvariant(),
                    changedMaps=approved.Plan.Targets.Where(t=>t.Changed)
                        .Select(t=>t.MapId).ToArray(),
                    operationsApplied=applied.Count,
                    requiresRestart=true,modsLoadedVerified=false,
                    remoteWritesAllowed=false,storeWriterTouched=false,
                    evidence="LOCAL_MANAGER_CONFIG_ATOMIC_BATCH_PENDING_RESTART"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-MOD-COPY-SAFETY-CHECK-FAILED");
        }
    }

    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>serial.Dispose();
}
