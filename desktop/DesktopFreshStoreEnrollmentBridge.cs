using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// One-shot, local-only new EMPTY Store enrollment. Requires an existing
/// ClusterRegistry membership; neither the renderer nor the bridge writes
/// transfer data, creates a cluster session, converts a world or starts ASA.
/// </summary>
public sealed class DesktopFreshStoreEnrollmentBridge : IDisposable
{
    private sealed record Approval(Guid Token,Guid MapId,string ClusterId,
        string Fingerprint,long Revision,DateTimeOffset Created);
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim serial=new(1,1);
    private Approval? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopFreshStoreEnrollmentBridge(ClusterService service)=>
        cluster=service??throw new ArgumentNullException(nameof(service));

    private LocalMap? Owned(Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id);
    private static bool GuidId(JsonElement root,string name,out Guid value)
    {
        value=Guid.Empty;
        return root.TryGetProperty(name,out var raw)&&
            raw.ValueKind==JsonValueKind.String&&
            Guid.TryParse(raw.GetString(),out value)&&value!=Guid.Empty;
    }
    private static bool Text(JsonElement root,string name,out string text)
    {
        text="";
        if(!root.TryGetProperty(name,out var raw)||
           raw.ValueKind!=JsonValueKind.String)return false;
        text=raw.GetString()??"";
        return text.Length is >0 and <=64&&text.All(c=>
            char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)return Fail(null,"UM-FRESH-STORE-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var req=doc.RootElement;
            if(req.ValueKind!=JsonValueKind.Object||
               !GuidId(req,"id",out var requestId)||
               !req.TryGetProperty("method",out var route)||
               route.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-FRESH-STORE-REQUEST");
            id=requestId.ToString("D");
            string? method=route.GetString();
            if(method is not ("freshStore.enrollmentStatus" or
                "freshStore.enrollmentPreview" or "freshStore.enrollmentApply"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="freshStore.enrollmentStatus")
                {
                    if(!GuidId(req,"mapId",out var mapId))
                        return Fail(id,"UM-FRESH-STORE-LOCAL-MAP");
                    var map=Owned(mapId);
                    if(map==null)return Fail(id,"UM-FRESH-STORE-LOCAL-MAP");
                    return Ok(id,new{
                        mapId,mapName=map.Name,
                        enrolled=FreshStoreEnrollmentPolicy.IsEnrolled(map),
                        historicalProtected=map.HistoricalStoreProtected,
                        clusterId=map.FreshStoreClusterId,
                        startsServer=false,originalStoreTouched=false,
                        evidence="LOCAL_MAP_NEW_STORE_ENROLLMENT_STATUS"
                    });
                }
                if(method=="freshStore.enrollmentPreview")
                {
                    pending=null;
                    if(!GuidId(req,"mapId",out var mapId)||
                       !Text(req,"clusterId",out string clusterId))
                        return Fail(id,"UM-FRESH-STORE-MAP-OR-CLUSTER");
                    var map=Owned(mapId);
                    if(map==null)return Fail(id,"UM-FRESH-STORE-LOCAL-MAP");
                    var plan=await Task.Run(()=>FreshStoreEnrollmentPolicy.Prepare(
                        cluster,map,clusterId));
                    pending=new Approval(Guid.NewGuid(),map.Id,plan.ClusterId,
                        plan.Fingerprint,plan.RegistryRevision,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pending.Token,mapId,mapName=map.Name,
                        clusterId=plan.ClusterId,
                        gamePort=map.GamePort,queryPort=map.QueryPort,
                        steamBuild=plan.InstalledBuild,
                        transferIdentityCheckedLocally=true,
                        remoteIdentityCertified=false,
                        conversionPerformed=false,serverStarted=false,
                        originalStoreTouched=false,automaticRetry=false,
                        evidence="NEW_EMPTY_STORE_ENROLLMENT_PREVIEW_ONLY"
                    });
                }
                if(!GuidId(req,"token",out var token)||pending==null||
                   pending.Token!=token||
                   DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(3))
                    return Fail(id,"UM-FRESH-STORE-PREVIEW-EXPIRED");
                if(!req.TryGetProperty("confirm",out var yes)||
                   yes.ValueKind!=JsonValueKind.True||
                   !req.TryGetProperty("typedApproval",out var typed)||
                   typed.ValueKind!=JsonValueKind.String||
                   typed.GetString()!="NEW EMPTY STORE")
                    return Fail(id,"UM-FRESH-STORE-EXPLICIT-APPROVAL");
                var approved=pending;pending=null; // one-shot, including failures
                var original=Owned(approved.MapId);
                if(original==null)return Fail(id,"UM-FRESH-STORE-LOCAL-MAP");
                var current=await Task.Run(()=>FreshStoreEnrollmentPolicy.Prepare(
                    cluster,original,approved.ClusterId));
                if(current.Fingerprint!=approved.Fingerprint||
                   current.RegistryRevision!=approved.Revision)
                    return Fail(id,"UM-FRESH-STORE-STATE-CHANGED");
                using var lease=cluster.Operations.Acquire(original.InstallationPath,
                    UntamedOperationKind.Configuration);
                var locked=FreshStoreEnrollmentPolicy.Prepare(
                    cluster,original,approved.ClusterId);
                if(!ReferenceEquals(Owned(approved.MapId),original)||
                   locked.Fingerprint!=approved.Fingerprint)
                    return Fail(id,"UM-FRESH-STORE-LEASE-CHANGED");
                var copy=JsonSerializer.Deserialize<LocalMap>(
                    JsonSerializer.Serialize(original))
                    ??throw new InvalidDataException("UM-FRESH-STORE-CLONE");
                copy.Arguments=locked.Arguments;
                copy.FreshStoreClusterId=locked.ClusterId;
                copy.FreshStoreTransferDirectory=locked.TransferDirectory;
                copy.AutoStart=false;
                // No conversion, shared directory write, RCON or ASA launch.
                lease.MarkMutationStarted();
                cluster.ReplaceMap(original,copy);
                lease.Complete();
                return Ok(id,new{
                    mapId=copy.Id,mapName=copy.Name,
                    enrolled=true,clusterId=copy.FreshStoreClusterId,
                    conversionPerformed=false,serverStarted=false,
                    originalStoreTouched=false,requiresManualStart=true,
                    evidence="NEW_EMPTY_STORE_ATOMIC_LOCAL_MANAGER_PROFILE_ONLY"
                });
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            // Avoid leaking registered UNC paths, CLI values, map secrets.
            return Fail(id,"UM-FRESH-STORE-PREFLIGHT-OR-ENROLLMENT-FAILED");
        }
    }

    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public void Dispose()=>serial.Dispose();
}
