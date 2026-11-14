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


    private static async Task<JsonElement> CallMode(DesktopFreshStoreLoaderModeBridge bridge,
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
    // Synthetic PE64 HEADER ONLY, never a runnable executable.
    private static void WriteSyntheticPe64(string path)
    {
        byte[] data=new byte[512];
        data[0]=0x4d;data[1]=0x5a;data[0x3c]=0x80;
        data[0x80]=0x50;data[0x81]=0x45;
        data[0x84]=0x64;data[0x85]=0x86;
        data[0x98]=0x0b;data[0x99]=0x02;
        File.WriteAllBytes(path,data);
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
        // The shared Start/Restart monitor accepts only a verified observed
        // process pair and an independently owned child RCON port. Neither
        // the test nor the monitor starts a Windows/ASA process here.
        var syntheticStarted=DateTimeOffset.UtcNow.AddSeconds(-40);
        var syntheticParent=new ProcessIdentity{
            Pid=57102,StartedUtc=syntheticStarted,
            ExecutablePath=@"C:\Synthetic\AsaApiLoader.exe",
            Arguments="TheIsland_WP -usestore"
        };
        var syntheticChild=new ProcessIdentity{
            Pid=57103,StartedUtc=syntheticStarted.AddSeconds(7),
            ExecutablePath=@"C:\Synthetic\ArkAscendedServer.exe",
            Arguments="TheIsland_WP -usestore"
        };
        var syntheticObserved=new ProcessCheck(true,"Verified synthetic family",
            syntheticChild,false,syntheticParent);
        Check(FreshStoreApiPairObserver.MatchesVerifiedPair(
                syntheticObserved,syntheticParent,syntheticChild.ExecutablePath,
                syntheticChild.Arguments,37551,
                (port,pid)=>port==37551&&pid==syntheticChild.Pid),
            "shared Start/Restart API pair observer requires exact physical parent and child-owned RCON");
        var reusedParent=new ProcessIdentity{
            Pid=syntheticParent.Pid,StartedUtc=syntheticStarted.AddSeconds(1),
            ExecutablePath=syntheticParent.ExecutablePath,Arguments=syntheticParent.Arguments
        };
        Check(!FreshStoreApiPairObserver.MatchesVerifiedPair(
                syntheticObserved,reusedParent,syntheticChild.ExecutablePath,
                syntheticChild.Arguments,37551,(_,_)=>true)&&
              !FreshStoreApiPairObserver.MatchesVerifiedPair(
                syntheticObserved,syntheticParent,syntheticChild.ExecutablePath,
                syntheticChild.Arguments,37551,(_,_)=>false)&&
              !FreshStoreApiPairObserver.MatchesVerifiedPair(
                new ProcessCheck(false,"Loader only",null,false,syntheticParent),
                syntheticParent,syntheticChild.ExecutablePath,
                syntheticChild.Arguments,37551,(_,_)=>true),
            "reused Loader PID, foreign RCON ownership and missing ASA child never certify API pair");
        Check(!FreshStoreApiPairObserver.MatchesVerifiedPair(
                syntheticObserved,syntheticParent,
                @"C:\Synthetic\OtherGame.exe",syntheticChild.Arguments,
                37551,(_,_)=>true)&&
              !FreshStoreApiPairObserver.MatchesVerifiedPair(
                syntheticObserved,syntheticParent,syntheticChild.ExecutablePath,
                "TheIsland_WP -converttostore",37551,(_,_)=>true),
            "foreign ASA executable or altered Store arguments cannot certify Loader child");

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
                    "The Island","Synthetic Store Map",7851,27551)+
                    " -mods=100123,100124 -passivemods=100124",
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

            // Original Kova ID is source-provided, not always literally
            // "KovaCluster". No generic fresh Store enrollment may create
            // a second writer in ANY cluster holding historical local data.
            Guid sourceHistoricalId=Guid.Parse(
                "11111111-2222-4333-8444-555555555556");
            manager.AddMap(new LocalMap{
                Id=sourceHistoricalId,Name="Imported Synthetic Historical Member"
            });
            var addOld=manager.Registry.PreviewMapAssignments(
                "new-local-cluster",new[]{mapId,sourceHistoricalId});
            Check(addOld.IsValid,"fixture can assign source member before historical protection");
            manager.Registry.ApplyMapAssignments("new-local-cluster",
                new[]{mapId,sourceHistoricalId},addOld.BaseRevision);
            var imported=manager.Config.LocalMaps.Single(
                m=>m.Id==sourceHistoricalId);
            imported.HistoricalStoreProtected=true;
            imported.HistoricalArchiveSha256=new string('A',64);
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(
                    manager,map,"new-local-cluster"),
                "generic new Store preflight rejects source-named historical cluster membership");
            imported.HistoricalStoreProtected=false;
            imported.HistoricalArchiveSha256="";
            var removeOld=manager.Registry.PreviewMapAssignments(
                "new-local-cluster",new[]{mapId});
            Check(removeOld.IsValid,
                "fixture can remove restored nonhistorical member");
            manager.Registry.ApplyMapAssignments(
                "new-local-cluster",new[]{mapId},removeOld.BaseRevision);
            manager.RemoveMap(sourceHistoricalId);

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
            var unknownFlag=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(map))!;
            unknownFlag.Arguments+=" -UnreviewedCommand=1";
            manager.ReplaceMap(map,unknownFlag);
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(manager,unknownFlag,
                    "new-local-cluster"),
                "unreviewed launch flags cannot enter a source-derived Store Start profile");
            manager.ReplaceMap(unknownFlag,map);

            // PR #118 permits explicit custom Unreal levels during native
            // first-setup. The subsequent fresh Store enrollment must accept
            // that EXACT validated command line, without relaxing official
            // level pinning or permitting a second command-line token.
            var customMap=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(map))!;
            customMap.Name="Custom Atlantis Community";
            customMap.Arguments=AsaSteamMapCatalog.LaunchArgumentsForLevel(
                "Atlantis_WP","Synthetic Store Map",7851,27551)+
                " -mods=100123,100124 -passivemods=100124";
            manager.ReplaceMap(map,customMap);
            var customPlan=FreshStoreEnrollmentPolicy.Prepare(
                manager,customMap,"new-local-cluster");
            Check(customPlan.Arguments.StartsWith(
                    "Atlantis_WP?listen?SessionName=\"Synthetic Store Map\"",
                    StringComparison.Ordinal)&&
                  customPlan.Arguments.EndsWith(" -usestore",
                    StringComparison.Ordinal),
                "strict native custom-level profile joins new empty Store without guessing level");
            var customInjected=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(customMap))!;
            customInjected.Arguments=customInjected.Arguments.Replace(
                "Atlantis_WP?listen","Atlantis_WP?Injected=1?listen",
                StringComparison.Ordinal);
            manager.ReplaceMap(customMap,customInjected);
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(
                    manager,customInjected,"new-local-cluster"),
                "injected custom level options cannot enter Store startup");
            manager.ReplaceMap(customInjected,map);
            var officialOverride=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(map))!;
            officialOverride.Arguments=AsaSteamMapCatalog.LaunchArgumentsForLevel(
                "Unverified_WP","Synthetic Store Map",7851,27551)+
                " -mods=100123,100124 -passivemods=100124";
            manager.ReplaceMap(map,officialOverride);
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(
                    manager,officialOverride,"new-local-cluster"),
                "official map level cannot be overridden by a custom startup token");
            manager.ReplaceMap(officialOverride,map);
            var incorrectPassive=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(map))!;
            incorrectPassive.Arguments=incorrectPassive.Arguments.Replace(
                "-passivemods=100124","-passivemods=100999",StringComparison.Ordinal);
            manager.ReplaceMap(map,incorrectPassive);
            Reject(()=>FreshStoreEnrollmentPolicy.Prepare(manager,incorrectPassive,
                    "new-local-cluster"),
                "passive mod not present in enabled order cannot enter Store profile");
            manager.ReplaceMap(incorrectPassive,map);
            var plan=FreshStoreEnrollmentPolicy.Prepare(manager,map,"new-local-cluster");
            Check(plan.Arguments.Contains(" -usestore",StringComparison.Ordinal)&&
                  plan.Arguments.CountAt("-usestore")==1&&
                  !plan.Arguments.Contains("converttostore",StringComparison.OrdinalIgnoreCase)&&
                  plan.Arguments.Contains(
                      "-mods=100123,100124 -passivemods=100124 -clusterid=new-local-cluster",
                      StringComparison.Ordinal)&&
                  plan.Arguments.Contains("-clusterid=new-local-cluster")&&
                  plan.Arguments.Contains("-ClusterDirOverride=\""+transfer+"\"",
                      StringComparison.Ordinal)&&
                  plan.InstalledBuild=="980812"&&plan.RegistryRevision>0,
                "new empty Store launch uses existing cluster metadata, exactly one -usestore, zero conversion");
            // Independent new map, SteamCMD installation, RCON/INI and
            // ClusterRegistry membership. Previewing one map must not revoke
            // the other map's pending enrollment. No ASA process is started.
            Guid secondMapId=Guid.Parse("11111111-2222-4333-8444-555555555557");
            string secondInstall=Path.Combine(root,"future-ragnarok-install");
            string secondBinary=Path.Combine(secondInstall,"ShooterGame","Binaries","Win64");
            Directory.CreateDirectory(secondBinary);
            File.WriteAllBytes(Path.Combine(secondBinary,"ArkAscendedServer.exe"),
                new byte[]{77,90,7,9});
            string secondSteamApps=Path.Combine(secondInstall,"steamapps");
            Directory.CreateDirectory(secondSteamApps);
            File.WriteAllText(Path.Combine(secondSteamApps,"appmanifest_2430930.acf"),
                File.ReadAllText(Path.Combine(apps,"appmanifest_2430930.acf")));
            string secondTransfer=Path.Combine(root,"future-transfer");
            Directory.CreateDirectory(secondTransfer);
            var secondMap=new LocalMap{
                Id=secondMapId,Name="Ragnarok",
                InstallationPath=secondInstall,
                ExecutablePath=Path.Combine(secondBinary,"ArkAscendedServer.exe"),
                GamePort=7853,QueryPort=27553,RconPort=37553,
                Arguments=AsaSteamMapCatalog.LaunchArguments(
                    "Ragnarok","Future Store Map",7853,27553),
                AutoStart=false
            };
            AsaInitialRconSettings.CreateForNewServer(
                secondInstall,secondMap.RconPort,syntheticAdminPassword);
            secondMap.RconPasswordProtected=SecureStore.Protect(syntheticAdminPassword);
            manager.AddMap(secondMap);
            manager.Registry.CreateCluster(
                "future-preview-cluster","Another New Store",secondTransfer);
            var secondAssignment=manager.Registry.PreviewMapAssignments(
                "future-preview-cluster",new[]{secondMapId});
            manager.Registry.ApplyMapAssignments(
                "future-preview-cluster",new[]{secondMapId},secondAssignment.BaseRevision);
            Check(FreshStoreEnrollmentPolicy.Prepare(manager,secondMap,
                    "future-preview-cluster").MapId==secondMapId,
                "independently owned future ASA map has valid Store enrollment preflight");
            string untouched=map.Arguments;
            using(var bridge=new DesktopFreshStoreEnrollmentBridge(manager))
            {
                var state=await Call(bridge,"freshStore.enrollmentStatus",new{mapId});
                Check(state.GetProperty("ok").GetBoolean()&&
                      !state.GetProperty("data").GetProperty("enrolled").GetBoolean(),
                    "new empty Store native status is read-only and not yet enrolled");
                var reserved=await Call(bridge,"freshStore.enrollmentPreview",
                    new{mapId,clusterId="KovaCluster"});
                Check(!reserved.GetProperty("ok").GetBoolean()&&
                      reserved.GetProperty("error").GetString()==
                        "UM-FRESH-STORE-KOVA-CLUSTER-RESERVED",
                    "native enrollment reports the exact reserved historical cluster blocker without exposing UNC paths or other secrets");
                var preview=await Call(bridge,"freshStore.enrollmentPreview",
                    new{mapId,clusterId="new-local-cluster"});
                Check(preview.GetProperty("ok").GetBoolean()&&
                      !preview.GetProperty("data").GetProperty("serverStarted").GetBoolean()&&
                      !preview.GetRawText().Contains(transfer,StringComparison.OrdinalIgnoreCase),
                    "native preview exposes neither transfer path nor a fictional server Start");
                var token=preview.GetProperty("data").GetProperty("token").GetString()!;
                var secondPreview=await Call(bridge,
                    "freshStore.enrollmentPreview",
                    new{mapId=secondMapId,clusterId="future-preview-cluster"});
                Check(secondPreview.GetProperty("ok").GetBoolean()&&
                      !secondPreview.GetRawText().Contains(secondTransfer,
                          StringComparison.OrdinalIgnoreCase),
                    "second local Store map preview is independently authorized and path-redacted");
                string secondToken=secondPreview.GetProperty("data")
                    .GetProperty("token").GetString()!;
                Check(secondToken!=token,"two map approvals never share a token");
                var secondNotApproved=await Call(bridge,"freshStore.enrollmentApply",
                    new{token=secondToken,confirm=true,typedApproval="NO"});
                Check(secondNotApproved.GetProperty("error").GetString()==
                      "UM-FRESH-STORE-EXPLICIT-APPROVAL"&&
                      !FreshStoreEnrollmentPolicy.IsEnrolled(secondMap),
                    "second-map approval remains recognized without modifying its Store profile");
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
            SteamStoppedMapUpdater.DemandSafeLocalInstallation(manager,enrolled);
            Check(true,"exact enrolled Store map can preflight local stopped-only manual SteamCMD maintenance");
            var pristineScope=SteamStoppedMapUpdater.PreUpdateBackupScope(manager,enrolled);
            Check(pristineScope.HasFlag(LocalBackupScope.Ini)&&
                  !pristineScope.HasFlag(LocalBackupScope.WorldAndProfiles),
                "never-started enrolled Store protects configured INI without requiring a nonexistent world");
            var pristineBackup=LocalBackupWorkflow.Preview(enrolled,
                manager.Check(enrolled),Path.Combine(profile,"first-map-steam-prebackup"),
                pristineScope);
            Check(pristineBackup.Files>0&&pristineBackup.SelectedBytes>0,
                "INI-only first-map Steam protection has a real verified, recoverable backup plan");
            var previousProcess=enrolled.LastProcess;
            try
            {
                enrolled.LastProcess=new ProcessIdentity{
                    Pid=65101,StartedUtc=DateTimeOffset.UtcNow,
                    ExecutablePath=enrolled.ExecutablePath,Arguments=enrolled.Arguments
                };
                Check(SteamStoppedMapUpdater.PreUpdateBackupScope(manager,enrolled)
                    .HasFlag(LocalBackupScope.WorldAndProfiles),
                    "a map with persisted prior ASA launch cannot waive its world backup");
            }
            finally { enrolled.LastProcess=previousProcess; }
            string worldFolder=Path.Combine(installation,"ShooterGame","Saved","SavedArks");
            string zeroByteWorld=Path.Combine(worldFolder,"TheIsland_WP.ark");
            Directory.CreateDirectory(worldFolder);
            File.WriteAllBytes(zeroByteWorld,Array.Empty<byte>());
            try
            {
                Check(SteamStoppedMapUpdater.PreUpdateBackupScope(manager,enrolled)
                    .HasFlag(LocalBackupScope.WorldAndProfiles),
                    "even a zero-byte world placeholder prohibits config-only Steam backup waiver");
            }
            finally
            {
                File.Delete(zeroByteWorld);
                Directory.Delete(worldFolder);
            }
            Check(!SteamStoppedMapUpdater.PreUpdateBackupScope(manager,enrolled)
                      .HasFlag(LocalBackupScope.WorldAndProfiles),
                "removing isolated synthetic placeholder restores untouched first-world eligibility");
            // The exact installed Store profile can use natural ASA
            // SessionName text containing "ConvertToStore". Only actual
            // startup flags may cause a native conversion denial.
            string enrolledArgs=enrolled.Arguments;
            try
            {
                enrolled.Arguments=enrolledArgs.Replace("Synthetic Store Map",
                    "ConvertToStore Celebration",StringComparison.Ordinal);
                FreshStoreEnrollmentPolicy.DemandEnrolled(manager,enrolled);
                Check(AsaStoreArgumentSwitches.ContainsStoreWriter(enrolled.Arguments) &&
                      !AsaStoreArgumentSwitches.ContainsConversion(enrolled.Arguments),
                    "ordinary ConvertToStore server title does not lock an approved fresh Store writer");
                enrolled.Arguments=enrolledArgs.Replace(" -usestore",
                    " -converttostore -usestore",StringComparison.Ordinal);
                Reject(()=>FreshStoreEnrollmentPolicy.DemandEnrolled(manager,enrolled),
                    "actual Store conversion switch remains forbidden for an already enrolled local writer");
            }
            finally { enrolled.Arguments=enrolledArgs; }
            Check(enrolled.FreshStoreClusterId=="new-local-cluster"&&
                  enrolled.Arguments.EndsWith(" -usestore",StringComparison.Ordinal)&&
                  enrolled.HistoricalArchiveSha256==""&&
                  enrolled.HistoricalMapKey==""&&
                  !enrolled.AutoStart&&
                  UntamedWorldBackup.HasLiveOrHistoricalStore(enrolled),
                "new Store is durable and distinct from Kova archive, conversion and autostart");
            Check(enrolled.FreshStoreTransferFileId.Length==64,
                "new Store enrollment durably pins the actual Windows transfer directory file ID");
            Check(AsaApiOwnedInstaller.ListCompletedRollbacks(manager,mapId).Count==0,
                "enrolled local Store permits stopped-only API Core inspection without any transaction or world mutation");

            // Loader selection for a newly enrolled (NOT historical Kova) Store
            // profile uses only synthetic PE headers and no real ASA process.
            using(var modeBridge=new DesktopFreshStoreLoaderModeBridge(manager))
            {
                var initial=await CallMode(modeBridge,"freshStore.loaderModeStatus",
                    new{mapId});
                Check(initial.GetProperty("ok").GetBoolean()&&
                      initial.GetProperty("data").GetProperty("canChangeMode").GetBoolean()&&
                      !initial.GetProperty("data").GetProperty("useApi").GetBoolean(),
                    "new enrolled Store defaults to direct ASA launch with verified stopped profile");

                var missingApi=await CallMode(modeBridge,"freshStore.loaderModePreview",
                    new{mapId,useApi=true});
                Check(!missingApi.GetProperty("ok").GetBoolean(),
                    "new Store refuses Loader mode without locally verified official API Core files");

                string api=Path.Combine(binary,"ArkApi");
                Directory.CreateDirectory(Path.Combine(api,"Plugins"));
                WriteSyntheticPe64(Path.Combine(binary,"AsaApiLoader.exe"));
                WriteSyntheticPe64(Path.Combine(api,"AsaApi.dll"));
                File.WriteAllText(Path.Combine(binary,"config.json"),"{}");
                string transferIdentity=enrolled.FreshStoreTransferFileId;
                var choose=await CallMode(modeBridge,"freshStore.loaderModePreview",
                    new{mapId,useApi=true});
                Check(choose.GetProperty("ok").GetBoolean()&&
                      !choose.GetProperty("data").GetProperty("startsServer").GetBoolean()&&
                      !choose.GetProperty("data").GetProperty("cacheRuntimeCertified").GetBoolean()&&
                      !choose.GetRawText().Contains(transfer,StringComparison.OrdinalIgnoreCase),
                    "Loader selection previews exact local files without spawn or cache runtime claims");
                string modeToken=choose.GetProperty("data").GetProperty("token").GetString()!;
                var wrong=await CallMode(modeBridge,"freshStore.loaderModeApply",
                    new{token=modeToken,confirm=true,typedApproval="NO"});
                Check(!wrong.GetProperty("ok").GetBoolean(),
                    "wrong Loader approval changes no Store profile");
                var selected=await CallMode(modeBridge,"freshStore.loaderModeApply",
                    new{token=modeToken,confirm=true,typedApproval="FRESH STORE API MODE"});
                Check(selected.GetProperty("ok").GetBoolean()&&
                      !selected.GetProperty("data").GetProperty("serverStarted").GetBoolean(),
                    "explicit one-shot Loader approval changes only local Manager launch preference");
                var repeated=await CallMode(modeBridge,"freshStore.loaderModeApply",
                    new{token=modeToken,confirm=true,typedApproval="FRESH STORE API MODE"});
                Check(!repeated.GetProperty("ok").GetBoolean(),
                    "Loader mode approval token cannot be replayed");
                var loaderProfile=manager.Config.LocalMaps.Single(m=>m.Id==mapId);
                Check(loaderProfile.StartWithAsaApi&&
                      loaderProfile.ApiLoaderArguments==loaderProfile.Arguments&&
                      loaderProfile.Arguments.EndsWith(" -usestore",StringComparison.Ordinal)&&
                      loaderProfile.FreshStoreTransferFileId==transferIdentity,
                    "new Store uses exact original -usestore args in API Loader without altering transfer identity");
                FreshStoreEnrollmentPolicy.DemandEnrolled(manager,loaderProfile);

                var returnDirect=await CallMode(modeBridge,"freshStore.loaderModePreview",
                    new{mapId,useApi=false});
                Check(returnDirect.GetProperty("ok").GetBoolean(),
                    "verified stopped Store may explicitly return to direct ASA launch");
                var returned=await CallMode(modeBridge,"freshStore.loaderModeApply",
                    new{token=returnDirect.GetProperty("data").GetProperty("token").GetString(),
                        confirm=true,typedApproval="FRESH STORE API MODE"});
                Check(returned.GetProperty("ok").GetBoolean(),
                    "switching back to direct launch never spawns ASA or converts Store");
                enrolled=manager.Config.LocalMaps.Single(m=>m.Id==mapId);
                Check(!enrolled.StartWithAsaApi&&
                      enrolled.ApiLoaderArguments==enrolled.Arguments&&
                      enrolled.FreshStoreTransferFileId==transferIdentity,
                    "direct launch retains Loader discovery identity to block orphan child bypass");
            }
            var aliasSwap=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(enrolled))!;
            aliasSwap.FreshStoreTransferFileId=new string('F',64);
            manager.ReplaceMap(enrolled,aliasSwap);
            Reject(()=>FreshStoreEnrollmentPolicy.DemandEnrolled(manager,aliasSwap),
                "changed physical transfer-directory fingerprint blocks Store process authorization");
            Reject(()=>SteamStoppedMapUpdater.DemandSafeLocalInstallation(manager,aliasSwap),
                "manual SteamCMD also rejects changed Store transfer file identity before any download");
            Reject(()=>AsaApiOwnedInstaller.ListCompletedRollbacks(manager,mapId),
                "changed Store transfer identity also blocks API Core update or rollback inspection");
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

                // A map-local API preference change between Start preview
                // and Apply MUST invalidate the earlier approved mode.
                var stale=await CallStart(startBridge,
                    "servers.freshStartPreview",new{mapId});
                Check(stale.GetProperty("ok").GetBoolean(),
                    "fresh Store can prepare another synthetic start after token completion");
                string staleToken=stale.GetProperty("data").GetProperty("token").GetString()!;
                using(var modeChange=new DesktopFreshStoreLoaderModeBridge(manager))
                {
                    var activate=await CallMode(modeChange,
                        "freshStore.loaderModePreview",new{mapId,useApi=true});
                    Check(activate.GetProperty("ok").GetBoolean(),
                        "stopped Store can approve Loader mode while prior Start preview is pending");
                    var modeReceipt=await CallMode(modeChange,
                        "freshStore.loaderModeApply",
                        new{token=activate.GetProperty("data").GetProperty("token").GetString(),
                            confirm=true,typedApproval="FRESH STORE API MODE"});
                    Check(modeReceipt.GetProperty("ok").GetBoolean(),
                        "Loader preference is saved without executing the stale Start token");
                }
                var rejected=await CallStart(startBridge,
                    "servers.freshStartApply",new{token=staleToken,confirm=true});
                Check(!rejected.GetProperty("ok").GetBoolean()&&syntheticStartCalls==1,
                    "changing API Loader mode invalidates an old Start approval, with no launch");
                using(var restoreMode=new DesktopFreshStoreLoaderModeBridge(manager))
                {
                    var direct=await CallMode(restoreMode,
                        "freshStore.loaderModePreview",new{mapId,useApi=false});
                    var receipt=await CallMode(restoreMode,
                        "freshStore.loaderModeApply",
                        new{token=direct.GetProperty("data").GetProperty("token").GetString(),
                            confirm=true,typedApproval="FRESH STORE API MODE"});
                    Check(receipt.GetProperty("ok").GetBoolean(),
                        "after denied stale Start, local Store mode can safely return to direct ASA");
                }
                enrolled=manager.Config.LocalMaps.Single(m=>m.Id==mapId);
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
