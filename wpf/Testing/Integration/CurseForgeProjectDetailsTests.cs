using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class CurseForgeProjectDetailsTests
{
    private const string SyntheticKey="SYNTHETIC_APPROVED_CURSEFORGE_DETAILS_ONLY";
    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int Calls;
        public readonly System.Collections.Generic.List<string> Paths=new();
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            string path=request.RequestUri?.AbsolutePath??"";
            Paths.Add(path);
            if(request.Method!=HttpMethod.Get ||
               request.RequestUri?.Host!="api.curseforge.com" ||
               !request.Headers.TryGetValues("x-api-key",out var value) ||
               value.SingleOrDefault()!=SyntheticKey)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            string json=path switch
            {
                "/v1/games" =>
                    """{"data":[{"id":4418,"slug":"ark-survival-ascended","name":"ARK: Survival Ascended"}]}""",
                "/v1/mods/928548" =>
                    """{"data":{"id":928548,"gameId":4418,"name":"Synthetic WPF Mod","summary":"Details preview","dateModified":"2026-11-05T10:00:00Z","authors":[{"name":"Alice"},{"name":"Bob"}],"categories":[{"name":"Structures"}],"logo":{"thumbnailUrl":"https://media.forgecdn.net/test.webp"},"links":{"websiteUrl":"https://www.curseforge.com/ark-survival-ascended/mods/synthetic"},"latestFiles":[{"displayName":"v1.4 synthetic"}]}}""",
                "/v1/mods/928548/description" =>
                    """{"data":"<p>Supports <strong>ASA</strong> &amp; servers.</p><script>alert(1)</script><style>unsafe css</style><iframe>tracker</iframe>"}""",
                "/v1/mods/928548/files" =>
                    """{"data":[{"id":14001,"modId":928548,"displayName":"Synthetic 1.4.0","fileDate":"2026-10-20T10:30:00Z","fileLength":10485760,"isAvailable":true,"gameVersions":["ASA Windows","1.4.0"],"dependencies":[{"modId":920001,"relationType":3},{"modId":920002,"relationType":2},{"modId":920003,"relationType":5}]}],"pagination":{"totalCount":1}}""",
                _ => """{"data":[]}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")
            });
        }
    }
    private static void Check(bool valid,string label)
    {
        if(!valid)throw new InvalidOperationException("CURSEFORGE DETAILS: "+label);
        Console.WriteLine("PASS CurseForge details: "+label);
    }
    public static async Task Run()
    {
        var handler=new FixtureHandler();
        using var http=new HttpClient(handler);
        var api=new CurseForgeCatalog(http);
        var info=await api.GetAsaModAsync(SyntheticKey,928548);
        Check(info.Id==928548 && info.Authors.SequenceEqual(new[]{"Alice","Bob"}) &&
            info.Categories.SequenceEqual(new[]{"Structures"}) &&
            info.ModifiedUtc.HasValue && info.ThumbnailUrl!=null,
            "single official ASA project resolves authors, category, date and allowlisted thumbnail");

        string text=await api.GetModDescriptionTextAsync(SyntheticKey,928548);
        Check(text.Contains("Supports",StringComparison.Ordinal) &&
            text.Contains("ASA",StringComparison.Ordinal) &&
            text.Contains("& servers",StringComparison.Ordinal) &&
            !text.Contains("alert(1)",StringComparison.Ordinal) &&
            !text.Contains("unsafe css",StringComparison.Ordinal) &&
            !text.Contains("<strong>",StringComparison.Ordinal),
            "full provider HTML description converted to inert non-script WPF text");

        var files=await api.GetModFilesAsync(SyntheticKey,928548);
        Check(files.ModId==928548 && files.Files.Count==1 &&
            files.Files[0].SizeBytes==10_485_760 &&
            files.Files[0].GameVersions.Contains("ASA Windows") &&
            files.Files[0].Relations.Count==3 &&
            files.Files[0].Dependencies.SequenceEqual(new[]{920001}) &&
            files.Files[0].Relations.Any(x=>x.Type==CurseForgeRelationType.OptionalDependency) &&
            files.Files[0].Relations.Any(x=>x.Type==CurseForgeRelationType.Incompatible),
            "file versions preserve required/optional/incompatible dependency distinctions");

        Check(handler.Paths.All(x=>!x.Contains("download",StringComparison.OrdinalIgnoreCase)) &&
            handler.Calls==4,
            "mod detail inspection remains strictly read-only and requires no file download");

        bool invalid=false;
        try{await api.GetModDescriptionTextAsync(SyntheticKey,0);}
        catch(ArgumentOutOfRangeException){invalid=true;}
        Check(invalid && handler.Calls==4,
            "invalid project IDs are rejected without a network request");

        bool notTrusted=false;
        try{await api.GetAsaModAsync("WRONG_SYNTHETIC_KEY",928548);}
        catch(UnauthorizedAccessException){notTrusted=true;}
        Check(notTrusted,"provider denial is not mislabeled as a verified CurseForge project");
    }
}
