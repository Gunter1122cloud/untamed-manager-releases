using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// GitHub Releases metadata only. The WebView never receives a download/install
/// capability, arbitrary repository URL or access to the installed application
/// directory. Windows updates must be applied by a verified external updater,
/// not by replacing files while server control is running.
/// </summary>
public sealed class DesktopUpdateStatusBridge
{
    public const string Repository="Gunter1122cloud/untamed-manager-releases";
    private readonly Func<UpdateChannel,CancellationToken,Task<UpdateRelease?>> fetch;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopUpdateStatusBridge(
        Func<UpdateChannel,CancellationToken,Task<UpdateRelease?>>? provider=null)
    {
        fetch=provider??ObsidianReleaseCatalog.CheckAsync;
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)return Fail(null,"UM-UPDATE-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !root.TryGetProperty("id",out var idRaw) ||
               idRaw.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(idRaw.GetString(),out var parsed) ||
               !root.TryGetProperty("method",out var methodRaw) ||
               methodRaw.ValueKind!=JsonValueKind.String)
                return Fail(null,"UM-UPDATE-INVALID-MESSAGE");
            id=parsed.ToString("D");
            string? method=methodRaw.GetString();
            if(method is not ("updates.status" or "updates.check"))
                return Fail(id,"UM-METHOD-DENIED");
            string installed=ObsidianReleaseCatalog.InstalledTag;
            string running=UpdateCenter.RunningVersion.ToString();
            if(method=="updates.status")
                return Ok(id,new{
                    repository=Repository,runningVersion=running,installedTag=installed,
                    provider="GITHUB_RELEASES",updateCheckPerformed=false,
                    installationPermitted=false,updaterVerified=false,
                    evidence="LOCAL_RELEASE_IDENTITY_NO_NETWORK"
                });

            string channelRaw="stable";
            if(root.TryGetProperty("channel",out var channelField))
            {
                if(channelField.ValueKind!=JsonValueKind.String)
                    return Fail(id,"UM-UPDATE-INVALID-CHANNEL");
                channelRaw=channelField.GetString()??"";
            }
            UpdateChannel channel=channelRaw switch{
                "stable"=>UpdateChannel.Stable,
                "preview"=>UpdateChannel.Preview,
                _=>throw new ArgumentException("Unsupported release channel")
            };
            // Read-only network operation must not stall the desktop or
            // run in the background indefinitely.
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(18));
            var release=await fetch(channel,timeout.Token);
            if(release==null)
                return Ok(id,new{
                    repository=Repository,runningVersion=running,installedTag=installed,
                    channel=channelRaw,found=false,newer=false,
                    tag=(string?)null,version=(string?)null,assetName=(string?)null,
                    sha256=(string?)null,releaseNotes=(string?)null,
                    installationPermitted=false,updaterVerified=false,
                    evidence="GITHUB_RELEASE_METADATA_NOT_AN_INSTALLER"
                });
            if(!ObsidianReleaseCatalog.IsTrusted(release))
                return Fail(id,"UM-UPDATE-UNTRUSTED-RELEASE");
            bool newer=ObsidianReleaseCatalog.IsNewer(release,UpdateCenter.RunningVersion,installed);
            string notes=release.ChangeLog.Length>2400?release.ChangeLog[..2400]:release.ChangeLog;
            return Ok(id,new{
                repository=Repository,runningVersion=running,installedTag=installed,
                channel=channelRaw,found=true,newer,
                tag=release.Tag,version=release.Version.ToString(3),
                assetName=release.AssetName,sha256=release.Sha256,
                releaseNotes=notes,installationPermitted=false,
                updaterVerified=false,
                evidence="GITHUB_RELEASE_METADATA_NOT_AN_INSTALLER"
            });
        }
        catch(Exception ex) when(ex is JsonException or ArgumentException or
            System.Net.Http.HttpRequestException or TaskCanceledException or
            OperationCanceledException or TimeoutException or
            InvalidOperationException)
        {
            return Fail(id,"UM-UPDATE-CHECK-FAILED");
        }
    }

    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>
        JsonSerializer.Serialize(new{id,ok=false,error},Json);
}
