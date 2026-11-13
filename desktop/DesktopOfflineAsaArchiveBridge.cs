using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit OFFLINE copied-cluster backup from a separate Windows test PC.
/// UI selects only native folders; no arbitrary renderer paths or remote writes.
/// No SQL, live ASA, original SMB/Store mutation or production restore.
/// </summary>
public sealed class DesktopOfflineAsaArchiveBridge : IDisposable
{
    private sealed record Approval(Guid Token,string Hp,string Ryzen,string Transfer,
        string Parent,DateTimeOffset PreparedUtc);
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public string State="queued";
        public string Code="PREPARED";
        public string Name="";
        public int VerifiedMaps;
        public int TransferFiles;
        public int ConfigurationFiles;
        public DateTimeOffset Changed=DateTimeOffset.UtcNow;
    }
    private readonly Func<string,string?> pick;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly object sync=new();
    private Approval? pending;
    private Job? current;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopOfflineAsaArchiveBridge(Func<string,string?>? directoryPicker=null)
        =>pick=directoryPicker??PickNative;

    private static string? PickNative(string label)
    {
        using var dialog=new FolderBrowserDialog
        {
            Description=label,ShowNewFolderButton=true,
            UseDescriptionForTitle=true
        };
        return dialog.ShowDialog()==DialogResult.OK?dialog.SelectedPath:null;
    }
    private static string Name(string path)
    {
        var name=Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length>100?name[..100]:name;
    }
    private static object Snapshot(Job job)
    {
        lock(job)return new{
            jobId=job.Id,state=job.State,code=job.Code,
            archiveName=job.Name,verifiedMaps=job.VerifiedMaps,
            transferFiles=job.TransferFiles,
            configurationFiles=job.ConfigurationFiles,changedUtc=job.Changed,
            liveConsistencyCertified=false,storeWriterCertified=false,
            productionRestoreAllowed=false,sqlUsed=false,
            evidence="OFFLINE_FILE_INTEGRITY_ONLY_NO_LIVE_WRITER"
        };
    }
    private static void Stage(Job job,string state,string code,
        string name="",int maps=0,int transfers=0,int config=0)
    {
        lock(job)
        {
            job.State=state;job.Code=code;job.Name=name;
            job.VerifiedMaps=maps;job.TransferFiles=transfers;
            job.ConfigurationFiles=config;
            job.Changed=DateTimeOffset.UtcNow;
        }
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >1100)return Fail(null,"UM-CLUSTER-ARCHIVE-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var request=doc.RootElement;
            if(request.ValueKind!=JsonValueKind.Object ||
               !request.TryGetProperty("id",out var requestId) ||
               requestId.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(requestId.GetString(),out var parsedId) ||
               parsedId==Guid.Empty ||
               !request.TryGetProperty("method",out var route) ||
               route.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-CLUSTER-ARCHIVE-REQUEST");
            id=parsedId.ToString("D");
            string? method=route.GetString();
            if(method is not ("cluster.offlinePreview" or
                "cluster.offlineApply" or "cluster.offlineStatus"))
                return Fail(id,"UM-METHOD-DENIED");
            await gate.WaitAsync();
            try
            {
                if(method=="cluster.offlineStatus")
                {
                    if(!request.TryGetProperty("jobId",out var jobId) ||
                       jobId.ValueKind!=JsonValueKind.String ||
                       !Guid.TryParse(jobId.GetString(),out var jobGuid) ||
                       current==null||current.Id!=jobGuid)
                        return Fail(id,"UM-CLUSTER-ARCHIVE-JOB-NOT-FOUND");
                    return Ok(id,Snapshot(current));
                }
                if(current!=null)
                {
                    lock(current)
                    {
                        if(current.State is "running" or "queued")
                            return Fail(id,"UM-CLUSTER-ARCHIVE-BUSY");
                    }
                }
                if(method=="cluster.offlinePreview")
                {
                    pending=null;
                    var hp=pick("Válaszd ki az 5 HP map OFFLINE mentéscsomagjainak szülőmappáját");
                    if(string.IsNullOrWhiteSpace(hp))return Fail(id,"UM-CLUSTER-ARCHIVE-CANCELLED");
                    var ryzen=pick("Válaszd ki az 5 Ryzen map OFFLINE mentéscsomagjainak szülőmappáját");
                    if(string.IsNullOrWhiteSpace(ryzen))return Fail(id,"UM-CLUSTER-ARCHIVE-CANCELLED");
                    var transfer=pick("Válaszd ki a közös Store/transfer mappa ELŐRE ELKÉSZÍTETT HELYI MÁSOLATÁT");
                    if(string.IsNullOrWhiteSpace(transfer))return Fail(id,"UM-CLUSTER-ARCHIVE-CANCELLED");
                    var parent=pick("Válaszd ki az ÚJ, SQL-mentes cluster-archívum célkönyvtárának SZÜLŐMAPPÁJÁT");
                    if(string.IsNullOrWhiteSpace(parent))return Fail(id,"UM-CLUSTER-ARCHIVE-CANCELLED");
                    if(!Directory.Exists(hp)||!Directory.Exists(ryzen)||
                       !Directory.Exists(transfer)||!Directory.Exists(parent))
                        return Fail(id,"UM-CLUSTER-ARCHIVE-FOLDERS-MISSING");
                    // Stop early if selections are not offline copies. Native
                    // Core repeats exhaustive path, map and hash validation.
                    bool hpConfig=Directory.Exists(Path.Combine(hp,"CONFIG"));
                    bool ryzenConfig=Directory.Exists(Path.Combine(ryzen,"CONFIG"));
                    int HostMapCount(string source)=>Directory.GetDirectories(source)
                        .Count(path=>!Path.GetFileName(path).Equals("CONFIG",
                            StringComparison.OrdinalIgnoreCase));
                    if(HostMapCount(hp)!=5||HostMapCount(ryzen)!=5||
                       hpConfig!=ryzenConfig ||
                       !File.Exists(Path.Combine(transfer,".kova-store-v1","operation.json")))
                        return Fail(id,"UM-CLUSTER-ARCHIVE-COPY-INCOMPLETE");
                    var token=Guid.NewGuid();
                    pending=new(token,hp,ryzen,transfer,parent,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token,hpCopies=5,ryzenCopies=5,
                        configurationCopiesPresent=hpConfig&&ryzenConfig,
                        transferMetadataPresent=true,
                        hpFolderName=Name(hp),ryzenFolderName=Name(ryzen),
                        transferFolderName=Name(transfer),destinationFolderName=Name(parent),
                        requiresExplicitApproval=true,
                        originalStoreTouched=false,sqlUsed=false,
                        liveSnapshotConsistencyCertified=false,
                        evidence="NATIVE_OFFLINE_FOLDER_SELECTION_NO_WRITES"
                    });
                }
                if(!request.TryGetProperty("token",out var approval) ||
                   approval.ValueKind!=JsonValueKind.String ||
                   !Guid.TryParse(approval.GetString(),out var ticket)||
                   !request.TryGetProperty("confirm",out var confirmed)||
                   confirmed.ValueKind!=JsonValueKind.True ||
                   pending==null||pending.Token!=ticket)
                    return Fail(id,"UM-CLUSTER-ARCHIVE-APPROVAL-REQUIRED");
                var approved=pending;pending=null;
                if(DateTimeOffset.UtcNow-approved.PreparedUtc>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-CLUSTER-ARCHIVE-APPROVAL-EXPIRED");
                var job=new Job();current=job;
                _=Task.Run(()=>
                {
                    try
                    {
                        Stage(job,"running","VERIFYING_OFFLINE_INPUTS");
                        string archive=Path.Combine(approved.Parent,
                            "Untamed-ASA-Offline-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+
                            "-"+job.Id.ToString("N"));
                        var report=OfflineAsaClusterArchive.BuildFromFolders(
                            approved.Hp,approved.Ryzen,approved.Transfer,archive);
                        Stage(job,"completed","SHA256_VERIFIED",Name(archive),
                            report.Maps.Length,report.Transfer.Length,report.Config?.Length??0);
                    }
                    catch(Exception)
                    {
                        // Failed/partial copy keeps .incomplete for operator
                        // review. Never relabel failure as success or retry.
                        Stage(job,"needsOperator","ARCHIVE_FAILED_CHECK_OFFLINE_SOURCE_OR_INCOMPLETE_STAGE");
                    }
                });
                return Ok(id,new{jobId=job.Id,started=true,sqlUsed=false,
                    productionRestoreAllowed=false,originalStoreTouched=false,
                    evidence="OFFLINE_ASA_ONLY_ARCHIVE_JOB_STARTED"});
            }
            finally{gate.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-CLUSTER-ARCHIVE-UNAVAILABLE");
        }
    }

    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>gate.Dispose();
}
