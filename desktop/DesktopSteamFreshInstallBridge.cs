using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit new-install-only ASA SteamCMD workflow. The existing verified
/// Core owns network download/process/journal, never this WebView2 adapter.
/// No install is started by preflight; finished binary is registered only
/// after a second explicit click. No ASA launch, saves or Store mutation.
/// </summary>
public sealed class DesktopSteamFreshInstallBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim serial = new(1,1);
    private readonly object stateGate = new();
    private readonly Dictionary<Guid,Prepared> prepared = new();
    private InstallJob? currentJob;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Prepared(
        Guid Token,Guid MapId,string Name,string Root,string SteamExe,
        bool DownloadSteamCmd,bool ExplicitRetry,DateTimeOffset CreatedUtc);
    private sealed class InstallJob
    {
        public required Guid Id {get;init;}
        public required Prepared Request {get;init;}
        public readonly CancellationTokenSource Cancellation = new();
        public string Stage = "prepared";
        public string Detail = "Jóváhagyott ASA-telepítés előkészítve";
        public string? ErrorCode;
        public double? Percent;
        public bool Completed;
        public bool Registered;
        public SteamFreshInstallReceipt? Receipt;
    }

    private sealed class SynchronousProgress : IProgress<SteamInstallProgress>
    {
        private readonly Action<SteamInstallProgress> onReport;
        public SynchronousProgress(Action<SteamInstallProgress> action)=>onReport=action;
        public void Report(SteamInstallProgress update)=>onReport(update);
    }

    public DesktopSteamFreshInstallBridge(ClusterService source) =>
        cluster=source ?? throw new ArgumentNullException(nameof(source));

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Error(null,"UM-INVALID-MESSAGE");
            using var document=JsonDocument.Parse(raw);
            var data=document.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
                !GetGuid(data,"id",out var requestId) ||
                !data.TryGetProperty("method",out var methodValue) ||
                methodValue.ValueKind!=JsonValueKind.String)
                return Error(null,"UM-INVALID-MESSAGE");
            id=requestId.ToString("D");
            string? method=methodValue.GetString();
            if(method is not ("steam.freshPreflight" or "steam.freshStart" or
                "steam.freshStatus" or "steam.freshCancel" or "steam.freshRegister"))
                return Error(id,"UM-METHOD-DENIED");

            await serial.WaitAsync();
            try
            {
                if(method=="steam.freshPreflight")
                {
                    if(!TextField(data,"mapName",100,out var mapName) ||
                       !TextField(data,"installRoot",520,out var installRoot) ||
                       !TextField(data,"steamExe",520,out var steamExe) ||
                       !BoolField(data,"allowSteamCmdDownload",out var download))
                        return Error(id,"UM-FRESH-INVALID-PATH");
                    bool retry=false;
                    if(data.TryGetProperty("explicitRetry",out var retryValue))
                    {
                        if(retryValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return Error(id,"UM-FRESH-INVALID-RETRY");
                        retry=retryValue.GetBoolean();
                    }
                    mapName=LocalMapNameRules.Normalize(mapName);
                    string level=AsaSteamMapCatalog.LevelFor(mapName);
                    installRoot=Canonical(installRoot);
                    steamExe=Canonical(steamExe);
                    if(!Path.GetFileName(steamExe).Equals("steamcmd.exe",StringComparison.OrdinalIgnoreCase))
                        return Error(id,"UM-FRESH-INVALID-STEAMCMD");
                    if(Overlap(installRoot,Path.GetDirectoryName(steamExe)!))
                        return Error(id,"UM-FRESH-ROOT-OVERLAP");
                    if(cluster.Config.LocalMaps.Any(m=>
                        !string.IsNullOrWhiteSpace(m.InstallationPath) &&
                        Overlap(installRoot,Canonical(m.InstallationPath))))
                        return Error(id,"UM-FRESH-ROOT-OVERLAP");
                    if(cluster.Config.LocalMaps.Any(m=>m.Name.Equals(mapName,
                        StringComparison.OrdinalIgnoreCase)))
                        return Error(id,"UM-FRESH-DUPLICATE-MAP");

                    // Existing installed servers must be IMPORTED instead.
                    var interrupted=SteamFreshInstall.InspectIncomplete(installRoot);
                    Guid mapId;
                    if(interrupted!=null)
                    {
                        if(!retry ||
                           !string.Equals(interrupted.MapName,mapName,StringComparison.OrdinalIgnoreCase) ||
                           !Same(interrupted.SteamCmdExecutable,steamExe))
                            return Error(id,"UM-FRESH-RETRY-REVIEW-REQUIRED");
                        mapId=interrupted.MapId;
                    }
                    else
                    {
                        if(retry)return Error(id,"UM-FRESH-NO-RETRY-JOURNAL");
                        if(Directory.Exists(installRoot) &&
                           Directory.EnumerateFileSystemEntries(installRoot).Any())
                            return Error(id,"UM-FRESH-ROOT-NOT-EMPTY");
                        mapId=Guid.NewGuid();
                    }
                    if(!File.Exists(steamExe) && !download)
                        return Error(id,"UM-FRESH-STEAMCMD-MISSING");
                    bool validSteam = File.Exists(steamExe);
                    if(validSteam)SteamCmdPreflight.Inspect(steamExe);
                    // Only one live native Steam download at a time.
                    lock(stateGate)
                    {
                        if(currentJob!=null && !currentJob.Completed && !currentJob.Registered &&
                           currentJob.ErrorCode==null)
                            return Error(id,"UM-FRESH-INSTALL-IN-PROGRESS");
                    }
                    var token=Guid.NewGuid();
                    prepared.Clear();
                    prepared[token]=new(token,mapId,mapName,installRoot,steamExe,download,
                        retry,DateTimeOffset.UtcNow);
                    return Ok(id,new{
                        token,mapName,mapLevel=level,folderName=Path.GetFileName(installRoot),
                        steamCmdPresent=validSteam,
                        needsOfficialSteamCmdDownload=!validSteam&&download,
                        existingInterruptedInstall=interrupted!=null,
                        minimumFreeGiB=35,
                        evidence="FRESH_INSTALL_PREFLIGHT_NO_FILE_MUTATION"
                    });
                }

                if(method=="steam.freshStatus")
                {
                    if(!GetGuid(data,"jobId",out var jobId))
                        return Error(id,"UM-FRESH-INVALID-JOB");
                    lock(stateGate)
                    {
                        if(currentJob==null || currentJob.Id!=jobId)
                            return Error(id,"UM-FRESH-JOB-NOT-FOUND");
                        return Ok(id,Describe(currentJob));
                    }
                }

                if(method=="steam.freshStart")
                {
                    if(!GetGuid(data,"token",out var token) ||
                       !prepared.TryGetValue(token,out var request) ||
                       DateTimeOffset.UtcNow-request.CreatedUtc>TimeSpan.FromMinutes(10))
                        return Error(id,"UM-FRESH-PREFLIGHT-EXPIRED");
                    if(!BoolField(data,"confirm",out var confirmed) || !confirmed)
                        return Error(id,"UM-FRESH-EXPLICIT-CONFIRMATION-REQUIRED");
                    lock(stateGate)
                    {
                        if(currentJob!=null && !currentJob.Completed && currentJob.ErrorCode==null)
                            return Error(id,"UM-FRESH-INSTALL-IN-PROGRESS");
                    }
                    prepared.Remove(token);
                    var job=new InstallJob { Id=Guid.NewGuid(),Request=request };
                    lock(stateGate) currentJob=job;
                    // The worker has its own bounded cancellation and state.
                    // No SteamCMD process is started until the user's explicit
                    // confirmation; the existing Core rechecks root and locks.
                    _=Task.Run(()=>RunInstallAsync(job));
                    return Ok(id,new{jobId=job.Id,started=true,
                        evidence="EXPLICIT_FRESH_INSTALL_STARTED"});
                }

                if(!GetGuid(data,"jobId",out var currentId))
                    return Error(id,"UM-FRESH-INVALID-JOB");
                InstallJob? selected;
                lock(stateGate)selected=currentJob?.Id==currentId?currentJob:null;
                if(selected==null)return Error(id,"UM-FRESH-JOB-NOT-FOUND");

                if(method=="steam.freshCancel")
                {
                    lock(stateGate)
                    {
                        if(!selected.Completed && !selected.Registered &&
                           selected.ErrorCode==null)
                            selected.Cancellation.Cancel();
                    }
                    return Ok(id,new{cancelRequested=true,
                        evidence="STEAM_INSTALL_CANCEL_REQUEST_ONLY"});
                }
                if(method=="steam.freshRegister")
                {
                    if(!BoolField(data,"confirm",out var confirmed) || !confirmed)
                        return Error(id,"UM-FRESH-EXPLICIT-CONFIRMATION-REQUIRED");
                    SteamFreshInstallReceipt? receipt;
                    lock(stateGate)
                    {
                        if(!selected.Completed || selected.Registered ||
                           selected.ErrorCode!=null || selected.Receipt==null)
                            return Error(id,"UM-FRESH-NOT-VERIFIED");
                        receipt=selected.Receipt;
                    }
                    if(!File.Exists(receipt.ServerExecutable) ||
                       AsaSteamBuildCatalog.InstalledBuild(receipt.InstallationRoot)!=receipt.BuildId)
                        return Error(id,"UM-FRESH-RECEIPT-INVALID");
                    // Recheck root/map overlap and original binary location.
                    if(cluster.Config.LocalMaps.Any(m=>
                        m.Name.Equals(selected.Request.Name,StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(m.InstallationPath) &&
                        Overlap(Canonical(m.InstallationPath),receipt.InstallationRoot))))
                        return Error(id,"UM-FRESH-ROOT-OVERLAP");
                    var localMap=new LocalMap{
                        Id=receipt.MapId,Name=selected.Request.Name,
                        InstallationPath=receipt.InstallationRoot,
                        ExecutablePath=receipt.ServerExecutable,
                        GamePort=0,QueryPort=0,RconPort=0,
                        Arguments="",AutoStart=false,StartWithAsaApi=false
                    };
                    cluster.AddMap(localMap); // original atomic CAS Manager-only config.
                    lock(stateGate)
                    {
                        selected.Registered=true;
                        selected.Stage="registered";
                        selected.Detail="ASA telepítés regisztrálva; portok és indítás később állítható";
                    }
                    return Ok(id,new{
                        mapId=localMap.Id,mapName=localMap.Name,
                        needsConfiguration=true,serverStarted=false,
                        evidence="VERIFIED_STEAM_FILES_REGISTERED_MANAGER_PROFILE_ONLY"
                    });
                }
                return Error(id,"UM-METHOD-DENIED");
            }
            finally {serial.Release();}
        }
        catch(Exception)
        {
            // No untrusted SteamCMD output, paths, API credentials or
            // process command line in renderer responses.
            return Error(id,"UM-FRESH-OPERATION-FAILED");
        }
    }

    private async Task RunInstallAsync(InstallJob job)
    {
        try
        {
            var req=job.Request;
            var request=new SteamFreshInstallRequest(req.MapId,req.Name,"",
                req.Root,req.SteamExe,req.DownloadSteamCmd,
                ExplicitRetry:req.ExplicitRetry);
            var progress=new SynchronousProgress(item=>{
                lock(stateGate)
                {
                    job.Stage=item.Stage;
                    job.Detail=item.Detail.Length>200?item.Detail[..200]:item.Detail;
                    job.Percent=item.Percent is >=0 and <=100?item.Percent:null;
                }
            });
            var receipt=await SteamFreshInstall.InstallAsync(
                request,cluster.Config.LocalMaps.ToArray(),progress,job.Cancellation.Token);
            lock(stateGate)
            {
                job.Receipt=receipt;
                job.Completed=true;
                job.Stage="verified";
                job.Percent=100;
                job.Detail="ASA EXE és Steam appmanifest ellenőrizve. Szerver nincs elindítva.";
            }
        }
        catch(OperationCanceledException)
        {
            lock(stateGate)
            {
                job.ErrorCode="UM-FRESH-CANCELED";
                job.Stage="canceled";
                job.Detail="SteamCMD megszakítva; csak kézi ellenőrzés után ismételhető";
            }
        }
        catch(Exception)
        {
            lock(stateGate)
            {
                job.ErrorCode="UM-FRESH-INSTALL-FAILED";
                job.Stage="failed";
                job.Detail="SteamCMD nem fejezte be az ellenőrzött telepítést";
            }
        }
    }

    private static object Describe(InstallJob job) => new {
        jobId=job.Id,mapName=job.Request.Name,
        stage=job.Stage,detail=job.Detail,percent=job.Percent,
        verified=job.Completed && job.Receipt!=null &&
            job.ErrorCode==null,
        registered=job.Registered,errorCode=job.ErrorCode,
        evidence="NATIVE_STEAMCMD_JOB_STATE"
    };

    private static string Canonical(string input)
    {
        if(string.IsNullOrWhiteSpace(input) ||
           !Path.IsPathFullyQualified(input) ||
           input.StartsWith(@"\\",StringComparison.Ordinal) ||
           input.StartsWith("//",StringComparison.Ordinal) ||
           input.Contains('~'))
            throw new InvalidDataException("Explicit local Windows fixed-disk path required");
        string full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Only a local fixed drive is supported");
        for(var directory=new DirectoryInfo(full);directory!=null;directory=directory.Parent)
            if(directory.Exists && (directory.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked/junction paths refused");
        return full;
    }
    private static bool Same(string first,string second)=>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
    private static bool Overlap(string a,string b)=>
        Same(a,b) ||
        a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
        b.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static bool GetGuid(JsonElement root,string name,out Guid result)
    {
        result=Guid.Empty;
        return root.TryGetProperty(name,out var value) &&
            value.ValueKind==JsonValueKind.String &&
            Guid.TryParse(value.GetString(),out result) && result!=Guid.Empty;
    }
    private static bool TextField(JsonElement root,string name,int max,out string result)
    {
        result="";
        if(!root.TryGetProperty(name,out var value) ||
           value.ValueKind!=JsonValueKind.String)
            return false;
        result=value.GetString()?.Trim()??"";
        return result.Length>0 && result.Length<=max &&
            !result.Contains('\0') && !result.Any(char.IsControl);
    }
    private static bool BoolField(JsonElement root,string name,out bool result)
    {
        result=false;
        if(!root.TryGetProperty(name,out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        result=value.GetBoolean();
        return true;
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()
    {
        lock(stateGate)currentJob?.Cancellation.Cancel();
        serial.Dispose();
    }
}
