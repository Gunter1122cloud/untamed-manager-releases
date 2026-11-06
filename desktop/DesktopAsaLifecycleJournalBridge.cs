using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Read-only view of the durable per-installation operation lease. The
/// native operation coordinator keeps this journal across Manager process
/// restarts, so a crashed Start/Stop cannot be silently retried from React.
/// No filesystem paths or credentials are sent to WebView2.
/// </summary>
public sealed class DesktopAsaLifecycleJournalBridge
{
    private readonly ClusterService cluster;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public DesktopAsaLifecycleJournalBridge(ClusterService local)=>
        cluster=local??throw new ArgumentNullException(nameof(local));

    public Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >1024)return Task.FromResult(Error(null,"UM-LIFECYCLE-JOURNAL-REQUEST"));
            using var doc=JsonDocument.Parse(raw);
            var request=doc.RootElement;
            if(request.ValueKind!=JsonValueKind.Object||
               !request.TryGetProperty("id",out var rawId)||
               rawId.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(rawId.GetString(),out Guid requestId)||
               requestId==Guid.Empty||
               !request.TryGetProperty("method",out var method)||
               method.ValueKind!=JsonValueKind.String ||
               method.GetString()!="servers.lifecycleJournal"||
               !request.TryGetProperty("mapId",out var rawMap)||
               rawMap.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(rawMap.GetString(),out Guid mapId)||
               mapId==Guid.Empty)
                return Task.FromResult(Error(null,"UM-LIFECYCLE-JOURNAL-REQUEST"));
            id=requestId.ToString("D");
            var owned=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
            if(owned==null)return Task.FromResult(Error(id,"UM-MAP-NOT-OWNED"));
            var journal=cluster.Operations.Observe(owned.InstallationPath);
            bool requiresReview=journal?.Phase is
                UntamedOperationPhase.Prepared or
                UntamedOperationPhase.Mutating or
                UntamedOperationPhase.NeedsOperator;
            return Task.FromResult(Ok(id,new{
                mapId=owned.Id,mapName=owned.Name,
                journalExists=journal!=null,
                phase=journal?.Phase.ToString(),
                kind=journal?.Kind.ToString(),
                changedUtc=journal?.UpdatedUtc,
                needsOperatorReview=requiresReview,
                autoResumeAllowed=false,
                storeTouched=false,
                evidence="DURABLE_INSTALLATION_OPERATION_JOURNAL_READ_ONLY"
            }));
        }
        catch(Exception)
        {
            return Task.FromResult(Error(id,"UM-LIFECYCLE-JOURNAL-UNAVAILABLE"));
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
}
