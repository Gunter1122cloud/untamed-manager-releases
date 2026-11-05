using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class CurseForgeCompleteFilesTests
{
    private const string Key="SYNTHETIC_COMPLETE_ASA_FILES_APPROVED_KEY";
    private sealed class Fixture : HttpMessageHandler
    {
        public int Pages;
        public bool DuplicateSecond,ChangedTotal,TooMany,WrongGame;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken cancellationToken)
        {
            if(request.RequestUri?.Host!="api.curseforge.com" ||
               !request.Headers.TryGetValues("x-api-key",out var keys) ||
               keys.SingleOrDefault()!=Key)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            string route=request.RequestUri!.AbsolutePath;
            string body;
            if(route=="/v1/games")
                body="""{"data":[{"id":4418,"slug":"ark-survival-ascended","name":"ARK: Survival Ascended"}]}""";
            else if(route=="/v1/mods/928548")
                body="{\"data\":{\"id\":928548,\"gameId\":"+
                    (WrongGame?"123":"4418")+",\"name\":\"Synthetic mod\"}}";
            else if(route=="/v1/mods/928548/files")
            {
                Pages++;
                string query=request.RequestUri.Query;
                int index=query.Contains("index=25",StringComparison.Ordinal)?25:0;
                int count=index==0?25:1;
                int total=TooMany?401:(ChangedTotal&&index==25?27:26);
                var files=Enumerable.Range(0,count).Select(i=>{
                    int id=10000+index+i;
                    if(index==25&&DuplicateSecond)id=10000;
                    return "{\"id\":"+id+",\"modId\":928548,\"isAvailable\":true,"+
                        "\"displayName\":\"Verified file\", \"fileDate\":\"2026-11-05T00:00:00Z\"}";
                });
                body="{\"data\":["+string.Join(",",files)+"],\"pagination\":{\"totalCount\":"+
                    total+"}}";
            }
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent(body,Encoding.UTF8,"application/json")
            });
        }
    }
    private static void Check(bool valid,string what)
    {
        if(!valid)throw new InvalidOperationException("COMPLETE CF FILES: "+what);
        Console.WriteLine("PASS complete CurseForge files: "+what);
    }
    public static async Task Run()
    {
        using(var client=new HttpClient(new Fixture()))
        {
            var result=await new CurseForgeCatalog(client)
                .GetCompleteAsaModFilesAsync(Key,928548);
            Check(result.Files.Count==26 && result.Total==26 &&
                result.Index==0 && !result.HasMore &&
                result.Files.Select(x=>x.FileId).Distinct().Count()==26,
                "two provider pages joined only after exact ASA game ID and file identity checks");
            var local=new LocalModEvidence("928548",true,10000,
                "Synthetic proof","Test fixture exact ID");
            var comparison=ModUpdatePlanner.Assess(local,result);
            Check(comparison.State==ModUpdateState.NewerFileAvailable &&
                comparison.LatestFileId==10025,
                "complete 26-file list supports grounded local-vs-provider version decision");
        }
        foreach(string scenario in new[]{"duplicate","changing-total","more-than-cap","wrong-game"})
        {
            var handler=new Fixture{
                DuplicateSecond=scenario=="duplicate",
                ChangedTotal=scenario=="changing-total",
                TooMany=scenario=="more-than-cap",
                WrongGame=scenario=="wrong-game"
            };
            using var client=new HttpClient(handler);
            bool rejected=false;
            try{await new CurseForgeCatalog(client)
                .GetCompleteAsaModFilesAsync(Key,928548);}
            catch(InvalidDataException){rejected=true;}
            Check(rejected,"fail closed on "+scenario+" provider metadata");
            if(scenario is "more-than-cap" or "wrong-game")
                Check(handler.Pages<=1,
                    "invalid provider ownership/oversized total avoids excess page requests");
        }
    }
}
