using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Bounded, on-demand operational evidence for the locally owned ASA servers.
/// No commands, credentials, remote writes, Store changes or generated telemetry.
/// Process resources are trusted only after matching PID + Windows start time.
/// Read-only recurring jobs execute only in the independent durable supervisor.
/// The renderer may create/disable an explicitly approved local inspection,
/// never schedule a server lifecycle, RCON, Store, SteamCMD or peer write.
/// </summary>
public sealed class DesktopOperationsCenterBridge
{
    private readonly ClusterService cluster;
    private readonly DesktopRecurringReadOnlySupervisor? supervisor;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DesktopOperationsCenterBridge(ClusterService source,
        DesktopRecurringReadOnlySupervisor? readOnlySupervisor=null)
    {
        cluster=source??throw new ArgumentNullException(nameof(source));
        supervisor=readOnlySupervisor;
    }

    public Task<string> HandleAsync(string raw)
    {
        string? id = null;
        try
        {
            if(raw.Length is < 2 or > 1200)return Task.FromResult(Fail(null,"UM-OPS-INVALID-REQUEST"));
            using var document = JsonDocument.Parse(raw);
            var request = document.RootElement;
            if(request.ValueKind != JsonValueKind.Object ||
               !request.TryGetProperty("id",out var rid) ||
               rid.ValueKind != JsonValueKind.String ||
               !Guid.TryParse(rid.GetString(),out var requestId) ||
               requestId == Guid.Empty ||
               !request.TryGetProperty("method",out var methodValue) ||
               methodValue.ValueKind != JsonValueKind.String)
                return Task.FromResult(Fail(null,"UM-OPS-INVALID-REQUEST"));
            id = requestId.ToString("D");
            string? method = methodValue.GetString();
            if(method is not ("operations.snapshot" or "operations.map" or
                "operations.scheduleCreate" or "operations.scheduleDisable"))
                return Task.FromResult(Fail(id,"UM-METHOD-DENIED"));

            if(method=="operations.scheduleCreate")
            {
                // Operator must explicitly approve this local Manager-config
                // write. Never accept untrusted absolute paths or raw commands.
                if(!Confirm(request)||
                   !request.TryGetProperty("mapId",out var mapField)||
                   mapField.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(mapField.GetString(),out var mapId)||
                   !request.TryGetProperty("kind",out var kindField)||
                   kindField.ValueKind!=JsonValueKind.String||
                   !Enum.TryParse<OperationalTaskKind>(kindField.GetString(),false,out var kind)||
                   !request.TryGetProperty("intervalMinutes",out var interval)||
                   interval.ValueKind!=JsonValueKind.Number||
                   !interval.TryGetInt32(out int minutes))
                    return Task.FromResult(Fail(id,"UM-OPS-SCHEDULE-INVALID-OR-UNAPPROVED"));
                var created=cluster.CreateReadOnlySchedule(mapId,kind,minutes);
                return Task.FromResult(Ok(id,new {
                    jobId=created.Id,mapId=created.Map,kind=created.Kind.ToString(),
                    intervalMinutes=created.IntervalMinutes,enabled=created.Enabled,
                    autoLifecycleAllowed=false,remoteCommandsAllowed=false,
                    evidence="PERSISTED_LOCAL_READ_ONLY_SCHEDULE_ONLY"
                }));
            }
            if(method=="operations.scheduleDisable")
            {
                if(!Confirm(request)||
                   !request.TryGetProperty("jobId",out var jobField)||
                   jobField.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(jobField.GetString(),out var jobId))
                    return Task.FromResult(Fail(id,"UM-OPS-SCHEDULE-INVALID-OR-UNAPPROVED"));
                var disabled=cluster.DisableReadOnlySchedule(jobId);
                return Task.FromResult(Ok(id,new{
                    jobId=disabled.Id,enabled=false,autoLifecycleAllowed=false,
                    evidence="LOCAL_READ_ONLY_SCHEDULE_DISABLED"
                }));
            }

            Guid selectedId = Guid.Empty;
            if(method == "operations.map")
            {
                if(!request.TryGetProperty("mapId",out var selected) ||
                   selected.ValueKind != JsonValueKind.String ||
                   !Guid.TryParse(selected.GetString(),out selectedId) ||
                   selectedId == Guid.Empty)
                    return Task.FromResult(Fail(id,"UM-OPS-INVALID-MAP"));
                if(!cluster.Config.LocalMaps.Any(m => m.Id == selectedId))
                    return Task.FromResult(Fail(id,"UM-MAP-NOT-OWNED"));
            }

            var now=DateTimeOffset.UtcNow;
            // No mutation or state capture on a status read. The Core checks
            // process command, executable and start identity itself.
            var maps = cluster.Config.LocalMaps
                .Where(m => method == "operations.snapshot" || m.Id == selectedId)
                .Take(50).Select(m => Describe(m,now)).ToArray();
            // Read the persisted due cursor, not a moving estimate relative
            // to each screen refresh. If journal access fails, expose the
            // distinction explicitly and keep all server operations blocked.
            IReadOnlyDictionary<Guid,DateTimeOffset> durableDue=
                new System.Collections.Generic.Dictionary<Guid,DateTimeOffset>();
            string journalState=supervisor?.RuntimeState??"not-configured";
            if(supervisor!=null)
            {
                try{durableDue=supervisor.DueDates();}
                catch(Exception){journalState="journal-unavailable";}
            }
            var schedules = cluster.SnapshotRecurringSchedules()
                .Where(job => method == "operations.snapshot" || job.Map == selectedId)
                .Take(200).Select(job => {
                    var owned=cluster.Config.LocalMaps.FirstOrDefault(m=>m.Id==job.Map);
                    try {
                        var due=OperationalScheduler.Next(job,now,
                            owned!=null,owned?.Maintenance??false);
                        return new {
                            id=job.Id.ToString("D"), mapId=job.Map.ToString("D"),
                            mapName=owned?.Name ?? "Nincs helyi map",
                            kind=job.Kind.ToString(),frequency=job.Frequency.ToString(),
                            enabled=job.Enabled,
                            nextUtc=(DateTimeOffset?)(durableDue.TryGetValue(job.Id,out var actualDue)?
                                actualDue:due.DueUtc),
                            state=due.Status, autoExecutionPermitted=due.AutoExecutionPermitted
                        };
                    }catch(Exception) {
                        return new {
                            id=job.Id.ToString("D"),mapId=job.Map.ToString("D"),
                            mapName=owned?.Name ?? "Nincs helyi map",
                            kind=job.Kind.ToString(),frequency=job.Frequency.ToString(),
                            enabled=job.Enabled,nextUtc=(DateTimeOffset?)null,
                            state="invalid-schedule",autoExecutionPermitted=false
                        };
                    }
                }).ToArray();
            RecurringJobReceipt[] receipts=Array.Empty<RecurringJobReceipt>();
            string supervisorState=journalState;
            if(supervisor!=null)
            {
                try{receipts=supervisor.Recent(40);}
                catch(Exception){supervisorState="journal-unavailable";}
            }
            return Task.FromResult(Ok(id,new {
                hostName=cluster.Config.LocalHostName,
                observedUtc=now, maps, schedules,
                scheduleReceipts=receipts,supervisorState,
                schedulingIsReadOnly=true,
                remoteCommandsAllowed=false,
                evidence="LOCAL_OS_PROCESS_AND_SAVED_SCHEDULE_READ_ONLY"
            }));
        }
        catch(Exception)
        {
            return Task.FromResult(Fail(id,"UM-OPS-OBSERVATION-UNAVAILABLE"));
        }
    }

    private static bool Confirm(JsonElement obj)=>
        obj.TryGetProperty("confirm",out var value) &&
        value.ValueKind==JsonValueKind.True;

    private object Describe(LocalMap map,DateTimeOffset now)
    {
        bool storeProtected=KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map);
        ProcessCheck state;
        try
        {
            // Lifecycle authorization is NOT process telemetry. Check()
            // intentionally denies historical Store maps before inspecting
            // Windows; show the actual observed identity instead of falsely
            // reporting a running legacy ASA as stopped. No control granted.
            state=storeProtected ? cluster.ObserveLocalProcessesReadOnly(map)
                : cluster.Check(map);
        }
        catch(Exception)
        {
            state=new ProcessCheck(false,
                "Helyi folyamatellenőrzés nem végezhető el",null);
        }
        bool running=state.Running && !state.ConfirmedStopped &&
            state.Identity is {Pid:>0};
        int? pid=running?state.Identity!.Pid:null;
        DateTimeOffset? started=running?state.Identity!.StartedUtc:null;
        long? uptimeSeconds=running && started<=now &&
            (now-started.Value).TotalDays<365
            ? (long)(now-started.Value).TotalSeconds : null;
        double? memoryMiB=null;
        bool memoryVerified=false;
        // A reused PID must not reveal another process's RAM figure.
        if(pid is >0 && started.HasValue)
        {
            try
            {
                using var process=Process.GetProcessById(pid.Value);
                DateTimeOffset osStart=new(process.StartTime.ToUniversalTime());
                if(!process.HasExited && Math.Abs((osStart-started.Value).TotalSeconds)<2)
                {
                    memoryMiB=Math.Round(process.WorkingSet64/1048576d,1);
                    memoryVerified=true;
                }
            }
            catch(Exception) { /* OS access denied or PID vanished: unknown */ }
        }
        return new {
            mapId=map.Id.ToString("D"), mapName=map.Name,
            running,confirmedStopped=state.ConfirmedStopped && !state.Running,
            status=state.Status, pid, startedUtc=started, uptimeSeconds,
            memoryMiB, memoryVerified, maintenance=map.Maintenance,
            storeProtected,gamePort=map.GamePort,
            queryPort=map.QueryPort,
            rconConfigured=map.RconPort>0 && map.RconHost=="127.0.0.1" &&
                !string.IsNullOrWhiteSpace(map.RconPasswordProtected),
            rconPort=map.RconPort>0?map.RconPort:(int?)null,
            autoStartConfigured=map.AutoStart,
            lifecycleActionsVerified=false,
            observationOnly=true,
            evidence=map.HistoricalStoreProtected?
                "HISTORICAL_STORE_V1_PROCESS_OBSERVATION_NO_LIFECYCLE_RIGHTS":
                "EXACT_LOCAL_PROCESS_IDENTITY_NO_NETWORK_OR_FILE_WRITE"
        };
    }

    private static string Ok(string id,object data) =>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error) =>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
}
