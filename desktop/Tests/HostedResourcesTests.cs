using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;

internal static class HostedResourcesTests
{
    internal static async Task RunAsync()
    {
        using var bridge=new DesktopHostedResourcesBridge(allowEphemeral:true);
        async Task<JsonElement> Call(string method,object? args=null)
        {
            var fields=new System.Collections.Generic.Dictionary<string,object?>{{"id",Guid.NewGuid()},{"method",method}};
            if(args!=null)foreach(var item in JsonSerializer.SerializeToElement(args).EnumerateObject())fields[item.Name]=item.Value.Clone();
            using var reply=JsonDocument.Parse(await bridge.HandleAsync(JsonSerializer.Serialize(fields)));
            return reply.RootElement.Clone();
        }
        static void Check(bool value,string label){if(!value)throw new InvalidOperationException("HOSTED RESOURCE: "+label);Console.WriteLine("PASS hosted resources: "+label);}
        string profile=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Obsidian-Resources-"+Guid.NewGuid().ToString("N"));
        try
        {
            async Task<JsonElement> Send(DesktopHostedResourcesBridge service,string method,object? args=null)
            {
                var fields=new System.Collections.Generic.Dictionary<string,object?>{{"id",Guid.NewGuid()},{"method",method}};
                if(args!=null)foreach(var item in JsonSerializer.SerializeToElement(args).EnumerateObject())fields[item.Name]=item.Value.Clone();
                using var response=JsonDocument.Parse(await service.HandleAsync(JsonSerializer.Serialize(fields)));
                return response.RootElement.Clone();
            }
            int restoredPort;
            using(var first=new DesktopHostedResourcesBridge(allowEphemeral:true,profileRoot:profile))
            {
                var saved=await Send(first,"resources.publish",new{kind="BadWordListURL",content="persistence-fixture",port=0,confirm=true});
                Check(saved.GetProperty("ok").GetBoolean(),"durable publish");
                restoredPort=saved.GetProperty("data").GetProperty("port").GetInt32();
                string disk=System.IO.File.ReadAllText(System.IO.Path.Combine(profile,"hosted-resources.dpapi"));
                Check(!disk.Contains("persistence-fixture"),"encrypted disk content");
            }
            using(var reopened=new DesktopHostedResourcesBridge(profileRoot:profile))
            {
                var restored=await Send(reopened,"resources.status");
                Check(restored.GetProperty("data").GetProperty("running").GetBoolean(),"listener restored");
                using var httpRestore=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(5)};
                Check(await httpRestore.GetStringAsync("http://127.0.0.1:"+restoredPort+"/BadWordListURL")=="persistence-fixture","persisted HTTP body");
                Check((await Send(reopened,"resources.stop",new{confirm=true})).GetProperty("ok").GetBoolean(),"durable stop");
            }
            using var stopped=new DesktopHostedResourcesBridge(profileRoot:profile);
            Check(!(await Send(stopped,"resources.status")).GetProperty("data").GetProperty("running").GetBoolean(),"stop persists");
        }
        finally{if(System.IO.Directory.Exists(profile))System.IO.Directory.Delete(profile,true);}
        var initial=await Call("resources.status");
        Check(!initial.GetProperty("data").GetProperty("running").GetBoolean(),"default stopped");
        var denied=await Call("resources.publish",new{kind="File",content="test",port=0,confirm=true});
        Check(!denied.GetProperty("ok").GetBoolean(),"unknown resource refused");
        var reply=await Call("resources.publish",new{kind="BadWordListURL",content="synthetic-word\n",port=0,confirm=true});
        Check(reply.GetProperty("ok").GetBoolean(),"explicit publish starts loopback");
        int port=reply.GetProperty("data").GetProperty("port").GetInt32();
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(5)};
        string root="http://127.0.0.1:"+port;
        Check(await http.GetStringAsync(root+"/BadWordListURL")=="synthetic-word\n","real HTTP body matches");
        using var missing=await http.GetAsync(root+"/missing");
        Check(missing.StatusCode==HttpStatusCode.NotFound,"unknown path not found");
        using var post=await http.PostAsync(root+"/BadWordListURL",new StringContent("change"));
        Check(post.StatusCode==HttpStatusCode.BadRequest,"POST never writes");
        var stop=await Call("resources.stop",new{confirm=true});
        Check(!stop.GetProperty("data").GetProperty("running").GetBoolean()&&stop.GetProperty("data").GetProperty("resources").GetArrayLength()==0,"stop clears resources");
    }
}
