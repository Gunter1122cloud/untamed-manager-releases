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

        var local=LegacyStoreV1PeerProof.FromLocal(original) with
        {
            PhysicalDirectoryTag=physicalTag,PhysicalObservedUtc=witness.ObservedUtc
        };
        Check(local.LocalMetadataValid&&local.ActiveMapCount==5&&
              local.OriginalMapRosterTag.Length==64&&
              local.OriginalMapRosterTag.All(Uri.IsHexDigit)&&
              !local.OriginalFiveRegisteredInManager,
            "original five GUID/level pairs are fingerprinted from original local Kova files, not presumed registered");
        var syntheticBothEnrolled=LegacyStoreV1PairComparison.Compare(
            local with{OriginalFiveRegisteredInManager=true},
            local with{DeviceTag=Hash("other-device"),
                OriginalFiveRegisteredInManager=true},true);
        Check(syntheticBothEnrolled.BothOriginalFiveRegisteredInManagers&&
              !syntheticBothEnrolled.LiveWriterCertified&&
              !syntheticBothEnrolled.CharacterTravelVerified,
            "two independently registered original map rosters are historical identity proof only");
        var remote=local with{DeviceTag=Hash("other-synthetic-original-device")};
        // A three/four-host Manager cannot assume the FIRST fresh LAN peer
        // is its original historical Kova counterpart. Only exact shared
        // session/path and distinct original device can be considered.
        LanPeerObservation Peer(string host,LegacyStoreV1PeerProof? proof,
            bool fresh=true,string? announcedHost=null)=>
            new() {
                HostId=host,IsFresh=fresh,
                Snapshot=new HostStatus{
                    HostId=announcedHost??host,
                    HostName="synthetic-"+host[..8],
                    ObservedUtc=DateTimeOffset.UtcNow,
                    OriginalStoreV1=proof
                }
            };
        string unrelatedHost=Guid.NewGuid().ToString("N");
        string originalPeer=Guid.NewGuid().ToString("N");
        string anotherHost=Guid.NewGuid().ToString("N");
        var unrelated=remote with{
            SessionTag=Hash("different-cluster-not-original")
        };
        var remotePeers=new[]{
            Peer(unrelatedHost,unrelated),
            Peer(originalPeer,remote),
            Peer(anotherHost,null)
        };
        var resolved=LegacyStoreV1PairComparison.CompareAcrossPeers(
            local,remotePeers);
        Check(resolved.CopiedMetadataPairMatches&&
              resolved.SamePhysicalTransferDirectory&&
              resolved.OriginalPeerHostId==originalPeer&&
              !resolved.LiveWriterCertified&&!resolved.CharacterTravelVerified,
            "three fresh Managers choose the actual historical Kova peer, not the first unrelated host");
        var offlineOriginal=LegacyStoreV1PairComparison.CompareAcrossPeers(
            local,new[]{
                Peer(unrelatedHost,unrelated),
                Peer(originalPeer,remote,false),
                Peer(anotherHost,null)
            });
        Check(!offlineOriginal.CopiedMetadataPairMatches&&
              !offlineOriginal.SamePhysicalTransferDirectory&&
              offlineOriginal.OriginalPeerHostId==""&&
              offlineOriginal.StatusCode=="STORE_ORIGINAL_PEER_NOT_FOUND",
            "other online Managers cannot substitute for the offline original Store peer");
        var conflicting=LegacyStoreV1PairComparison.CompareAcrossPeers(
            local,new[]{
                Peer(originalPeer,remote),
                Peer(anotherHost,remote with{
                    DeviceTag=Hash("another-original-device")
                })
            });
        Check(!conflicting.CopiedMetadataPairMatches&&
              !conflicting.SamePhysicalTransferDirectory&&
              conflicting.OriginalPeerHostId==""&&
              conflicting.StatusCode=="STORE_ORIGINAL_PEER_AMBIGUOUS",
            "two independently paired candidate Store hosts are ambiguous and must fail closed");
        var spoofedStatus=LegacyStoreV1PairComparison.CompareAcrossPeers(
            local,new[]{Peer(originalPeer,remote,true,unrelatedHost)});
        Check(!spoofedStatus.CopiedMetadataPairMatches&&
              spoofedStatus.OriginalPeerHostId=="",
            "authenticated peer host ID must match its packet's own Manager host ID");
        var expiredStatus=LegacyStoreV1PairComparison.CompareAcrossPeers(
            local,new[]{
                new LanPeerObservation{
                    HostId=originalPeer,IsFresh=true,
                    Snapshot=new HostStatus{
                        HostId=originalPeer,
                        ObservedUtc=DateTimeOffset.UtcNow.AddHours(-2),
                        OriginalStoreV1=remote
                    }
                }
            });
        Check(!expiredStatus.CopiedMetadataPairMatches&&
              expiredStatus.StatusCode=="STORE_PEER_NOT_FRESH",
            "stale LAN snapshot cannot be revived by a misleading IsFresh bit");
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
        var currentPhysicalWitness=witness;
        bool simulateOfflineUnc=false;
        bool stillGreenWhileReprobing=false;
        ClusterService? reprobingManager=null;
        await using(var manager=new ClusterService("physical-test",managerProfile,
            _=>new ProcessCheck(false,"Synthetic stopped only",null,true),
            legacyStoreProof:()=>currentLocalSource,
            physicalStoreProbe:host=>{
                stillGreenWhileReprobing=reprobingManager?.ReadLegacyStoreV1PeerProof()
                    ?.PhysicalDirectoryTag==physicalTag;
                if(simulateOfflineUnc)throw new IOException(
                    "Synthetic SMB disconnected; no original UNC accessed");
                return currentPhysicalWitness with{
                    HostId=Guid.Parse(host).ToString("N")
                };
            }))
        {
            // Native end-to-end read-only preview for a future map that
            // must eventually join the EXISTING source-pinned Kova cluster.
            // The real profile is never used: UNC identity is injected into
            // this synthetic test only and no Store writer is opened.
            foreach(var member in members)
                manager.AddMap(new LocalMap{
                    Id=Guid.Parse(member.id),Name=member.map,
                    HistoricalMapKey=member.map,InstallationPath=member.root,
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
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==true,
                "all five original GUID/level pairs plus their pinned four-file fingerprints and Kova cluster assignments match actual Manager registrations");
            var membership=manager.Config.ClusterRegistry.MapAssignments[0];
            membership.ClusterId="unrelated-synthetic-cluster";
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==false,
                "reassigning one original map to an unrelated cluster revokes five-map registration parity without altering original source");
            membership.ClusterId=originalRegistered.ClusterId;
            var firstRegistered=manager.Config.LocalMaps.Single(m=>m.Id==Guid.Parse(members[0].id));
            string originalKey=firstRegistered.HistoricalMapKey;
            firstRegistered.HistoricalMapKey="WrongLevelIdentity";
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==false,
                "rebound original map level under the same GUID is not registered Kova parity");
            firstRegistered.HistoricalMapKey=originalKey;
            string originalRoot=firstRegistered.InstallationPath;
            firstRegistered.InstallationPath=Path.Combine(root,"wrong-original-source-root");
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==false,
                "same original GUID and level with a DIFFERENT registered ASA installation root is not source parity");
            firstRegistered.InstallationPath=originalRoot;
            string originalTag=firstRegistered.HistoricalArchiveSha256;
            firstRegistered.HistoricalArchiveSha256=Hash("invalid-original-kova-four-files");
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==false,
                "Manager registration from different four-file historical Store session is not source parity");
            firstRegistered.HistoricalArchiveSha256=originalTag;
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==true,
                "restoring exact synthetic source-pinned map identities recovers read-only registration parity");
            Guid futureId=Guid.NewGuid();
            manager.AddMap(new LocalMap{
                Id=futureId,Name="Synthetic Future Atlantis",
                InstallationPath=Path.Combine(root,"future-map-install")
            });
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==true,
                "an unrelated sixth fresh map does not erase the original Kova five-map registration");
            Guid independentId=Guid.NewGuid();
            manager.AddMap(new LocalMap{
                Id=independentId,Name="Synthetic Independent Historical Profile",
                HistoricalMapKey="Independent_WP",
                HistoricalStoreProtected=true,
                HistoricalArchiveSha256=Hash("independent historical four-file set"),
                InstallationPath=Path.Combine(root,"independent-store-root")
            });
            var independent=manager.Config.LocalMaps.Single(m=>m.Id==independentId);
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==true,
                "an unrelated historical Store profile never suppresses a valid original 5-map roster");
            independent.HistoricalArchiveSha256=report.OriginalFourFilesTag;
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==false,
                "a sixth historical map claiming the SAME original source fingerprint creates ambiguous ownership and fails closed");
            independent.HistoricalArchiveSha256=Hash("independent historical four-file set");
            Check(manager.ReadLegacyStoreV1PeerProof()?.OriginalFiveRegisteredInManager==true,
                "removing the synthetic original-source impersonation restores original roster parity without mutating Kova");
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
            reprobingManager=manager;
            var bridge=new DesktopLanSettingsBridge(manager);
            var probe=await Call(bridge,"lan.physicalStoreProbe");
            Check(probe.GetProperty("ok").GetBoolean()&&
                  probe.GetProperty("data").GetProperty("localPhysicalDirectoryIdVerified").GetBoolean()&&
                  !probe.GetProperty("data").GetProperty("productionRestoreAllowed").GetBoolean()&&
                  !probe.GetProperty("data").GetProperty("originalStoreFilesWritten").GetBoolean(),
                "native LAN bridge exposes explicit physical proof with no raw UNC or mutation authority");
            Check(manager.ReadLegacyStoreV1PeerProof()?.PhysicalDirectoryTag==physicalTag,
                "explicit physical proof appears only in local RAM for encrypted LAN status");
            Check(!stillGreenWhileReprobing,
                "a fresh explicit physical probe revokes old directory-ID status BEFORE the probe reads SMB");
            currentPhysicalWitness=currentPhysicalWitness with{
                PhysicalDirectoryTag=Hash("different-unverified-transfer-id")};
            var changedDirectory=await Call(bridge,"lan.physicalStoreProbe");
            Check(changedDirectory.GetProperty("ok").GetBoolean()&&
                  manager.ReadLegacyStoreV1PeerProof()?.PhysicalDirectoryTag==
                      currentPhysicalWitness.PhysicalDirectoryTag,
                "new physical directory identity replaces old Manager-only proof after an accepted reprobe");
            currentPhysicalWitness=currentPhysicalWitness with{
                OriginalFourFilesTag=Hash("changed-original-meta-not-accepted")};
            var invalidOriginal=await Call(bridge,"lan.physicalStoreProbe");
            Check(!invalidOriginal.GetProperty("ok").GetBoolean()&&
                  manager.ReadLegacyStoreV1PeerProof()?.PhysicalDirectoryTag=="",
                "failed original four-file match REVOKES prior matching physical identity");
            currentPhysicalWitness=witness;
            var recoveredProof=await Call(bridge,"lan.physicalStoreProbe");
            Check(recoveredProof.GetProperty("ok").GetBoolean()&&
                  manager.ReadLegacyStoreV1PeerProof()?.PhysicalDirectoryTag==physicalTag,
                "fresh independent original source and physical ID proof can recover after a failed reprobe");
            simulateOfflineUnc=true;
            var disconnected=await Call(bridge,"lan.physicalStoreProbe");
            Check(!disconnected.GetProperty("ok").GetBoolean()&&
                  manager.ReadLegacyStoreV1PeerProof()?.PhysicalDirectoryTag=="",
                "unreachable synthetic SMB source immediately revokes the earlier positive physical proof");
            simulateOfflineUnc=false;
            var onlineAgain=await Call(bridge,"lan.physicalStoreProbe");
            Check(onlineAgain.GetProperty("ok").GetBoolean()&&
                  manager.ReadLegacyStoreV1PeerProof()?.PhysicalDirectoryTag==physicalTag,
                "verified operator retry alone restores a physical witness; no implicit background re-probe");
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
                bootstrap,localLegacyDataDirectory:original))
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
                var bootstrapReplay=await CallHistoricalBootstrap(registryBridge,
                    "historical.originalClusterApply",approvalToken,
                    "EREDETI KOVA REGISZTER");
                Check(!bootstrapReplay.GetProperty("ok").GetBoolean(),
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
