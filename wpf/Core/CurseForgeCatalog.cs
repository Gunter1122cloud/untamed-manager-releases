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
    public IReadOnlyList<string> Authors {get;init;}=Array.Empty<string>();
    public IReadOnlyList<string> Categories {get;init;}=Array.Empty<string>();
    public IReadOnlyList<string> Screenshots {get;init;}=Array.Empty<string>();
    public long DownloadCount {get;init;}
    public bool? AllowModDistribution {get;init;}
    public bool? IsAvailable {get;init;}
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
    public long SizeBytes {get;init;}
    public IReadOnlyList<string> GameVersions {get;init;}=Array.Empty<string>();
    public long? FileFingerprint {get;init;}
    public string DistributionState {get;init;}="Letöltési jogosultság nem igazolt";
}
public sealed record CurseForgeFilesResult(int ModId,IReadOnlyList<CurseForgeFile> Files,
    int Index,int Total,bool HasMore);
public sealed record CurseForgeCategory(int Id,string Name,int GameId,bool IsClass,int ParentCategoryId);
public sealed record CurseForgeFeaturedResult(int GameId,IReadOnlyList<CurseForgeMod> Featured,
    IReadOnlyList<CurseForgeMod> Popular,IReadOnlyList<CurseForgeMod> RecentlyUpdated);
public enum CurseForgeSearchSortField
{
    ProviderDefault=0,Featured=1,Popularity=2,LastUpdated=3,Name=4,
    Author=5,TotalDownloads=6,Category=7,GameVersion=8,
    EarlyAccess=9,FeaturedReleased=10,ReleasedDate=11,Rating=12
}
public sealed record CurseForgeSearchOptions(string Term="",int CategoryId=0,
    int ClassId=0,int AuthorId=0,CurseForgeSearchSortField Sort=CurseForgeSearchSortField.ProviderDefault,
    bool Descending=true);

public sealed class CurseForgeCatalog
{
    private const int PageSize=25;
    private const int MaxJsonBytes=2_000_000;
    private readonly HttpClient http;
    private int? cachedAsaGame;
    private string? cachedKeyDigest;
    public CurseForgeCatalog(HttpClient httpClient) =>
        http=httpClient??throw new ArgumentNullException(nameof(httpClient));

    // Do not guess an API credential's validity from its shape. Operators may
    // provision the exact value supplied by their provider, including strings
    // resembling a password hash. Only a real CurseForge API request can verify
    // acceptance. Never echo the credential in logs, UI or repository content.
    public static void ValidateApiKey(string? key)
    {
        if(string.IsNullOrWhiteSpace(key) || key.Length>512 ||
           key.Any(char.IsControl))
            throw new ArgumentException("A CurseForge credential is required",nameof(key));
        var candidate=key.Trim();
        if(candidate.StartsWith("{",StringComparison.Ordinal) ||
           candidate.StartsWith("[",StringComparison.Ordinal))
            throw new ArgumentException("Paste only the credential value, not a JSON document",nameof(key));
    }

    // The connection test queries game metadata only: no downloads and no ASA changes.
    public async Task<int> VerifyAsaKeyAsync(string key,CancellationToken ct=default)
    {
        ValidateApiKey(key);
        return await FindAsaGameAsync(key,ct);
    }

    public Task<CurseForgeSearchResult> SearchAsaAsync(string key,string term,int index=0,
        CancellationToken ct=default) =>
        SearchAsaAsync(key,new CurseForgeSearchOptions(term),index,ct);

    /// <summary>Documented ASA search: name OR author free text, category and provider sort.</summary>
    public async Task<CurseForgeSearchResult> SearchAsaAsync(
        string key,CurseForgeSearchOptions options,int index=0,CancellationToken ct=default)
    {
        ValidateApiKey(key);
        ArgumentNullException.ThrowIfNull(options);
        string term=(options.Term??"").Trim();
        if(term.Length>80 || (term.Length is >0 and <2))
            throw new ArgumentException("Search text must contain 2-80 characters",nameof(options));
        if(options.CategoryId<0 || options.ClassId<0 || options.AuthorId<0 ||
           !Enum.IsDefined(options.Sort) || index<0 || index+PageSize>10000 ||
           index%PageSize!=0)
            throw new ArgumentOutOfRangeException(nameof(options));
        int game=await FindAsaGameAsync(key,ct);
        string path="/v1/mods/search?gameId="+game+"&index="+index+"&pageSize="+PageSize;
        if(term.Length>0)path+="&searchFilter="+Uri.EscapeDataString(term);
        if(options.CategoryId>0)path+="&categoryId="+options.CategoryId;
        if(options.ClassId>0)path+="&classId="+options.ClassId;
        if(options.AuthorId>0)path+="&authorId="+options.AuthorId;
        if(options.Sort!=CurseForgeSearchSortField.ProviderDefault)
            path+="&sortField="+(int)options.Sort+"&sortOrder="+
                (options.Descending?"desc":"asc");
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

    public async Task<IReadOnlyList<CurseForgeCategory>> GetAsaCategoriesAsync(
        string key,CancellationToken ct=default)
    {
        ValidateApiKey(key);
        int game=await FindAsaGameAsync(key,ct);
        using var document=await Request(key,"/v1/categories?gameId="+game,ct);
        return Array(document.RootElement,"data").EnumerateArray()
            .Where(x=>Int(x,"id")>0 && Int(x,"gameId")==game)
            .Take(250)
            .Select(x=>new CurseForgeCategory(Int(x,"id"),
                (Str(x,"name")??"").Trim(),game,
                Bool(x,"isClass")==true,Int(x,"parentCategoryId")))
            .Where(x=>x.Name.Length is >0 and <=100)
            .ToArray();
    }

    /// <summary>Documented POST /v1/mods/featured: metadata only, never a mod download.</summary>
    public async Task<CurseForgeFeaturedResult> GetAsaFeaturedAsync(
        string key,CancellationToken ct=default)
    {
        ValidateApiKey(key);
        int game=await FindAsaGameAsync(key,ct);
        string payload=JsonSerializer.Serialize(new {
            gameId=game,excludedModIds=System.Array.Empty<int>(),gameVersionTypeId=0
        });
        using var document=await Request(key,"/v1/mods/featured",ct,
            HttpMethod.Post,payload);
        if(!document.RootElement.TryGetProperty("data",out var entry) ||
           entry.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("CurseForge featured response is missing data");
        IReadOnlyList<CurseForgeMod> Group(string field)
        {
            var arr=Array(entry,field);
            return arr.EnumerateArray().Take(100)
                .Select(x=>ParseAsaMod(x,game)).Where(x=>x!=null)
                .Select(x=>x!).ToArray();
        }
        return new(game,Group("featured"),Group("popular"),Group("recentlyUpdated"));
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
        ValidateApiKey(key);
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

    /// <summary>
    /// Read-only provider description. CurseForge returns HTML; never render
    /// markup in a WPF WebBrowser. Decode a bounded, inert text preview only.
    /// </summary>
    public async Task<string> GetModDescriptionTextAsync(string key,int modId,
        CancellationToken ct=default)
    {
        ValidateApiKey(key);
        if(modId<10000)throw new ArgumentOutOfRangeException(nameof(modId));
        using var document=await Request(key,
            "/v1/mods/"+modId+"/description?stripped=true",ct);
        if(!document.RootElement.TryGetProperty("data",out var entry) ||
           entry.ValueKind!=JsonValueKind.String)
            throw new InvalidDataException("CurseForge description response is invalid");
        string html=entry.GetString()??"";
        if(html.Length>150_000)
            throw new InvalidDataException("CurseForge description is too long");
        string inert=System.Text.RegularExpressions.Regex.Replace(html,
            @"<\s*(script|style|iframe)\b[^>]*>.*?<\s*/\s*\1\s*>"," ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase|
            System.Text.RegularExpressions.RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(200));
        string safe=System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Replace(inert,@"<[^>]*>"," ",
                System.Text.RegularExpressions.RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(200)));
        safe=new string(safe.Where(c=>!char.IsControl(c) || c is '\r' or '\n' or '\t')
            .ToArray()).Trim();
        return safe.Length>10_000?safe[..10_000]+" …":safe;
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
        {
            ThumbnailUrl=thumbnail,
            Authors=Names(item,"authors",12),
            Categories=Names(item,"categories",20),
            Screenshots=Images(item,"screenshots",12),
            DownloadCount=Math.Max(0,Long(item,"downloadCount")),
            AllowModDistribution=Bool(item,"allowModDistribution"),
            IsAvailable=Bool(item,"isAvailable")
        };
    }

    // Official Core API file metadata only. Not a download entitlement and not
    // proof that a mod is Crossplay, free or enabled on the local ASA runtime.
    public async Task<CurseForgeFilesResult> GetModFilesAsync(string key,int modId,
        int index=0,CancellationToken ct=default)
    {
        ValidateApiKey(key);
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
            long size=Long(item,"fileLength");
            files.Add(new CurseForgeFile(fileId,modId,display,published,available,deps){
                Relations=relations,
                SizeBytes=Math.Max(0,size),
                GameVersions=Strings(item,"gameVersions",25)
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

    private async Task<JsonDocument> Request(string key,string relative,CancellationToken ct,
        HttpMethod? method=null,string? jsonBody=null)
    {
        ct.ThrowIfCancellationRequested();
        if(jsonBody!=null && ((method??HttpMethod.Get)!=HttpMethod.Post ||
           jsonBody.Length>8192))
            throw new InvalidDataException("Invalid CurseForge JSON request payload");
        using var request=new HttpRequestMessage(method??HttpMethod.Get,
            new Uri("https://api.curseforge.com"+relative,UriKind.Absolute));
        request.Headers.Add("x-api-key",key);
        request.Headers.Accept.ParseAdd("application/json");
        if(jsonBody!=null)
            request.Content=new StringContent(jsonBody,Encoding.UTF8,"application/json");
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
    private static bool? Bool(JsonElement root,string name)
    {
        if(!root.TryGetProperty(name,out var v))return null;
        return v.ValueKind switch{
            JsonValueKind.True=>true,
            JsonValueKind.False=>false,
            _=>null
        };
    }
    private static string? TrustedPicture(string? offered)
    {
        if(!Uri.TryCreate(offered,UriKind.Absolute,out var uri) ||
           uri.Scheme!=Uri.UriSchemeHttps || uri.UserInfo.Length>0)return null;
        if(uri.Host.Equals("media.forgecdn.net",StringComparison.OrdinalIgnoreCase) ||
           uri.Host.EndsWith(".forgecdn.net",StringComparison.OrdinalIgnoreCase))
            return uri.ToString();
        return null;
    }
    private static IReadOnlyList<string> Images(JsonElement root,string field,int cap)
    {
        if(!root.TryGetProperty(field,out var data) ||
           data.ValueKind!=JsonValueKind.Array)return System.Array.Empty<string>();
        return data.EnumerateArray().Take(cap).Select(x=>
            TrustedPicture(Str(x,"thumbnailUrl")??Str(x,"url")))
            .Where(x=>x!=null).Select(x=>x!)
            .Distinct(StringComparer.Ordinal).ToArray();
    }
    private static IReadOnlyList<string> Names(JsonElement root,string name,int cap)
    {
        if(!root.TryGetProperty(name,out var values) ||
           values.ValueKind!=JsonValueKind.Array)return System.Array.Empty<string>();
        return values.EnumerateArray().Take(cap).Select(x=>Str(x,"name"))
            .Where(x=>!string.IsNullOrWhiteSpace(x) && x!.Length<=120 &&
                !x.Any(char.IsControl)).Select(x=>x!).Distinct(StringComparer.Ordinal)
            .ToArray();
    }
    private static IReadOnlyList<string> Strings(JsonElement root,string name,int cap)
    {
        if(!root.TryGetProperty(name,out var values) ||
           values.ValueKind!=JsonValueKind.Array)return System.Array.Empty<string>();
        return values.EnumerateArray().Take(cap)
            .Where(x=>x.ValueKind==JsonValueKind.String)
            .Select(x=>x.GetString())
            .Where(x=>!string.IsNullOrWhiteSpace(x) && x!.Length<=80 &&
                !x.Any(char.IsControl)).Select(x=>x!).ToArray();
    }
    private static long Long(JsonElement root,string name) =>
        root.ValueKind==JsonValueKind.Object &&
        root.TryGetProperty(name,out var val) && val.ValueKind==JsonValueKind.Number &&
        val.TryGetInt64(out long number)?number:0L;
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
