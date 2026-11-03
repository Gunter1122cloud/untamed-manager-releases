using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// Live READ-ONLY catalog of the official community ASA API upstream.
// Never downloads, unpacks, overwrites or activates API DLL/EXE files.
public sealed record AsaApiReleaseAsset(string Name,long Bytes,
    string Url,string? OfficialSha256);
public sealed record AsaApiReleaseInfo(string Tag,DateTimeOffset? PublishedUtc,
    string HtmlUrl,AsaApiReleaseAsset[] Assets,
    bool BinaryUpdateAuthorized,string Statement);
public static class AsaApiReleaseCatalog
{
    public const string ApiUri="https://api.github.com/repos/ArkServerApi/AsaApi/releases/latest";

    public static AsaApiReleaseInfo Parse(string json)
    {
        if(string.IsNullOrWhiteSpace(json)||json.Length>1_000_000)
            throw new InvalidDataException("Invalid ASA API release JSON length");
        using var document=JsonDocument.Parse(json,new JsonDocumentOptions{
            MaxDepth=24,AllowTrailingCommas=false});
        var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("Expected a GitHub release object");
        string tag=RequiredString(root,"tag_name");
        if(tag.Length>64||tag.Any(c=>!(char.IsLetterOrDigit(c)||c is '.' or '_' or '-')))
            throw new InvalidDataException("Unsafe API release tag");
        string html=RequiredString(root,"html_url");
        if(!Uri.TryCreate(html,UriKind.Absolute,out var u)||
            u.Scheme!="https"||u.Host!="github.com"||
            !u.AbsolutePath.StartsWith("/ArkServerApi/AsaApi/releases/tag/",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ASA API release is not the official project URL");
        if(root.TryGetProperty("draft",out var draft) &&draft.ValueKind==JsonValueKind.True)
            throw new InvalidDataException("Draft API release must not be treated as current");
        if(root.TryGetProperty("prerelease",out var pre)&&pre.ValueKind==JsonValueKind.True)
            throw new InvalidDataException("Prerelease API not selected as stable");
        DateTimeOffset? date=null;
        if(root.TryGetProperty("published_at",out var published) &&
           published.ValueKind==JsonValueKind.String &&
           DateTimeOffset.TryParse(published.GetString(),out var d))date=d;
        if(!root.TryGetProperty("assets",out var data)||
           data.ValueKind!=JsonValueKind.Array || data.GetArrayLength()>50)
            throw new InvalidDataException("Expected bounded ASA API release asset array");
        var assets=new List<AsaApiReleaseAsset>();
        foreach(var item in data.EnumerateArray())
        {
            string name=RequiredString(item,"name");
            if(name.Length>180 ||name.Contains("..")||
               name.IndexOfAny(new[]{'/','\\','\r','\n'})>=0)
                throw new InvalidDataException("Unsafe ASA API release asset name");
            string location=RequiredString(item,"browser_download_url");
            if(!Uri.TryCreate(location,UriKind.Absolute,out var assetUri)||
               assetUri.Scheme!="https"||assetUri.Host!="github.com"||
               !assetUri.AbsolutePath.StartsWith("/ArkServerApi/AsaApi/releases/download/",
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe/nonofficial API release asset URL");
            if(!item.TryGetProperty("size",out var sizeNode)||
               !sizeNode.TryGetInt64(out long size) || size is < 1 or > 1024L*1024*1024)
                throw new InvalidDataException("Unsafe API release asset size");
            string? sha=null;
            if(item.TryGetProperty("digest",out var digest) &&
               digest.ValueKind==JsonValueKind.String)
            {
                string value=digest.GetString()??"";
                if(value.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase))
                {
                    string candidate=value[7..];
                    if(candidate.Length!=64||candidate.Any(c=>!Uri.IsHexDigit(c)))
                        throw new InvalidDataException("Invalid official SHA-256 digest");
                    sha=candidate.ToUpperInvariant();
                }
            }
            assets.Add(new(name,size,location,sha));
        }
        return new(tag,date,html,assets.ToArray(),false,
            "Live upstream metadata only. No ASA/API compatibility, copyright entitlement, "+
            "download authorization or safe plugin hot reload has been proven.");
    }

    public static async Task<AsaApiReleaseInfo> FetchLatestAsync(
        HttpClient http,CancellationToken ct=default)
    {
        if(http==null)throw new ArgumentNullException(nameof(http));
        using var request=new HttpRequestMessage(HttpMethod.Get,ApiUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UntamedManager","1.6"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response=await http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>1_000_000)
            throw new InvalidDataException("Oversized ASA API release response");
        await using var body=await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer=new MemoryStream();
        byte[] bytes=new byte[32_768];int n;
        while((n=await body.ReadAsync(bytes,timeout.Token))>0)
        {
            if(buffer.Length+n>1_000_000)
                throw new InvalidDataException("Oversized streaming ASA API release response");
            buffer.Write(bytes,0,n);
        }
        return Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }
    private static string RequiredString(JsonElement item,string name)
    {
        if(item.ValueKind!=JsonValueKind.Object ||
           !item.TryGetProperty(name,out var node)||
           node.ValueKind!=JsonValueKind.String ||
           string.IsNullOrWhiteSpace(node.GetString()))
            throw new InvalidDataException("Missing ASA API release metadata: "+name);
        return node.GetString()!;
    }
}
