using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

internal static class FreshStoreEnrollmentTests
{
    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException("FRESH STORE TEST FAILED: "+message);
        Console.WriteLine("PASS new empty Store: "+message);
    }

    private static async Task<JsonElement> Call(DesktopFreshStoreEnrollmentBridge bridge,
        string method,object? parameters=null)
    {
        var request=new System.Collections.Generic.Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(parameters!=null)
            foreach(var entry in JsonSerializer.SerializeToElement(parameters).EnumerateObject())
                request[entry.Name]=entry.Value.Clone();
        using var response=JsonDocument.Parse(
            await bridge.HandleAsync(JsonSerializer.Serialize(request)));
        return response.RootElement.Clone();
    }
    private static async Task<JsonElement> CallStart(DesktopFreshAsaStartBridge bridge,
        string method,object? parameters=null)
    {
        var request=new System.Collections.Generic.Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(parameters!=null)
            foreach(var entry in JsonSerializer.SerializeToElement(parameters).EnumerateObject())
                request[entry.Name]=entry.Value.Clone();
        using var response=JsonDocument.Parse(
            await bridge.HandleAsync(JsonSerializer.Serialize(request)));
        return response.RootElement.Clone();
    }

    private static void Reject(Action operation,string reason)
    {
        bool denied=false;
        try{operation();}
        catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException){denied=true;}
        Check(denied,reason);
    }

    public static async Task Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"new-store-empty-synthetic");
        string installation=Path.Combine(root,"fresh-asa");
        string binary=Path.Combine(installation,"ShooterGame","Binaries","Win64");
        string transfer=Path.Combine(root,"synthetic transfer with spaces");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(transfer);
        File.WriteAllBytes(Path.Combine(binary,"ArkAscendedServer.exe"),new byte[]{77,90,7,8});
        string apps=Path.Combine(installation,"steamapps");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps,"appmanifest_2430930.acf"),
            "\"AppState\"\r\n{\r\n\"appid\" \"2430930\"\r\n\"buildid\" \"980812\"\r\n}\r\n");
        string profile=Path.Combine(root,"manager");
        Guid mapId=Guid.Parse("11111111-2222-4333-8444-555555555555");
        await using(var manager=new ClusterService("new-store-synthetic",profile,
            _=>new ProcessCheck(false,"Synthetic confirmed stopped",null,true)))
        {
            var map=new LocalMap{
                Id=mapId,Name="The Island",InstallationPath=installation,
                ExecutablePath=Path.Combine(binary,"ArkAscendedServer.exe"),
                GamePort=7851,QueryPort=27551,RconPort=37551,
                Arguments=AsaSteamMapCatalog.LaunchArguments(
                    "The Island","Synthetic Store Map",7851,27551),
                AutoStart=false
            };
            const string syntheticAdminPassword="SyntheticOnlyAdmin2026!";
            AsaInitialRconSettings.CreateForNewServer(
                installation,37551,syntheticAdminPassword);
            map.RconPasswordProtected=SecureStore.Protect(syntheticAdminPassword);
            manager.AddMap(map);
            Check(AsaReadyProbe.IsRegisteredServerBinary(map)&&
                  AsaRconConfigurationEvidence.Matches(map)&&
                  AsaSteamBuildCatalog.InstalledBuild(installation)=="980812",
                "isolated SteamCMD fixture has a registered ASA EXE and exact build manifest");
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(manager,map,"new-local-cluster"),
                "a new map cannot silently enroll before local cluster creation and assignment");
            manager.Registry.CreateCluster("new-local-cluster","Synthetic New Store",transfer);
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(manager,map,"new-local-cluster"),
                "cluster creation alone cannot implicitly assign a local map");
            var pending=manager.Registry.PreviewMapAssignments(
                "new-local-cluster",new[]{mapId});
            manager.Registry.ApplyMapAssignments(
                "new-local-cluster",new[]{mapId},pending.BaseRevision);
            var missingRcon=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(map))!;
            missingRcon.RconPasswordProtected="";
            manager.ReplaceMap(map,missingRcon);
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(
                manager,missingRcon,"new-local-cluster"),
                "RCON/INI must be configured before new Store enrollment, never repaired after a first Start");
            manager.ReplaceMap(missingRcon,map);
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(manager,map,"KovaCluster"),
                "new Store map cannot enroll directly into protected historical KovaCluster");
            var plan=FreshStoreEnrollmentPolicy.Prepare(manager,map,"new-local-cluster");
            Check(plan.Arguments.Contains(" -usestore",StringComparison.Ordinal)&&
                  plan.Arguments.CountAt("-usestore")==1&&
                  !plan.Arguments.Contains("converttostore",StringComparison.OrdinalIgnoreCase)&&
                  plan.Arguments.Contains("-clusterid=new-local-cluster")&&
                  plan.Arguments.Contains("-ClusterDirOverride=\""+transfer+"\"",
                      StringComparison.Ordinal)&&
                  plan.InstalledBuild=="980812"&&plan.RegistryRevision>0,
                "new empty Store launch uses existing cluster metadata, exactly one -usestore, zero conversion");
            string untouched=map.Arguments;
            using(var bridge=new DesktopFreshStoreEnrollmentBridge(manager))
            {
                var state=await Call(bridge,"freshStore.enrollmentStatus",new{mapId});
                Check(state.GetProperty("ok").GetBoolean()&&
                      !state.GetProperty("data").GetProperty("enrolled").GetBoolean(),
                    "new empty Store native status is read-only and not yet enrolled");
                var preview=await Call(bridge,"freshStore.enrollmentPreview",
                    new{mapId,clusterId="new-local-cluster"});
                Check(preview.GetProperty("ok").GetBoolean()&&
                      !preview.GetProperty("data").GetProperty("serverStarted").GetBoolean()&&
                      !preview.GetRawText().Contains(transfer,StringComparison.OrdinalIgnoreCase),
                    "native preview exposes neither transfer path nor a fictional server Start");
                var token=preview.GetProperty("data").GetProperty("token").GetString()!;
                var notApproved=await Call(bridge,"freshStore.enrollmentApply",
                    new{token,confirm=true,typedApproval="NO"});
                Check(!notApproved.GetProperty("ok").GetBoolean()&&
                      map.Arguments==untouched,
                    "wrong approval phrase preserves exact source launch profile");
                var saved=await Call(bridge,"freshStore.enrollmentApply",
                    new{token,confirm=true,typedApproval="NEW EMPTY STORE"});
                Check(saved.GetProperty("ok").GetBoolean()&&
                      !saved.GetProperty("data").GetProperty("serverStarted").GetBoolean(),
                    "approved native Store enrollment saves only local Manager metadata");
                var replay=await Call(bridge,"freshStore.enrollmentApply",
                    new{token,confirm=true,typedApproval="NEW EMPTY STORE"});
                Check(!replay.GetProperty("ok").GetBoolean(),
                    "successful native approval cannot be replayed");
                var untrusted=await Call(bridge,"freshStore.executeConversion",
                    new{mapId,confirm=true});
                Check(!untrusted.GetProperty("ok").GetBoolean(),
                    "the bridge offers no conversion, remote write or arbitrary Store command");
            }
            var enrolled=manager.Config.LocalMaps.Single(x=>x.Id==mapId);
            FreshStoreEnrollmentPolicy.DemandEnrolled(manager,enrolled);
            Check(enrolled.FreshStoreClusterId=="new-local-cluster"&&
                  enrolled.Arguments.EndsWith(" -usestore",StringComparison.Ordinal)&&
                  enrolled.HistoricalArchiveSha256==""&&
                  enrolled.HistoricalMapKey==""&&
                  !enrolled.AutoStart&&
                  UntamedWorldBackup.HasLiveOrHistoricalStore(enrolled),
                "new Store is durable and distinct from Kova archive, conversion and autostart");
            Check(enrolled.FreshStoreTransferFileId.Length==64,
                "new Store enrollment durably pins the actual Windows transfer directory file ID");
            var aliasSwap=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(enrolled))!;
            aliasSwap.FreshStoreTransferFileId=new string('F',64);
            manager.ReplaceMap(enrolled,aliasSwap);
            Reject(()=>FreshStoreEnrollmentPolicy.DemandEnrolled(manager,aliasSwap),
                "changed physical transfer-directory fingerprint blocks Store process authorization");
            manager.ReplaceMap(aliasSwap,enrolled);
            Check(manager.Check(enrolled).ConfirmedStopped&&
                  !manager.Check(enrolled).Running,
                "new Store map uses local native process observation, not historical Kova guard");
            // Start preview/approval is exercised with a synthetic launcher.
            // No ASA process is spawned, and the result deliberately does
            // not certify RCON readiness, characters, or cross-host travel.
            int syntheticStartCalls=0;
            using(var startBridge=new DesktopFreshAsaStartBridge(manager,
                (id,approved)=>{
                    syntheticStartCalls++;
                    Check(id==mapId&&approved,
                        "new Store Start routes to exactly the selected local map");
                    return new ProcessIdentity{
                        Pid=65217,StartedUtc=DateTimeOffset.UtcNow,
                        ExecutablePath=enrolled.ExecutablePath,
                        Arguments=enrolled.Arguments
                    };
                }))
            {
                var pre=await CallStart(startBridge,
                    "servers.freshStartPreview",new{mapId});
                Check(pre.GetProperty("ok").GetBoolean()&&
                      !pre.GetProperty("data").GetProperty("serverStarted").GetBoolean()&&
                      syntheticStartCalls==0,
                    "new Store Start preview is guarded and has no process side effect");
                string approvedToken=pre.GetProperty("data")
                    .GetProperty("token").GetString()!;
                var refused=await CallStart(startBridge,
                    "servers.freshStartApply",new{token=approvedToken,confirm=false});
                Check(!refused.GetProperty("ok").GetBoolean()&&syntheticStartCalls==0,
                    "unapproved new Store Start dispatches no command");
                var accepted=await CallStart(startBridge,
                    "servers.freshStartApply",new{token=approvedToken,confirm=true});
                Check(accepted.GetProperty("ok").GetBoolean()&&
                      accepted.GetProperty("data").GetProperty("pid").GetInt32()==65217&&
                      !accepted.GetProperty("data").GetProperty("readyCertified").GetBoolean()&&
                      syntheticStartCalls==1,
                    "new Store Start can reach approved native lifecycle without claiming ASA readiness");
                var replay=await CallStart(startBridge,
                    "servers.freshStartApply",new{token=approvedToken,confirm=true});
                Check(!replay.GetProperty("ok").GetBoolean()&&syntheticStartCalls==1,
                    "new Store Start approval token is strictly one shot");
            }
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(manager,enrolled,"new-local-cluster"),
                "enrolled Store cannot be enrolled or converted again");
            var profileChange=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(enrolled))!;
            profileChange.Arguments=profileChange.Arguments.Replace(
                "-usestore","-converttostore",StringComparison.Ordinal);
            manager.ReplaceMap(enrolled,profileChange);
            Reject(()=>FreshStoreEnrollmentPolicy.DemandEnrolled(manager,profileChange),
                "conversion flag cannot be smuggled into an already enrolled map");
            manager.ReplaceMap(profileChange,enrolled);
            manager.Registry.CreateCluster("second-local-cluster","Other Synthetic Store",
                Path.Combine(root,"other-transfer"));
            var reassignment=manager.Registry.PreviewMapAssignments(
                "second-local-cluster",new[]{mapId});
            manager.Registry.ApplyMapAssignments(
                "second-local-cluster",new[]{mapId},reassignment.BaseRevision);
            Reject(()=>FreshStoreEnrollmentPolicy.DemandEnrolled(manager,enrolled),
                "later cluster membership change invalidates original Store writer authorization");
        }
        await using(var restarted=new ClusterService("new-store-synthetic",profile,
            _=>new ProcessCheck(false,"Synthetic stopped",null,true)))
        {
            var saved=restarted.Config.LocalMaps.Single(x=>x.Id==mapId);
            Check(saved.FreshStoreClusterId=="new-local-cluster"&&
                  saved.HistoricalMapKey==""&&
                  restarted.Config.ClusterRegistry.MapAssignments.Single(x=>
                      x.MapId==mapId).ClusterId=="second-local-cluster",
                "restart preserves the exact pending mismatch, never silently rewrites historical Store metadata");
            Reject(()=>FreshStoreEnrollmentPolicy.DemandEnrolled(restarted,saved),
                "reloaded Manager still blocks a stale cluster assignment");
        }

        string previouslyUsed=Path.Combine(root,"used-install");
        string usedBinary=Path.Combine(previouslyUsed,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(usedBinary);
        File.WriteAllBytes(Path.Combine(usedBinary,"ArkAscendedServer.exe"),new byte[]{77,90});
        Directory.CreateDirectory(Path.Combine(previouslyUsed,"steamapps"));
        File.Copy(Path.Combine(apps,"appmanifest_2430930.acf"),
            Path.Combine(previouslyUsed,"steamapps","appmanifest_2430930.acf"));
        string saves=Path.Combine(previouslyUsed,"ShooterGame","Saved",
            "SavedArks","TheIsland_WP");
        Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves,"TheIsland_WP.ark"),Array.Empty<byte>());
        await using(var usedManager=new ClusterService("new-store-used-denial",
            Path.Combine(root,"used-profile"),
            _=>new ProcessCheck(false,"Synthetic stopped",null,true)))
        {
            var usedMap=new LocalMap{
                Id=Guid.NewGuid(),Name="The Island",
                InstallationPath=previouslyUsed,
                ExecutablePath=Path.Combine(usedBinary,"ArkAscendedServer.exe"),
                GamePort=7852,QueryPort=27552,
                Arguments=AsaSteamMapCatalog.LaunchArguments(
                    "The Island","Previously Used World",7852,27552)
            };
            usedManager.AddMap(usedMap);
            usedManager.Registry.CreateCluster("used-denied","Used transfer",transfer);
            var assigned=usedManager.Registry.PreviewMapAssignments(
                "used-denied",new[]{usedMap.Id});
            usedManager.Registry.ApplyMapAssignments(
                "used-denied",new[]{usedMap.Id},assigned.BaseRevision);
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(
                usedManager,usedMap,"used-denied"),
                "a zero-byte existing world is protected, not treated as an unused Store map");
        }
    }
}

internal static class FreshStoreTestStringExtensions
{
    public static int CountAt(this string value,string token)=>
        value.Split(token,StringSplitOptions.None).Length-1;
}
