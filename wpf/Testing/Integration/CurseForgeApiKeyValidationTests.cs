using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

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
            if(key=="DENIED_SYNTHETIC_KEY" || key.StartsWith("$2a$10$",StringComparison.Ordinal))
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
        // All credentials are synthetic fixtures; no operator secret in GitHub.
        string fakeHash="$2a$10$"+new string('A',53);
        var handler=new StubHandler();
        using var http=new HttpClient(handler);
        var catalog=new CurseForgeCatalog(http);
        foreach(string invalid in new[]{
            "", "  ", "{\"ok\":true,\"token\":\"not-a-key\"}", "[\"invalid\"]"
        })
        {
            bool refused=false;
            try { CurseForgeCatalog.ValidateApiKey(invalid); }
            catch(ArgumentException) { refused=true; }
            Check(refused,"missing secret or JSON document rejected");
            bool preflight=false;
            try { await catalog.VerifyAsaKeyAsync(invalid); }
            catch(ArgumentException) { preflight=true; }
            Check(preflight && handler.Requests==0,
                "invalid input rejected before any outbound HTTP");
        }
        CurseForgeCatalog.ValidateApiKey(fakeHash);
        bool tokenWasSent=false;
        try { await catalog.VerifyAsaKeyAsync(fakeHash); }
        catch(UnauthorizedAccessException) { tokenWasSent=true; }
        Check(tokenWasSent && handler.Requests==1,
            "opaque bcrypt-shaped value is sent unchanged; provider alone decides acceptance");
        int game=await catalog.VerifyAsaKeyAsync("APPROVED_SYNTHETIC_KEY");
        Check(game==4418 && handler.Requests==2,
            "official read-only game-catalog query supplies x-api-key header");
        int cached=await catalog.VerifyAsaKeyAsync("APPROVED_SYNTHETIC_KEY");
        Check(cached==4418 && handler.Requests==2,
            "repeated verification avoids unnecessary API calls for same key");
        bool denied=false;
        try {await catalog.VerifyAsaKeyAsync("DENIED_SYNTHETIC_KEY");}
        catch(UnauthorizedAccessException) { denied=true; }
        Check(denied && handler.Requests==3,
            "401/403 denial is not presented as a working CurseForge key");

        string root=Path.Combine(Path.GetTempPath(),"Untamed-CF-Import-"+
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var cluster=new ClusterService("testcf",Path.Combine(root,"profile"));
            string staged=Path.Combine(root,CurseForgeCredentialBootstrap.FileName);
            File.WriteAllText(staged,SecureStore.Protect(fakeHash));
            bool imported=CurseForgeCredentialBootstrap.TryImport(cluster,root);
            Check(imported &&
                SecureStore.Unprotect(cluster.Config.CurseForgeApiKeyProtected)==fakeHash &&
                !File.Exists(staged),
                "private one-time DPAPI token import accepts exact opaque value");
            string persisted=File.ReadAllText(cluster.Store.FilePath);
            Check(!persisted.Contains(fakeHash,StringComparison.Ordinal),
                "profile JSON never stores the provided credential in plaintext");

            string original=cluster.Config.CurseForgeApiKeyProtected;
            File.WriteAllText(staged,SecureStore.Protect("OTHER_SYNTHETIC_KEY"));
            Check(!CurseForgeCredentialBootstrap.TryImport(cluster,root) &&
                cluster.Config.CurseForgeApiKeyProtected==original &&
                File.Exists(staged),
                "pending import cannot silently overwrite an existing credential");
        }
        finally
        {
            try{Directory.Delete(root,true);}catch(IOException){}
        }
    }
}
