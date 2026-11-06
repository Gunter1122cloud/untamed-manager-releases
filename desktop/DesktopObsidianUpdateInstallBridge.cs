using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Obsidian-specific staged updater, NEVER the legacy WPF app id or installer.
/// The renderer cannot choose paths, SHA, repository or executable arguments.
/// Every installation requires a separate human preview/download and one-shot
/// final confirmation. Only the verified external WinForms updater can replace
/// the UI program; it has the original full-directory rollback transaction.
/// </summary>
public sealed class DesktopObsidianUpdateInstallBridge
{
    private sealed record Pending(Guid Token,UpdateRelease Release,
        string Package,string Helper,string Application,DateTimeOffset Expires);
    private Pending? prepared;
    private readonly SemaphoreSlim gate=new(1,1);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ClusterService? cluster;
    private readonly Func<UpdateChannel,CancellationToken,Task<UpdateRelease?>> fetch;
    private readonly Func<UpdateRelease,string,CancellationToken,Task<string>> download;
    private readonly Func<string,bool> validateExecutable;

    public DesktopObsidianUpdateInstallBridge(
        ClusterService? localCluster=null,
        Func<UpdateChannel,CancellationToken,Task<UpdateRelease?>>? provider=null,
        Func<UpdateRelease,string,CancellationToken,Task<string>>? downloader=null,
        Func<string,bool>? appTrust=null)
    {
        cluster=localCluster;
        fetch=provider??ObsidianReleaseCatalog.CheckAsync;
        download=downloader??((release,dir,ct)=>
            UpdateCenter.DownloadAndVerifyAsync(release,dir,null,ct));
        validateExecutable=appTrust??(path=>{
            string canonical=Path.GetFullPath(path);
            string expected=Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs","UntamedObsidianPreview","UntamedDesktop.exe"));
            return string.Equals(canonical,expected,StringComparison.OrdinalIgnoreCase);
        });
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)return Error(null,"UM-UPDATE-INVALID-MESSAGE");
            using var document=JsonDocument.Parse(raw);
            var data=document.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !data.TryGetProperty("id",out var ident)||
               ident.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(ident.GetString(),out var value)||value==Guid.Empty||
               !data.TryGetProperty("method",out var methodValue)||
               methodValue.ValueKind!=JsonValueKind.String)
                return Error(null,"UM-UPDATE-INVALID-MESSAGE");
            id=value.ToString("D");
            var method=methodValue.GetString();
            if(method is not ("updates.prepare" or "updates.install"))
                return Error(id,"UM-METHOD-DENIED");
            if(!data.TryGetProperty("confirm",out var confirm)||
               confirm.ValueKind!=JsonValueKind.True)
                return Error(id,"UM-UPDATE-EXPLICIT-CONFIRMATION-REQUIRED");
            await gate.WaitAsync();
            try
            {
                if(method=="updates.prepare")
                {
                    prepared=null;
                    if(!data.TryGetProperty("channel",out var c)||
                       c.ValueKind!=JsonValueKind.String)
                        return Error(id,"UM-UPDATE-INVALID-CHANNEL");
                    string? rawChannel=c.GetString();
                    UpdateChannel channel=rawChannel switch{
                        "stable"=>UpdateChannel.Stable,
                        "preview"=>UpdateChannel.Preview,
                        _=>throw new InvalidDataException("Unknown update channel")
                    };
                    string exe=Environment.ProcessPath??"";
                    if(!exe.EndsWith("UntamedDesktop.exe",StringComparison.OrdinalIgnoreCase)||
                       !validateExecutable(exe))
                        return Error(id,"UM-UPDATE-INSTALLATION-UNVERIFIED");
                    string helper=Path.Combine(Path.GetDirectoryName(exe)!,"UntamedUpdater.exe");
                    if(!File.Exists(helper))
                        return Error(id,"UM-UPDATE-HELPER-NOT-PACKAGED");
                    using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(9));
                    var release=await fetch(channel,deadline.Token);
                    if(release==null||!ObsidianReleaseCatalog.IsTrusted(release)||
                       !ObsidianReleaseCatalog.IsNewer(release,
                           UpdateCenter.RunningVersion,ReleaseCatalog.InstalledTag))
                        return Error(id,"UM-UPDATE-NO-VERIFIED-NEWER-OBSIDIAN");
                    string root=Path.Combine(UpdateCenter.UpdateRoot,"ObsidianStage");
                    string staged=await download(release,root,deadline.Token);
                    if(Path.GetFullPath(staged)!=Path.GetFullPath(Path.Combine(root,release.AssetName)))
                        throw new InvalidDataException("Updater staging path differs");
                    // Always re-hash the staged installer: injected test runners
                    // and a formerly valid on-disk file cannot bypass SHA-256.
                    using(var stream=new FileStream(staged,FileMode.Open,FileAccess.Read,FileShare.Read))
                    {
                        var digest=Convert.ToHexString(SHA256.HashData(stream));
                        if(!string.Equals(digest,release.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Installer hash mismatch");
                    }
                    // Optional publisher certificate pin is a native profile
                    // setting, never passed through JavaScript.
                    string pinned=cluster?.Config.PublisherCertificateThumbprint??"";
                    if(!string.IsNullOrWhiteSpace(pinned))
                        UpdateCenter.CheckAuthenticodePublisher(staged,pinned);
                    prepared=new(Guid.NewGuid(),release,staged,helper,exe,
                        DateTimeOffset.UtcNow.AddMinutes(7));
                    return Ok(id,new {
                        token=prepared.Token,version=release.Version.ToString(3),
                        tag=release.Tag,assetName=release.AssetName,
                        sha256=release.Sha256,
                        fileBytes=new FileInfo(staged).Length,
                        verified=true,requiresFinalApproval=true,
                        updaterPackaged=true,automaticInstallationStarted=false,
                        evidence="OBSIDIAN_INSTALLER_SHA256_STAGED_ONLY"
                    });
                }

                if(!data.TryGetProperty("token",out var tokenData)||
                   tokenData.ValueKind!=JsonValueKind.String||
                   !Guid.TryParse(tokenData.GetString(),out var token)||
                   prepared==null||prepared.Token!=token)
                    return Error(id,"UM-UPDATE-PREPARE-EXPIRED");
                Pending saved=prepared;
                // Consume the token before starting any external process;
                // ambiguous startup cannot trigger automatic replay.
                prepared=null;
                if(saved.Expires<DateTimeOffset.UtcNow||
                   saved.Application!=Environment.ProcessPath||
                   !validateExecutable(saved.Application)||
                   !File.Exists(saved.Helper)||
                   !ObsidianReleaseCatalog.IsTrusted(saved.Release))
                    return Error(id,"UM-UPDATE-PREPARE-EXPIRED");

                using(var stream=new FileStream(saved.Package,FileMode.Open,
                    FileAccess.Read,FileShare.Read))
                {
                    string verified=Convert.ToHexString(SHA256.HashData(stream));
                    if(!string.Equals(verified,saved.Release.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                        return Error(id,"UM-UPDATE-INSTALLER-CHANGED");
                }
                string stagedHelper=UpdateCenter.StageUpdater(saved.Helper);
                string backup=Path.Combine(UpdateCenter.UpdateRoot,
                    "rollback-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+
                    "-"+Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(backup);
                var start=new ProcessStartInfo(stagedHelper){
                    UseShellExecute=false,CreateNoWindow=false,
                    WorkingDirectory=Path.GetDirectoryName(stagedHelper)!
                };
                foreach(var argument in new[]{
                    "--pid",Environment.ProcessId.ToString(),
                    "--installer",saved.Package,
                    "--app",saved.Application,
                    "--expected",saved.Release.Version.ToString(3),
                    "--tag",saved.Release.Tag,
                    "--profile","default",
                    "--backup",backup
                })start.ArgumentList.Add(argument);
                using var process=Process.Start(start)??
                    throw new IOException("Verified external updater could not start");
                return Ok(id,new{
                    externalUpdaterStarted=true,needsManagerExit=true,
                    processId=process.Id,tag=saved.Release.Tag,
                    serverProcessesTouched=false,
                    evidence="EXTERNAL_ROLLBACK_UPDATER_HANDOFF"
                });
            }
            finally{gate.Release();}
        }
        catch(Exception e) when(e is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or ArgumentException or
            JsonException or System.Net.Http.HttpRequestException or
            TaskCanceledException or OperationCanceledException)
        {
            return Error(id,"UM-UPDATE-NATIVE-VERIFICATION-FAILED");
        }
    }
    private static string Ok(string id,object value)=>
        JsonSerializer.Serialize(new{id,ok=true,data=value},Json);
    private static string Error(string? id,string value)=>
        JsonSerializer.Serialize(new{id,ok=false,error=value},Json);
}
