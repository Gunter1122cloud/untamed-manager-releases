using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// Read-only CurseForge Core API client. Requires a user-provided, approved x-api-key.
// Mod downloads, subscriptions and installation are intentionally not implemented.
public sealed record CurseForgeMod(int Id,string Name,string Summary,string? Website,
    string? LatestFile,DateTimeOffset? ModifiedUtc)
{
    // Provider supplied, HTTPS-only, allowlisted artwork. Null never implies a missing mod.
    public string? ThumbnailUrl { get; init; }
}
public sealed record CurseForgeSearchResult(int GameId,IReadOnlyList<CurseForgeMod> Mods,
    int TotalCount,int Index,bool HasMore);
public enum CurseForgeRelationType
{
    None=0,EmbeddedLibrary=1,OptionalDependency=2,RequiredDependency=3,
    Tool=4,Incompatible=5,Include=6
}
public sealed record CurseForgeRelation(int ModId,CurseForgeRelationType Type);
public sealed record CurseForgeFile(int FileId,int ModId,string Name,DateTimeOffset? Published,
    bool Available,IReadOnlyList<int> Dependencies)
{
    // Exact CurseForge relation categories are preserved independently.
    // 'Dependencies' retains only required/embedded entries, never optional,
    // incompatible, included content or a standalone tool.
    public IReadOnlyList<CurseForgeRelation> Relations {get;init;}=
        Array.Empty<CurseForgeRelation>();
}
public sealed record CurseForgeFilesResult(int ModId,IReadOnlyList<CurseForgeFile> Files,
    int Index,int Total,bool HasMore);

public sealed class CurseForgeCatalog
{
    private const int PageSize=25;
    private const int MaxJsonBytes=2_000_000;
    private readonly HttpClient http;
    private int? cachedAsaGame;
    private string? cachedKeyDigest;
    public CurseForgeCatalog(HttpClient httpClient) =>
        http=httpClient??throw new ArgumentNullException(nameof(httpClient));

    public async Task<CurseForgeSearchResult> SearchAsaAsync(string key,string term,int index=0,
        CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(key) || key.Length>512 || key.Any(char.IsControl))
            throw new ArgumentException("Approved CurseForge API key required",nameof(key));
        term=term?.Trim()??"";
        if(term.Length<2 || term.Length>80)
            throw new ArgumentException("Search text must contain 2-80 characters",nameof(term));
        if(index<0 || index+PageSize>10000 || index%PageSize!=0)
            throw new ArgumentOutOfRangeException(nameof(index));
        int game=await FindAsaGameAsync(key,ct);
        string path="/v1/mods/search?gameId="+game+"&searchFilter="+
            Uri.EscapeDataString(term)+"&index="+index+"&pageSize="+PageSize;
        using var document=await Request(key,path,ct);
        var root=document.RootElement;
        var data=Array(root,"data");
        var mods=new List<CurseForgeMod>();
        foreach(var item in data.EnumerateArray())
        {
            var mod=ParseAsaMod(item,game);
            if(mod!=null)mods.Add(mod);
        }
        int total=0;
        if(root.TryGetProperty("pagination",out var pagination) &&
           pagination.ValueKind==JsonValueKind.Object)
            total=Int(pagination,"totalCount");
        return new(game,mods,total,index,index+PageSize<Math.Min(total,10000));
    }

    /// <summary>
    /// Fetch one configured project by its exact ID. This is metadata-only,
    /// never a download, subscription, update or evidence of a loaded mod.
    /// The provider's gameId is checked against the authenticated ASA catalog.
    /// </summary>
    public async Task<CurseForgeMod> GetAsaModAsync(string key,int modId,
        CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(string.IsNullOrWhiteSpace(key) || key.Length>512 || key.Any(char.IsControl))
            throw new ArgumentException("Approved CurseForge API key required",nameof(key));
        if(modId<10000)throw new ArgumentOutOfRangeException(nameof(modId));
        int game=await FindAsaGameAsync(key,ct);
        using var document=await Request(key,"/v1/mods/"+modId,ct);
        if(!document.RootElement.TryGetProperty("data",out var entry) ||
           entry.ValueKind!=JsonValueKind.Object ||
           Int(entry,"id")!=modId)
            throw new InvalidDataException("CurseForge mod lookup returned an invalid project ID");
        return ParseAsaMod(entry,game) ??
            throw new InvalidDataException("The requested project is not an ARK Survival Ascended mod");
    }

    private static CurseForgeMod? ParseAsaMod(JsonElement item,int game)
    {
        int id=Int(item,"id");
        string name=Str(item,"name")??"";
        if(id<=0 || name.Length==0 || Int(item,"gameId")!=game)return null;
        string? url=null;
        if(item.TryGetProperty("links",out var links) && links.ValueKind==JsonValueKind.Object)
        {
            var offered=Str(links,"websiteUrl");
            if(Uri.TryCreate(offered,UriKind.Absolute,out var parsed) &&
               parsed.Scheme==Uri.UriSchemeHttps &&
               (parsed.Host.Equals("curseforge.com",StringComparison.OrdinalIgnoreCase) ||
                parsed.Host.EndsWith(".curseforge.com",StringComparison.OrdinalIgnoreCase)))
                url=parsed.ToString();
        }
        string? file=null;
        if(item.TryGetProperty("latestFiles",out var latest) &&
           latest.ValueKind==JsonValueKind.Array && latest.GetArrayLength()>0)
            file=Str(latest[0],"displayName")??Str(latest[0],"fileName");
        DateTimeOffset? date=null;
        if(DateTimeOffset.TryParse(Str(item,"dateModified"),out var parsedDate))
            date=parsedDate;
        string? thumbnail=null;
        if(item.TryGetProperty("logo",out var logo) && logo.ValueKind==JsonValueKind.Object)
        {
            string? offered=Str(logo,"thumbnailUrl")??Str(logo,"url");
            if(Uri.TryCreate(offered,UriKind.Absolute,out var picture) &&
               picture.Scheme==Uri.UriSchemeHttps &&
               (picture.Host.Equals("media.forgecdn.net",StringComparison.OrdinalIgnoreCase) ||
                picture.Host.EndsWith(".forgecdn.net",StringComparison.OrdinalIgnoreCase)))
                thumbnail=picture.ToString();
        }
        return new CurseForgeMod(id,name,Str(item,"summary")??"",url,file,date)
            {ThumbnailUrl=thumbnail};
    }

    // Official Core API file metadata only. Not a download entitlement and not
    // proof that a mod is Crossplay, free or enabled on the local ASA runtime.
    public async Task<CurseForgeFilesResult> GetModFilesAsync(string key,int modId,
        int index=0,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(key) || key.Length>512 || key.Any(char.IsControl))
            throw new ArgumentException("Approved CurseForge API key required",nameof(key));
        if(modId<10000 || index<0 || index>9975 || index%25!=0)
            throw new ArgumentOutOfRangeException(nameof(modId));
        using var document=await Request(key,
            "/v1/mods/"+modId+"/files?index="+index+"&pageSize=25",ct);
        var root=document.RootElement;
        var data=Array(root,"data");
        var files=new List<CurseForgeFile>();
        foreach(var item in data.EnumerateArray())
        {
            int fileId=Int(item,"id");
            if(fileId<=0 || Int(item,"modId")!=modId)continue;
            string display=Str(item,"displayName")??Str(item,"fileName")??"";
            if(display.Length==0)display="File "+fileId;
            DateTimeOffset? published=null;
            if(DateTimeOffset.TryParse(Str(item,"fileDate"),out var when))
                published=when;
            var deps=new List<int>();
            var relations=new List<CurseForgeRelation>();
            if(item.TryGetProperty("dependencies",out var dependencies) &&
               dependencies.ValueKind==JsonValueKind.Array)
            {
                if(dependencies.GetArrayLength()>100)
                    throw new InvalidDataException("CurseForge file relation list exceeds limit");
                foreach(var dependency in dependencies.EnumerateArray())
                {
                    int id=Int(dependency,"modId");
                    if(id<=0)continue;
                    int kind=Int(dependency,"relationType");
                    // Unknown relation types are represented as None, never
                    // silently converted into RequiredDependency.
                    var type=Enum.IsDefined(typeof(CurseForgeRelationType),kind)?
                        (CurseForgeRelationType)kind:CurseForgeRelationType.None;
                    if(relations.Any(x=>x.ModId==id&&x.Type!=type))
                        throw new InvalidDataException("Conflicting CurseForge relations for one mod");
                    if(relations.Any(x=>x.ModId==id))continue;
                    relations.Add(new(id,type));
                    if(type is CurseForgeRelationType.EmbeddedLibrary or
                               CurseForgeRelationType.RequiredDependency)
                        deps.Add(id);
                }
            }
            bool available=item.TryGetProperty("isAvailable",out var availability) &&
                availability.ValueKind==JsonValueKind.True;
            files.Add(new CurseForgeFile(fileId,modId,display,published,available,deps){
                Relations=relations
            });
        }
        int total=files.Count;
        if(root.TryGetProperty("pagination",out var page)&&page.ValueKind==JsonValueKind.Object)
            total=Int(page,"totalCount");
        return new(modId,files,index,total,index+25<Math.Min(total,10000));
    }

    private async Task<int> FindAsaGameAsync(string key,CancellationToken ct)
    {
        var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        if(cachedAsaGame.HasValue && cachedKeyDigest==digest)return cachedAsaGame.Value;
        // Resolve game ID from the authenticated catalog. Do not assume that an
        // old hard-coded ID remains valid or that an empty search is authoritative.
        for(int index=0;index<10000;index+=50)
        {
            using var document=await Request(key,
                "/v1/games?index="+index+"&pageSize=50",ct);
            var data=Array(document.RootElement,"data");
            foreach(var item in data.EnumerateArray())
            {
                string name=Str(item,"name")??"";
                string slug=Str(item,"slug")??"";
                if(slug.Equals("ark-survival-ascended",StringComparison.OrdinalIgnoreCase) ||
                   name.Replace(":","").Replace(" ","")
                       .Equals("ARKSurvivalAscended",StringComparison.OrdinalIgnoreCase))
                {
                    int id=Int(item,"id");
                    if(id>0){cachedAsaGame=id;cachedKeyDigest=digest;return id;}
                }
            }
            if(data.GetArrayLength()<50)break;
        }
        throw new InvalidDataException(
            "ARK Survival Ascended is not visible to this approved CurseForge API key");
    }

    private async Task<JsonDocument> Request(string key,string relative,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,
            new Uri("https://api.curseforge.com"+relative,UriKind.Absolute));
        request.Headers.Add("x-api-key",key);
        request.Headers.Accept.ParseAdd("application/json");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(response.StatusCode==HttpStatusCode.Unauthorized ||
           response.StatusCode==HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("CurseForge API access denied; check approved key");
        if((int)response.StatusCode==429)
            throw new IOException("CurseForge API rate limit reached; retry later");
        if(!response.IsSuccessStatusCode)
            throw new HttpRequestException("CurseForge request failed: HTTP "+(int)response.StatusCode);
        if(response.Content.Headers.ContentLength>MaxJsonBytes)
            throw new InvalidDataException("CurseForge JSON response exceeds safe size");
        await using var input=await response.Content.ReadAsStreamAsync(ct);
        using var memory=new MemoryStream();
        var buffer=new byte[16384];
        int count;
        while((count=await input.ReadAsync(buffer,ct))>0)
        {
            if(memory.Length+count>MaxJsonBytes)
                throw new InvalidDataException("CurseForge JSON response exceeds safe size");
            memory.Write(buffer,0,count);
        }
        memory.Position=0;
        return await JsonDocument.ParseAsync(memory,cancellationToken:ct);
    }
    private static JsonElement Array(JsonElement root,string name)
    {
        if(!root.TryGetProperty(name,out var arr) || arr.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("CurseForge JSON missing '"+name+"' array");
        return arr;
    }
    private static string? Str(JsonElement root,string name) =>
        root.ValueKind==JsonValueKind.Object &&
        root.TryGetProperty(name,out var val) && val.ValueKind==JsonValueKind.String
            ? val.GetString():null;
    private static int Int(JsonElement root,string name) =>
        root.ValueKind==JsonValueKind.Object &&
        root.TryGetProperty(name,out var val) && val.ValueKind==JsonValueKind.Number &&
        val.TryGetInt32(out var number)?number:0;
}
