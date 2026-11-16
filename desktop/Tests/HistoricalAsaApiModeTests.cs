using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// Real native mode-persistence route against an isolated synthetic historical
// profile. Does not create an ASA process, RCON listener, Store writer or SQL.
internal static class HistoricalAsaApiModeTests
{
    private static void Check(bool pass,string what)
    {
        if(!pass)throw new InvalidOperationException(
            "HISTORICAL ASA API MODE REGRESSION FAILED: "+what);
        Console.WriteLine("PASS historical per-map ASA API: "+what);
    }

    private static async Task<JsonElement> Call(
        DesktopHistoricalActiveStartBridge bridge,string method,object? args=null)
    {
        var request=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(args!=null)
        {
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(args));
            foreach(var field in doc.RootElement.EnumerateObject())
                request[field.Name]=field.Value.Clone();
        }
        using var result=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(request)));
        return result.RootElement.Clone();
    }

    public static async Task Run(string root)
    {
        string installation=Path.Combine(root,"historical-api-mode-without-rcon");
        string binary=Path.Combine(installation,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binary);
        string server=Path.Combine(binary,"ArkAscendedServer.exe");
        File.WriteAllBytes(server,new byte[]{0x4d,0x5a,0x90,0x00});
        string profile=Path.Combine(root,"historical-api-mode-manager");
        await using var cluster=new ClusterService("historical-api-mode-ci",profile,
            _=>new ProcessCheck(false,"synthetic stopped",null,true));
        var map=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",InstallationPath=installation,
            ExecutablePath=server,HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('A',64),
            HistoricalMapKey="TheIsland_WP",
            GamePort=7777,QueryPort=27015,RconPort=0,
            Arguments="",ApiLoaderArguments="",
            StartWithAsaApi=false,
            AutoStart=false,MonitorRcon=false
        };
        cluster.AddMap(map);
        // Exact process/PE64 checks belong to production PhysicalModePreflight.
        // Only the explicit stopped-family check is injected for this fixture.
        using var bridge=new DesktopHistoricalActiveStartBridge(
            cluster,null,null,loaderModePreflight:(_,_)=>true);
        // The actual Core Configuration lease transitions its own journal
        // to Prepared. It must not be mistaken for another Manager's
        // unfinished operation during the second physical mode preflight.
        var ownLeaseGuard=typeof(DesktopHistoricalActiveStartBridge).GetMethod(
            "ModeOperationJournalAllows",
            System.Reflection.BindingFlags.NonPublic|
            System.Reflection.BindingFlags.Instance)??throw new InvalidOperationException(
                "Historical Loader own-lease preflight guard missing");
        using(var lease=cluster.Operations.Acquire(
            map.InstallationPath,UntamedOperationKind.Configuration))
        {
            bool outside=(bool)(ownLeaseGuard.Invoke(bridge,new object[]{map,false}) ??
                throw new InvalidOperationException("No pre-lease journal result"));
            bool inside=(bool)(ownLeaseGuard.Invoke(bridge,new object[]{map,true}) ??
                throw new InvalidOperationException("No inside-lease journal result"));
            Check(!outside&&inside,
                "own Prepared Configuration journal blocks foreign preview but not approved under-lease mode apply");
            lease.Complete();
        }
        var status=await Call(bridge,"historical.loaderModeStatus",
            new{mapId=map.Id});
        Check(status.GetProperty("ok").GetBoolean()&&
              status.GetProperty("data").GetProperty("canChangeMode").GetBoolean()&&
              !status.GetProperty("data").GetProperty("useApi").GetBoolean(),
            "original Store-v1 mode status is editable with RCONPort=0");

        var preview=await Call(bridge,"historical.loaderModePreview",
            new{mapId=map.Id,useApi=true});
        Check(preview.GetProperty("ok").GetBoolean()&&
              !map.StartWithAsaApi,
            "preview does not mutate historical map or launch ASA");
        string token=preview.GetProperty("data")
            .GetProperty("token").GetString()!;
        var committed=await Call(bridge,"historical.loaderModeApply",
            new{token,confirm=true});
        Check(committed.GetProperty("ok").GetBoolean()&&
              cluster.Config.LocalMaps.Single().StartWithAsaApi&&
              cluster.Config.LocalMaps.Single().RconPort==0,
            "one-shot approval persists ASA API preference without RCON");
        var replay=await Call(bridge,"historical.loaderModeApply",
            new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "historical mode approval cannot be replayed");
        var deniedStart=await Call(bridge,"historical.activeStartPreview",
            new{mapId=map.Id});
        Check(!deniedStart.GetProperty("ok").GetBoolean(),
            "missing real RCON still blocks Start even after choosing API");

        // A harmless Loader-mode preview is NOT an approved launch-mode
        // mutation and therefore must not consume an original Start token.
        string secondInstallation=Path.Combine(root,"historical-api-mode-start-preview");
        string secondServer=Path.Combine(secondInstallation,"ShooterGame",
            "Binaries","Win64","ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(secondServer)!);
        File.WriteAllBytes(secondServer,new byte[]{0x4d,0x5a,0x90,0x00});
        var restartable=new LocalMap{
            Id=Guid.NewGuid(),Name="Ragnarok",InstallationPath=secondInstallation,
            ExecutablePath=secondServer,HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('B',64),
            HistoricalMapKey="Ragnarok_WP",GamePort=7837,QueryPort=27077,
            RconPort=37075,RconPasswordProtected=SecureStore.Protect("SYNTHETIC_RCON_ONLY"),
            Arguments="",ApiLoaderArguments="",StartWithAsaApi=false,
            AutoStart=false,MonitorRcon=false
        };
        cluster.AddMap(restartable);
        bool allowModeCommit=true;
        using var previewBridge=new DesktopHistoricalActiveStartBridge(cluster,
            preparation:(_)=>new LegacyStoreV1NativeStartPlan(
                "Ragnarok_WP?listen -Port=7837 -QueryPort=27077 -usestore",
                new string('A',64),7837,27077,37075,"SYNTHETIC_ONLY"),
            launcher:(_,_,_)=>throw new InvalidOperationException(
                "SYNTHETIC START MUST NEVER BE EXECUTED"),
            loaderModePreflight:(_,_)=>allowModeCommit);
        var startPreview=await Call(previewBridge,"historical.activeStartPreview",
            new{mapId=restartable.Id});
        Check(startPreview.GetProperty("ok").GetBoolean(),
            "synthetic native original Start approval is issued without Process.Start");
        string startToken=startPreview.GetProperty("data")
            .GetProperty("token").GetString()!;
        var modePreview=await Call(previewBridge,"historical.loaderModePreview",
            new{mapId=restartable.Id,useApi=true});
        Check(modePreview.GetProperty("ok").GetBoolean(),
            "uncommitted Loader selection previews without mutating source");
        var firstUnconfirmedStart=await Call(previewBridge,"historical.activeStartApply",
            new{token=startToken,confirm=false,typedApproval="STORE-V1 ACTIVE START"});
        Check(firstUnconfirmedStart.GetProperty("error").GetString()==
              "UM-STORE-ACTIVE-EXPLICIT-APPROVAL",
            "Loader mode preview did not revoke original Start approval");
        var modeToken=modePreview.GetProperty("data").GetProperty("token").GetString()!;
        // Refuse a stale mode transition after preview. The Start ticket
        // must still exist: there was NO committed change to launch mode.
        allowModeCommit=false;
        var rejectedMode=await Call(previewBridge,"historical.loaderModeApply",
            new{token=modeToken,confirm=true});
        Check(!rejectedMode.GetProperty("ok").GetBoolean()&&
              rejectedMode.GetProperty("error").GetString()==
                "UM-STORE-LOADER-MODE-STATE-CHANGED",
            "stale Loader-mode approval is rejected before profile mutation");
        var retainedStart=await Call(previewBridge,"historical.activeStartApply",
            new{token=startToken,confirm=false,typedApproval="STORE-V1 ACTIVE START"});
        Check(retainedStart.GetProperty("error").GetString()==
                "UM-STORE-ACTIVE-EXPLICIT-APPROVAL",
            "rejected Loader mode apply preserves unchanged approved Start ticket");
        allowModeCommit=true;
        var newMode=await Call(previewBridge,"historical.loaderModePreview",
            new{mapId=restartable.Id,useApi=true});
        Check(newMode.GetProperty("ok").GetBoolean(),
            "new review is available after a rejected Loader transition");
        var freshToken=newMode.GetProperty("data").GetProperty("token").GetString()!;
        var change=await Call(previewBridge,"historical.loaderModeApply",
            new{token=freshToken,confirm=true});
        Check(change.GetProperty("ok").GetBoolean(),
            "committed Loader choice changes only historical Manager preference");
        var oldStart=await Call(previewBridge,"historical.activeStartApply",
            new{token=startToken,confirm=false,typedApproval="STORE-V1 ACTIVE START"});
        Check(oldStart.GetProperty("error").GetString()==
              "UM-STORE-ACTIVE-APPROVAL-EXPIRED",
            "actual committed Loader mode change revokes stale old-mode Start token");
    }
}
