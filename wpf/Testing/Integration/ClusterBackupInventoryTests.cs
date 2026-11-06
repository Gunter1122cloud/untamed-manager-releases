using System;
using System.Collections.Generic;
using System.Linq;
using UntamedManager.Core;

public static class ClusterBackupInventoryTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new Exception("Cluster backup inventory failed: "+reason);
        Console.WriteLine("PASS backup-inventory "+reason);
    }
    public static void Run()
    {
        Guid hp=Guid.NewGuid(),ryzen=Guid.NewGuid(),batch=Guid.NewGuid();
        var maps=Enumerable.Range(0,10).Select(i=>
            new TransferMapOwner(Guid.NewGuid(),i<5?hp:ryzen)).ToArray();
        var entries=new List<ClusterBackupComponent>();
        void Add(Guid host,Guid? map,BackupComponentKind kind,string name)=>
            entries.Add(new(batch,host,map,kind,name,new string('A',64),12));
        foreach(var map in maps)
        {
            Add(map.HostId,map.MapId,BackupComponentKind.WorldProfiles,"SavedGames");
            Add(map.HostId,map.MapId,BackupComponentKind.PluginPersistentData,"plugins");
            Add(map.HostId,map.MapId,BackupComponentKind.IniConfiguration,"ini");
        }
        Add(hp,null,BackupComponentKind.ManagerConfiguration,"hp-manager-config");
        Add(ryzen,null,BackupComponentKind.ManagerConfiguration,"ryzen-manager-config");
        Add(hp,null,BackupComponentKind.VaultStore,"untamed-cluster-vault");
        Add(hp,null,BackupComponentKind.ClusterTransfer,"cluster-transfer");
        foreach(string db in new[]{"arkshop","arkpermissions","lethalquests"})
            Add(ryzen,null,BackupComponentKind.SqlDatabase,db);
        var result=ClusterBackupInventory.Verify(batch,maps,entries);
        Check(result.Complete&&result.VerifiedComponents==37 &&
              !result.ProductionRestoreCertified,
            "37-component 5+5 map inventory complete but never considered production restore proof");
        var removed=entries.Where(x=>x.Kind!=BackupComponentKind.ClusterTransfer).ToArray();
        Check(!ClusterBackupInventory.Verify(batch,maps,removed).Complete,
            "missing cluster-transfer snapshot blocks restoring");
        var wrongOwner=entries.ToArray();
        wrongOwner[0]=wrongOwner[0] with {HostId=ryzen};
        Check(!ClusterBackupInventory.Verify(batch,maps,wrongOwner).Complete,
            "misowned world backup cannot complete shared batch");
        var wrongBatch=entries.ToArray();
        wrongBatch[0]=wrongBatch[0] with {BatchId=Guid.NewGuid()};
        Check(!ClusterBackupInventory.Verify(batch,maps,wrongBatch).Complete,
            "wrong operation batch cannot mix two moments in time");
        var corrupt=entries.ToArray();
        corrupt[0]=corrupt[0] with {Sha256="not-a-sha256"};
        Check(!ClusterBackupInventory.Verify(batch,maps,corrupt).Complete,
            "unverified digest refuses backup component");
        var repeated=entries.Append(entries[0]).ToArray();
        Check(!ClusterBackupInventory.Verify(batch,maps,repeated).Complete,
            "duplicate restore member refused");
        var foreign=entries.Append(new ClusterBackupComponent(
            batch,hp,null,BackupComponentKind.SqlDatabase,"unapproved",
            new string('A',64),12)).ToArray();
        Check(!ClusterBackupInventory.Verify(batch,maps,foreign).Complete,
            "unexpected SQL database rejected");
        Check(!ClusterBackupInventory.Verify(batch,maps.Take(9).ToArray(),entries).Complete,
            "incomplete two-host cluster cannot publish restorable batch");
    }
}
