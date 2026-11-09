using System;
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
            PhysicalDirectoryTag=physicalTag,PhysicalObservedUtc=witness.ObservedUtc
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
        await using(var manager=new ClusterService("physical-test",managerProfile,
            _=>new ProcessCheck(false,"Synthetic stopped only",null,true),
            legacyStoreProof:()=>local with{PhysicalDirectoryTag="",
                PhysicalObservedUtc=null},
            physicalStoreProbe:host=>witness with{HostId=Guid.Parse(host).ToString("N")}))
        {
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
            var status=await Call(bridge,"lan.status");
            Check(status.GetProperty("ok").GetBoolean()&&
                  !status.GetProperty("data").GetProperty("originalStorePair")
                    .GetProperty("samePhysicalTransferDirectory").GetBoolean(),
                "local proof ALONE cannot certify remote UNC identity or authorize a Store transfer");
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
