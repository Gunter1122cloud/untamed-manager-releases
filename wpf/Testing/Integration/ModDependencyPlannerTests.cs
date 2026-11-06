using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class ModDependencyPlannerTests
{
    private sealed class FakeHandler(string json):HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Requests++;
            if(request.RequestUri?.Host!="api.curseforge.com"||
               request.RequestUri?.AbsolutePath!="/v1/mods/928548/files"||
               request.Headers.GetValues("x-api-key").Single()!="isolated-test-key")
                throw new IOException("Unexpected CurseForge endpoint or header in fixture");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent(json)
            });
        }
    }
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new Exception("CurseForge relation test failed: "+reason);
        Console.WriteLine("PASS curseforge-dependencies "+reason);
    }
    private static CurseForgeFile File(int id,int mod,params CurseForgeRelation[] relations)=>
        new(id,mod,"isolated",DateTimeOffset.UtcNow,true,
            relations.Where(r=>r.Type is CurseForgeRelationType.RequiredDependency or
                CurseForgeRelationType.EmbeddedLibrary).Select(r=>r.ModId).ToArray()){
            Relations=relations
        };
    public static async Task Run()
    {
        const string json="""
        {"data":[
          {"id":101,"modId":928548,"displayName":"Isolated ASA file",
           "isAvailable":true,"fileDate":"2026-11-04T12:00:00Z",
           "dependencies":[
             {"modId":930170,"relationType":3},
             {"modId":912815,"relationType":1},
             {"modId":925000,"relationType":2},
             {"modId":930200,"relationType":5},
             {"modId":930201,"relationType":4},
             {"modId":930202,"relationType":6}
           ]}
        ],"pagination":{"index":0,"pageSize":25,"totalCount":1}}
        """;
        var handler=new FakeHandler(json);
        using var http=new HttpClient(handler);
        var page=await new CurseForgeCatalog(http).GetModFilesAsync(
            "isolated-test-key",928548);
        Check(handler.Requests==1&&page.Files.Count==1,
            "request uses official CurseForge endpoint without network access");
        var root=page.Files.Single();
        Check(root.Relations.Count==6 &&
            root.Dependencies.SequenceEqual(new[]{930170,912815}),
            "required and embedded library dependencies exclude optional, tool, include and incompatible entries");
        Check(root.Relations.Single(x=>x.ModId==925000).Type==
            CurseForgeRelationType.OptionalDependency &&
            root.Relations.Single(x=>x.ModId==930200).Type==
            CurseForgeRelationType.Incompatible,
            "provider relationship types survive metadata parsing");
        var missing=ModDependencyPlanner.Review(928548,new[]{root},Array.Empty<string>());
        Check(!missing.Consistent&&missing.MissingRequired.SequenceEqual(new[]{912815,930170}) &&
            missing.OptionalMods.SequenceEqual(new[]{925000}) &&
            !missing.AutoInstallAllowed,
            "missing required files prevent dependency plan while optional remains suggestion");
        var depA=File(201,930170);
        var depB=File(202,912815);
        var ready=ModDependencyPlanner.Review(928548,new[]{root,depA,depB},
            Array.Empty<string>());
        Check(ready.Consistent && ready.DependencyFirstOrder.SequenceEqual(
            new[]{930170,912815,928548})&&!ready.AutoInstallAllowed,
            "verified complete remote graph returns deterministic dependency-first order, not install permission");
        var incompatible=ModDependencyPlanner.Review(928548,new[]{root,depA,depB},
            new[]{"930200"});
        Check(!incompatible.Consistent &&
            incompatible.IncompatibleEnabled.Single()==930200,
            "installed incompatible project blocks plan");
        var cyclic=ModDependencyPlanner.Review(928548,
            new[]{root,File(201,930170,new CurseForgeRelation(928548,CurseForgeRelationType.RequiredDependency)),depB},
            Array.Empty<string>());
        Check(!cyclic.Consistent&&cyclic.Cyclic.Contains(928548),
            "circular required dependency cannot pass review");
        var unknown=File(102,928548,new CurseForgeRelation(928549,CurseForgeRelationType.None));
        var unknownPreview=ModDependencyPlanner.Review(928548,new[]{unknown},
            Array.Empty<string>());
        Check(!unknownPreview.Consistent && unknownPreview.Unclassified.Single()==928549,
            "new provider relation types fail closed, never implied required installs");
        var legacy= new CurseForgeFile(103,928548,"old",null,true,new[]{930170});
        var legacyPreview=ModDependencyPlanner.Review(928548,new[]{legacy,depA},
            Array.Empty<string>());
        Check(!legacyPreview.Consistent && legacyPreview.Unclassified.Single()==930170,
            "untyped historical dependency input cannot silently grant install order");

        const string conflicting="""
        {"data":[{"id":101,"modId":928548,"displayName":"Conflicting",
          "isAvailable":true,
          "dependencies":[{"modId":930170,"relationType":3},
                          {"modId":930170,"relationType":5}]}]}
        """;
        using var conflictedHttp=new HttpClient(new FakeHandler(conflicting));
        bool rejected=false;
        try{await new CurseForgeCatalog(conflictedHttp).GetModFilesAsync(
            "isolated-test-key",928548);}
        catch(InvalidDataException){rejected=true;}
        Check(rejected,
            "provider metadata naming the same mod as required and incompatible is rejected");
    }
}
