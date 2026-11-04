using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class CurseForgeDependencyResolverTests
{
    private sealed class TestHandler(Func<int,int,string> payload):HttpMessageHandler
    {
        public readonly List<int> FetchedModIds=new();
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req,CancellationToken ct)
        {
            if(req.RequestUri?.Host!="api.curseforge.com"||
               req.RequestUri.AbsolutePath.Split('/').Length!=5 ||
               !req.RequestUri.AbsolutePath.EndsWith("/files",StringComparison.Ordinal)||
               req.Headers.GetValues("x-api-key").Single()!="TEST_ONLY_APPROVED")
                throw new IOException("Unexpected endpoint or API header");
            int id=int.Parse(req.RequestUri.AbsolutePath.Split('/')[3]);
            int index=int.Parse(req.RequestUri.Query.TrimStart('?').Split('&')
                .Single(s=>s.StartsWith("index=",StringComparison.Ordinal)).Split('=')[1]);
            FetchedModIds.Add(id);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent(payload(id,index))
            });
        }
    }
    private static void Assert(bool value,string explanation)
    {
        if(!value)throw new Exception("CurseForge dependency closure failed: "+explanation);
        Console.WriteLine("PASS curseforge-dependency-closure "+explanation);
    }
    private static string Result(int id,string relations="",int count=1)=>
        "{\"data\":[{\"id\":"+(id+1000)+",\"modId\":"+id+
        ",\"displayName\":\"CI dependency\",\"isAvailable\":true,"+
        "\"fileDate\":\"2026-10-01T00:00:00Z\",\"dependencies\":["+relations+
        "]}],\"pagination\":{\"index\":0,\"pageSize\":25,\"totalCount\":"+count+"}}";
    public static async Task Run()
    {
        string ParentRelations=
            "{\"modId\":930170,\"relationType\":3},"+
            "{\"modId\":912815,\"relationType\":1},"+
            "{\"modId\":925000,\"relationType\":2},"+
            "{\"modId\":930200,\"relationType\":5}";
        var handler=new TestHandler((id,index)=>id==928548?
            Result(id,ParentRelations):Result(id));
        using var http=new HttpClient(handler);
        var resolver=new CurseForgeDependencyResolver(new CurseForgeCatalog(http));
        var ok=await resolver.ResolveAsync("TEST_ONLY_APPROVED",928548,
            Array.Empty<string>());
        Assert(ok.FullMetadataRetrieved&&ok.HttpPagesRead==3&&
            ok.Preview?.DependencyFirstOrder.SequenceEqual(
                new[]{930170,912815,928548})==true &&
            !ok.DownloadOrInstallAllowed,
            "fetches exact mandatory dependency chain and returns deterministic review order");
        Assert(handler.FetchedModIds.SequenceEqual(new[]{928548,930170,912815})&&
            !handler.FetchedModIds.Contains(925000),
            "optional project is not fetched or installed");
        var conflict=await resolver.ResolveAsync("TEST_ONLY_APPROVED",928548,
            new[]{"930200"});
        Assert(!conflict.FullMetadataRetrieved&&
            conflict.Preview?.IncompatibleEnabled.Single()==930200,
            "enabled incompatible project prevents dependency closure");

        var cyclic=new TestHandler((id,index)=>id==928548?
            Result(id,"{\"modId\":930170,\"relationType\":3}"):
            Result(id,"{\"modId\":928548,\"relationType\":3}"));
        using var cycleHttp=new HttpClient(cyclic);
        var cycle=await new CurseForgeDependencyResolver(
            new CurseForgeCatalog(cycleHttp)).ResolveAsync("TEST_ONLY_APPROVED",
                928548,Array.Empty<string>());
        Assert(!cycle.FullMetadataRetrieved&&cycle.Preview?.Cyclic.Contains(928548)==true &&
            cyclic.FetchedModIds.Count==2,
            "a cyclic two-mod graph terminates and is rejected");

        var paged=new TestHandler((id,index)=>Result(id,"",1000));
        using var pageHttp=new HttpClient(paged);
        var incomplete=await new CurseForgeDependencyResolver(
            new CurseForgeCatalog(pageHttp)).ResolveAsync("TEST_ONLY_APPROVED",
                928548,Array.Empty<string>());
        Assert(!incomplete.FullMetadataRetrieved&&incomplete.HttpPagesRead==8&&
            paged.FetchedModIds.Count==8,
            "uncapped provider pagination cannot produce false latest-file proof");

        using var cts=new CancellationTokenSource();cts.Cancel();
        bool cancelled=false;
        try{await resolver.ResolveAsync("TEST_ONLY_APPROVED",928548,
            Array.Empty<string>(),cts.Token);}
        catch(OperationCanceledException){cancelled=true;}
        Assert(cancelled,"pre-cancelled dependency resolver never continues HTTP traversal");
    }
}
