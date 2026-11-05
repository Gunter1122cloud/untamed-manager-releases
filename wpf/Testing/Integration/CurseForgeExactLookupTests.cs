using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class CurseForgeExactLookupTests
{
    private sealed class OfflineCatalogHandler : HttpMessageHandler
    {
        public int Calls {get;private set;}
        public int ModId {get;set;}=928548;
        public int GameId {get;set;}=4418;
        public string Website {get;set;}="https://www.curseforge.com/ark-survival-ascended/mods/example";
        public string Thumbnail {get;set;}="https://media.forgecdn.net/avatars/123/456.jpg";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Method!=HttpMethod.Get ||
               request.RequestUri?.Host!="api.curseforge.com" ||
               !request.Headers.TryGetValues("x-api-key",out var keys) ||
               keys.SingleOrDefault()!="CI_APPROVED_KEY_ONLY")
                throw new InvalidDataException("Unexpected remote address or missing approved key");
            Calls++;
            string payload=request.RequestUri.AbsolutePath switch
            {
                "/v1/games"=>JsonSerializer.Serialize(new {
                    data=new[]{new{id=4418,name="ARK: Survival Ascended",
                        slug="ark-survival-ascended"}}
                }),
                "/v1/mods/928548"=>JsonSerializer.Serialize(new {
                    data=new {id=ModId,gameId=GameId,name="Verified ASA mod",
                        summary="Synthetic metadata, not an installed mod",
                        links=new {websiteUrl=Website},
                        logo=new {thumbnailUrl=Thumbnail},
                        latestFiles=new[]{new {displayName="Synthetic latest file"}},
                        dateModified="2026-10-10T00:00:00Z"}
                }),
                _=>throw new InvalidDataException("Unexpected CurseForge endpoint")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent(payload)
            });
        }
    }

    private static void Assert(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(
            "CURSEFORGE EXACT LOOKUP: "+reason);
        Console.WriteLine("PASS CurseForge exact lookup: "+reason);
    }

    public static async Task Run()
    {
        var handler=new OfflineCatalogHandler();
        using var http=new HttpClient(handler);
        var catalog=new CurseForgeCatalog(http);
        var result=await catalog.GetAsaModAsync("CI_APPROVED_KEY_ONLY",928548);
        Assert(result.Id==928548 && result.Name=="Verified ASA mod" &&
            result.LatestFile=="Synthetic latest file" &&
            result.ThumbnailUrl=="https://media.forgecdn.net/avatars/123/456.jpg" &&
            result.Website=="https://www.curseforge.com/ark-survival-ascended/mods/example" &&
            handler.Calls==2,
            "exact ASA project fetch resolves game and project via approved read-only API");

        handler.Thumbnail="https://evil.example/assets/mod.jpg";
        handler.Website="http://curseforge.com/unencrypted";
        var blockedLinks=await catalog.GetAsaModAsync("CI_APPROVED_KEY_ONLY",928548);
        Assert(blockedLinks.Id==928548 &&
            blockedLinks.ThumbnailUrl is null && blockedLinks.Website is null &&
            handler.Calls==3,
            "provider-supplied untrusted thumbnails and non-HTTPS links never render");

        handler.GameId=1;
        bool wrongGameDenied=false;
        try{await catalog.GetAsaModAsync("CI_APPROVED_KEY_ONLY",928548);}
        catch(InvalidDataException){wrongGameDenied=true;}
        Assert(wrongGameDenied,
            "foreign-game project ID is rejected even when provider returns a valid record");
        handler.GameId=4418;

        handler.ModId=999999;
        bool wrongIdDenied=false;
        try{await catalog.GetAsaModAsync("CI_APPROVED_KEY_ONLY",928548);}
        catch(InvalidDataException){wrongIdDenied=true;}
        Assert(wrongIdDenied,
            "provider must return the exact requested ID, not an adjacent project");
        handler.ModId=928548;

        int before=handler.Calls;
        bool invalidIdDenied=false, missingKeyDenied=false;
        try{await catalog.GetAsaModAsync("CI_APPROVED_KEY_ONLY",1);}
        catch(ArgumentOutOfRangeException){invalidIdDenied=true;}
        try{await catalog.GetAsaModAsync("",928548);}
        catch(ArgumentException){missingKeyDenied=true;}
        Assert(invalidIdDenied && missingKeyDenied && handler.Calls==before,
            "invalid IDs and absent approval cannot trigger HTTP requests");

        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        bool cancellationObserved=false;
        try{await catalog.GetAsaModAsync("CI_APPROVED_KEY_ONLY",928548,cancelled.Token);}
        catch(OperationCanceledException){cancellationObserved=true;}
        Assert(cancellationObserved,"cancellation fails closed before fetching project metadata");
    }
}
