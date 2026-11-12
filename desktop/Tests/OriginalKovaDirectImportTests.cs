using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UntamedDesktop;
using UntamedManager.Core;

// Direct Kova import exercises the REAL native bridge and the source-pinned
// Core read path using only private TEMP files and fake ASA executable names.
// No original user Kova data, UNC share or ASA/RCON process is opened.
internal static class OriginalKovaDirectImportTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new InvalidOperationException("DIRECT KOVA IMPORT: "+reason);
        Console.WriteLine("PASS direct Kova import: "+reason);
    }

    private static Task<JsonElement> Call(
        DesktopHistoricalStoreImportBridge bridge,string method,
        object? values=null)=>Call(bridge.HandleAsync,method,values);

    private static async Task<JsonElement> Call(
        Func<string,Task<string>> handle,string method,object? values=null)
    {
        var request=new System.Collections.Generic.Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(values!=null)
        {
            using var extra=JsonDocument.Parse(JsonSerializer.Serialize(values));
            foreach(var prop in extra.RootElement.EnumerateObject())
                request[prop.Name]=prop.Value.Clone();
        }
        string result=await handle(JsonSerializer.Serialize(request));
        using var parsed=JsonDocument.Parse(result);
        return parsed.RootElement.Clone();
    }

    public static async Task Run(string sandbox)
    {
        string baseRoot=Path.Combine(sandbox,"direct-kova-import-lab");
        string local=Path.Combine(baseRoot,"synthetic-kova");
        string manager=Path.Combine(baseRoot,"obsidian-profile");
        Directory.CreateDirectory(local);
        string[] mapKeys={"TheIsland_WP","Astraeos_WP","Valguero_WP",
            "Extinction_WP","Genesis_WP"};
        string[] ids=Enumerable.Range(0,5).Select(_=>Guid.NewGuid().ToString("D")).ToArray();
        string session=Guid.NewGuid().ToString("D");
        string device=Guid.NewGuid().ToString("D");
        var members=mapKeys.Select((name,i)=>new{
            id=ids[i],map=name,root=Path.Combine(baseRoot,"synthetic-asa-"+i)
        }).ToArray();
        foreach(var member in members)
        {
            string exe=Path.Combine(member.root,"ShooterGame","Binaries","Win64",
                "ArkAscendedServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe,new byte[]{0x4d,0x5a,0,0});
        }
        var originalCluster=new{
            id=VaultProtocol.ClusterId,mode="shared",
            path=@"\\synthetic-nonexistent\store-transfer",members
        };
        var states=ids.Select((id,i)=>new
        {
            id,
            value=new{
                phase="active",sharedSession=session,root=members[i].root,
                map=mapKeys[i],backupId=Guid.NewGuid().ToString("D")
            }
        }).ToDictionary(x=>x.id,x=>x.value);
        var store=new{schema=1,maps=states};
        var lockFile=new{schema=1,session,selected=ids,
            configHash=StoreV1Fingerprint.Compute(
                JsonSerializer.SerializeToElement(originalCluster)),
            config=originalCluster};
        string[] names={
            "cluster-v1.json","cluster-store-v1.json",
            "shared-store-device-v1.json","shared-store-lock-v1.json"
        };
        string[] source={
            JsonSerializer.Serialize(originalCluster),JsonSerializer.Serialize(store),
            JsonSerializer.Serialize(new{schema=1,id=device}),
            JsonSerializer.Serialize(lockFile)
        };
        for(int i=0;i<names.Length;i++)
            File.WriteAllText(Path.Combine(local,names[i]),source[i]);
        string launchProfilePath=Path.Combine(local,"launch-profiles-v1.json");
        var launchRows=ids.Select((id,i)=>new{
            id,root=members[i].root,
            launch=new{
                map=mapKeys[i],gamePort=7777+i*10,queryPort=27015+i*10,
                mods=new[]{"123456789","987654321"},
                passiveMods=new[]{"123456789"}
            }
        }).ToArray();
        string launchBytes=JsonSerializer.Serialize(new{version=1,rows=launchRows});
        File.WriteAllText(launchProfilePath,launchBytes);
        string launchHash=Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(launchProfilePath)));
        string[] hashes=names.Select(n=>Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(local,n))))).ToArray();
        var discovered=LegacyStoreV1OriginalImport.Inspect(local);
        Check(discovered.Maps.Length==5&&
              discovered.Maps.Select(x=>x.MapId.ToString("D")).SequenceEqual(ids)&&
              discovered.Maps.Select(x=>x.OriginalInstallationRoot)
                .SequenceEqual(members.Select(x=>x.root))&&
              discovered.Maps.All(x=>x.EnabledMods.SequenceEqual(
                  new[]{"123456789","987654321"})&&
                  x.PassiveMods.SequenceEqual(new[]{"123456789"}))&&
              discovered.LaunchProfileSha256==launchHash,
            "five original GUIDs, map keys and installation paths remain byte-bound");

        // Migration of a PREVIOUSLY imported Manager profile is separate
        // from the first-time import, with explicit one-use native approval.
        // All files below are synthetic TEMP fixtures, not production Kova.
        await using(var existingManager=new ClusterService(
            "existing-pre-pin",Path.Combine(baseRoot,"existing-pre-pin")))
        {
            var firstSource=discovered.Maps[0];
            existingManager.AddMap(new LocalMap{
                Id=firstSource.MapId,Name="Previously imported map",
                HistoricalStoreProtected=true,
                HistoricalArchiveSha256=discovered.FourFilesSha256,
                HistoricalMapKey=firstSource.MapKey,
                InstallationPath=firstSource.OriginalInstallationRoot,
                HistoricalEnabledMods=firstSource.EnabledMods.ToList(),
                HistoricalPassiveMods=firstSource.PassiveMods.ToList(),
                GamePort=firstSource.GamePort,QueryPort=firstSource.QueryPort
            });
            using var originalBridge=new DesktopHistoricalStoreImportBridge(
                existingManager,localLegacyDataDirectory:local);
            var sourcePreview=await Call(originalBridge,"historical.sourcePinPreview");
            Check(sourcePreview.GetProperty("ok").GetBoolean()&&
                sourcePreview.GetProperty("data").GetProperty(
                    "verifiedLocalMaps").GetInt32()==1,
                "existing pre-pin Manager offers a non-mutating migration preview");
            string sourceToken=sourcePreview.GetProperty("data")
                .GetProperty("token").GetString()!;
            var sourceRejected=await Call(originalBridge,
                "historical.sourcePinApply",
                new{token=sourceToken,confirm=true,typedApproval="INVALID"});
            Check(!sourceRejected.GetProperty("ok").GetBoolean(),
                "source migration requires the exact typed native approval");
            var sourceApplied=await Call(originalBridge,
                "historical.sourcePinApply",
                new{token=sourceToken,confirm=true,
                    typedApproval="OBSIDIAN FORRÁS ÁTVÉTEL"});
            Check(sourceApplied.GetProperty("ok").GetBoolean()&&
                sourceApplied.GetProperty("data").GetProperty(
                    "originalKovaRequiredAfterPin").GetBoolean()==false,
                "approved legacy migration writes only the Obsidian Manager source");
            string hidden=local+"-independent-pin-test";
            Directory.Move(local,hidden);
            try
            {
                var afterKova=await Call(originalBridge,"historical.originalList");
                Check(afterKova.GetProperty("ok").GetBoolean(),
                    "migrated original map list remains readable with missing Kova");
            }
            finally{Directory.Move(hidden,local);}
        }

        // A stopped or running original Kova map must NOT receive newly
        // synthesized cluster or transfer settings. Verify the exact ASA
        // launch arguments before any real Process.Start could occur.
        var firstOriginal=discovered.Maps[0];
        var parityMap=new LocalMap{
            Id=firstOriginal.MapId,Name="The Island",
            InstallationPath=firstOriginal.OriginalInstallationRoot,
            HistoricalStoreProtected=true,
            HistoricalArchiveSha256=discovered.FourFilesSha256,
            HistoricalMapKey=firstOriginal.MapKey,
            HistoricalEnabledMods=firstOriginal.EnabledMods.ToList(),
            HistoricalPassiveMods=firstOriginal.PassiveMods.ToList(),
            GamePort=firstOriginal.GamePort,QueryPort=firstOriginal.QueryPort
        };
        string originalIni="[ServerSettings]"+Environment.NewLine+
            "Port="+firstOriginal.GamePort+Environment.NewLine+
            "QueryPort="+firstOriginal.QueryPort+Environment.NewLine;
        var launch=LegacyStoreV1ActiveLaunch.Build(new(
            firstOriginal.MapKey,firstOriginal.GamePort,firstOriginal.QueryPort,
            firstOriginal.EnabledMods,firstOriginal.PassiveMods,
            VaultProtocol.ClusterId,originalCluster.path,"active",originalIni));
        LegacyStoreV1SourceLaunchParity.RequireAsaLaunchArguments(
            parityMap,discovered,originalCluster.path,originalIni,launch.Arguments);
        // Approved original source is copied to an independent, bounded
        // Manager-owned profile. Disabling the synthetic Kova source must not
        // break source parity or silently invent a new transfer directory.
        string pinnedManager=Path.Combine(baseRoot,"standalone-source-profile");
        LegacyStoreV1PinnedSource.Capture(local,pinnedManager,
            discovered.FourFilesSha256,discovered.LaunchProfileSha256);
        LegacyStoreV1PinnedSource.Capture(local,pinnedManager,
            discovered.FourFilesSha256,discovered.LaunchProfileSha256);
        string hiddenLocal=local+"-temporarily-offline";
        Directory.Move(local,hiddenLocal);
        try
        {
            string pinned=LegacyStoreV1PinnedSource.Resolve(pinnedManager,local,parityMap);
            var frozen=LegacyStoreV1OriginalImport.Inspect(pinned);
            Check(!string.Equals(pinned,local,StringComparison.OrdinalIgnoreCase)&&
                  frozen.FourFilesSha256==discovered.FourFilesSha256&&
                  frozen.LaunchProfileSha256==discovered.LaunchProfileSha256,
                "approved standalone Store source preserves five IDs and mod/port order without original local Kova directory");
            string oldEpoch=LegacyStoreV1PinnedSource.RuntimeFingerprint(
                pinnedManager,local,parityMap);
            Check(oldEpoch.Length==64&&oldEpoch.All(Uri.IsHexDigit),
                "original local Store source has a valid Obsidian epoch with Kova absent");
            string existingGus=AsaInitialRconSettings.NewIniPath(
                parityMap.InstallationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(existingGus)!);
            File.WriteAllText(existingGus,"[ServerSettings]\\nRCONEnabled=True\\n");
            string iniEpoch=LegacyStoreV1PinnedSource.RuntimeFingerprint(
                pinnedManager,local,parityMap);
            Check(iniEpoch.Length==64&&iniEpoch!=oldEpoch,
                "new ASA INI bytes invalidate an earlier local RCON/SaveWorld epoch");
            File.AppendAllText(existingGus,"SessionName=Changed\\n");
            string changedIniEpoch=LegacyStoreV1PinnedSource.RuntimeFingerprint(
                pinnedManager,local,parityMap);
            Check(changedIniEpoch!=iniEpoch,
                "editing ASA GameUserSettings.ini cannot reuse old local approval");
            var edited=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(parityMap))!;
            edited.GamePort=7781;edited.QueryPort=27019;
            edited.HistoricalEnabledMods=new(){"987654321","123456789"};
            edited.HistoricalPassiveMods=new(){"123456789"};
            edited.HistoricalRuntimeClusterId="ObsidianCluster";
            edited.HistoricalRuntimeTransferDirectory=
                @"\\synthetic-nonexistent\obsidian-transfer";
            LegacyStoreV1PinnedSource.RequireImmutableIdentity(edited,frozen);
            string nextEpoch=LegacyStoreV1PinnedSource.RuntimeFingerprint(
                pinnedManager,local,edited);
            Check(nextEpoch.Length==64&&nextEpoch!=oldEpoch,
                "approved runtime configuration changes advance the local RCON/SaveWorld epoch");
            var editableMods=ModStateController.Read(edited);
            var editedLaunch=LegacyStoreV1ActiveLaunch.Build(new(
                edited.HistoricalMapKey,edited.GamePort,edited.QueryPort,
                editableMods.EnabledOrder.ToArray(),editableMods.Passive.ToArray(),
                edited.HistoricalRuntimeClusterId,
                edited.HistoricalRuntimeTransferDirectory,"active","",
                SourcePinnedClusterOnly:false));
            string exe=Path.Combine(edited.InstallationPath,
                "ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
            string observedCommand="\""+exe+"\" "+string.Join(" ",editedLaunch.Arguments);
            Check(editedLaunch.ModOrder.SequenceEqual(
                    new[]{"987654321","123456789"})&&
                  editedLaunch.Arguments.Contains("-clusterid=ObsidianCluster")&&
                  LegacyStoreV1ProcessObserver.MatchesOriginalActiveCommand(
                    observedCommand,exe,edited),
                "confirmed runtime port/mod/cluster edits retain original GUID but reattach against Obsidian settings without Kova");
            edited.HistoricalRuntimeTransferDirectory=
                @"\\synthetic-nonexistent\other-transfer";
            Check(!LegacyStoreV1ProcessObserver.MatchesOriginalActiveCommand(
                    observedCommand,exe,edited),
                "a changed approved transfer directory rejects stale ASA process attribution");
        }
        finally{Directory.Move(hiddenLocal,local);}
        string pinnedLaunch=Path.Combine(pinnedManager,"original-store-v1-source",
            "launch-profiles-v1.json");
        File.AppendAllText(pinnedLaunch,"\\nTAMPER");
        bool pinTamperDenied=false;
        try{LegacyStoreV1PinnedSource.Resolve(pinnedManager,local,parityMap);}
        catch(InvalidDataException){pinTamperDenied=true;}
        Check(pinTamperDenied,
            "tampered Manager-owned source pin fails closed rather than falling back to Kova");
        Check(launch.Arguments.Contains("-clusterid="+VaultProtocol.ClusterId)&&
              launch.Arguments.Contains("-ClusterDirOverride="+originalCluster.path)&&
              launch.Arguments.Count(x=>x=="-usestore")==1&&
              !launch.Arguments.Any(x=>x.Contains("converttostore")),
            "registered original map inherits exactly the KovaCluster identity, physical transfer UNC and once-only active Store flag");
        bool RefusesChanged(Action apply)
        {
            try{apply();return false;}
            catch(InvalidDataException){return true;}
        }
        Check(RefusesChanged(()=>{
              parityMap.QueryPort++;
              try{LegacyStoreV1SourceLaunchParity.RequireOriginalMap(parityMap,discovered);}
              finally{parityMap.QueryPort--;}
            })&&
            RefusesChanged(()=>{
              parityMap.HistoricalEnabledMods.Reverse();
              try{LegacyStoreV1SourceLaunchParity.RequireOriginalMap(parityMap,discovered);}
              finally{parityMap.HistoricalEnabledMods.Reverse();}
            })&&
            RefusesChanged(()=>LegacyStoreV1SourceLaunchParity.RequireAsaLaunchArguments(
              parityMap,discovered,originalCluster.path,originalIni,
              launch.Arguments.Where(x=>!x.StartsWith("-ClusterDirOverride=")).ToArray())),
            "changed query port, mod order or missing transfer override refuse Obsidian launch parity");
        // Other original peer carries five OTHER GUIDs but must produce
        // the identical cluster ID and the SAME original transfer directory.
        var peerIds=Enumerable.Range(0,5).Select(_=>Guid.NewGuid()).ToArray();
        string[] ryzenMaps={"ScorchedEarth_WP","LostColony_WP",
            "Ragnarok_WP","TheCenter_WP","Aberration_WP"};
        var peerMaps=peerIds.Select((id,i)=>new LegacyStoreV1OriginalMap(
            id,ryzenMaps[i],Path.Combine(baseRoot,"synthetic-peer-"+i),
            7827+i*10,27215+i*10,firstOriginal.EnabledMods,
            firstOriginal.PassiveMods)).ToArray();
        // Peer Kova metadata is local to its host, so hashes need not be the
        // same even though both original launchers share KovaCluster/UNC.
        string peerOriginalFourHash=new string('D',64);
        var secondSource=new LegacyStoreV1OriginalLocalSelection(
            peerOriginalFourHash,new string('E',64),peerMaps);
        var second=new LocalMap{
            Id=peerMaps[0].MapId,Name="Scorched Earth",
            InstallationPath=peerMaps[0].OriginalInstallationRoot,
            HistoricalStoreProtected=true,
            HistoricalArchiveSha256=peerOriginalFourHash,
            HistoricalMapKey=peerMaps[0].MapKey,
            HistoricalEnabledMods=peerMaps[0].EnabledMods.ToList(),
            HistoricalPassiveMods=peerMaps[0].PassiveMods.ToList(),
            GamePort=peerMaps[0].GamePort,QueryPort=peerMaps[0].QueryPort
        };
        var peerLaunch=LegacyStoreV1ActiveLaunch.Build(new(
            peerMaps[0].MapKey,peerMaps[0].GamePort,peerMaps[0].QueryPort,
            peerMaps[0].EnabledMods,peerMaps[0].PassiveMods,
            VaultProtocol.ClusterId,originalCluster.path,"active",""));
        LegacyStoreV1SourceLaunchParity.RequireAsaLaunchArguments(
            second,secondSource,originalCluster.path,"",peerLaunch.Arguments);
        Check(peerLaunch.Arguments.Contains("-clusterid="+VaultProtocol.ClusterId)&&
              peerLaunch.Arguments.Contains("-ClusterDirOverride="+originalCluster.path),
            "both isolated original host launch profiles preserve one KovaCluster and transfer share, without character-movement code");

        // Every map on BOTH original hosts gets exactly the source launcher
        // grammar; this test never moves a profile or copies world files.
        var sourceByHost=new[]{discovered,secondSource};
        var launchCount=0;
        foreach(var verifiedSource in sourceByHost)
        {
            foreach(var mapOriginal in verifiedSource.Maps)
            {
                var registered=new LocalMap{
                    Id=mapOriginal.MapId,
                    Name=mapOriginal.MapKey,
                    InstallationPath=mapOriginal.OriginalInstallationRoot,
                    HistoricalStoreProtected=true,
                    HistoricalArchiveSha256=verifiedSource.FourFilesSha256,
                    HistoricalMapKey=mapOriginal.MapKey,
                    HistoricalEnabledMods=mapOriginal.EnabledMods.ToList(),
                    HistoricalPassiveMods=mapOriginal.PassiveMods.ToList(),
                    GamePort=mapOriginal.GamePort,QueryPort=mapOriginal.QueryPort
                };
                var sourceArgs=LegacyStoreV1ActiveLaunch.Build(new(
                    mapOriginal.MapKey,mapOriginal.GamePort,mapOriginal.QueryPort,
                    mapOriginal.EnabledMods,mapOriginal.PassiveMods,
                    VaultProtocol.ClusterId,originalCluster.path,"active",""));
                LegacyStoreV1SourceLaunchParity.RequireAsaLaunchArguments(
                    registered,verifiedSource,originalCluster.path,"",sourceArgs.Arguments);
                Check(sourceArgs.Arguments.Count(a=>a.StartsWith(
                          "-ClusterDirOverride=",StringComparison.OrdinalIgnoreCase))==1&&
                      sourceArgs.Arguments.Count(a=>a.StartsWith(
                          "-clusterid=",StringComparison.OrdinalIgnoreCase))==1&&
                      sourceArgs.Arguments.Count(a=>a.Equals(
                          "-usestore",StringComparison.OrdinalIgnoreCase))==1,
                    "ASA native argument parity for "+mapOriginal.MapKey);
                launchCount++;
            }
        }
        Check(launchCount==10,
            "all ten original maps receive the same Kova cluster/transfer identity on their respective hosts");
        Check(RefusesChanged(()=>LegacyStoreV1SourceLaunchParity.RequireAsaLaunchArguments(
                parityMap,discovered,originalCluster.path,originalIni,
                launch.Arguments.Concat(new[]{"-usestore"}).ToArray()))&&
              RefusesChanged(()=>LegacyStoreV1SourceLaunchParity.RequireAsaLaunchArguments(
                parityMap,discovered,originalCluster.path,originalIni,
                launch.Arguments.Select(a=>a.StartsWith("-clusterid=",
                    StringComparison.OrdinalIgnoreCase)?
                    "-clusterid=ObsidianCluster":a).ToArray())),
            "duplicate Store flag and renamed cluster identity are rejected before process launch");

        await using var cluster=new ClusterService(
            "direct-kova-test",manager,
            _=>new ProcessCheck(false,"isolated synthetic stopped",null,true));
        using var bridge=new DesktopHistoricalStoreImportBridge(
            cluster,
            _=>new ProcessCheck(false,"isolated synthetic stopped",null,true),local);
        // Production WebView2 HOST DISPATCH -> real native handler, using
        // ONLY a private TEMP synthetic Kova profile. This is not a React
        // mock and does not access any live Store, ASA writer or remote peer.
        // Route selection is the exact helper called by ObsidianWindow's
        // WebMessageReceived event, including its read-only fallback.
        bool hitFallback=false;
        async Task<string> ThroughActualHostDispatcher(string raw)
        {
            using var request=JsonDocument.Parse(raw);
            var method=request.RootElement.GetProperty("method").GetString();
            return await ObsidianWindow.DispatchHistoricalOrReadOnlyAsync(
                method,raw,bridge.HandleAsync,body=>{
                    hitFallback=true;
                    return Task.FromResult(JsonSerializer.Serialize(new{
                        id=request.RootElement.GetProperty("id").GetString(),
                        ok=false,error="UM-METHOD-DENIED"}));
                });
        }
        var liveRoute=await Call(ThroughActualHostDispatcher,
            "historical.originalLiveBatchPreview");
        Check(!liveRoute.GetProperty("ok").GetBoolean()&&
              liveRoute.GetProperty("error").GetString()!="UM-METHOD-DENIED"&&
              !hitFallback,
            "real WebView2 host dispatcher routes originalLiveBatchPreview to native proof checks");
        var applyRoute=await Call(ThroughActualHostDispatcher,
            "historical.originalLiveBatchApply");
        Check(!applyRoute.GetProperty("ok").GetBoolean()&&
              applyRoute.GetProperty("error").GetString()!="UM-METHOD-DENIED"&&
              !hitFallback&&cluster.Config.LocalMaps.Count==0,
            "real WebView2 host dispatcher routes originalLiveBatchApply to native approval gate without writes");
        var unknownRoute=await Call(ThroughActualHostDispatcher,
            "historical.unsafeOperation");
        Check(unknownRoute.GetProperty("error").GetString()=="UM-METHOD-DENIED"&&
              hitFallback&&cluster.Config.LocalMaps.Count==0,
            "unknown historical method still follows deny-by-default fallback");

        var list=await Call(bridge,"historical.originalList");
        Check(list.GetProperty("ok").GetBoolean()&&
              list.GetProperty("data").GetProperty("maps").GetArrayLength()==5,
            "existing WebView route lists original source without external ZIP");

        var invalid=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[0],gamePort=7777,queryPort=7777});
        Check(!invalid.GetProperty("ok").GetBoolean(),
            "equal Game and Query ports are rejected before approval");
        var first=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[0],gamePort=7777,queryPort=27015});
        Check(first.GetProperty("ok").GetBoolean()&&
              first.GetProperty("data").GetProperty("originalIdsPreserved").GetBoolean(),
            "first synthetic map has a source-pinned, stopped-process preview");
        string token=first.GetProperty("data").GetProperty("token").GetString()!;
        var denied=await Call(bridge,"historical.originalRegister",
            new{token,confirm=false});
        Check(!denied.GetProperty("ok").GetBoolean(),
            "registration requires operator confirmation");
        var accepted=await Call(bridge,"historical.originalRegister",
            new{token,confirm=true});
        Check(accepted.GetProperty("ok").GetBoolean()&&
              !accepted.GetProperty("data").GetProperty("originalFilesWritten").GetBoolean(),
            "confirmed import registers Manager-only local map, no source writes");
        var imported=cluster.Config.LocalMaps.Single(x=>x.Id==Guid.Parse(ids[0]));
        Check(imported.HistoricalStoreProtected&&
              imported.InstallationPath==members[0].root&&
              imported.GamePort==7777&&imported.QueryPort==27015&&
              imported.Arguments==""&&imported.RconPort==0&&
              imported.HistoricalEnabledMods.SequenceEqual(
                  new[]{"123456789","987654321"})&&
              imported.HistoricalPassiveMods.SequenceEqual(new[]{"123456789"})&&
              !imported.AutoStart&&imported.HistoricalLaunchModeNeedsApproval,
            "stopped original import preserves identity but blocks unapproved direct ASA launch");
        bool startBlocked=false;
        try{_=LegacyStoreV1NativeStartPlanner.Prepare(cluster,imported);}
        catch(InvalidDataException ex)
        {
            startBlocked=ex.Message=="UM-STORE-LAUNCH-MODE-APPROVAL-REQUIRED";
        }
        Check(startBlocked,
            "unapproved stopped Kova import cannot reach any Process.Start or RCON preflight");

        // Native mode approval is kept separate from import. A test-only
        // preflight delegate models a STOPPED API-capable map without touching
        // any real Kova, ASA process, RCON listener or shared drive.
        await using(var modeCluster=new ClusterService("synthetic-mode-lab",
            Path.Combine(baseRoot,"launch-mode-approval-manager"),
            _=>new ProcessCheck(false,"synthetic stopped",null,true)))
        {
            var stagedMap=JsonSerializer.Deserialize<LocalMap>(
                JsonSerializer.Serialize(imported))!;
            stagedMap.RconPort=37015;
            stagedMap.RconPasswordProtected=SecureStore.Protect(
                "SYNTHETIC_TEST_ONLY_RCON_PASSWORD");
            modeCluster.AddMap(stagedMap);
            using var modeBridge=new DesktopHistoricalActiveStartBridge(
                modeCluster,null,null,(_,_)=>true);
            var modeStatus=await Call(modeBridge.HandleAsync,
                "historical.activeStartStatus",new{mapId=stagedMap.Id});
            Check(modeStatus.GetProperty("ok").GetBoolean()&&
                  modeStatus.GetProperty("data").GetProperty(
                      "launchModeApprovalRequired").GetBoolean()&&
                  modeStatus.GetProperty("data").GetProperty(
                      "confirmedStopped").GetBoolean(),
                "stopped import exposes unresolved mode AND physical stopped proof for UI selection");
            var deniedStart=await Call(modeBridge.HandleAsync,
                "historical.activeStartPreview",new{mapId=stagedMap.Id});
            Check(!deniedStart.GetProperty("ok").GetBoolean()&&
                  deniedStart.GetProperty("error").GetString()==
                    "UM-STORE-LAUNCH-MODE-APPROVAL-REQUIRED",
                "first direct ASA Start is denied while launch mode is unconfirmed");
            var approveApi=await Call(modeBridge.HandleAsync,
                "historical.loaderModePreview",
                new{mapId=stagedMap.Id,useApi=true});
            Check(approveApi.GetProperty("ok").GetBoolean()&&
                  approveApi.GetProperty("data").GetProperty("useApi").GetBoolean(),
                "stopped map can select ASA API Loader with explicit preview");
            var declined=await Call(modeBridge.HandleAsync,
                "historical.loaderModeApply",
                new{token=approveApi.GetProperty("data").GetProperty("token").GetString(),
                    confirm=false});
            Check(!declined.GetProperty("ok").GetBoolean()&&
                  stagedMap.HistoricalLaunchModeNeedsApproval,
                "no approval never changes launch mode or starts the server");
            var approveApiAgain=await Call(modeBridge.HandleAsync,
                "historical.loaderModePreview",
                new{mapId=stagedMap.Id,useApi=true});
            var selected=await Call(modeBridge.HandleAsync,
                "historical.loaderModeApply",
                new{token=approveApiAgain.GetProperty("data").GetProperty("token").GetString(),
                    confirm=true});
            Check(selected.GetProperty("ok").GetBoolean()&&
                  stagedMap.StartWithAsaApi&&
                  !stagedMap.HistoricalLaunchModeNeedsApproval&&
                  stagedMap.ApiLoaderPath.EndsWith("AsaApiLoader.exe",
                      StringComparison.OrdinalIgnoreCase),
                "explicit native approval selects Loader, clears gate and never launches ASA");
        }

        var duplicate=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[0],gamePort=7777,queryPort=27015});
        Check(!duplicate.GetProperty("ok").GetBoolean(),
            "duplicate original map cannot be silently registered twice");

        var preview=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[1],gamePort=7787,queryPort=27025});
        Check(preview.GetProperty("ok").GetBoolean(),
            "second local map may be staged independently");
        string secondToken=preview.GetProperty("data").GetProperty("token").GetString()!;
        string managerPinRoot=Path.Combine(manager,"original-store-v1-source");
        File.WriteAllText(Path.Combine(managerPinRoot,names[3]),source[3]+" ");
        var tampered=await Call(bridge,"historical.originalRegister",
            new{token=secondToken,confirm=true});
        Check(!tampered.GetProperty("ok").GetBoolean()&&
              cluster.Config.LocalMaps.Count==1,
            "changed Obsidian-owned pinned source rejects registration after preview");
        File.WriteAllText(Path.Combine(managerPinRoot,names[3]),source[3]);

        var launchPreview=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[1],gamePort=7787,queryPort=27025});
        Check(launchPreview.GetProperty("ok").GetBoolean(),
            "launch profile and original metadata both verified at preview");
        string changedLaunch=launchBytes.Replace("987654321","555555555",
            StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(managerPinRoot,"launch-profiles-v1.json"),
            changedLaunch);
        var deniedLaunch=await Call(bridge,"historical.originalRegister",
            new{
                token=launchPreview.GetProperty("data").GetProperty("token").GetString(),
                confirm=true
            });
        Check(!deniedLaunch.GetProperty("ok").GetBoolean()&&
              cluster.Config.LocalMaps.Count==1,
            "changed pinned mod load-order bytes after preview deny registration");
        File.WriteAllText(Path.Combine(managerPinRoot,"launch-profiles-v1.json"),
            launchBytes);

        // The retired Kova profile is no longer the running authority.
        // Mutating it after an explicit pinned import must NOT change the
        // registered immutable Obsidian source or block the next local map.
        File.WriteAllText(Path.Combine(local,names[3]),source[3]+" ");
        var independent=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[1],gamePort=7787,queryPort=27025});
        Check(independent.GetProperty("ok").GetBoolean(),
            "retired local Kova source changes do not invalidate verified Obsidian source");
        File.WriteAllText(Path.Combine(local,names[3]),source[3]);

        for(int i=1;i<5;i++)
        {
            var staged=await Call(bridge,"historical.originalPreview",
                new{mapId=ids[i],gamePort=7777+i*10,queryPort=27015+i*10});
            Check(staged.GetProperty("ok").GetBoolean(),
                "fresh original map preview accepted for map "+i);
            var receipt=await Call(bridge,"historical.originalRegister",
                new{
                    token=staged.GetProperty("data").GetProperty("token").GetString(),
                    confirm=true
                });
            Check(receipt.GetProperty("ok").GetBoolean(),
                "original map registration succeeds for map "+i);
        }
        Check(cluster.Config.LocalMaps.Count==5&&
              cluster.Config.LocalMaps.All(m=>m.HistoricalStoreProtected),
            "five original local Store map registrations are available in real Manager registry");
        Check(names.Select(n=>Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(local,n)))))
                .SequenceEqual(hashes)&&
              Convert.ToHexString(SHA256.HashData(
                  File.ReadAllBytes(launchProfilePath)))==launchHash,
            "original four Store metadata and launch order remain byte-identical after imports");

        // The same native historical bridge must keep read-only parity
        // available after Kova's synthetic local directory disappears.
        string temporarilyMissingKova=local+"-bridge-offline";
        Directory.Move(local,temporarilyMissingKova);
        try
        {
            var withoutKova=await Call(bridge,"historical.originalList");
            Check(withoutKova.GetProperty("ok").GetBoolean()&&
                  withoutKova.GetProperty("data").GetProperty("maps").GetArrayLength()==5,
                "imported original map roster remains readable with Obsidian source pin and no Kova profile");
        }
        finally{Directory.Move(temporarilyMissingKova,local);}

        // A second empty Manager profile imports all five original maps in ONE
        // ConfigStore write, without ever calling original Kova/ASA mutation.
        string batchManager=Path.Combine(baseRoot,"atomic-batch-manager");
        await using var batchCluster=new ClusterService(
            "direct-kova-batch-test",batchManager,
            _=>new ProcessCheck(false,"isolated synthetic stopped",null,true));
        bool uncertainFifth=true;
        using var batchBridge=new DesktopHistoricalStoreImportBridge(
            batchCluster,map=>map.HistoricalMapKey==mapKeys[4]&&uncertainFifth
                ?new ProcessCheck(false,"synthetic uncertain",null,false)
                :new ProcessCheck(false,"isolated synthetic stopped",null,true),local);
        var deniedBatch=await Call(batchBridge,"historical.originalBatchPreview");
        Check(!deniedBatch.GetProperty("ok").GetBoolean()&&
              batchCluster.Config.LocalMaps.Count==0,
            "one unknown original process denies all-map import before ANY registration");
        uncertainFifth=false;
        var preparedBatch=await Call(batchBridge,"historical.originalBatchPreview");
        Check(preparedBatch.GetProperty("ok").GetBoolean()&&
              preparedBatch.GetProperty("data").GetProperty("localMaps").GetInt32()==5,
            "all five original map identities and stopped processes pass one batch preview");
        string batchToken=preparedBatch.GetProperty("data").GetProperty("token").GetString()!;
        var noConsent=await Call(batchBridge,"historical.originalBatchRegister",
            new{token=batchToken,confirm=false});
        Check(!noConsent.GetProperty("ok").GetBoolean()&&
              batchCluster.Config.LocalMaps.Count==0,
            "atomic import requires one explicit operator confirmation");
        var batchResult=await Call(batchBridge,"historical.originalBatchRegister",
            new{token=batchToken,confirm=true});
        Check(batchResult.GetProperty("ok").GetBoolean()&&
              batchResult.GetProperty("data").GetProperty("importedMaps").GetInt32()==5&&
              batchCluster.Config.LocalMaps.Count==5&&
              batchCluster.Config.LocalMaps.Select(m=>m.Id.ToString("D")).SequenceEqual(ids),
            "one approved native batch registers five original GUIDs atomically");
        Check(batchCluster.Config.LocalMaps.All(m=>m.HistoricalStoreProtected&&
              m.HistoricalLaunchModeNeedsApproval&&
              !m.AutoStart&&m.RconPort==0),
            "all batch-imported maps require explicit launch-mode selection before first Start");
        var afterBatch=await Call(batchBridge,"historical.originalBatchPreview");
        Check(!afterBatch.GetProperty("ok").GetBoolean(),
            "repeating an already imported five-map batch does not duplicate entries");
        Check(names.Select(n=>Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(local,n)))))
                .SequenceEqual(hashes)&&
              Convert.ToHexString(SHA256.HashData(
                  File.ReadAllBytes(launchProfilePath)))==launchHash,
            "atomic five-map batch leaves every synthetic original metadata byte unchanged");
    }
}
