using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

/// <summary>
/// Real DesktopAsaApiCoreBridge with an in-memory HTTP upstream and two
/// separately registered, synthetic Windows installation directories.
/// Inspect its private one-shot approvals only; never apply a binary,
/// launch ASA, contact GitHub or open a TCP RCON socket.
/// </summary>
internal static class AsaApiCoreMapApprovalTests
{
    private static void Check(bool assertion,string description)
    {
        if(!assertion)throw new InvalidOperationException(
            "ASA API CORE MAP APPROVAL: "+description);
        Console.WriteLine("PASS API CORE map approval: "+description);
    }

    private sealed class OfficialCatalogFixture : HttpMessageHandler
    {
        private static readonly string Json=JsonSerializer.Serialize(new
        {
            tag_name="2.03",
            html_url="https://github.com/ArkServerApi/AsaApi/releases/tag/2.03",
            published_at="2026-11-01T12:00:00Z",
            draft=false,
            prerelease=false,
            assets=new[]{
                new {
                    name="AsaApiSynthetic.zip",
                    browser_download_url="https://github.com/ArkServerApi/AsaApi/releases/download/2.03/AsaApiSynthetic.zip",
                    size=1024,
                    digest="sha256:"+new string('A',64)
                }
            }
        });
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken cancellationToken)
        {
            if(request.Method!=HttpMethod.Get||
               request.RequestUri?.AbsoluteUri!=AsaApiReleaseCatalog.ApiUri)
                throw new InvalidOperationException(
                    "synthetic CORE fixture refuses non-catalog HTTP");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent(Json,Encoding.UTF8,"application/json")
            });
        }
    }

    private static async Task<JsonElement> Call(DesktopAsaApiCoreBridge bridge,
        string method,object args)
    {
        using var parameters=JsonDocument.Parse(JsonSerializer.Serialize(args));
        var request=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        foreach(var pair in parameters.RootElement.EnumerateObject())
            request[pair.Name]=pair.Value.Clone();
        using var reply=JsonDocument.Parse(
            await bridge.HandleAsync(JsonSerializer.Serialize(request)));
        return reply.RootElement.Clone();
    }

    private static string Token(DesktopAsaApiCoreBridge bridge,Guid mapId)
    {
        var field=typeof(DesktopAsaApiCoreBridge).GetField(
            "pendingByMap",BindingFlags.Instance|BindingFlags.NonPublic);
        Check(field!=null,"native CORE approval dictionary is present");
        var cache=(IDictionary)field!.GetValue(bridge)!;
        Check(cache.Contains(mapId),"map has a separate native CORE approval");
        object approval=cache[mapId]!;
        return approval.GetType().GetProperty("Token")!.GetValue(approval)!.ToString()!;
    }

    public static async Task Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"native-api-core-map-approval-fixture");
        Directory.CreateDirectory(root);
        LocalMap[] maps=new LocalMap[2];
        for(int i=0;i<maps.Length;i++)
        {
            string install=Path.Combine(root,"independent-core-map-"+i);
            string binary=Path.Combine(install,"ShooterGame","Binaries",
                "Win64","ArkAscendedServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllBytes(binary,new byte[]{0x4d,0x5a,0x90,0x00});
            maps[i]=new LocalMap{
                Id=Guid.NewGuid(),Name="Synthetic CORE map "+i,
                InstallationPath=install,ExecutablePath=binary,
                Arguments="TheIsland_WP?listen -port="+(7950+i)+
                    " -QueryPort="+(27150+i),
                GamePort=7950+i,QueryPort=27150+i
            };
        }
        await using var cluster=new ClusterService("api-core-map-approval",
            Path.Combine(root,"isolated-manager-profile"),
            _=>new ProcessCheck(false,"synthetic confirmed stopped",null,true));
        foreach(var map in maps)cluster.AddMap(map);
        using var http=new HttpClient(new OfficialCatalogFixture());
        using var bridge=new DesktopAsaApiCoreBridge(cluster,http);
        var first=await Call(bridge,"api.corePreview",
            new{mapId=maps[0].Id,assetName="AsaApiSynthetic.zip"});
        Check(first.GetProperty("ok").GetBoolean(),
            "first local map gets verified official synthetic catalog preview");
        string tokenA=first.GetProperty("data").GetProperty("token").GetString()!;
        Check(Token(bridge,maps[0].Id)==tokenA,
            "native CORE cache holds first map's exact token");
        var rejected=await Call(bridge,"api.corePreview",
            new{mapId=maps[0].Id,assetName="NotOfficial.zip"});
        Check(!rejected.GetProperty("ok").GetBoolean()&&
              Token(bridge,maps[0].Id)==tokenA,
            "rejected upstream asset cannot silently revoke existing approval");
        var second=await Call(bridge,"api.corePreview",
            new{mapId=maps[1].Id,assetName="AsaApiSynthetic.zip"});
        Check(second.GetProperty("ok").GetBoolean(),
            "second independent local map has an official synthetic preview");
        string tokenB=second.GetProperty("data").GetProperty("token").GetString()!;
        Check(tokenA!=tokenB&&
              Token(bridge,maps[0].Id)==tokenA&&
              Token(bridge,maps[1].Id)==tokenB,
            "two map approvals coexist without cross-map invalidation");
        var replaced=await Call(bridge,"api.corePreview",
            new{mapId=maps[0].Id,assetName="AsaApiSynthetic.zip"});
        Check(replaced.GetProperty("ok").GetBoolean()&&
              Token(bridge,maps[0].Id)!=tokenA&&
              Token(bridge,maps[1].Id)==tokenB,
            "successful same-map replacement updates only its own approval");
        // These tests deliberately NEVER call api.coreApply/rollbackApply:
        // metadata preview does not authorize a mutation or contact live ASA.
    }
}
