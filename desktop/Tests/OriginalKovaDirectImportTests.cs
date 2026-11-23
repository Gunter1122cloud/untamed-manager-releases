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

        // The legacy writer lock remains an ordered FIVE-map authorization,
        // even after another independent local map is added to the current
        // cluster metadata. No additional Store owner/session is invented.
        {
            string extraId=Guid.NewGuid().ToString("D");
            string extraRoot=Path.Combine(baseRoot,"synthetic-eventmap");
            // Supply the same exact Windows layout as the original five.
            // Runtime fingerprint requires an actual local executable path,
            // even for this isolated, deliberately non-runnable fixture.
            string extraExe=Path.Combine(extraRoot,"ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(extraExe)!);
            File.WriteAllBytes(extraExe,new byte[]{0x4d,0x5a,0,0});
            // Local disk evidence is explicitly weaker than UE mount/readiness.
            string modPayload=Path.Combine(extraRoot,"ShooterGame","Binaries","Win64","ShooterGame","Mods","83374","123456789_111","MapFixture");
            Directory.CreateDirectory(modPayload);
            string mapManifest=Path.Combine(modPayload,"Manifest_UFSFiles_Win64.txt");
            File.WriteAllText(mapManifest,"MapFixture/Content/Asa_N_Chill_Hub.umap\t2026-01-01\n");
            string mapContainer=Path.Combine(modPayload,"MapFixture.utoc");
            File.WriteAllBytes(mapContainer,new byte[]{1});
            var partialContent=SupplementalMapContentProbe.Inspect(extraRoot,"Asa_N_Chill_Hub",new[]{"123456789","987654321"});
            Check(partialContent.LevelManifestProjects.Length==0&&partialContent.UnverifiedProjects.Length==2,
                "manifest alone or incomplete IoStore pair does not prove a map payload");
            File.WriteAllBytes(Path.Combine(modPayload,"MapFixture.ucas"),new byte[]{2});
            var contentProof=SupplementalMapContentProbe.Inspect(extraRoot,"Asa_N_Chill_Hub",new[]{"123456789","987654321"});
            Check(contentProof.State=="level-manifest-and-payload-found"&&contentProof.LevelManifestProjects.SequenceEqual(new[]{"123456789"})&&
                contentProof.UnverifiedProjects.SequenceEqual(new[]{"987654321"})&&contentProof.Fingerprint!=partialContent.Fingerprint,
                "local exact level manifest plus package pair is detected without downloading or asserting runtime readiness");
            var wrongLevel=SupplementalMapContentProbe.Inspect(extraRoot,"Future_WP",new[]{"123456789"});
            Check(wrongLevel.LevelManifestProjects.Length==0&&wrongLevel.State=="level-content-unverified",
                "same mod package cannot prove a different level");
            var currentMembers=members.Concat(new[]{
                new{id=extraId,map="Asa_N_Chill_Hub",root=extraRoot}
            }).ToArray();
            var currentCluster=new{
                id=VaultProtocol.ClusterId,mode="shared",
                path=originalCluster.path,members=currentMembers
            };
            var currentStore=states.ToDictionary(x=>x.Key,x=>x.Value);
            currentStore.Add(extraId,new{
                phase="active",sharedSession=Guid.NewGuid().ToString("D"),
                root=extraRoot,map="Asa_N_Chill_Hub",
                backupId=Guid.NewGuid().ToString("D")
            });
            string expandedCluster=JsonSerializer.Serialize(currentCluster);
            string expandedStore=JsonSerializer.Serialize(new{schema=1,maps=currentStore});
            var eventLaunch=new{
                id=extraId,root=extraRoot,
                launch=new{
                    map="Asa_N_Chill_Hub",gamePort=7897,queryPort=27197,
                    mods=new[]{"123456789","987654321"},
                    passiveMods=new[]{"123456789"}
                }
            };
            string expandedLaunch=JsonSerializer.Serialize(new{
                version=1,rows=launchRows.Concat(new[]{eventLaunch}).ToArray()
            });
            string cfile=Path.Combine(local,"cluster-v1.json");
            string sfile=Path.Combine(local,"cluster-store-v1.json");
            string lfile=Path.Combine(local,"shared-store-lock-v1.json");
            string oldLock=source[3];
            void SetExpanded()
            {
                File.WriteAllText(cfile,expandedCluster);
                File.WriteAllText(sfile,expandedStore);
                File.WriteAllText(lfile,oldLock);
                File.WriteAllText(launchProfilePath,expandedLaunch);
            }
            bool DeniesExpanded()
            {
                try
                {
                    var result=LegacyStoreV1OriginalImport.Inspect(local);
                    return !result.Maps.Select(x=>x.MapId.ToString("D"))
                        .SequenceEqual(ids);
                }
                catch(InvalidDataException){return true;}
                catch(IOException){return true;}
            }
            try
            {
                SetExpanded();
                var expandedStatus=LegacyStoreV1LocalReader.Inspect(local);
                var expandedImport=LegacyStoreV1OriginalImport.Inspect(local);
                Check(expandedStatus.LocalSnapshotReady&&
                    expandedStatus.Maps.Length==5&&
                    expandedStatus.Maps.All(x=>x.Selected)&&
                    expandedImport.Maps.Length==5&&
                    expandedImport.Maps.Select(x=>x.MapId.ToString("D"))
                        .SequenceEqual(ids)&&
                    !expandedImport.Maps.Any(x=>x.MapKey=="Asa_N_Chill_Hub")&&
                    expandedImport.FourFilesSha256!=discovered.FourFilesSha256,
                    "six local entries preserve exactly five original selected Store owners without adding Chillhub");
                var supplemental=expandedImport.AdditionalMaps.Single();
                Check(supplemental.MapId.ToString("D")==extraId&&
                    supplemental.MapKey=="Asa_N_Chill_Hub"&&supplemental.Active&&
                    !supplemental.SessionMatches&&supplemental.GamePort==7897&&
                    supplemental.QueryPort==27197&&supplemental.EnabledMods!.Length==2&&
                    supplemental.LaunchState=="source-launch-matched",
                    "additional Event source GUID and launch facts are visible without granting historical session ownership");
                bool refusedSession=false;
                try{_=LegacyStoreV1PinnedSource.RequireSupplemental(expandedImport,Guid.Parse(extraId));}
                catch(InvalidDataException e) when(e.Message=="UM-STORE-EXTRA-SESSION-MISMATCH"){refusedSession=true;}
                Check(refusedSession,"supplemental claim cannot override a mismatched session");
                Check(supplemental.SessionBinding=="foreign-or-invalid-session",
                    "a foreign supplemental session is distinct from a missing session field");
                // Exact separately-activated source shape seen on Event:
                // active + backup ID but NO sharedSession field. Prove it is
                // visible without ever granting the old writer authority.
                var independent=System.Text.Json.Nodes.JsonNode.Parse(expandedStore)!;
                independent["maps"]![extraId]!.AsObject().Remove("sharedSession");
                File.WriteAllText(sfile,independent.ToJsonString());
                try
                {
                    var sessionless=LegacyStoreV1OriginalImport.Inspect(local);
                    var missing=sessionless.AdditionalMaps.Single();
                    Check(missing.SessionBinding=="missing-shared-session"&&
                          !missing.SessionMatches&&missing.Active&&
                          missing.BackupIdentityPresent&&
                          missing.LaunchState=="source-launch-matched"&&
                          sessionless.Maps.Length==5,
                        "separately activated source remains an unselected, untrusted candidate");
                    bool denied=false;
                    try{_=LegacyStoreV1PinnedSource.RequireSupplemental(sessionless,Guid.Parse(extraId));}
                    catch(InvalidDataException ex) when(ex.Message=="UM-STORE-EXTRA-SESSION-MISMATCH")
                    {denied=true;}
                    Check(denied,
                        "source without sharedSession is not silently promoted to original Store writer");
                }
                finally {File.WriteAllText(sfile,expandedStore);}
                // Missing, foreign and matched Store session cases remain
                // three distinct source facts, never authority by inference.
                
                var validExtra=expandedImport with{AdditionalMaps=new[]{supplemental with{SessionMatches=true}}};
                var extraOwner=new LocalMap{Id=Guid.Parse(extraId),HistoricalStoreProtected=true,
                    HistoricalSupplementalSource=true,HistoricalArchiveSha256=validExtra.FourFilesSha256,
                    HistoricalMapKey=supplemental.MapKey,InstallationPath=supplemental.OriginalInstallationRoot};
                LegacyStoreV1PinnedSource.RequireImmutableIdentity(extraOwner,validExtra);
                Check(validExtra.Maps.Length==5&&LegacyStoreV1PinnedSource.RequireSupplemental(validExtra,extraOwner.Id).MapId==extraOwner.Id,
                    "supplemental source validates separately without adding an original selected map");
                await using(var expandedManager=new ClusterService(
                    "six-member-event-registry-test",
                    Path.Combine(baseRoot,"six-member-event-registry-profile")))
                {
                    foreach(var historicalRow in expandedImport.Maps)
                        expandedManager.AddMap(new LocalMap{
                            Id=historicalRow.MapId,
                            Name=historicalRow.MapKey,
                            InstallationPath=historicalRow.OriginalInstallationRoot,
                            HistoricalStoreProtected=true,
                            HistoricalArchiveSha256=expandedImport.FourFilesSha256,
                            HistoricalMapKey=historicalRow.MapKey,
                            GamePort=historicalRow.GamePort,
                            QueryPort=historicalRow.QueryPort
                        });
                    using var expandedBridge=new DesktopHistoricalStoreImportBridge(
                        expandedManager,localLegacyDataDirectory:local);
                    var supplementalList=await Call(expandedBridge,"historical.originalList");
                    var extra=supplementalList.GetProperty("data").GetProperty("additionalMaps")[0];
                    Check(extra.GetProperty("mapId").GetString()==extraId&&
                        !extra.GetProperty("selectedHistoricalOwner").GetBoolean()&&
                        !extra.GetProperty("storeWriterAuthorized").GetBoolean(),
                        "native supplemental source roster distinguishes Event from original five");
                    var mismatch=await Call(expandedBridge,"historical.supplementalPreview",
                        new{mapId=extraId,stoppedUseApi=true});
                    Check(!mismatch.GetProperty("ok").GetBoolean()&&
                        mismatch.GetProperty("error").GetString()=="UM-STORE-EXTRA-SESSION-MISMATCH",
                        "real native supplemental preview reports mismatched session without network or process mutation");
                    var directMismatch=await Call(expandedBridge,"historical.supplementalStart",new{mapId=extraId,stoppedUseApi=true});
                    Check(!directMismatch.GetProperty("ok").GetBoolean()&&directMismatch.GetProperty("error").GetString()=="UM-STORE-EXTRA-SESSION-MISMATCH"&&expandedManager.Config.LocalMaps.Count==5,
                        "one-click extra registration retains the exact shared-session guard and has no failure-side profile write");
                    var clusterPreview=await Call(expandedBridge,
                        "historical.originalClusterPreview");
                    Check(clusterPreview.GetProperty("ok").GetBoolean()&&
                        clusterPreview.GetProperty("data")
                            .GetProperty("fiveOriginalMaps").GetInt32()==5,
                        "historical cluster registry previews only five original members in six-member metadata");
                }
                var historical=expandedImport.Maps[0];
                var stillOriginal=new LocalMap{
                    Id=historical.MapId,Name="Previously imported historical map",
                    InstallationPath=historical.OriginalInstallationRoot,
                    HistoricalStoreProtected=true,
                    HistoricalArchiveSha256=discovered.FourFilesSha256,
                    HistoricalMapKey=historical.MapKey,
                    GamePort=historical.GamePort,QueryPort=historical.QueryPort
                };
                LegacyStoreV1PinnedSource.RequireImmutableIdentity(
                    stillOriginal,expandedImport);
                string approvedExpandedManager=Path.Combine(baseRoot,
                    "approved-expanded-store-source");
                Directory.CreateDirectory(approvedExpandedManager);
                LegacyStoreV1PinnedSource.Capture(local,approvedExpandedManager,
                    expandedImport.FourFilesSha256,expandedImport.LaunchProfileSha256);
                Check(LegacyStoreV1PinnedSource.HasApprovedLocalSource(
                    approvedExpandedManager,stillOriginal)&&
                    LegacyStoreV1PinnedSource.Resolve(approvedExpandedManager,
                        local,stillOriginal)!=local&&
                    stillOriginal.HistoricalArchiveSha256==
                        discovered.FourFilesSha256,
                    "explicit six-map source pin preserves immutable original five-map hash without granting event ownership");
                // A separate Manager pin is published before the single
                // profile+registry CAS. Simulate interruption after pin,
                // reopen the same Manager profile, reuse the original GUID.
                string supplementalProfile=Path.Combine(baseRoot,"supplemental-atomic-manager");
                await using(var firstManager=new ClusterService("supplemental-pin-test",supplementalProfile))
                {
                    foreach(var originalRow in expandedImport.Maps)
                        firstManager.AddMap(new LocalMap{Id=originalRow.MapId,Name=originalRow.MapKey,
                            InstallationPath=originalRow.OriginalInstallationRoot,
                            HistoricalStoreProtected=true,HistoricalArchiveSha256=expandedImport.FourFilesSha256,
                            HistoricalMapKey=originalRow.MapKey,GamePort=originalRow.GamePort,QueryPort=originalRow.QueryPort});
                    LegacyStoreV1PinnedSource.Capture(local,firstManager.Store.Root,
                        expandedImport.FourFilesSha256,expandedImport.LaunchProfileSha256);
                    var registry=firstManager.Registry.Snapshot();
                    firstManager.Registry.ImportHistoricalOriginalLocalMembership(VaultProtocol.ClusterId,
                        originalCluster.path,expandedImport.FourFilesSha256,
                        expandedImport.Maps.Select(x=>(x.MapId,x.MapKey)).ToArray(),registry.Revision);
                    var eligibleStore=currentStore.ToDictionary(x=>x.Key,x=>x.Value);
                    eligibleStore[extraId]=currentStore[extraId] with{sharedSession=session};
                    File.WriteAllText(sfile,JsonSerializer.Serialize(new{schema=1,maps=eligibleStore}));
                    var eligible=LegacyStoreV1OriginalImport.Inspect(local);
                    LegacyStoreV1PinnedSource.CaptureSupplemental(local,firstManager.Store.Root,
                        Guid.Parse(extraId),eligible.FourFilesSha256,eligible.LaunchProfileSha256);
                    Check(firstManager.Config.LocalMaps.Count==5&&
                        !firstManager.Registry.Snapshot().ClusterRegistry.MapAssignments.Any(x=>x.MapId==Guid.Parse(extraId)),
                        "published supplemental pin by itself has no profile or registry authority");
                }
                await using(var recovered=new ClusterService("supplemental-pin-test",supplementalProfile))
                {
                    var eligible=LegacyStoreV1OriginalImport.Inspect(local);
                    string originalPin=LegacyStoreV1PinnedSource.PreferredProfile(recovered.Store.Root,local);
                    string originalSha=LegacyStoreV1OriginalImport.Inspect(originalPin).FourFilesSha256;
                    string oldProfiles=JsonSerializer.Serialize(recovered.Config.LocalMaps);
                    string oldSchedules=JsonSerializer.Serialize(recovered.Config.RecurringSchedules);
                    LegacyStoreV1PinnedSource.CaptureSupplemental(local,recovered.Store.Root,
                        Guid.Parse(extraId),eligible.FourFilesSha256,eligible.LaunchProfileSha256);
                    string firstLaunchSha=eligible.LaunchProfileSha256;
                    File.AppendAllText(launchProfilePath,"\n ");
                    eligible=LegacyStoreV1OriginalImport.Inspect(local);
                    Check(eligible.LaunchProfileSha256!=firstLaunchSha,"legitimate source reserialization changes launch hash");
                    bool reconciled=LegacyStoreV1PinnedSource.ReconcileUnregisteredSupplemental(local,recovered,
                        Guid.Parse(extraId),eligible.FourFilesSha256,eligible.LaunchProfileSha256);
                    string orphanRoot=Path.Combine(recovered.Store.Root,"supplemental-store-v1",Guid.Parse(extraId).ToString("N"));
                    var archived=Directory.GetDirectories(orphanRoot,"orphan-archive-*").Single();
                    Check(reconciled&&LegacyStoreV1OriginalImport.Inspect(archived).LaunchProfileSha256==firstLaunchSha&&
                        recovered.Config.LocalMaps.Count==5&&
                        !LegacyStoreV1PinnedSource.ReconcileUnregisteredSupplemental(local,recovered,Guid.Parse(extraId),eligible.FourFilesSha256,eligible.LaunchProfileSha256),
                        "changed orphan pin archives prior exact proof; repeated current approval is idempotent");
                    var row=LegacyStoreV1PinnedSource.RequireSupplemental(eligible,Guid.Parse(extraId));
                    var map=new LocalMap{Id=row.MapId,Name=row.MapKey,InstallationPath=row.OriginalInstallationRoot,
                        HistoricalStoreProtected=true,HistoricalSupplementalSource=true,
                        HistoricalSupplementalImportedUtc=DateTimeOffset.UtcNow,
                        HistoricalArchiveSha256=eligible.FourFilesSha256,HistoricalMapKey=row.MapKey,
                        GamePort=row.GamePort!.Value,QueryPort=row.QueryPort!.Value,
                        HistoricalEnabledMods=row.EnabledMods!.ToList(),HistoricalPassiveMods=row.PassiveMods!.ToList()};
                    var registry=recovered.Registry.Snapshot();
                    bool rejectedStale=false;
                    try{recovered.RegisterSupplementalHistoricalMap(map,VaultProtocol.ClusterId,originalCluster.path,registry.Revision-1);}
                    catch(InvalidOperationException e) when(e.Message=="UM-STORE-EXTRA-REGISTRY-CHANGED"){rejectedStale=true;}
                    int originalGamePort=map.GamePort;
                    map.GamePort=recovered.Config.LocalMaps[0].GamePort;
                    bool rejectedPort=false;
                    try{recovered.RegisterSupplementalHistoricalMap(map,VaultProtocol.ClusterId,originalCluster.path,registry.Revision);}
                    catch(InvalidDataException e) when(e.Message=="UM-STORE-EXTRA-PORT-CONFLICT"){rejectedPort=true;}
                    map.GamePort=originalGamePort;
                    bool rejectedCluster=false;
                    try{recovered.RegisterSupplementalHistoricalMap(map,VaultProtocol.ClusterId,originalCluster.path+"-wrong",registry.Revision);}
                    catch(InvalidDataException e) when(e.Message=="UM-STORE-EXTRA-CLUSTER-MISMATCH"){rejectedCluster=true;}
                    Check(rejectedStale&&rejectedPort&&rejectedCluster&&
                        recovered.Config.LocalMaps.Count==5&&
                        registry.Revision==recovered.Registry.Snapshot().Revision,
                        "stale revision, conflicting UDP port and wrong transfer root cannot publish a supplemental profile");
                    recovered.RegisterSupplementalHistoricalMap(map,VaultProtocol.ClusterId,originalCluster.path,registry.Revision);
                    Check(recovered.Config.LocalMaps.Count==6&&
                        recovered.Registry.Snapshot().ClusterRegistry.MapAssignments.Any(x=>x.MapId==map.Id)&&
                        LegacyStoreV1PinnedSource.HasApprovedLocalSource(recovered.Store.Root,map)&&
                        LegacyStoreV1OriginalImport.Inspect(originalPin).FourFilesSha256==originalSha&&
                        oldProfiles==JsonSerializer.Serialize(recovered.Config.LocalMaps.Take(5).ToList())&&
                        oldSchedules==JsonSerializer.Serialize(recovered.Config.RecurringSchedules),
                        "retry after orphan pin atomically publishes one Event GUID and membership without changing original profiles/pin/schedules");
                    bool supplementalDuplicate=false;
                    try{recovered.RegisterSupplementalHistoricalMap(map,VaultProtocol.ClusterId,originalCluster.path,
                        recovered.Registry.Snapshot().Revision);}
                    catch(InvalidDataException){supplementalDuplicate=true;}
                    Check(supplementalDuplicate&&recovered.Config.LocalMaps.Count==6,"duplicate supplemental apply cannot add a second profile");
                    string currentLaunch=File.ReadAllText(launchProfilePath);
                    File.AppendAllText(launchProfilePath,"\n ");
                    try{
                        var changed=LegacyStoreV1OriginalImport.Inspect(local);
                        bool ownedDenied=false;
                        try{LegacyStoreV1PinnedSource.ReconcileUnregisteredSupplemental(local,recovered,map.Id,changed.FourFilesSha256,changed.LaunchProfileSha256);}
                        catch(InvalidDataException ex) when(ex.Message=="UM-STORE-EXTRA-PIN-ALREADY-OWNED"){ownedDenied=true;}
                        Check(ownedDenied&&LegacyStoreV1PinnedSource.HasApprovedLocalSource(recovered.Store.Root,map)&&
                            Directory.GetDirectories(orphanRoot,"orphan-archive-*").Length==1,
                            "committed supplemental map pin cannot be replaced by later source drift");
                    }finally{File.WriteAllText(launchProfilePath,currentLaunch);}

                }
                await using(var reopened=new ClusterService("supplemental-pin-test",supplementalProfile))
                {
                    var map=reopened.Config.LocalMaps.Single(x=>x.Id==Guid.Parse(extraId));
                    Check(map.HistoricalSupplementalImportedUtc.HasValue&&
                        LegacyStoreV1PinnedSource.HasApprovedLocalSource(reopened.Store.Root,map)&&
                        reopened.Registry.Snapshot().ClusterRegistry.MapAssignments.Any(x=>x.MapId==map.Id),
                        "restart retains audited Event registration and exact original GUID");
                    using(var replayBridge=new DesktopHistoricalStoreImportBridge(reopened,localLegacyDataDirectory:local))
                    {
                        var directReplay=await Call(replayBridge,"historical.supplementalStart",new{mapId=map.Id,stoppedUseApi=false});
                        Check(directReplay.GetProperty("ok").GetBoolean()&&directReplay.GetProperty("data").GetProperty("alreadyRegistered").GetBoolean()&&reopened.Config.LocalMaps.Count==6,
                            "replayed one-click registration returns existing audited GUID without adding a second map");
                    }
                    // Runtime source proof must resolve the registered supplemental pin,
                    // not fall back to the five-map peer writer selection or a remote share.
                    var fingerprint=LegacyStoreV1PinnedSource.RuntimeFingerprint(
                        reopened.Store.Root,Path.Combine(baseRoot,"removed-legacy-profile"),map);
                    Check(fingerprint.Length==64&&fingerprint.All(Uri.IsHexDigit)&&
                        ModStateController.Read(map).EnabledOrder.SequenceEqual(new[]{"123456789","987654321"})&&
                        ModStateController.Read(map).Passive.SequenceEqual(new[]{"123456789"}),
                        "reopened Event resolves a local lifecycle source fingerprint with exact enabled/passive mods");
                }
                // Seventh and eighth source entries may represent a future
                // official level and a distinct installation of an existing level.
                string seventh=Guid.NewGuid().ToString("D"), eighth=Guid.NewGuid().ToString("D");
                string seventhRoot=Path.Combine(baseRoot,"synthetic-future-asa"), eighthRoot=Path.Combine(baseRoot,"synthetic-second-event");
                var furtherMembers=currentMembers.Concat(new[]{
                    new{id=seventh,map="FutureOfficial_WP",root=seventhRoot},
                    new{id=eighth,map="Asa_N_Chill_Hub",root=eighthRoot}
                }).ToArray();
                var furtherStates=currentStore.ToDictionary(x=>x.Key,x=>x.Value);
                furtherStates[extraId]=currentStore[extraId] with{sharedSession=session};
                furtherStates[seventh]=currentStore[extraId] with{sharedSession=session,root=seventhRoot,map="FutureOfficial_WP",backupId=Guid.NewGuid().ToString("D")};
                furtherStates[eighth]=currentStore[extraId] with{sharedSession=session,root=eighthRoot,backupId=Guid.NewGuid().ToString("D")};
                var furtherLaunch=launchRows.Concat(new[]{eventLaunch,
                    new{id=seventh,root=seventhRoot,launch=new{map="FutureOfficial_WP",gamePort=7907,queryPort=27207,mods=Array.Empty<string>(),passiveMods=Array.Empty<string>()}},
                    new{id=eighth,root=eighthRoot,launch=new{map="Asa_N_Chill_Hub",gamePort=7917,queryPort=27217,mods=new[]{"123456789"},passiveMods=Array.Empty<string>()}}
                }).ToArray();
                File.WriteAllText(cfile,JsonSerializer.Serialize(new{id=VaultProtocol.ClusterId,mode="shared",path=originalCluster.path,members=furtherMembers}));
                File.WriteAllText(sfile,JsonSerializer.Serialize(new{schema=1,maps=furtherStates}));
                File.WriteAllText(launchProfilePath,JsonSerializer.Serialize(new{version=1,rows=furtherLaunch}));
                var future=LegacyStoreV1OriginalImport.Inspect(local);
                Check(future.Maps.Select(x=>x.MapId.ToString("D")).SequenceEqual(ids)&&future.AdditionalMaps.Length==3&&
                    LegacyStoreV1PinnedSource.RequireSupplemental(future,Guid.Parse(seventh)).MapKey=="FutureOfficial_WP"&&
                    LegacyStoreV1PinnedSource.RequireSupplemental(future,Guid.Parse(eighth)).OriginalInstallationRoot==eighthRoot,
                    "seventh future level and eighth separate same-level source preserve original owner GUIDs");
                SetExpanded();
                // Even if an expanded current cluster is represented in a
                // fresh matching lock, only its explicitly selected five
                // original GUIDs may enter the historical adapter.
                File.WriteAllText(lfile,JsonSerializer.Serialize(new{
                    schema=1,session,selected=ids,
                    configHash=StoreV1Fingerprint.Compute(
                        JsonSerializer.SerializeToElement(currentCluster)),
                    config=currentCluster
                }));
                Check(LegacyStoreV1OriginalImport.Inspect(local)
                    .Maps.Select(x=>x.MapId.ToString("D")).SequenceEqual(ids),
                    "newly hashed six-member metadata does not expand the five selected legacy maps");

                // Exact #2961 Aberration regression: both the current
                // 6-member cluster AND the saved lock configuration are
                // internally valid. The old Obsidian hash still identifies
                // the previously approved five-member metadata. This must
                // offer an EXTRA operator consent, never accept silently.
                var fullCurrent=LegacyStoreV1OriginalImport.Inspect(local);
                var fullStatus=LegacyStoreV1LocalReader.Inspect(local);
                var historicalProfiles=discovered.Maps.Select(row=>new LocalMap{
                    Id=row.MapId,Name=row.MapKey,
                    HistoricalStoreProtected=true,
                    HistoricalArchiveSha256=discovered.FourFilesSha256,
                    HistoricalMapKey=row.MapKey,
                    InstallationPath=row.OriginalInstallationRoot,
                    GamePort=row.GamePort,QueryPort=row.QueryPort
                }).ToArray();
                var originalAberration=historicalProfiles[0];
                Check(fullStatus.LocalSnapshotReady&&
                    fullStatus.ClusterHashVerified&&
                    fullStatus.CurrentLocalMemberCount==6&&
                    fullCurrent.CurrentLocalMemberCount==6&&
                    fullCurrent.FourFilesSha256!=discovered.FourFilesSha256&&
                    LegacyStoreV1ExpandedSourceReview.RequireExplicitReview(
                        originalAberration,historicalProfiles,fullStatus,fullCurrent)&&
                    historicalProfiles.All(x=>x.HistoricalArchiveSha256==
                        discovered.FourFilesSha256),
                    "six-member current-valid lock plus old five-map approval requires separate source consent, never hash adoption");
                var driftedProfiles=historicalProfiles.Select(x=>x).ToArray();
                driftedProfiles[1]=new LocalMap{
                    Id=historicalProfiles[1].Id,
                    HistoricalStoreProtected=true,
                    HistoricalArchiveSha256=discovered.FourFilesSha256,
                    HistoricalMapKey=historicalProfiles[1].HistoricalMapKey,
                    InstallationPath=Path.Combine(baseRoot,"unauthorized-drift-root")
                };
                bool driftRejected=false;
                try{_=LegacyStoreV1ExpandedSourceReview.RequireExplicitReview(
                    originalAberration,driftedProfiles,fullStatus,fullCurrent);}
                catch(InvalidDataException){driftRejected=true;}
                Check(driftRejected,
                    "changed root in ANY historical map blocks source reapproval even with correct current lock hash");
                var missingProfiles=historicalProfiles.Take(4).ToArray();
                bool missingRejected=false;
                try{_=LegacyStoreV1ExpandedSourceReview.RequireExplicitReview(
                    originalAberration,missingProfiles,fullStatus,fullCurrent);}
                catch(InvalidDataException){missingRejected=true;}
                Check(missingRejected,
                    "missing original Obsidian historical profile blocks source reapproval");
                var foreignHashProfiles=historicalProfiles.Select(x=>x).ToArray();
                foreignHashProfiles[1]=new LocalMap{
                    Id=historicalProfiles[1].Id,
                    HistoricalStoreProtected=true,
                    HistoricalArchiveSha256=new string('C',64),
                    HistoricalMapKey=historicalProfiles[1].HistoricalMapKey,
                    InstallationPath=historicalProfiles[1].InstallationPath
                };
                bool hashRejected=false;
                try{_=LegacyStoreV1ExpandedSourceReview.RequireExplicitReview(
                    originalAberration,foreignHashProfiles,fullStatus,fullCurrent);}
                catch(InvalidDataException){hashRejected=true;}
                Check(hashRejected,
                    "five historical map approvals must share the immutable original source fingerprint");

                // Adding a second, seventh, or many later local maps must
                // never require editing this guard again. Only five GUIDs
                // stay historical; every extra member/record is validated
                // by the same source reader, including the refreshed lock.
                foreach(int totalMaps in new[]{7,12,32,33})
                {
                    var further=Enumerable.Range(0,totalMaps-6).Select(n=>new{
                        id=Guid.NewGuid().ToString("D"),
                        map="EventMap"+n+"_WP",
                        root=Path.Combine(baseRoot,"future-event-"+n)
                    }).ToArray();
                    var expandedMembers=currentMembers.Concat(further).ToArray();
                    var expandedRecords=currentStore.ToDictionary(x=>x.Key,x=>x.Value);
                    foreach(var newMap in further)
                        expandedRecords.Add(newMap.id,new{
                            phase="active",
                            sharedSession=Guid.NewGuid().ToString("D"),
                            root=newMap.root,map=newMap.map,
                            backupId=Guid.NewGuid().ToString("D")
                        });
                    var expandedConfig=new{
                        id=VaultProtocol.ClusterId,mode="shared",
                        path=originalCluster.path,members=expandedMembers
                    };
                    var additionalLaunch=further.Select((m,n)=>new{
                        id=m.id,root=m.root,
                        launch=new{
                            map=m.map,gamePort=8000+n*2,
                            queryPort=28000+n*2,
                            mods=new[]{"123456789","987654321"},
                            passiveMods=new[]{"123456789"}
                        }
                    });
                    File.WriteAllText(cfile,JsonSerializer.Serialize(expandedConfig));
                    File.WriteAllText(sfile,JsonSerializer.Serialize(new{
                        schema=1,maps=expandedRecords
                    }));
                    File.WriteAllText(lfile,JsonSerializer.Serialize(new{
                        schema=1,session,selected=ids,
                        configHash=StoreV1Fingerprint.Compute(
                            JsonSerializer.SerializeToElement(expandedConfig)),
                        config=expandedConfig
                    }));
                    File.WriteAllText(launchProfilePath,JsonSerializer.Serialize(new{
                        version=1,rows=launchRows.Concat(new[]{eventLaunch})
                            .Concat(additionalLaunch).ToArray()
                    }));
                    if(totalMaps==33)
                    {
                        bool boundRejected=false;
                        try{_=LegacyStoreV1LocalReader.Inspect(local);}
                        catch(InvalidDataException){boundRejected=true;}
                        Check(boundRejected,
                            "out-of-bounds 33-member current source cannot authorize historical review");
                        continue;
                    }
                    var validated=LegacyStoreV1LocalReader.Inspect(local);
                    var inspected=LegacyStoreV1OriginalImport.Inspect(local);
                    Check(validated.LocalSnapshotReady&&
                        validated.ClusterHashVerified&&validated.LocalLockVerified&&
                        validated.CurrentLocalMemberCount==totalMaps&&
                        inspected.CurrentLocalMemberCount==totalMaps&&
                        inspected.SelectedHistoricalSubsetOfExpandedRoster&&
                        inspected.Maps.Length==5&&
                        inspected.Maps.Select(x=>x.MapId.ToString("D"))
                            .SequenceEqual(ids)&&
                        historicalProfiles.All(original=>
                            LegacyStoreV1ExpandedSourceReview.RequireExplicitReview(
                                original,historicalProfiles,validated,inspected))&&
                        historicalProfiles.All(original=>
                            original.HistoricalArchiveSha256==
                                discovered.FourFilesSha256),
                        "current lock supports "+totalMaps+
                        " local maps while preserving five historical sources and separate operator approval");
                    if(totalMaps==12)
                    {
                        var corrupt=expandedRecords.ToDictionary(x=>x.Key,x=>x.Value);
                        var extra=further[0];
                        corrupt[extra.id]=new{
                            phase="active",sharedSession=Guid.NewGuid().ToString("D"),
                            root=Path.Combine(baseRoot,"unauthorized-extra-map-root"),
                            map=extra.map,backupId=Guid.NewGuid().ToString("D")
                        };
                        File.WriteAllText(sfile,JsonSerializer.Serialize(new{
                            schema=1,maps=corrupt
                        }));
                        bool corruptDenied=false;
                        try{_=LegacyStoreV1OriginalImport.Inspect(local);}
                        catch(InvalidDataException){corruptDenied=true;}
                        Check(corruptDenied,
                            "tampered extra map root is rejected in a 12-member historical roster");
                    }
                }
                SetExpanded();

                // Historical selected[] and members[] may have different
                // valid orderings; do not invent an equality rule here.
                // Reordering selected[] still changes the FULL four-file
                // source tag and invalidates any previous approval token.
                SetExpanded();
                File.WriteAllText(lfile,JsonSerializer.Serialize(new{
                    schema=1,session,selected=ids.Reverse().ToArray(),
                    configHash=StoreV1Fingerprint.Compute(
                        JsonSerializer.SerializeToElement(originalCluster)),
                    config=originalCluster
                }));
                var reordered=LegacyStoreV1OriginalImport.Inspect(local);
                Check(reordered.Maps.Select(x=>x.MapId.ToString("D"))
                          .SequenceEqual(ids)&&
                    reordered.FourFilesSha256!=expandedImport.FourFilesSha256,
                    "reordered selected IDs never reuse an earlier four-file Store approval hash");
                SetExpanded();
                File.WriteAllText(lfile,JsonSerializer.Serialize(new{
                    schema=1,session,
                    selected=ids.Take(4).Concat(new[]{extraId}).ToArray(),
                    configHash=StoreV1Fingerprint.Compute(
                        JsonSerializer.SerializeToElement(originalCluster)),
                    config=originalCluster
                }));
                Check(DeniesExpanded(),
                    "independent event GUID cannot replace a historical selected member");
                SetExpanded();
                currentStore[ids[4]]=new{
                    phase="active",sharedSession=Guid.NewGuid().ToString("D"),
                    root=members[4].root,map=mapKeys[4],
                    backupId=Guid.NewGuid().ToString("D")
                };
                File.WriteAllText(sfile,JsonSerializer.Serialize(new{
                    schema=1,maps=currentStore
                }));
                Check(DeniesExpanded(),
                    "foreign session on selected historical map remains blocked");
                currentStore[ids[4]]=states[ids[4]];
                SetExpanded();
                File.WriteAllText(lfile,JsonSerializer.Serialize(new{
                    schema=1,session,selected=ids,
                    configHash=new string('A',64),config=originalCluster
                }));
                Check(DeniesExpanded(),
                    "tampered historical config fingerprint is never treated as a valid expanded roster");
                SetExpanded();
                var changedMembers=currentMembers.Select(x=>x.id==ids[0]
                    ?new{x.id,x.map,root=Path.Combine(baseRoot,"wrong-original-root")}
                    :x).ToArray();
                File.WriteAllText(cfile,JsonSerializer.Serialize(new{
                    id=VaultProtocol.ClusterId,mode="shared",
                    path=originalCluster.path,members=changedMembers
                }));
                Check(DeniesExpanded(),
                    "changed historical member root cannot be hidden by an added event map");
            }
            finally
            {
                for(int i=0;i<names.Length;i++)
                    File.WriteAllText(Path.Combine(local,names[i]),source[i]);
                File.WriteAllText(launchProfilePath,launchBytes);
            }
            Check(LegacyStoreV1OriginalImport.Inspect(local).FourFilesSha256==
                  discovered.FourFilesSha256,
                "expanded-roster tests restore every original synthetic Store source byte");
        }

        // Kova 1.8.3 explicitly accepts mod IDs of 1–12 digits. An
        // original five-map source with a short numeric ID is valid and
        // must not be refused by Obsidian solely for ID length.
        try
        {
            File.WriteAllText(launchProfilePath,launchBytes.Replace(
                "123456789","7",StringComparison.Ordinal));
            var shortIdSource=LegacyStoreV1OriginalImport.Inspect(local);
            Check(shortIdSource.Maps.Length==5&&
                  shortIdSource.Maps.All(m=>m.EnabledMods.SequenceEqual(
                      new[]{"7","987654321"})&&
                      m.PassiveMods.SequenceEqual(new[]{"7"})),
                "original Kova source accepts one-digit mod ID with exact passive order");
        }
        finally { File.WriteAllText(launchProfilePath,launchBytes); }
        Check(LegacyStoreV1OriginalImport.Inspect(local)
                .LaunchProfileSha256==launchHash,
            "synthetic original launch source restored byte-for-byte after short-ID test");

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
                using var registry=new DesktopClusterRegistryBridge(
                    existingManager,originalLegacyProfile:local);
                var expansion=await Call(registry.HandleAsync,
                    "cluster.historicalExpansionInspect",
                    new{mapId=firstSource.MapId,clusterId=VaultProtocol.ClusterId});
                Check(expansion.GetProperty("ok").GetBoolean()&&
                      expansion.GetProperty("data")
                        .GetProperty("originalSourceVerified").GetBoolean()&&
                      !expansion.GetProperty("data")
                        .GetProperty("newMapStoreEnrollmentAllowed").GetBoolean(),
                    "read-only historical expansion can verify the approved Obsidian-owned source while Kova is absent, without granting Store writer permissions");
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
        Check(LegacyStoreV1PinnedSource.HasApprovedLocalSource(pinnedManager,parityMap),
            "approved local Store source remains valid without another Kova DONE peer");
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
            File.Delete(existingGus);
            Check(LegacyStoreV1PinnedSource.RuntimeFingerprint(
                    pinnedManager,local,parityMap)==oldEpoch,
                "temporary INI test leaves imported map's source epoch unchanged");
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
        var invalidAgain=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[0],gamePort=7777,queryPort=7777});
        Check(!invalidAgain.GetProperty("ok").GetBoolean(),
            "rejected original map preview keeps previously approved import");
        var refreshed=await Call(bridge,"historical.originalPreview",
            new{mapId=ids[0],gamePort=7777,queryPort=27015});
        Check(refreshed.GetProperty("ok").GetBoolean(),
            "valid second preview replaces original map ticket without clearing others");
        string newToken=refreshed.GetProperty("data")
            .GetProperty("token").GetString()!;
        Check(newToken!=token,
            "original Store import replacement uses a fresh one-shot token");
        var replaced=await Call(bridge,"historical.originalRegister",
            new{token,confirm=true});
        Check(!replaced.GetProperty("ok").GetBoolean(),
            "superseded original Store import token cannot be replayed");
        token=newToken;
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

        // Complete the original post-import API/direct mode choice through
        // the native approval path, then exercise *real* stopped-map runtime
        // setting previews with UTF-16 BOM INI files on two independent maps.
        // Neither a mocked response nor an unapproved Store write qualifies.
        using(var modeChoice=new DesktopHistoricalActiveStartBridge(
            cluster,null,null,loaderModePreflight:(_,_)=>true))
        {
            foreach(int i in new[]{0,1})
            {
                var m=cluster.Config.LocalMaps.Single(x=>x.Id.ToString("D")==ids[i]);
                var modePreview=await Call(modeChoice.HandleAsync,
                    "historical.loaderModePreview",
                    new{mapId=m.Id,useApi=false});
                Check(modePreview.GetProperty("ok").GetBoolean(),
                    "synthetic imported original map mode can be explicitly confirmed");
                var modeApplied=await Call(modeChoice.HandleAsync,
                    "historical.loaderModeApply",
                    new{token=modePreview.GetProperty("data").GetProperty("token").GetString(),
                        confirm=true});
                Check(modeApplied.GetProperty("ok").GetBoolean()&&
                      !m.HistoricalLaunchModeNeedsApproval,
                    "real native direct ASA mode approval unblocks stopped-map settings");
                var gusPath=AsaInitialRconSettings.NewIniPath(m.InstallationPath);
                Directory.CreateDirectory(Path.GetDirectoryName(gusPath)!);
                var utf16=Encoding.Unicode;
                File.WriteAllBytes(gusPath,utf16.GetPreamble().Concat(
                    utf16.GetBytes("[ServerSettings]\r\n")).ToArray());
            }
        }
        var settingsTokens=new string[2];
        for(int i=0;i<2;i++)
        {
            var m=cluster.Config.LocalMaps.Single(x=>x.Id.ToString("D")==ids[i]);
            var status=await Call(bridge,"historical.runtimeSettingsStatus",
                new{mapId=m.Id});
            Check(status.GetProperty("ok").GetBoolean()&&
                  status.GetProperty("data").GetProperty("originalSourcePinned").GetBoolean(),
                "original local source-pinned runtime settings work without a remote peer");
            var runtimePreview=await Call(bridge,"historical.runtimeSettingsPreview",
                new{mapId=m.Id,gamePort=m.GamePort,queryPort=m.QueryPort,
                    clusterId=VaultProtocol.ClusterId,transferRoot=originalCluster.path});
            Check(runtimePreview.GetProperty("ok").GetBoolean(),
                "native original Store runtime preview decodes UTF-16 BOM GUS.ini");
            settingsTokens[i]=runtimePreview.GetProperty("data").GetProperty("token").GetString()!;
        }
        var firstSettingsMap=cluster.Config.LocalMaps.Single(x=>x.Id.ToString("D")==ids[0]);
        var deniedRuntime=await Call(bridge,"historical.runtimeSettingsPreview",
            new{mapId=firstSettingsMap.Id,gamePort=firstSettingsMap.GamePort,
                queryPort=firstSettingsMap.GamePort,
                clusterId=VaultProtocol.ClusterId,transferRoot=originalCluster.path});
        Check(!deniedRuntime.GetProperty("ok").GetBoolean(),
            "invalid Store runtime re-preview leaves both map approvals untouched");
        Check(settingsTokens[0]!=settingsTokens[1],
            "two original runtime port/cluster approvals have distinct tokens");
        for(int i=0;i<2;i++)
        {
            var applied=await Call(bridge,"historical.runtimeSettingsApply",
                new{token=settingsTokens[i],confirm=true,
                    typedApproval="OBSIDIAN MAP KONFIG"});
            Check(applied.GetProperty("ok").GetBoolean()&&
                  applied.GetProperty("data").GetProperty("updated").GetBoolean(),
                "one original map runtime commit does not revoke sibling approved settings");
            var replay=await Call(bridge,"historical.runtimeSettingsApply",
                new{token=settingsTokens[i],confirm=true,
                    typedApproval="OBSIDIAN MAP KONFIG"});
            Check(!replay.GetProperty("ok").GetBoolean(),
                "runtime settings commit is single-use even across distinct Store maps");
        }
        Check(names.Select(n=>Convert.ToHexString(SHA256.HashData(
                 File.ReadAllBytes(Path.Combine(local,n))))).SequenceEqual(hashes)&&
              File.ReadAllText(launchProfilePath)==launchBytes,
            "UTF-16 runtime settings approvals changed only Manager profiles, not original Store source files");

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
