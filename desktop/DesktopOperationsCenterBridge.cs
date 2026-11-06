using System;
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
/// Recurring jobs are shown from persisted Manager configuration and never run.
/// </summary>
public sealed class DesktopOperationsCenterBridge
{
    private readonly ClusterService cluster;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DesktopOperationsCenterBridge(ClusterService source) =>
        cluster = source ?? throw new ArgumentNullException(nameof(source));

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
            if(method is not ("operations.snapshot" or "operations.map"))
                return Task.FromResult(Fail(id,"UM-METHOD-DENIED"));

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
            var schedules = method == "operations.snapshot"
                ? cluster.Config.RecurringSchedules.Take(200).Select(job => {
                    var owned=cluster.Config.LocalMaps.FirstOrDefault(m=>m.Id==job.Map);
                    try {
                        var due=OperationalScheduler.Next(job,now,
                            owned!=null,owned?.Maintenance??false);
                        return new {
                            id=job.Id.ToString("D"), mapId=job.Map.ToString("D"),
                            mapName=owned?.Name ?? "Nincs helyi map",
                            kind=job.Kind.ToString(),frequency=job.Frequency.ToString(),
                            enabled=job.Enabled, nextUtc=(DateTimeOffset?)due.DueUtc,
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
                }).ToArray()
                : Array.Empty<object>();
            return Task.FromResult(Ok(id,new {
                hostName=cluster.Config.LocalHostName,
                observedUtc=now, maps, schedules,
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

    private object Describe(LocalMap map,DateTimeOffset now)
    {
        var state=cluster.Check(map);
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
        bool historical=KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map);
        return new {
            mapId=map.Id.ToString("D"), mapName=map.Name,
            running,confirmedStopped=state.ConfirmedStopped && !state.Running,
            status=state.Status, pid, startedUtc=started, uptimeSeconds,
            memoryMiB, memoryVerified, maintenance=map.Maintenance,
            storeProtected=historical,gamePort=map.GamePort,
            queryPort=map.QueryPort,
            rconConfigured=map.RconPort>0 && map.RconHost=="127.0.0.1" &&
                !string.IsNullOrWhiteSpace(map.RconPasswordProtected),
            rconPort=map.RconPort>0?map.RconPort:(int?)null,
            autoStartConfigured=map.AutoStart,
            lifecycleActionsVerified=false,
            evidence="EXACT_LOCAL_PROCESS_IDENTITY_NO_NETWORK_OR_FILE_WRITE"
        };
    }

    private static string Ok(string id,object data) =>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error) =>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
}
