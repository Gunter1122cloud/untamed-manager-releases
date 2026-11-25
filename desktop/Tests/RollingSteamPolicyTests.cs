using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

internal static class RollingSteamPolicyTests
{
    private static void Check(bool ok,string label)
    {
        if(!ok)throw new InvalidOperationException("ROLLING STEAM POLICY: "+label);
        Console.WriteLine("PASS rolling Steam: "+label);
    }
    private static async Task<JsonElement> Call(DesktopRollingSteamSupervisor runner,string method,
        Guid? mapId=null,bool? enabled=null,Guid? runId=null,bool confirm=false)
    {
        using var doc=JsonDocument.Parse(await runner.HandleAsync(JsonSerializer.Serialize(new{
            id=Guid.NewGuid(),method,mapId,enabled,runId,confirm,apiRiskAccepted=false})));
        return doc.RootElement.Clone();
    }
    internal static async Task Run(string root)
    {
        string profile=Path.Combine(root,"rolling-steam-policy");
        await using var cluster=new ClusterService("rolling-steam-policy",profile,
            _=>throw new InvalidOperationException("Unexpected process probe"));
        var map=new LocalMap{Name="Rolling fixture",InstallationPath=Path.Combine(profile,"asa"),
            ExecutablePath=Path.Combine(profile,"asa","ShooterGame","Binaries","Win64","ArkAscendedServer.exe")};
        cluster.AddMap(map);
        int dispatches=0,lanes=0;
        Task<string> Never(string _){dispatches++;throw new InvalidOperationException("Unexpected lifecycle dispatch");}
        using var lifecycle=new DesktopLifecycleBatchBridge(cluster,Never,Never,Never,Never,Never,Never,Never);
        using var steam=new DesktopStoppedSteamUpdateBridge(cluster,execute:(_,_)=>{
            dispatches++;throw new InvalidOperationException("Unexpected SteamCMD");
        });
        using var hostGate=new SemaphoreSlim(1,1);
        DesktopRollingSteamSupervisor New()=>new(cluster,lifecycle,steam,hostGate,
            ()=>{lanes++;return false;},()=>{},()=>false);
        string legacyPath=Path.Combine(cluster.Store.Root,"scheduled-stopped-steam-v1.json");
        string old=JsonSerializer.Serialize(new{Schedules=new[]{new ScheduledStoppedSteamUpdate(map.Id,60,
            new string('A',64),true,DateTimeOffset.UtcNow.AddDays(-1),null,"scheduled",null,null)}});
        File.WriteAllText(legacyPath,old);
        using(var first=New())
        {
            var status=await Call(first,"steam.rollingStatus");
            Check(status.GetProperty("ok").GetBoolean()&&
                status.GetProperty("data").GetProperty("settings").GetArrayLength()==0,
                "new feature defaults disabled and ignores old enabled schedules");
            await first.TickOnceAsync();
            Check(dispatches==0&&lanes==0&&File.ReadAllText(legacyPath)==old,
                "disabled tick never acquires native lane or changes legacy schedule");
            var refused=await Call(first,"steam.rollingSet",map.Id,true,confirm:true);
            Check(!refused.GetProperty("ok").GetBoolean(),"suppressed or unstarted supervisor cannot be enabled");
            var disabled=await Call(first,"steam.rollingSet",map.Id,false,confirm:true);
            Check(disabled.GetProperty("ok").GetBoolean(),"explicit disable writes only new local settings");
        }
        var previous=new DesktopRollingSteamSupervisor.Run(Guid.NewGuid(),map.Id,"running","updating",
            "123456","123457",true,DateTimeOffset.UtcNow.AddMinutes(-20));
        string journal=Path.Combine(cluster.Store.Root,"rolling-steam-v1.json");
        File.WriteAllText(journal,JsonSerializer.Serialize(new{
            settings=new[]{new DesktopRollingSteamSupervisor.Setting(map.Id,true,false,new string('B',64))},
            current=previous},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        string original=File.ReadAllText(journal);
        using(var reopened=New())
        {
            await reopened.TickOnceAsync();
            Check(lanes==0&&dispatches==0&&File.ReadAllText(journal)==original,
                "interrupted persisted run cannot replay on Manager relaunch");
            var denied=await Call(reopened,"steam.rollingReview",runId:Guid.NewGuid(),confirm:true);
            Check(!denied.GetProperty("ok").GetBoolean()&&File.ReadAllText(journal)==original,
                "wrong recovery ID cannot rewrite journal");
            var reviewed=await Call(reopened,"steam.rollingReview",runId:previous.Id,confirm:true);
            Check(reviewed.GetProperty("ok").GetBoolean()&&reviewed.GetProperty("data").GetProperty("noReplay").GetBoolean(),
                "explicit review preserves no-replay outcome");
            var status=await Call(reopened,"steam.rollingStatus");
            Check(!status.GetProperty("data").GetProperty("settings")[0].GetProperty("enabled").GetBoolean()&&
                status.GetProperty("data").GetProperty("current").GetProperty("state").GetString()=="reviewed"&&
                File.Exists(Path.Combine(cluster.Store.Root,"rolling-steam-reviewed-"+previous.Id.ToString("N")+".json")),
                "review archives interrupted receipt and disables future automatic updates");
            await reopened.TickOnceAsync();
            Check(dispatches==0&&lanes==0&&File.ReadAllText(legacyPath)==old,
                "review never starts or stops ASA and leaves retired journal intact");
        }
    }
}
