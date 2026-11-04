using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UntamedManager.Core;

public static class ClusterBatchArchiveTests
{
    static void Check(bool condition,string reason)
    {
        if(!condition)throw new Exception("Cluster batch archive test failed: "+reason);
        Console.WriteLine("PASS cluster-batch "+reason);
    }
    static bool Refused(Action work)
    {
        try{work();return false;}
        catch(Exception ex) when(ex is IOException or InvalidDataException){return true;}
    }
    public static void Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"cluster-batch-t07");
        string inputsDir=Path.Combine(root,"input");
        Directory.CreateDirectory(inputsDir);
        Guid hp=Guid.NewGuid(),ryzen=Guid.NewGuid(),batch=Guid.NewGuid();
        var maps=Enumerable.Range(0,10).Select(i=>new TransferMapOwner(
            Guid.NewGuid(),i<5?hp:ryzen)).ToArray();
        var inputs=new List<ClusterBatchSource>();
        void Add(Guid owner,Guid? map,BackupComponentKind kind,string name)
        {
            string file=Path.Combine(inputsDir,inputs.Count.ToString("D2")+".data");
            File.WriteAllText(file, $"Test data for {kind}, host {owner:N}, map {map:N}, name {name}.");
            byte[] original=File.ReadAllBytes(file);
            inputs.Add(new(new ClusterBackupComponent(batch,owner,map,kind,name,
                Convert.ToHexString(SHA256.HashData(original)),original.Length),file));
        }
        foreach(var map in maps)
        {
            Add(map.HostId,map.MapId,BackupComponentKind.WorldProfiles,"SavedGames");
            Add(map.HostId,map.MapId,BackupComponentKind.PluginPersistentData,"plugins");
            Add(map.HostId,map.MapId,BackupComponentKind.IniConfiguration,"ini");
        }
        Add(hp,null,BackupComponentKind.ManagerConfiguration,"hp-config");
        Add(ryzen,null,BackupComponentKind.ManagerConfiguration,"ryzen-config");
        Add(hp,null,BackupComponentKind.VaultStore,"vault-store");
        Add(hp,null,BackupComponentKind.ClusterTransfer,"transfer");
        foreach(string db in new[]{"arkshop","arkpermissions","lethalquests"})
            Add(ryzen,null,BackupComponentKind.SqlDatabase,db);
        Check(inputs.Count==37,"full ten-map, transfer, vault and SQL inventory prepared");

        string invalid=Path.Combine(root,"wrong-source");
        var wrong=inputs.ToArray();
        wrong[0]=wrong[0] with { Component=wrong[0].Component with {
            Sha256=new string('0',64)
        }};
        Check(Refused(()=>ClusterBatchArchive.BuildOffline(batch,maps,wrong,invalid)) &&
              !Directory.Exists(invalid)&&!Directory.Exists(invalid+".incomplete"),
            "wrong SHA refuses before staging a single byte");

        string interrupted=Path.Combine(root,"interrupted");
        Check(Refused(()=>ClusterBatchArchive.BuildOffline(batch,maps,inputs,interrupted,
            index=>{if(index==2)throw new IOException("synthetic power failure");})) &&
            Directory.Exists(interrupted+".incomplete")&&!Directory.Exists(interrupted),
            "interrupted copy preserves incomplete forensic staging, not a published restore point");
        Check(Refused(()=>ClusterBatchArchive.Verify(interrupted+".incomplete")),
            "incomplete staging cannot masquerade as valid cluster backup");

        string destination=Path.Combine(root,"complete-offline-batch");
        var manifest=ClusterBatchArchive.BuildOffline(batch,maps,inputs,destination);
        Check(manifest.BatchId==batch&&manifest.Components.Length==37 &&
              !manifest.ProductionRestoreCertified&&!manifest.LiveAsaSaveConfirmed &&
              !manifest.StoreWriterOwnershipConfirmed&&!manifest.SqlTransactionBoundaryConfirmed,
            "37 distinct disk files published with explicit absence of live-server consistency proof");
        var validated=ClusterBatchArchive.Verify(destination);
        Check(validated.BatchId==batch && validated.Components.Length==37,
            "fresh disk read validates all published component hashes and ownership");
        Check(Refused(()=>ClusterBatchArchive.BuildOffline(batch,maps,inputs,destination)),
            "existing published batch cannot be silently overwritten");
        string objectFile=Path.Combine(destination,"objects","00.bin");
        File.WriteAllText(objectFile,"deliberately altered backup bytes");
        Check(Refused(()=>ClusterBatchArchive.Verify(destination)),
            "tampering with a single saved payload invalidates sealed batch");

        string extra=Path.Combine(root,"extra");
        ClusterBatchArchive.BuildOffline(batch,maps,inputs,extra);
        File.WriteAllText(Path.Combine(extra,"objects","unexpected.bin"),"rogue");
        Check(Refused(()=>ClusterBatchArchive.Verify(extra)),
            "unexpected file prevents acceptance of otherwise complete archive");

        Check(inputs.All(x=>Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(x.SourceFile)))==x.Component.Sha256),
            "successful, interrupted and rejected copies preserve all original offline inputs");
    }
}
