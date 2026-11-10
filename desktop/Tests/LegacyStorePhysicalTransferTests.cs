using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UntamedDesktop;
using UntamedManager.Core;

// Actual Windows file-ID read on isolated fixed-disk test folders; only
// original-Kova-shaped JSON bytes in this PRIVATE synthetic fixture.
// The source UNC path is deliberately fictitious and must never be opened.
internal static class LegacyStorePhysicalTransferTests
{
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(
            "PHYSICAL STORE TEST FAILED: "+reason);
        Console.WriteLine("PASS physical Store transfer: "+reason);
    }
    private static string Hash(string text)=>Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<JsonElement> Call(DesktopLanSettingsBridge bridge,string method)
    {
        string response=await bridge.HandleAsync(JsonSerializer.Serialize(
            new{id=Guid.NewGuid().ToString("D"),method}));
        using var parsed=JsonDocument.Parse(response);
        return parsed.RootElement.Clone();
    }

    private static async Task<JsonElement> CallExpansion(
        DesktopClusterRegistryBridge bridge,Guid mapId,string clusterId)
    {
        using var doc=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(new{
                id=Guid.NewGuid().ToString("D"),
                method="cluster.historicalExpansionInspect",mapId,clusterId
            })));
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> CallHistoricalBootstrap(
        DesktopHistoricalStoreImportBridge bridge,string method,
        string? token=null,string? typedApproval=null)
    {
        var request=new Dictionary<string,object?>{
            ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
        };
        if(token!=null)
        {
            request["token"]=token;
            request["confirm"]=true;
            request["typedApproval"]=typedApproval;
        }
        using var json=JsonDocument.Parse(await bridge.HandleAsync(
            JsonSerializer.Serialize(request)));
        return json.RootElement.Clone();
    }

    public static async Task Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"synthetic-physical-transfer");
        string original=Path.Combine(root,"original-four-files");
        string physical=Path.Combine(root,"physical-UNC-id-fixture");
        string other=Path.Combine(root,"different-physical-UNC-id-fixture");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(physical);
        Directory.CreateDirectory(other);
        string[] names={"TheIsland","Astraeos","Valguero","Extinction","Genesis1"};
        string[] ids=Enumerable.Range(0,5).Select(_=>Guid.NewGuid().ToString("D")).ToArray();
        string session=Guid.NewGuid().ToString("D");
        string device=Guid.NewGuid().ToString("D");
        string fakeUnc=@"\\synthetic-test-server\transfer-share";
        var members=ids.Select((id,i)=>new{
            id,map=names[i],root=Path.Combine(root,"fake-map-"+i)
        }).ToArray();
        var cluster=new{
            id=VaultProtocol.ClusterId,mode="shared",path=fakeUnc,members
        };
        var mapEntries=ids.Select((id,i)=>
            new KeyValuePair<string,object>(id,new{
                phase="active",sharedSession=session,root=members[i].root,
                map=names[i],backupId=Guid.NewGuid().ToString("D")
            })).ToDictionary(x=>x.Key,x=>x.Value);
        var sourceStore=new{schema=1,maps=mapEntries};
        var sourceDevice=new{schema=1,id=device};
        string configHash=StoreV1Fingerprint.Compute(
            JsonSerializer.SerializeToElement(cluster));
        var sourceLock=new{
            schema=1,session,selected=ids,configHash,config=cluster
        };
        string[] files={
            Path.Combine(original,"cluster-v1.json"),
            Path.Combine(original,"cluster-store-v1.json"),
            Path.Combine(original,"shared-store-device-v1.json"),
            Path.Combine(original,"shared-store-lock-v1.json")
        };
        string[] originalJson={
            JsonSerializer.Serialize(cluster),JsonSerializer.Serialize(sourceStore),
            JsonSerializer.Serialize(sourceDevice),JsonSerializer.Serialize(sourceLock)
        };
        for(int i=0;i<files.Length;i++)File.WriteAllText(files[i],originalJson[i]);
        string[] beforeHashes=files.Select(f=>Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(f)))).ToArray();
        var report=LegacyStoreV1LocalReader.Inspect(original);
        Check(report.LocalSnapshotReady&&report.Maps.Length==5&&
              report.Maps.All(m=>m.Active&&m.Selected&&m.SessionMatches),
            "synthetic original Kova four-file source validates without accessing a real profile");

        // A parseable all-zero GUID is NOT an original identity. Before this
        // regression, zero device/session/map/backup IDs could still produce a
        // ready five-map source and misleading two-host metadata evidence.
        string zeroId=Guid.Empty.ToString("D");
        void RejectModifiedOriginal(string[] changed,string reason)
        {
            try
            {
                for(int i=0;i<files.Length;i++)
                    File.WriteAllText(files[i],changed[i]);
                bool ready=false;
                try{ready=LegacyStoreV1LocalReader.Inspect(original).LocalSnapshotReady;}
                catch(InvalidDataException){/* rejected as invalid source */}
                Check(!ready,reason);
            }
            finally
            {
                for(int i=0;i<files.Length;i++)
                    File.WriteAllText(files[i],originalJson[i]);
            }
        }

        var badDevice=originalJson.ToArray();
        badDevice[2]=badDevice[2].Replace(device,zeroId,StringComparison.Ordinal);
        RejectModifiedOriginal(badDevice,
            "all-zero original device ID cannot pass the read-only Store source gate");

        var badSession=originalJson.Select(text=>
            text.Replace(session,zeroId,StringComparison.Ordinal)).ToArray();
        RejectModifiedOriginal(badSession,
            "consistent but all-zero original session ID cannot certify both peers");

        var badMap=originalJson.Select(text=>
            text.Replace(ids[0],zeroId,StringComparison.Ordinal)).ToArray();
        using(var changedCluster=JsonDocument.Parse(badMap[0]))
        {
            string changedHash=StoreV1Fingerprint.Compute(changedCluster.RootElement);
            badMap[3]=badMap[3].Replace(configHash,changedHash,StringComparison.Ordinal);
        }
        RejectModifiedOriginal(badMap,
            "self-consistent original five-map source containing an all-zero member ID is rejected");

        using(var storeDoc=JsonDocument.Parse(originalJson[1]))
        {
            string backup=storeDoc.RootElement.GetProperty("maps")
                .GetProperty(ids[0]).GetProperty("backupId").GetString()!;
            var badBackup=originalJson.ToArray();
            badBackup[1]=badBackup[1].Replace(backup,zeroId,StringComparison.Ordinal);
            RejectModifiedOriginal(badBackup,
                "all-zero original backup ID cannot be reported as a ready source");
        }
        Check(files.Select(f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
                .SequenceEqual(beforeHashes),
            "negative identity fixtures restore the four original synthetic files byte-for-byte");

        // REAL GetFileInformationByHandleEx file-ID probe on isolated fixed
        // local test folders, injected only as UNC mapping in native tests.
        string physicalTag=StorePathIdentity.LocalDirectoryFingerprint(physical);
        string sameTag=StorePathIdentity.LocalDirectoryFingerprint(
            Path.Combine(root,"physical-UNC-id-fixture"));
        string differentTag=StorePathIdentity.LocalDirectoryFingerprint(other);
        Check(physicalTag==sameTag&&physicalTag!=differentTag,
            "real Windows volume/file ID distinguishes same physical directory from another folder");
        string managerA=Guid.NewGuid().ToString("N");
        var witness=LegacyStoreV1PhysicalTransferEvidence.Capture(
            original,managerA,unc=>{
                Check(unc==fakeUnc,
                    "original transfer UNC is obtained from four locked Kova-shape files, not a renderer-supplied path");
                return physicalTag;
            });
        Check(witness.OriginalMetadataUnchanged&&witness.HostId==managerA&&
              witness.PhysicalDirectoryTag==physicalTag&&
              witness.SessionTag==report.SessionTag&&
              witness.OriginalFourFilesTag==report.OriginalFourFilesTag&&
              !witness.LiveStoreWriterCertified&&!witness.ProductionRestoreAllowed,
            "explicit on-demand physical witness pins the original session and denies writer/restore authority");
        Check(files.Select(f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
                .SequenceEqual(beforeHashes),
            "UNC file-ID witness leaves all original synthetic metadata byte-identical");
        bool linkedMutationDenied=false;
        _=LegacyStoreV1PhysicalTransferEvidence.Capture(original,managerA,_=>{
            try{File.WriteAllText(files[0],"THIS MUST NOT OVERWRITE AN ORIGINAL OPEN FILE");}
            catch(IOException){linkedMutationDenied=true;}
            return physicalTag;
        });
        Check(linkedMutationDenied&&File.ReadAllText(files[0])==originalJson[0],
            "original metadata handle blocks a competing writer for the entire physical UNC inspection");

        var local=new LegacyStoreV1PeerProof(true,true,5,report.SessionTag,
            report.SharedPathTag,report.DeviceTag,false,false,"SYNTHETIC_ORIGINAL")
        {
            PhysicalDirectoryTag=physicalTag,PhysicalObservedUtc=witness.ObservedUtc,
            OriginalFourFilesTag=report.OriginalFourFilesTag
        };
        var remote=local with{DeviceTag=Hash("other-synthetic-original-device")};
        var accepted=LegacyStoreV1PairComparison.Compare(local,remote,true);
        Check(accepted.CopiedMetadataPairMatches&&accepted.BothPhysicalWitnessesFresh&&
              accepted.SamePhysicalTransferDirectory&&!accepted.LiveWriterCertified&&
              !accepted.CharacterTravelVerified,
            "fresh encrypted-LAN-shaped peer witnesses prove same file ID without granting original Store writer");
        var different=LegacyStoreV1PairComparison.Compare(local,
            remote with{PhysicalDirectoryTag=differentTag},true);
        Check(different.CopiedMetadataPairMatches&&
              different.BothPhysicalWitnessesFresh&&
              !different.SamePhysicalTransferDirectory,
            "two identical UNC strings and valid sessions never override different physical file IDs");
        var differentUnc=LegacyStoreV1PairComparison.Compare(local,
            remote with{SharedPathTag=Hash("UNC_ALIAS_DIFFERENT_FROM_ORIGINAL")},true);
        Check(!differentUnc.SameSharedPath&&!differentUnc.CopiedMetadataPairMatches&&
              !differentUnc.BothPhysicalWitnessesFresh&&
              !differentUnc.SamePhysicalTransferDirectory&&
              differentUnc.StatusCode=="STORE_PEER_UNC_DIFFERS",
            "same Windows volume/file ID cannot certify transfer when the original UNC paths differ");
        var noWitness=LegacyStoreV1PairComparison.Compare(
            local,remote with{PhysicalDirectoryTag="",PhysicalObservedUtc=null},true);
        var stale=LegacyStoreV1PairComparison.Compare(local,remote with{
            PhysicalObservedUtc=DateTimeOffset.UtcNow.AddMinutes(-10)},true);
        var replay=LegacyStoreV1PairComparison.Compare(local,remote,false);
        var sameDevice=LegacyStoreV1PairComparison.Compare(local,
            remote with{DeviceTag=local.DeviceTag},true);
        Check(!noWitness.SamePhysicalTransferDirectory&&
              !stale.SamePhysicalTransferDirectory&&
              !replay.SamePhysicalTransferDirectory&&
              !sameDevice.SamePhysicalTransferDirectory,
            "missing, expired, stale LAN or same-device witness fails closed");

        string managerProfile=Path.Combine(root,"synthetic-manager-profile");
        var currentLocalSource=local with{PhysicalDirectoryTag="",
            PhysicalObservedUtc=null};
        await using(var manager=new ClusterService("physical-test",managerProfile,
            _=>new ProcessCheck(false,"Synthetic stopped only",null,true),
            legacyStoreProof:()=>currentLocalSource,
            physicalStoreProbe:host=>witness with{HostId=Guid.Parse(host).ToString("N")}))
        {
            // Native end-to-end read-only preview for a future map that
            // must eventually join the EXISTING source-pinned Kova cluster.
            // The real profile is never used: UNC identity is injected into
            // this synthetic test only and no Store writer is opened.
            foreach(var member in members)
                manager.AddMap(new LocalMap{
                    Id=Guid.Parse(member.id),Name=member.map,
                    HistoricalMapKey=member.map,
                    HistoricalStoreProtected=true,
                    HistoricalArchiveSha256=report.OriginalFourFilesTag
                });
            var originalRegistered=manager.Registry.CreateCluster(
                VaultProtocol.ClusterId,"Synthetic original Kova",fakeUnc);
            foreach(var member in members)
                manager.Config.ClusterRegistry.MapAssignments.Add(
                    new ClusterMapAssignment{
                        MapId=Guid.Parse(member.id),
                        ClusterId=originalRegistered.ClusterId
                    });
            Guid futureId=Guid.NewGuid();
            manager.AddMap(new LocalMap{
                Id=futureId,Name="Synthetic Future Atlantis",
                InstallationPath=Path.Combine(root,"future-map-install")
            });
            var beforeRevision=manager.Registry.Snapshot().Revision;
            using(var expansionBridge=new DesktopClusterRegistryBridge(
                manager,original,(first,second)=>
                    first==fakeUnc&&second==fakeUnc))
            {
                var expansion=await CallExpansion(expansionBridge,futureId,
                    originalRegistered.ClusterId);
                var inspected=expansion.GetProperty("data");
                Check(expansion.GetProperty("ok").GetBoolean() &&
                      inspected.GetProperty("originalSourceVerified").GetBoolean() &&
                      inspected.GetProperty("originalFivePreserved").GetBoolean() &&
                      inspected.GetProperty("samePhysicalTransferDirectory").GetBoolean() &&
                      !inspected.GetProperty("newMapStoreEnrollmentAllowed").GetBoolean() &&
                      !inspected.GetProperty("remoteWriterSessionCertified").GetBoolean() &&
                      !inspected.GetProperty("serverStarted").GetBoolean() &&
                      inspected.GetProperty("blockers").EnumerateArray()
                          .Any(x=>x.GetString()=="UM-EXPANSION-FRESH-ASA-SETUP-REQUIRED") &&
                      !expansion.GetRawText().Contains(fakeUnc,StringComparison.Ordinal),
                    "new-map Kova preflight reads exact original source and physical identity without authorizing Store enrollment");

                var another=manager.Registry.CreateCluster("unrelated-lab",
                    "Unrelated Synthetic",Path.Combine(root,"unrelated"));
                var mismatch=await CallExpansion(expansionBridge,futureId,
                    another.ClusterId);
                Check(mismatch.GetProperty("ok").GetBoolean() &&
                      !mismatch.GetProperty("data").GetProperty("localPreflightReady").GetBoolean() &&
                      mismatch.GetProperty("data").GetProperty("blockers").EnumerateArray()
                        .Any(x=>x.GetString()=="UM-EXPANSION-SOURCE-CLUSTER-ID-DIFFERS"),
                    "another cluster ID cannot impersonate historical Kova source");
            }
            Check(files.Select(f=>Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(f))))
                  .SequenceEqual(beforeHashes) &&
                  manager.Registry.Snapshot().Revision==beforeRevision+1,
                "historical expansion preflight leaves source files and registry unchanged");
            var before=manager.ReadLegacyStoreV1PeerProof();
            Check(before?.PhysicalDirectoryTag=="",
                "Manager never reads a Kova physical UNC file ID on startup or background polling");
            var bridge=new DesktopLanSettingsBridge(manager);
            var probe=await Call(bridge,"lan.physicalStoreProbe");
            Check(probe.GetProperty("ok").GetBoolean()&&
                  probe.GetProperty("data").GetProperty("localPhysicalDirectoryIdVerified").GetBoolean()&&
                  !probe.GetProperty("data").GetProperty("productionRestoreAllowed").GetBoolean()&&
                  !probe.GetProperty("data").GetProperty("originalStoreFilesWritten").GetBoolean(),
                "native LAN bridge exposes explicit physical proof with no raw UNC or mutation authority");
            Check(manager.ReadLegacyStoreV1PeerProof()?.PhysicalDirectoryTag==physicalTag,
                "explicit physical proof appears only in local RAM for encrypted LAN status");
            currentLocalSource=currentLocalSource with{
                OriginalFourFilesTag=Hash("synthetic-original-selection-changed")};
            Check(manager.ReadLegacyStoreV1PeerProof()?.PhysicalDirectoryTag=="",
                "same session and UNC cannot reuse a physical witness after original four-file bytes change");
            currentLocalSource=currentLocalSource with{
                OriginalFourFilesTag=report.OriginalFourFilesTag};
            var status=await Call(bridge,"lan.status");
            Check(status.GetProperty("ok").GetBoolean()&&
                  !status.GetProperty("data").GetProperty("originalStorePair")
                    .GetProperty("samePhysicalTransferDirectory").GetBoolean(),
                "local proof ALONE cannot certify remote UNC identity or authorize a Store transfer");
        }
        // Separate isolated TEMP profile: the #123 expansion fixture
        // already imported its original five, so bootstrap test starts with
        // a distinct, empty Manager registry and no shared Store writes.
        await using(var bootstrap=new ClusterService("metadata-bootstrap",
            Path.Combine(root,"bootstrap-manager"),_=>new ProcessCheck(false,
                "Synthetic stopped only",null,true)))
        {
            // A separate native Manager metadata transaction must now be
            // able to import the five source-pinned historical memberships.
            // Do not silently rewrite any original Store file or the UNC.
            foreach(var originalMap in members)
                bootstrap.AddMap(new LocalMap{
                    Id=Guid.Parse(originalMap.id),
                    Name=originalMap.map,
                    HistoricalStoreProtected=true,
                    HistoricalMapKey=originalMap.map,
                    HistoricalArchiveSha256=report.OriginalFourFilesTag
                });
            using(var registryBridge=new DesktopHistoricalStoreImportBridge(
                manager,localLegacyDataDirectory:original))
            {
                var peek=await CallHistoricalBootstrap(registryBridge,
                    "historical.originalClusterPreview");
                Check(peek.GetProperty("ok").GetBoolean() &&
                      peek.GetProperty("data").GetProperty("fiveOriginalMaps")
                        .GetInt32()==5 &&
                      peek.GetProperty("data").GetProperty("missingAssignments")
                        .GetInt32()==5 &&
                      !peek.GetRawText().Contains(fakeUnc,StringComparison.Ordinal),
                    "source-pinned historical Cluster bootstrap preview does not disclose UNC or mutate registry");
                string approvalToken=peek.GetProperty("data").GetProperty("token").GetString()!;
                var denied=await CallHistoricalBootstrap(registryBridge,
                    "historical.originalClusterApply",approvalToken,"WRONG");
                Check(!denied.GetProperty("ok").GetBoolean() &&
                      bootstrap.Registry.Snapshot().ClusterRegistry.Clusters.Count==0,
                    "wrong source-metadata approval never creates a cluster");
                var complete=await CallHistoricalBootstrap(registryBridge,
                    "historical.originalClusterApply",approvalToken,
                    "EREDETI KOVA REGISZTER");
                Check(complete.GetProperty("ok").GetBoolean() &&
                      complete.GetProperty("data").GetProperty("addedCount").GetInt32()==5 &&
                      !complete.GetProperty("data").GetProperty("serverStarted").GetBoolean() &&
                      bootstrap.Registry.Snapshot().ClusterRegistry.MapAssignments.Count==5 &&
                      bootstrap.Registry.Snapshot().ClusterRegistry.Clusters.Single()
                        .ClusterId=="kovacluster",
                    "explicit native commit atomically adopts five original assignments in Manager metadata only");
                var replay=await CallHistoricalBootstrap(registryBridge,
                    "historical.originalClusterApply",approvalToken,
                    "EREDETI KOVA REGISZTER");
                Check(!replay.GetProperty("ok").GetBoolean(),
                    "one-use metadata approval token cannot be replayed");
                long revision=bootstrap.Registry.Snapshot().Revision;
                var again=await CallHistoricalBootstrap(registryBridge,
                    "historical.originalClusterPreview");
                Check(again.GetProperty("ok").GetBoolean() &&
                      again.GetProperty("data").GetProperty("missingAssignments")
                        .GetInt32()==0,
                    "already imported original five members are shown as an idempotent no-op");
                string repeatToken=again.GetProperty("data").GetProperty("token").GetString()!;
                var repeat=await CallHistoricalBootstrap(registryBridge,
                    "historical.originalClusterApply",repeatToken,
                    "EREDETI KOVA REGISZTER");
                Check(repeat.GetProperty("ok").GetBoolean() &&
                      repeat.GetProperty("data").GetProperty("addedCount").GetInt32()==0 &&
                      bootstrap.Registry.Snapshot().Revision==revision,
                    "repeat of exact original metadata does not change revision");
                bool transferRebindBlocked=false;
                try{bootstrap.Registry.UpdateCluster("KovaCluster",
                    "Original Kova Store-v1",Path.Combine(root,"foreign-transfer"));}
                catch(UnauthorizedAccessException){transferRebindBlocked=true;}
                Check(transferRebindBlocked,
                    "original Kova transfer metadata remains protected after bootstrap");
            }

        }
        await using(var restarted=new ClusterService("physical-test",managerProfile,
            _=>new ProcessCheck(false,"Synthetic stopped only",null,true),
            legacyStoreProof:()=>local with{PhysicalDirectoryTag="",
                PhysicalObservedUtc=null}))
        {
            Check(restarted.ReadLegacyStoreV1PeerProof()?.PhysicalDirectoryTag=="",
                "physical directory witness expires on Manager restart and is never persisted");
        }
        Check(files.Select(f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
                .SequenceEqual(beforeHashes),
            "all Kova-shaped synthetic session, lock and map selections remain unchanged");
    }
}
