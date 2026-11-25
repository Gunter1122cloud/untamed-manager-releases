using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;

internal static class PlayerActivityNativeTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"obsidian-activity-"+Guid.NewGuid().ToString("N"));
        Guid map=Guid.NewGuid();
        const string player="0123456789abcdef";
        int pid=11;bool fail=false;long utc=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-45000;
        Task<string> Reply(string raw)
        {
            using var request=JsonDocument.Parse(raw);
            if(request.RootElement.GetProperty("method").GetString()!="rcon.listPlayers")throw new Exception("Unexpected command");
            return Task.FromResult(JsonSerializer.Serialize(new{ok=!fail,data=new{mapId=map,command="ListPlayers",readOnly=true,
                processPid=pid,processStartedUtc=DateTimeOffset.UtcNow.Date.AddDays(-1),observedUtc=DateTimeOffset.FromUnixTimeMilliseconds(utc),
                response="1. synthetic-player, "+player}}));
        }
        async Task<JsonElement> Call(DesktopPlayerActivityBridge b,string method,bool enabled=false)
        {
            using var r=JsonDocument.Parse(await b.HandleAsync(JsonSerializer.Serialize(new{id=Guid.NewGuid(),method,confirm=true,enabled})));
            if(!r.RootElement.GetProperty("ok").GetBoolean())throw new Exception("Activity request failed");
            return r.RootElement.GetProperty("data").Clone();
        }
        void Check(bool ok,string label){if(!ok)throw new Exception("ACTIVITY: "+label);Console.WriteLine("PASS native activity: "+label);}
        try
        {
            using(var bridge=new DesktopPlayerActivityBridge(root,Reply,Reply))
            {
                Check(!(await Call(bridge,"activity.status")).GetProperty("enabled").GetBoolean(),"opt-in default off");
                await Call(bridge,"activity.setEnabled",true);
                await bridge.PollAsync(new[]{(map,false)});
                utc+=10000;await bridge.PollAsync(new[]{(map,false)});
                var record=(await Call(bridge,"activity.status")).GetProperty("records")[0];
                Check(record.GetProperty("seconds").GetDouble()==10,"consecutive verified interval");
                pid++;utc+=10000;await bridge.PollAsync(new[]{(map,false)});
                Check((await Call(bridge,"activity.status")).GetProperty("records")[0].GetProperty("seconds").GetDouble()==10,"new PID breaks continuity");
                fail=true;utc+=10000;await bridge.PollAsync(new[]{(map,false)});
            }
            using(var reopened=new DesktopPlayerActivityBridge(root,Reply,Reply))
            {
                var status=await Call(reopened,"activity.status");
                Check(status.GetProperty("enabled").GetBoolean()&&status.GetProperty("records")[0].GetProperty("seconds").GetDouble()==10,"durable opt-in/history");
                fail=false;utc+=10000;await reopened.PollAsync(new[]{(map,false)});
                Check((await Call(reopened,"activity.status")).GetProperty("records")[0].GetProperty("seconds").GetDouble()==10,"restart never counts gap");
                await Call(reopened,"activity.clear");
                Check((await Call(reopened,"activity.status")).GetProperty("records").GetArrayLength()==0,"explicit clear");
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
