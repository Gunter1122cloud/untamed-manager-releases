using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Operator-driven COPY-ONLY restore test of an already verified ASA-only
/// cluster archive. The target is always an auto-generated TEMP lab folder,
/// never an ASA installation or Legacy/Store directory.
/// </summary>
public sealed class DesktopOfflineAsaRestoreBridge : IDisposable
{
    private sealed record Approval(Guid Token,string Archive,DateTimeOffset At);
    private sealed class Job
    {
        public Guid Id=Guid.NewGuid();
        public string State="queued",Stage="preflight",Name="";
        public int Maps,Files;
        public DateTimeOffset ChangedUtc=DateTimeOffset.UtcNow;
    }
    private readonly Func<string?> selectFolder;
    private readonly SemaphoreSlim serial=new(1,1);
    private Approval? pending;
    private Job? current;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopOfflineAsaRestoreBridge(Func<string?>? picker=null)=>
        selectFolder=picker??Pick;

    private static string? Pick()
    {
        using var dialog=new FolderBrowserDialog {
            Description="Válaszd ki a már elkészített, SQL-mentes Untamed ASA offline archívum mappáját",
            UseDescriptionForTitle=true,
            ShowNewFolderButton=false
        };
        return dialog.ShowDialog()==DialogResult.OK?dialog.SelectedPath:null;
    }
    private static string Name(string folder)
    {
        string name=Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return name.Length>100?name[..100]:name;
    }
    private static object Snapshot(Job j)
    {
        lock(j)return new{
            jobId=j.Id,state=j.State,stage=j.Stage,labName=j.Name,
            maps=j.Maps,files=j.Files,changedUtc=j.ChangedUtc,
            gameplayRestoredVerified=false,storeWriterVerified=false,
            productionRestoreAllowed=false,sqlUsed=false,
            evidence="ISOLATED_TEMP_RESTORE_FILE_INTEGRITY_ONLY"
        };
    }
    private static void Set(Job j,string state,string stage,
        string labName="",int maps=0,int files=0)
    {
        lock(j){
            j.State=state;j.Stage=stage;j.Name=labName;
            j.Maps=maps;j.Files=files;j.ChangedUtc=DateTimeOffset.UtcNow;
        }
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >900)return Fail(null,"UM-ASARESTORE-REQUEST");
            using var parsed=JsonDocument.Parse(raw);
            var data=parsed.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !data.TryGetProperty("id",out var requestId) ||
               requestId.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(requestId.GetString(),out var parsedId) ||
               parsedId==Guid.Empty ||
               !data.TryGetProperty("method",out var route) ||
               route.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-ASARESTORE-REQUEST");
            id=parsedId.ToString("D");
            string? method=route.GetString();
            if(method is not ("cluster.restoreLabPreview" or "cluster.restoreLabApply"
                or "cluster.restoreLabStatus"))
                return Fail(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="cluster.restoreLabStatus")
                {
                    if(!data.TryGetProperty("jobId",out var job)||
                       job.ValueKind!=JsonValueKind.String||
                       !Guid.TryParse(job.GetString(),out var jobId)||
                       current==null||current.Id!=jobId)
                        return Fail(id,"UM-ASARESTORE-JOB-UNKNOWN");
                    return Ok(id,Snapshot(current));
                }
                if(current!=null)
                    lock(current)
                        if(current.State is "queued" or "running")
                            return Fail(id,"UM-ASARESTORE-JOB-BUSY");
                if(method=="cluster.restoreLabPreview")
                {
                    pending=null;
                    string? source=selectFolder();
                    if(string.IsNullOrWhiteSpace(source))
                        return Fail(id,"UM-ASARESTORE-PICK-CANCELLED");
                    if(!Directory.Exists(source)||
                       !File.Exists(Path.Combine(source,"asa-cluster-index.json")) ||
                       !File.Exists(Path.Combine(source,"asa-cluster-index.sha256")) ||
                       !File.Exists(Path.Combine(source,"complete")))
                        return Fail(id,"UM-ASARESTORE-NOT-AN-ASA-ARCHIVE");
                    pending=new(Guid.NewGuid(),source,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token=pending.Token,archiveName=Name(source),
                        verified=false,requiresExplicitApproval=true,
                        target="FRESH_ISOLATED_TEMP_FOLDER_ONLY",
                        sqlUsed=false,productionRestoreAllowed=false,
                        evidence="NATIVE_ARCHIVE_PICKER_NO_DISK_CHANGES"
                    });
                }
                if(!data.TryGetProperty("token",out var token) ||
                   token.ValueKind!=JsonValueKind.String ||
                   !Guid.TryParse(token.GetString(),out var received) ||
                   !data.TryGetProperty("confirm",out var confirmed) ||
                   confirmed.ValueKind!=JsonValueKind.True ||
                   pending==null||pending.Token!=received)
                    return Fail(id,"UM-ASARESTORE-APPROVAL-REQUIRED");
                var approved=pending;pending=null;
                if(DateTimeOffset.UtcNow-approved.At>TimeSpan.FromMinutes(5))
                    return Fail(id,"UM-ASARESTORE-APPROVAL-EXPIRED");
                var work=new Job();current=work;
                _=Task.Run(()=>
                {
                    try
                    {
                        Set(work,"running","SOURCE_SHA256_VERIFICATION");
                        string lab=Path.Combine(Path.GetTempPath(),
                            "Untamed-AsaRestore-Lab-"+work.Id.ToString("N"));
                        var output=OfflineAsaRestoreLab.MaterializeToLab(approved.Archive,lab);
                        Set(work,"completed","LAB_FILE_HASHES_VERIFIED",Name(lab),
                            output.SourceArchive.Maps.Length,output.MapFiles.Length);
                    }
                    catch(Exception)
                    {
                        Set(work,"needsOperator",
                            "RESTORE_INCOMPLETE_OR_INVALID_ARCHIVE_CHECK_TEMP_STAGE");
                    }
                });
                return Ok(id,new{jobId=work.Id,started=true,sqlUsed=false,
                    productionRestoreAllowed=false,
                    evidence="ISOLATED_RESTORE_LAB_JOB_STARTED"});
            }
            finally{serial.Release();}
        }
        catch(Exception)
        {
            return Fail(id,"UM-ASARESTORE-SAFETY-CHECK-FAILED");
        }
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>serial.Dispose();
}
