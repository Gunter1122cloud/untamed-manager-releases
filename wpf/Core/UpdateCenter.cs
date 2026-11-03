using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public sealed record UpdateRelease(Version Version,string Tag,string ChangeLog,string AssetUrl,
    string Sha256,string AssetName);

public static class UpdateCenter
{
    private static readonly HttpClient Client = CreateClient();
    public static readonly Version RunningVersion =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0,0,0);
    private static HttpClient CreateClient()
    {
        var http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=true});
        http.Timeout=TimeSpan.FromMinutes(8);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UntamedManager","1.3"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
    public static bool ValidRepository(string repo) =>
        Regex.IsMatch(repo,@"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,99}/[a-zA-Z0-9_.-]{1,100}\z")
        && !repo.Contains("..",StringComparison.Ordinal);
    public static async Task<UpdateRelease?> CheckStableAsync(string repository,
        CancellationToken cancellationToken=default)
    {
        if(!ValidRepository(repository))throw new ArgumentException("Invalid GitHub repository",nameof(repository));
        string url="https://api.github.com/repos/"+repository+"/releases/latest";
        using var response=await Client.GetAsync(url,cancellationToken);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)
            return null; // Only prereleases exist or repository has no stable release.
        response.EnsureSuccessStatusCode();
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root=doc.RootElement;
        if(root.GetProperty("prerelease").GetBoolean()||root.GetProperty("draft").GetBoolean())
            throw new InvalidDataException("Release is not stable");
        string tag=root.GetProperty("tag_name").GetString()??"";
        string clean=tag.TrimStart('v','V');
        if(!Version.TryParse(clean,out var version))
            throw new InvalidDataException("Stable tag must be a semantic version, e.g. v1.3.0");
        string expected=$"UntamedManager-{version.Major}.{version.Minor}.{version.Build}-SETUP.exe";
        foreach(var item in root.GetProperty("assets").EnumerateArray())
        {
            string name=item.GetProperty("name").GetString()??"";
            if(!name.Equals(expected,StringComparison.OrdinalIgnoreCase))continue;
            string link=item.GetProperty("browser_download_url").GetString()??"";
            string expectedPrefix="https://github.com/"+repository+"/releases/download/";
            if(!link.StartsWith(expectedPrefix,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected release asset URL");
            string digest=item.TryGetProperty("digest",out var d)?d.GetString()??"":"";
            if(!Regex.IsMatch(digest,@"\Asha256:[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("Release asset has no trusted GitHub SHA-256 digest");
            return new UpdateRelease(version,tag,
                root.TryGetProperty("body",out var body)?body.GetString()??"":"",
                link,digest.Substring(7).ToLowerInvariant(),name);
        }
        throw new InvalidDataException("Stable release has no suitable signed/reproducible installer asset");
    }
    public static async Task<string> DownloadAndVerifyAsync(UpdateRelease release,
        string destinationDirectory,IProgress<(long received,long? total)>? progress=null,
        CancellationToken cancellationToken=default)
    {
        if(!Uri.TryCreate(release.AssetUrl,UriKind.Absolute,out var uri) || uri.Scheme!="https" ||
           uri.Host!="github.com")throw new InvalidDataException("Update must originate from GitHub HTTPS");
        Directory.CreateDirectory(destinationDirectory);
        string final=Path.Combine(destinationDirectory,release.AssetName);
        string temp=final+".download";
        try
        {
            using var response=await Client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,cancellationToken);
            response.EnsureSuccessStatusCode();
            long? size=response.Content.Headers.ContentLength;
            using var source=await response.Content.ReadAsStreamAsync(cancellationToken);
            await using(var output=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,65536,true))
            {
                byte[] buffer=new byte[65536];
                long received=0;
                int count;
                while((count=await source.ReadAsync(buffer,cancellationToken))>0)
                {
                    await output.WriteAsync(buffer.AsMemory(0,count),cancellationToken);
                    received+=count;progress?.Report((received,size));
                    if(received>700_000_000)throw new InvalidDataException("Installer exceeds size limit");
                }
            }
            await using(var stream=new FileStream(temp,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true))
            {
                string actual=Convert.ToHexString(await SHA256.HashDataAsync(stream,cancellationToken)).ToLowerInvariant();
                if(!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual),Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("GitHub SHA-256 digest mismatch. Update cancelled.");
            }
            File.Move(temp,final,true);return final;
        }
        finally {try{if(File.Exists(temp))File.Delete(temp);}catch{}}
    }
    public static void CheckAuthenticodePublisher(string path,string configuredThumbprint)
    {
        if(string.IsNullOrWhiteSpace(configuredThumbprint))return;
        // A configured publisher is pinned; an unsigned or different signer is rejected.
        using var certificate=new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        string actual=certificate.Thumbprint.Replace(" ","",StringComparison.Ordinal).ToUpperInvariant();
        string expected=configuredThumbprint.Replace(" ","",StringComparison.Ordinal).ToUpperInvariant();
        if(!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),Encoding.ASCII.GetBytes(expected)))
            throw new InvalidDataException("Installer publisher certificate does not match the pinned identity");
        using var chain=new X509Chain();
        chain.ChainPolicy.RevocationMode=X509RevocationMode.Online;
        if(!chain.Build(certificate))
            throw new InvalidDataException("Installer signing certificate chain validation failed");
    }
    public static string StageUpdater(string installedHelperPath)
    {
        if(!File.Exists(installedHelperPath))throw new FileNotFoundException("Updater helper not installed",installedHelperPath);
        string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UntamedManager","Updates","stage-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string copy=Path.Combine(dir,"UntamedUpdater.exe");
        File.Copy(installedHelperPath,copy);
        return copy;
    }
    public static string UpdateRoot=>Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UntamedManager","Updates");
}
