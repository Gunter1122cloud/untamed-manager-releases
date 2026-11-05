using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

// Local configuration editor only: cannot start/stop ASA, touch SQL/Store or download mods.
// A save requires a previously inspected, explicit in-memory draft and the existing
// ModStateController optimistic-concurrency filesystem transaction.
public sealed class DesktopModDraftBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly Dictionary<Guid,Draft> drafts = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Draft(Guid Token,Guid MapId,ModDraftSession Session,DateTimeOffset CreatedUtc);
    public DesktopModDraftBridge(ClusterService source) =>
        cluster = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Error(null,"UM-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var args=doc.RootElement;
            if(args.ValueKind!=JsonValueKind.Object ||
               !GetGuid(args,"id",out var requestId) ||
               !args.TryGetProperty("method",out var methodField) ||
               methodField.ValueKind!=JsonValueKind.String)
                return Error(null,"UM-INVALID-MESSAGE");
            id=requestId.ToString("D");
            string? method=methodField.GetString();
            if(method is not ("mods.draftStart" or "mods.draftStage" or
                "mods.draftUndo" or "mods.draftRedo" or
                "mods.draftDiscard" or "mods.draftSave"))
                return Error(id,"UM-METHOD-DENIED");

            await gate.WaitAsync();
            try
            {
                foreach(var expired in drafts.Where(x=>
                    DateTimeOffset.UtcNow-x.Value.CreatedUtc>TimeSpan.FromMinutes(15))
                    .Select(x=>x.Key).ToArray())drafts.Remove(expired);

                if(method=="mods.draftStart")
                {
                    if(!GetGuid(args,"mapId",out var mapId))
                        return Error(id,"UM-INVALID-MAP");
                    var local=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId);
                    if(local==null)return Error(id,"UM-MAP-NOT-OWNED");
                    if(local.HistoricalStoreProtected)return Error(id,"UM-HISTORICAL-STORE-LOCKED");
                    foreach(var old in drafts.Where(x=>x.Value.MapId==mapId)
                        .Select(x=>x.Key).ToArray())drafts.Remove(old);
                    if(drafts.Count>=8)return Error(id,"UM-TOO-MANY-DRAFTS");
                    var token=Guid.NewGuid();
                    var draft=new Draft(token,mapId,new ModDraftSession(local),DateTimeOffset.UtcNow);
                    drafts.Add(token,draft);
                    return Ok(id,Describe(draft));
                }
                if(!GetGuid(args,"draftId",out var draftId) ||
                   !drafts.TryGetValue(draftId,out var current))
                    return Error(id,"UM-DRAFT-NOT-FOUND");
                var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==current.MapId);
                if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                if(map.HistoricalStoreProtected)return Error(id,"UM-HISTORICAL-STORE-LOCKED");
                if(!current.Session.MatchesLive(map))return Error(id,"UM-DRAFT-CONFLICT");

                if(method=="mods.draftDiscard")
                {
                    drafts.Remove(draftId);
                    return Ok(id,new{discarded=true});
                }
                if(method=="mods.draftStage")
                {
                    if(!args.TryGetProperty("modId",out var mod) ||
                       mod.ValueKind!=JsonValueKind.String ||
                       !args.TryGetProperty("mode",out var mode) ||
                       mode.ValueKind!=JsonValueKind.String)
                        return Error(id,"UM-INVALID-MOD-EDIT");
                    ModMode? target=mode.GetString() switch {
                        "active"=>ModMode.Active,"passive"=>ModMode.Passive,
                        "disabled"=>ModMode.Disabled,"remove"=>null,
                        _=>throw new ArgumentException("Invalid edit mode")
                    };
                    int? position=null;
                    if(args.TryGetProperty("position",out var index))
                    {
                        if(index.ValueKind!=JsonValueKind.Number ||
                           !index.TryGetInt32(out int requested) ||
                           requested is <0 or >500)
                            return Error(id,"UM-INVALID-POSITION");
                        position=requested;
                    }
                    current.Session.Stage(mod.GetString()??"",target,position);
                }
                else if(method=="mods.draftUndo")current.Session.Undo();
                else if(method=="mods.draftRedo")current.Session.Redo();
                else if(method=="mods.draftSave")
                {
                    if(!current.Session.IsDirty)return Error(id,"UM-NO-CHANGES");
                    // Existing operation coordinator + CAS save; does NOT restart servers.
                    current.Session.Save(cluster);
                    var applied=ModStateController.Read(map);
                    drafts.Remove(draftId);
                    return Ok(id,new{
                        saved=true,pending=applied.Pending,
                        active=applied.Active,passive=applied.Passive,disabled=applied.Disabled,
                        evidence="CONFIGURED_ONLY_PENDING_REAL_RESTART"
                    });
                }
                return Ok(id,Describe(current));
            }
            finally {gate.Release();}
        }
        catch(Exception)
        {
            // Never echo launch arguments, file paths or native exceptions.
            return Error(id,"UM-DRAFT-OPERATION-FAILED");
        }
    }

    private static bool GetGuid(JsonElement obj,string key,out Guid value)
    {
        value=Guid.Empty;
        return obj.TryGetProperty(key,out var field) &&
            field.ValueKind==JsonValueKind.String &&
            Guid.TryParse(field.GetString(),out value) &&
            value!=Guid.Empty;
    }

    private static object Describe(Draft draft)
    {
        var projected=ModStateController.Read(draft.Session.Projected());
        return new {
            draftId=draft.Token,mapId=draft.MapId,
            active=projected.Active,passive=projected.Passive,
            disabled=projected.Disabled,pending=projected.Pending,
            dirty=draft.Session.IsDirty,
            canUndo=draft.Session.CanUndo,canRedo=draft.Session.CanRedo,
            differences=draft.Session.Differences().Select(d=>new{
                modId=d.ProjectId,
                beforeMode=d.BeforeMode?.ToString().ToLowerInvariant(),
                beforePosition=d.BeforePosition,
                afterMode=d.AfterMode?.ToString().ToLowerInvariant(),
                afterPosition=d.AfterPosition
            }).ToArray(),
            evidence="IN_MEMORY_MOD_DRAFT"
        };
    }

    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>gate.Dispose();
}
