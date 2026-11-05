using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class CurseForgeBrowseApiTests
{
    private const string Key="SYNTHETIC_CURSEFORGE_APPROVED_BROWSE_TEST_123";
    private sealed class Stub : HttpMessageHandler
    {
        public int Calls,Attempts;
        public string LastSearch="",FeaturedPayload="";
        public bool ThrottleOnce;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            if(request.RequestUri?.Host!="api.curseforge.com" ||
               !request.Headers.TryGetValues("x-api-key",out var keys) ||
               keys.Single()!=Key)
                return new(HttpStatusCode.Forbidden);
            string path=request.RequestUri.AbsolutePath;
            if(path=="/v1/categories" && ThrottleOnce && Attempts++==0)
                return new(HttpStatusCode.TooManyRequests);
            var json=path switch{
                "/v1/games" => """{"data":[{"id":4418,"slug":"ark-survival-ascended","name":"ARK: Survival Ascended"}]}""",
                "/v1/categories" => """{"data":[{"id":100,"gameId":4418,"name":"Structures","isClass":false,"parentCategoryId":0},{"id":222,"gameId":123,"name":"Wrong Game"}]}""",
                "/v1/mods/search" => """{"data":[{"id":928548,"gameId":4418,"name":"Test Mod","summary":"Synthetic","downloadCount":123456,"allowModDistribution":false,"isAvailable":true,"authors":[{"name":"Creator"}],"categories":[{"name":"Structures"}],"screenshots":[{"url":"https://media.forgecdn.net/safe.jpg"},{"url":"https://unsafe.example.net/file.jpg"}]},{"id":999999,"gameId":3333,"name":"Wrong game"}],"pagination":{"totalCount":26}}""",
                "/v1/mods/featured" => """{"data":{"featured":[{"id":928548,"gameId":4418,"name":"Featured ASA"}],"popular":[{"id":999101,"gameId":4418,"name":"Popular ASA"}],"recentlyUpdated":[{"id":999102,"gameId":4418,"name":"Recent ASA"},{"id":999103,"gameId":123,"name":"Not ASA"}]}}""",
                "/v1/mods/928548/files/14001" => """{"data":{"id":14001,"gameId":4418,"modId":928548,"displayName":"File v2","fileDate":"2026-11-05T10:00:00Z","isAvailable":true,"fileFingerprint":123456789,"fileLength":4096,"gameVersions":["ASA"],"dependencies":[{"modId":999101,"relationType":3},{"modId":999102,"relationType":5}]}}""",
                _ => """{"data":[]}"""
            };
            if(path=="/v1/mods/search")LastSearch=request.RequestUri.Query;
            if(path=="/v1/mods/featured")
            {
                if(request.Method!=HttpMethod.Post)
                    throw new InvalidDataException("Featured endpoint must use POST");
                FeaturedPayload=await request.Content!.ReadAsStringAsync(ct);
            }
            return new(HttpStatusCode.OK){
                Content=new StringContent(json,Encoding.UTF8,"application/json")
            };
        }
    }
    private static void Check(bool yes,string description)
    {
        if(!yes)throw new InvalidOperationException("CURSEFORGE BROWSE: "+description);
        Console.WriteLine("PASS CurseForge browse API: "+description);
    }
    public static async Task Run()
    {
        var handler=new Stub{ThrottleOnce=true};
        using var http=new HttpClient(handler);
        var catalog=new CurseForgeCatalog(http);
        var categories=await catalog.GetAsaCategoriesAsync(Key);
        Check(categories.Count==1 && categories[0].Name=="Structures" &&
            handler.Attempts==1 && handler.Calls==3,
            "ASA categories filtered by documented gameId with bounded 429 retry");

        var search=await catalog.SearchAsaAsync(Key,
            new CurseForgeSearchOptions("Creator",CategoryId:100,
                Sort:CurseForgeSearchSortField.Popularity),0);
        Check(search.Mods.Count==1 && search.Mods[0].Id==928548 &&
            search.Mods[0].Authors.SequenceEqual(new[]{"Creator"}) &&
            search.Mods[0].DownloadCount==123456 &&
            search.Mods[0].AllowModDistribution==false &&
            search.Mods[0].Screenshots.Count==1 &&
            search.HasMore && handler.LastSearch.Contains("categoryId=100") &&
            handler.LastSearch.Contains("sortField=2") &&
            handler.LastSearch.Contains("sortOrder=desc") &&
            handler.LastSearch.Contains("searchFilter=Creator"),
            "author-name search, server-side category/popularity sort, pagination and provider permissions");

        var featured=await catalog.GetAsaFeaturedAsync(Key);
        Check(featured.Featured.Count==1 && featured.Popular.Count==1 &&
            featured.RecentlyUpdated.Count==1 &&
            featured.Featured[0].Id==928548 &&
            handler.FeaturedPayload.Contains("\"gameId\":4418") &&
            handler.FeaturedPayload.Contains("\"excludedModIds\":[]"),
            "documented featured/popular/recent POST and ASA-only result validation");

        var exact=await catalog.GetExactAsaFileAsync(Key,928548,14001);
        Check(exact.FileId==14001 && exact.ModId==928548 &&
            exact.FileFingerprint==123456789 && exact.Dependencies.Single()==999101 &&
            exact.Relations.Any(x=>x.Type==CurseForgeRelationType.Incompatible) &&
            exact.DistributionState.Contains("ellenőrizendő"),
            "exact file ID metadata and incomplete distribution approval are preserved");

        bool invalidSort=false;
        try{await catalog.SearchAsaAsync(Key,
            new CurseForgeSearchOptions("abc",Sort:(CurseForgeSearchSortField)99));}
        catch(ArgumentOutOfRangeException){invalidSort=true;}
        Check(invalidSort,"unsupported sort enum denied before network request");

        bool invalidFile=false;
        try{await catalog.GetExactAsaFileAsync(Key,928548,-1);}
        catch(ArgumentOutOfRangeException){invalidFile=true;}
        Check(invalidFile,"invalid exact file ID denied before network request");

        Check(handler.Calls==6,
            "API browse uses no download, scraping or implicit installation endpoint");
    }
}
