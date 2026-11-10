using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit WebView2 allowlist for a STOPPED-only scheduled updater. No arbitrary
/// paths, command lines, Store identifiers, RCON or remote host operations are
/// accepted from the renderer. Creating a schedule requires a typed approval.
/// </summary>
public sealed class DesktopScheduledStoppedSteamBridge
{
    private readonly ClusterService cluster;
    private readonly DesktopScheduledStoppedSteamSupervisor supervisor;
    private const string Approval="AUTOMATIKUS LEÁLLÍTOTT FRISSÍTÉS";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopScheduledStoppedSteamBridge(ClusterService source,
        DesktopScheduledStoppedSteamSupervisor schedule)
    {
        cluster=source??throw new ArgumentNullException(nameof(source));
        supervisor=schedule??throw new ArgumentNullException(nameof(schedule));
    }

    private static string Ok(string? id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);

    public Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2000)
                return Task.FromResult(Fail(id,"UM-STEAM-SCHEDULE-MESSAGE"));
            using var doc=JsonDocument.Parse(raw);
            var args=doc.RootElement;
            if(args.ValueKind!=JsonValueKind.Object||
               !args.TryGetProperty("id",out var requestId)||
               requestId.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(requestId.GetString(),out var guid)||guid==Guid.Empty||
               !args.TryGetProperty("method",out var field)||
               field.ValueKind!=JsonValueKind.String)
                return Task.FromResult(Fail(id,"UM-STEAM-SCHEDULE-MESSAGE"));
            id=guid.ToString("D");
            var method=field.GetString();
            if(method is not ("steam.scheduleStatus" or "steam.scheduleSet" or
                "steam.scheduleDisable"))
                return Task.FromResult(Fail(id,"UM-METHOD-DENIED"));
            var schedule=supervisor.Snapshot();
            if(method=="steam.scheduleStatus")
                return Task.FromResult(Ok(id,new {
                    hostId=cluster.Config.LocalHostId,
                    runnerState=supervisor.RuntimeState,
                    schedules=schedule.Select(s=>new {
                        mapId=s.MapId,mapName=cluster.Config.LocalMaps
                            .FirstOrDefault(m=>m.Id==s.MapId)?.Name??"Nincs helyi map",
                        intervalMinutes=s.IntervalMinutes,enabled=s.Enabled,
                        nextDueUtc=s.NextDueUtc,lastAttemptUtc=s.LastAttemptUtc,
                        lastStatus=s.LastStatus,oldBuild=s.OldBuild,newBuild=s.NewBuild
                    }).ToArray(),
                    runningMapAutoStop=false,
                    runningMapAutoRestart=false,storeWriterTouched=false,
                    remoteHostUpdate=false,
                    evidence="DURABLE_OPT_IN_STOPPED_ONLY_STEAMCMD"
                }));
            if(!args.TryGetProperty("mapId",out var mapField)||
               mapField.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(mapField.GetString(),out var mapId)||
               mapId==Guid.Empty || !cluster.Config.LocalMaps.Any(m=>m.Id==mapId))
                return Task.FromResult(Fail(id,"UM-STEAM-SCHEDULE-NOT-LOCAL"));
            if(!args.TryGetProperty("confirm",out var confirmed)||
               confirmed.ValueKind!=JsonValueKind.True||
               !args.TryGetProperty("typedApproval",out var typed)||
               typed.ValueKind!=JsonValueKind.String||
               typed.GetString()!=Approval)
                return Task.FromResult(Fail(id,"UM-STEAM-SCHEDULE-APPROVAL"));
            ScheduledStoppedSteamUpdate result;
            if(method=="steam.scheduleSet")
            {
                if(!args.TryGetProperty("intervalMinutes",out var interval)||
                   interval.ValueKind!=JsonValueKind.Number||
                   !interval.TryGetInt32(out int minutes))
                    return Task.FromResult(Fail(id,"UM-STEAM-SCHEDULE-INTERVAL"));
                result=supervisor.Configure(mapId,minutes);
            }
            else result=supervisor.Disable(mapId);
            return Task.FromResult(Ok(id,new{
                mapId=result.MapId,enabled=result.Enabled,
                intervalMinutes=result.IntervalMinutes,
                nextDueUtc=result.NextDueUtc,lastStatus=result.LastStatus,
                autoStopRunning=false,autoRestartRunning=false,
                storeWriterTouched=false,
                evidence="EXPLICIT_LOCAL_STOPPED_ONLY_SCHEDULE_CHANGE"
            }));
        }
        catch(Exception)
        {
            return Task.FromResult(Fail(id,"UM-STEAM-SCHEDULE-FAIL-CLOSED"));
        }
    }
}
