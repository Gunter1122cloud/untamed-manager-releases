using System;
using System.Collections.Generic;
using System.Linq;

namespace UntamedManager.Core;

// T07 inventory barrier: fail closed on any missing component before publishing
// an offline candidate. Source freeze, ASA SaveComplete and two-host Store writer
// ownership are still separate real-server acceptance requirements.
public enum BackupComponentKind
{
    WorldProfiles,
    PluginPersistentData,
    IniConfiguration,
    VaultStore,
    ClusterTransfer,
    ManagerConfiguration,
    SqlDatabase
}
public sealed record ClusterBackupComponent(Guid BatchId,Guid HostId,Guid? MapId,
    BackupComponentKind Kind,string Name,string Sha256,long Length);
public sealed record ClusterBackupInventoryResult(bool Complete,int VerifiedComponents,
    string Reason)
{
    public bool ProductionRestoreCertified=>false;
}
public static class ClusterBackupInventory
{
    private static readonly HashSet<string> SqlNames=new(
        new[]{"arkshop","arkpermissions","lethalquests"},StringComparer.Ordinal);
    private static bool Hash(string? s)=>s!=null&&s.Length==64&&s.All(Uri.IsHexDigit);
    public static ClusterBackupInventoryResult Verify(Guid batchId,
        IReadOnlyList<TransferMapOwner> mapAssignments,
        IEnumerable<ClusterBackupComponent> components)
    {
        ClusterBackupInventoryResult Reject(string reason)=>new(false,0,reason);
        if(batchId==Guid.Empty||mapAssignments==null||components==null)
            return Reject("Explicit batch, map ownership and component manifest required");
        var maps=mapAssignments.ToArray();
        if(maps.Length!=10||maps.Select(m=>m.MapId).Distinct().Count()!=10||
           maps.Any(m=>m.MapId==Guid.Empty||m.HostId==Guid.Empty)||
           maps.Select(m=>m.HostId).Distinct().Count()!=2||
           maps.GroupBy(m=>m.HostId).Any(g=>g.Count()!=5))
            return Reject("Cluster requires exactly five unique maps on each of two hosts");
        var owner=maps.ToDictionary(m=>m.MapId,m=>m.HostId);
        var rows=components.Take(250).ToArray();
        if(rows.Length==0||rows.Length>=250)return Reject("Empty or excessive backup inventory");
        if(rows.Any(r=>r.BatchId!=batchId||r.HostId==Guid.Empty||
               r.Length<=0||!Hash(r.Sha256)||!Enum.IsDefined(r.Kind)||
               string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>128))
            return Reject("Tampered, missing or unverified backup component");
        if(rows.Any(r=>r.MapId.HasValue &&
                        (!owner.TryGetValue(r.MapId.Value,out var host)||host!=r.HostId)))
            return Reject("Foreign map or incorrect host ownership");
        if(rows.Any(r=>r.MapId.HasValue && r.Kind is not
               (BackupComponentKind.WorldProfiles or BackupComponentKind.PluginPersistentData or
                BackupComponentKind.IniConfiguration)))
            return Reject("Global component unexpectedly assigned to map");
        if(rows.Any(r=>!r.MapId.HasValue && r.Kind is
               (BackupComponentKind.WorldProfiles or BackupComponentKind.PluginPersistentData or
                BackupComponentKind.IniConfiguration)))
            return Reject("Per-map component has no map ownership");
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in rows)
        {
            string key=$"{row.Kind}|{row.HostId:N}|{row.MapId?.ToString("N")??""}|{row.Name}";
            if(!keys.Add(key))return Reject("Duplicate backup component identity");
        }
        foreach(var map in maps)
        foreach(var kind in new[]{BackupComponentKind.WorldProfiles,
                                  BackupComponentKind.PluginPersistentData,
                                  BackupComponentKind.IniConfiguration})
            if(rows.Count(r=>r.MapId==map.MapId && r.Kind==kind)!=1)
                return Reject("A map is missing world, plugin persistent data or configuration");
        foreach(Guid host in maps.Select(m=>m.HostId).Distinct())
            if(rows.Count(r=>!r.MapId.HasValue&&r.HostId==host&&
                             r.Kind==BackupComponentKind.ManagerConfiguration)!=1)
                return Reject("Missing per-host Manager configuration");
        if(rows.Count(r=>!r.MapId.HasValue&&r.Kind==BackupComponentKind.VaultStore)!=1||
           rows.Count(r=>!r.MapId.HasValue&&r.Kind==BackupComponentKind.ClusterTransfer)!=1)
            return Reject("Missing shared vault or transfer snapshot");
        var sql=rows.Where(r=>!r.MapId.HasValue&&r.Kind==BackupComponentKind.SqlDatabase).ToArray();
        if(sql.Length!=3||!sql.Select(r=>r.Name).ToHashSet(StringComparer.Ordinal).SetEquals(SqlNames))
            return Reject("Missing or unexpected MariaDB database");
        if(rows.Length!=10*3+2+1+1+3)
            return Reject("Unrecognized or extra inventory components");
        return new(true,rows.Length,
            "Offline inventory complete (37 members), not proof of ASA/Store/SQL consistency");
    }
}
