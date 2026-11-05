using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

// Purely synthetic fixtures. No operator credential, API network, ASA, or live host.
public static class CurseForgeApiKeyValidationTests
{
    private sealed class StubHandler:HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            Requests++;
            if(request.RequestUri?.Host!="api.curseforge.com" ||
               request.RequestUri.AbsolutePath!="/v1/games" ||
               !request.Headers.TryGetValues("x-api-key",out var headers))
                throw new InvalidDataException("Unexpected CurseForge request");
            string key=headers.Single();
            if(key=="DENIED_SYNTHETIC_KEY")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if(key!="APPROVED_SYNTHETIC_KEY")
                throw new InvalidDataException("Unexpected API credential fixture");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent(
                    "{\"data\":[{\"id\":4418,\"name\":\"ARK: Survival Ascended\",\"slug\":\"ark-survival-ascended\"}]}")
            });
        }
    }
    private static void Check(bool valid,string title)
    {
        if(!valid)throw new InvalidOperationException("CurseForge API key: "+title);
        Console.WriteLine("PASS CurseForge API key: "+title);
    }
    public static async Task Run()
    {
        string fakeHash="$2a$10$"+new string('A',53);
        string fakeHashB="$2b$12$"+new string('B',53);
        string fakeHashY="$2y$10$"+new string('C',53);
        var handler=new StubHandler();
        using var http=new HttpClient(handler);
        var catalog=new CurseForgeCatalog(http);
        foreach(string invalid in new[]{
            "", "  ", fakeHash, fakeHashB, fakeHashY,
            "{\"ok\":true,\"token\":\"not-a-key\"}", "[\"invalid\"]"
        })
        {
            bool refused=false;
            try { CurseForgeCatalog.ValidateApiKey(invalid); }
            catch(ArgumentException) { refused=true; }
            Check(refused,"invalid pasted response or bcrypt cannot be saved as API key");
            bool preflight=false;
            try { await catalog.VerifyAsaKeyAsync(invalid); }
            catch(ArgumentException) { preflight=true; }
            Check(preflight && handler.Requests==0,
                "invalid credential rejected before any outbound HTTP");
        }
        CurseForgeCatalog.ValidateApiKey("APPROVED_SYNTHETIC_KEY");
        int game=await catalog.VerifyAsaKeyAsync("APPROVED_SYNTHETIC_KEY");
        Check(game==4418 && handler.Requests==1,
            "official read-only game-catalog query supplies x-api-key header");
        int cached=await catalog.VerifyAsaKeyAsync("APPROVED_SYNTHETIC_KEY");
        Check(cached==4418 && handler.Requests==1,
            "repeated verification avoids unnecessary API calls for same key");
        bool denied=false;
        try {await catalog.VerifyAsaKeyAsync("DENIED_SYNTHETIC_KEY");}
        catch(UnauthorizedAccessException) { denied=true; }
        Check(denied && handler.Requests==2,
            "401/403 denial is not shown as a working CurseForge API key");
    }
}
