using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class DiscordStatusTests
{
    private static void Check(bool condition,string name)
    {
        if(!condition)throw new Exception("INTEGRATION TEST FAILED: "+name);
        Console.WriteLine("PASS discord "+name);
    }
    private sealed class FakeDiscordHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Body { get; private set; }="";
        public Uri? Requested { get; private set; }
        public HttpStatusCode Status { get; set; }=HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken token)
        {
            Calls++;
            Requested=request.RequestUri;
            Body=request.Content==null?"":
                await request.Content.ReadAsStringAsync(token);
            return new HttpResponseMessage(Status){
                Content=new StringContent(
                    """{"id":"123456789012345678"}""",
                    Encoding.UTF8,"application/json")
            };
        }
    }
    public static async Task Run()
    {
        const string sampleWebhook=
            "https://discord.com/api/webhooks/123456789012345678/"+
            "SYNTHETIC_NO_REAL_DISCORD_TOKEN_DO_NOT_SEND12345";
        Check(DiscordStatusPublisher.ValidWebhook(sampleWebhook) &&
            !DiscordStatusPublisher.ValidWebhook(
                sampleWebhook.Replace("discord.com","discord.com.attacker.example")) &&
            !DiscordStatusPublisher.ValidWebhook(
                sampleWebhook+"?redirect=https://untrusted.example") &&
            !DiscordStatusPublisher.ValidWebhook(
                sampleWebhook.Replace("https://","http://")) &&
            !DiscordStatusPublisher.ValidWebhook(
                sampleWebhook.Replace("discord.com","cdn.discord.com")),
            "Discord webhook sender accepts only exact official HTTPS endpoint without redirects");
        var now=DateTimeOffset.UtcNow;
        var owned=Guid.NewGuid();
        var snapshot=new SupervisorSnapshot("fixture",123,now,
            "fixture-supervisor.exe",now,new[]{
                new MapObservation(owned,"Astraeos @everyone\nSECRET-USER",true,
                    "fixture","fixture",12.5,1024,60,now,null,false,
                    "Monitoring","Unknown","None","None")
            });
        var ownedMaps=new HashSet<Guid>{owned};
        string preview=DiscordStatusPublisher.Compose(snapshot,ownedMaps,now);
        Check(preview.Contains("Astraeos everyoneSECRET-USER") &&
            !preview.Contains("@everyone") &&
            !preview.Contains("\\nSECRET-USER") &&
            preview.Contains("játékosok: ismeretlen") &&
            !preview.Contains("fixture-supervisor.exe"),
            "Discord preview removes @mentions/control chars and never invents player counts");
        bool foreign=false;
        try{DiscordStatusPublisher.Compose(snapshot,new HashSet<Guid>(),now);}
        catch(InvalidOperationException){foreign=true;}
        Check(foreign,"Discord blocks observations from non-owned map IDs");
        bool stale=false;
        try{DiscordStatusPublisher.Compose(snapshot,ownedMaps,
            now.AddHours(2));}
        catch(InvalidOperationException){stale=true;}
        Check(stale,"Discord refuses stale observations instead of posting fake online status");

        var handler=new FakeDiscordHandler();
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(5)};
        var result=await DiscordStatusPublisher.SendAsync(client,
            sampleWebhook,snapshot,ownedMaps);
        Check(result.Maps==1 &&result.MessageId=="123456789012345678"&&
            handler.Calls==1 &&handler.Requested?.Host=="discord.com" &&
            handler.Requested.Query=="?wait=true",
            "Discord one-shot outbound status POST requires a confirmed message ID");
        using(var payload=JsonDocument.Parse(handler.Body))
        {
            var root=payload.RootElement;
            Check(root.GetProperty("allowed_mentions").GetProperty("parse")
                .GetArrayLength()==0 &&
                root.GetProperty("content").GetString()?.Contains("@everyone")==false &&
                !handler.Body.Contains("fixture-supervisor.exe") &&
                !handler.Body.Contains("SECRET-RCON"),
                "Discord payload disables all mentions and excludes host secrets/process arguments");
        }
        handler.Status=HttpStatusCode.TooManyRequests;
        bool throttled=false;
        try{await DiscordStatusPublisher.SendAsync(client,
            sampleWebhook,snapshot,ownedMaps);}
        catch(IOException){throttled=true;}
        Check(throttled &&handler.Calls==2,
            "Discord 429 does not silently retry non-idempotent status messages");
        handler.Status=HttpStatusCode.Forbidden;
        bool forbidden=false;
        try{await DiscordStatusPublisher.SendAsync(client,
            sampleWebhook,snapshot,ownedMaps);}
        catch(UnauthorizedAccessException){forbidden=true;}
        Check(forbidden &&handler.Calls==3,
            "Discord invalid webhook authorization fails without token leakage or retry");
    }
}
