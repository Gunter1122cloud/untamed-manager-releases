using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class AsaApiReleaseStagingTests
{
    private sealed class FixtureHandler(byte[] content):HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req,CancellationToken ct)
        {
            Requests++;
            if(req.RequestUri?.AbsoluteUri!=
               "https://github.com/ArkServerApi/AsaApi/releases/download/2.03/AsaApi-2.03.zip")
                throw new IOException("Unexpected official release endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new ByteArrayContent(content)
            });
        }
    }
    private sealed class OfficialHandler(byte[] content):HttpMessageHandler
    {
        public int MetaQueries,Downloads;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            string url=request.RequestUri?.AbsoluteUri??"";
            if(url==AsaApiReleaseCatalog.ApiUri)
            {
                MetaQueries++;
                string sha=Convert.ToHexString(SHA256.HashData(content));
                string json=System.Text.Json.JsonSerializer.Serialize(new{
                    tag_name="2.03",
                    html_url="https://github.com/ArkServerApi/AsaApi/releases/tag/2.03",
                    draft=false,prerelease=false,
                    published_at="2026-11-01T10:00:00Z",
                    assets=new[]{new{
                        name="AsaApi-2.03.zip",size=content.Length,
                        browser_download_url=
                        "https://github.com/ArkServerApi/AsaApi/releases/download/2.03/AsaApi-2.03.zip",
                        digest="sha256:"+sha
                    }}
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                    Content=new StringContent(json)
                });
            }
            if(url=="https://github.com/ArkServerApi/AsaApi/releases/download/2.03/AsaApi-2.03.zip")
            {
                Downloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                    Content=new ByteArrayContent(content)
                });
            }
            throw new IOException("Unexpected URI in official ASA API test route");
        }
    }

    private static byte[] Package(bool traversal=false)
    {
        using var ms=new MemoryStream();
        using(var zip=new ZipArchive(ms,ZipArchiveMode.Create,true))
        {
            var item=zip.CreateEntry(traversal?"../malicious.dll":"ArkApi/AsaApi.dll");
            using var output=item.Open();
            byte[] fixture=System.Text.Encoding.UTF8.GetBytes(
                "synthetic release binary bytes; not an actual installed API");
            output.Write(fixture);
        }
        return ms.ToArray();
    }
    private static (AsaApiReleaseInfo Release,AsaApiReleaseAsset Asset)
        Approved(byte[] zip,string? hash=null,string? url=null)
    {
        string sha=hash??Convert.ToHexString(SHA256.HashData(zip));
        var asset=new AsaApiReleaseAsset("AsaApi-2.03.zip",zip.Length,
            url??"https://github.com/ArkServerApi/AsaApi/releases/download/2.03/AsaApi-2.03.zip",
            sha);
        return(new("2.03",DateTimeOffset.UtcNow,
            "https://github.com/ArkServerApi/AsaApi/releases/tag/2.03",
            new[]{asset},false,"isolated archive metadata"),asset);
    }
    private static string Target()=>Path.Combine(Path.GetTempPath(),
        "Untamed-ApiLab-"+Guid.NewGuid().ToString("N"));
    private static void Check(bool good,string label)
    {
        if(!good)throw new Exception("ASA API isolated package failed: "+label);
        Console.WriteLine("PASS asa-api-staging "+label);
    }
    private static async Task<bool> Refused(Func<Task> work)
    {
        try{await work();return false;}
        catch(Exception ex) when(ex is IOException or InvalidDataException or
            UnauthorizedAccessException or OperationCanceledException)
        {return true;}
    }
    public static async Task Run()
    {
        var binary=Package();
        var (release,asset)=Approved(binary);
        var handler=new FixtureHandler(binary);
        using var http=new HttpClient(handler);
        string output=Target();
        var staged=await AsaApiReleaseStaging.StageAsync(
            http,release,asset,output);
        Check(staged.Tag=="2.03"&&staged.Bytes==binary.Length&&
            staged.OfficialSha256==Convert.ToHexString(SHA256.HashData(binary))&&
            !staged.LiveInstallationAllowed&&handler.Requests==1,
            "real HTTP response streamed to separate Windows TEMP archive with verified publisher SHA-256");
        Check(File.ReadAllBytes(Path.Combine(output,"release.zip")).SequenceEqual(binary)&&
            AsaApiReleaseStaging.Verify(output).Bytes==binary.Length,
            "sealed artifact has byte-for-byte source integrity and independent revalidation");
        Check(!Directory.Exists(Path.Combine(output,"ArkApi"))&&
            !Directory.Exists(Path.Combine(output,"ShooterGame")),
            "verified ZIP was never extracted into any ASA API/plugin directory");
        Check(AsaApiReleaseStaging.VerifyAgainstPublisher(output,release,asset).Bytes==
            binary.Length,"archive remains tied to separately supplied official release metadata");
        var originalAsset=asset with {OfficialSha256=new string('0',64)};
        var alteredRelease=release with {Assets=new[]{originalAsset}};
        Check(await Refused(()=>Task.Run(()=>
            AsaApiReleaseStaging.VerifyAgainstPublisher(output,alteredRelease,originalAsset))),
            "forged publisher digest cannot re-certify an existing archive");
        var officialHandler=new OfficialHandler(binary);
        using(var officialHttp=new HttpClient(officialHandler))
        {
            var latest=await AsaApiReleaseStaging.StageLatestAsync(
                officialHttp,"AsaApi-2.03.zip",Target());
            Check(latest.OfficialSha256==asset.OfficialSha256 &&
                officialHandler.MetaQueries==1&&officialHandler.Downloads==1,
                "actual staging path obtains fresh official GitHub release SHA before downloading bytes");
        }
        Check(await Refused(()=>AsaApiReleaseStaging.StageAsync(
            http,release,asset,output)),
            "existing isolated output is never silently replaced");

        File.AppendAllText(Path.Combine(output,"release.zip"),"CHANGED");
        Check(await Refused(()=>Task.Run(()=>AsaApiReleaseStaging.Verify(output))),
            "one-byte change invalidates previously sealed API release");

        var (badRelease,badAsset)=Approved(binary,new string('0',64));
        string incorrect=Target();
        Check(await Refused(()=>AsaApiReleaseStaging.StageAsync(
            http,badRelease,badAsset,incorrect)) &&
            !Directory.Exists(incorrect) && Directory.Exists(incorrect+".incomplete"),
            "mismatched official SHA leaves forensic stage and never publishes success");

        var unsafeZip=Package(true);
        var (unsafeRelease,unsafeAsset)=Approved(unsafeZip);
        using var unsafeHttp=new HttpClient(new FixtureHandler(unsafeZip));
        string traversal=Target();
        Check(await Refused(()=>AsaApiReleaseStaging.StageAsync(
            unsafeHttp,unsafeRelease,unsafeAsset,traversal)) &&
            !Directory.Exists(traversal),
            "traversal member blocked before any API ZIP publication");

        var (foreignRelease,foreignAsset)=Approved(binary,
            url:"https://example.org/AsaApi-2.03.zip");
        string foreign=Target();
        Check(await Refused(()=>AsaApiReleaseStaging.StageAsync(
            http,foreignRelease,foreignAsset,foreign)) &&
            !Directory.Exists(foreign)&&!Directory.Exists(foreign+".incomplete"),
            "untrusted host rejected before HTTP request or destination write");

        var (unhashedRelease,unhashedAsset)=Approved(binary);
        unhashedAsset=unhashedAsset with {OfficialSha256=null};
        unhashedRelease=unhashedRelease with {Assets=new[]{unhashedAsset}};
        Check(await Refused(()=>AsaApiReleaseStaging.StageAsync(
            http,unhashedRelease,unhashedAsset,Target())),
            "upstream assets lacking publisher SHA-256 cannot be accepted");

        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        Check(await Refused(()=>AsaApiReleaseStaging.StageAsync(
            http,release,asset,Target(),cancelled.Token)),
            "cancelled release fetch does not create or modify a destination");

        string invalidDestination=Path.Combine(
            Path.GetTempPath(),"not-untamed-api-lab");
        Check(await Refused(()=>AsaApiReleaseStaging.StageAsync(
            http,release,asset,invalidDestination)),
            "download cannot target arbitrary directories or live installations");
    }
}
