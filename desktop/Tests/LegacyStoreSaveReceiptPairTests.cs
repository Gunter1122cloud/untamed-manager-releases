using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

// The original Kova/ASA installation and UNC share are never accessed.
// All five journals are new, synthetic Manager-owned files in isolated TEMP.
internal static class LegacyStoreSaveReceiptPairTests
{
    private const string SourceSha="AABBCCDDEEFF00112233445566778899"+
        "00112233445566778899AABBCCDDEEFF";
    private static string Sha(string x)=>Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(x)));
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(
            "HISTORICAL STORE SAVE PAIR TEST FAILED: "+reason);
        Console.WriteLine("PASS historical Store SaveComplete pairing: "+reason);
    }
    private static bool Denies(Action action)
    {
        try{action();return false;}
        catch(Exception e) when(e is InvalidDataException or
            InvalidOperationException or IOException or UnauthorizedAccessException)
        {return true;}
    }
    private static async Task<JsonElement> Call(
        DesktopLanSettingsBridge bridge,string method)
    {
        string raw=await bridge.HandleAsync(JsonSerializer.Serialize(
            new{id=Guid.NewGuid().ToString("D"),method}));
        using var doc=JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    public static async Task Run(string sandbox)
    {
        string root=Path.Combine(sandbox,"isolated-five-map-save-pair");
        Directory.CreateDirectory(root);
        var maps=Enumerable.Range(0,5).Select(i=>new LocalMap{
            Id=Guid.NewGuid(),Name="Synthetic "+i,
            HistoricalStoreProtected=true,
            HistoricalMapKey="SyntheticMap"+i,
            InstallationPath=Path.Combine(root,"synthetic-server-"+i)
        }).ToArray();
        var source=new LegacyStoreV1PeerProof(true,true,5,Sha("session"),
            Sha("source-UNC"),Sha("source-device"),false,false,
            "SYNTHETIC_LOCAL_ORIGINAL_NO_LIVE_WRITER")
        {OriginalFourFilesTag=Sha("all-original-four-file-bytes")};
        string hostId=Guid.NewGuid().ToString("D");
        var missing=LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
            root,hostId,source,maps);
        Check(missing.RecentCompletedMapReceipts==0&&
              !missing.AllFiveRecentLocalReceipts&&
              !missing.SharedWriterCertified&&!missing.ProductionRestoreAllowed,
            "missing receipts cannot become a local SaveComplete success");

        var firstFour=maps.Take(4).ToArray();
        foreach(var map in firstFour)
        {
            var operation=Guid.NewGuid();
            var pidStart=DateTimeOffset.UtcNow.AddMinutes(-5);
            LegacyStoreV1SaveEvidenceJournal.Begin(
                root,operation,map.Id,41001,pidStart,SourceSha);
            LegacyStoreV1SaveEvidenceJournal.Complete(
                root,operation,map.Id,41001,pidStart,SourceSha,
                DateTimeOffset.UtcNow,true,false);
        }
        var incomplete=LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
            root,hostId,source,maps);
        Check(incomplete.RecentCompletedMapReceipts==4&&
              !incomplete.AllFiveRecentLocalReceipts,
            "four of five local SaveComplete journals never becomes complete");
        var fifth=maps[4];
        var fifthOp=Guid.NewGuid();
        var fifthStart=DateTimeOffset.UtcNow.AddMinutes(-5);
        LegacyStoreV1SaveEvidenceJournal.Begin(
            root,fifthOp,fifth.Id,41002,fifthStart,SourceSha);
        Check(Denies(()=>LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
            root,hostId,source,maps)),
            "pending original local SaveWorld receipt fails closed");
        LegacyStoreV1SaveEvidenceJournal.Complete(root,fifthOp,
            fifth.Id,41002,fifthStart,SourceSha,
            DateTimeOffset.UtcNow,true,false);
        var completed=LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
            root,hostId,source,maps);
        Check(completed.RecentCompletedMapReceipts==5&&
              completed.AllFiveRecentLocalReceipts&&
              !completed.SharedWriterCertified&&
              !completed.BothHostsSaveCompleteCertified&&
              !completed.ProductionRestoreAllowed,
            "five durable receipts are HISTORICAL local-only evidence, never writer certification");
        var expired=LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
            root,hostId,source,maps,DateTimeOffset.UtcNow.AddMinutes(12));
        Check(expired.RecentCompletedMapReceipts==0&&
              !expired.AllFiveRecentLocalReceipts,
            "time-expired local receipts fail closed");
        Check(Denies(()=>LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
            root,hostId,source,maps.Take(4).ToArray()))&&
              Denies(()=>LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
                  root,hostId,source,maps.Concat(new[]{maps[0]}).ToArray()))&&
              Denies(()=>LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
                  root,hostId,source with{LocalMetadataValid=false},maps)),
            "missing, duplicated or unverifiable original local maps are denied");

        var local=source with{
            PhysicalDirectoryTag=Sha("physical-file-id"),
            PhysicalObservedUtc=DateTimeOffset.UtcNow,
            LocalSaveReceiptOriginalFourTag=source.OriginalFourFilesTag,
            LocalSaveReceiptSessionTag=source.SessionTag,
            LocalSaveReceiptObservedUtc=completed.ObservedUtc,
            RecentLocalSaveReceipts=completed.RecentCompletedMapReceipts,
            AllFiveRecentLocalSaveReceipts=completed.AllFiveRecentLocalReceipts
        };
        var remote=local with{DeviceTag=Sha("other-device")};
        var pair=LegacyStoreV1PairComparison.Compare(local,remote,true);
        Check(pair.SamePhysicalTransferDirectory&&
              pair.BothHaveFiveRecentHistoricalLocalSaveReceipts&&
              !pair.LiveWriterCertified&&!pair.CharacterTravelVerified,
            "matching two-host local receipt and physical tags still cannot claim live writer authority");
        var denied=LegacyStoreV1PairComparison.Compare(local,
            remote with{LocalSaveReceiptOriginalFourTag=Sha("mutated-source")},true);
        var noPhysical=LegacyStoreV1PairComparison.Compare(local,
            remote with{PhysicalDirectoryTag=""},true);
        var stalePeer=LegacyStoreV1PairComparison.Compare(local,remote,false);
        Check(!denied.BothHaveFiveRecentHistoricalLocalSaveReceipts&&
              !noPhysical.BothHaveFiveRecentHistoricalLocalSaveReceipts&&
              !stalePeer.BothHaveFiveRecentHistoricalLocalSaveReceipts,
            "changed source fingerprint, missing physical witness and stale LAN reject paired receipt success");

        string managerRoot=Path.Combine(root,"manager-profile");
        var mapCopies=maps.Select(x=>new LocalMap{
            Id=x.Id,Name=x.Name,HistoricalStoreProtected=true,
            HistoricalMapKey=x.HistoricalMapKey,
            InstallationPath=x.InstallationPath
        }).ToArray();
        LegacyStoreV1PeerProof current=source;
        await using(var manager=new ClusterService("pair-lab",root,
            _=>new ProcessCheck(false,"Synthetic stopped only",null,true),
            legacyStoreProof:()=>current))
        {
            foreach(var map in mapCopies)manager.AddMap(map);
            // The test Manager's actual journal root is manager.Store.Root,
            // so use a distinct local fixture there and leave Kova untouched.
            foreach(var map in mapCopies)
            {
                var operation=Guid.NewGuid();
                var start=DateTimeOffset.UtcNow.AddMinutes(-5);
                LegacyStoreV1SaveEvidenceJournal.Begin(manager.Store.Root,
                    operation,map.Id,41003,start,SourceSha);
                LegacyStoreV1SaveEvidenceJournal.Complete(manager.Store.Root,
                    operation,map.Id,41003,start,SourceSha,
                    DateTimeOffset.UtcNow,true,false);
            }
            Check(manager.ReadLegacyStoreV1PeerProof()?.RecentLocalSaveReceipts==0,
                "Manager startup does not silently probe local durable receipts");
            var bridge=new DesktopLanSettingsBridge(manager);
            var response=await Call(bridge,"lan.historicalSaveProbe");
            Check(response.GetProperty("ok").GetBoolean()&&
                  response.GetProperty("data").GetProperty(
                    "recentCompletedMapReceipts").GetInt32()==5&&
                  !response.GetProperty("data").GetProperty(
                    "crossHostSaveCompleteCertified").GetBoolean()&&
                  !response.GetProperty("data").GetProperty(
                    "productionRestoreAllowed").GetBoolean(),
                "real native LAN bridge reads only the explicitly requested local receipt summary");
            Check(manager.ReadLegacyStoreV1PeerProof()?.RecentLocalSaveReceipts==5,
                "explicit receipt witness is cached only in Manager memory");
            current=current with{OriginalFourFilesTag=Sha("original-session-changed")};
            Check(manager.ReadLegacyStoreV1PeerProof()?.RecentLocalSaveReceipts==0,
                "old local receipt witness cannot survive changed original four-file identity");
        }

        var foreignSource=Sha("a-different-original-Store-v1-session-and-lock");
        var changedOperation=Guid.NewGuid();
        var changedStart=DateTimeOffset.UtcNow.AddMinutes(-2);
        LegacyStoreV1SaveEvidenceJournal.Begin(
            root,changedOperation,maps[0].Id,41004,changedStart,foreignSource);
        LegacyStoreV1SaveEvidenceJournal.Complete(
            root,changedOperation,maps[0].Id,41004,changedStart,foreignSource,
            DateTimeOffset.UtcNow,true,false);
        Check(Denies(()=>LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
            root,hostId,source,maps)),
            "five receipts from mixed original Store session/lock source epochs never combine into a valid set");

        string file=Path.Combine(root,"historical-save-evidence-v1",
            maps[0].Id.ToString("N")+".json");
        byte[] originalBytes=File.ReadAllBytes(file);
        File.WriteAllText(file,"{}");
        Check(Denies(()=>LegacyStoreV1HistoricalSaveReceiptProbe.Capture(
            root,hostId,source,maps)),
            "damaged SHA-256 local journal blocks even a previously complete receipt set");
        File.WriteAllBytes(file,originalBytes);
    }
}
