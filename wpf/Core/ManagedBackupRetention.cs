using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record BackupQuarantineEntry(string Transaction,string Package,string Status);
public sealed record BackupQuarantineResult(Guid Operation,string Transaction,string Package);

// Retention for the Manager's explicit per-map LOCAL backup collection only.
// Does not read or restore a live map, SQL, shared Store or a custom backup root.
public static class ManagedBackupRetention
{
    private sealed record Intent(int Schema,Guid Map,Guid Operation,string Profile,
        string SourceName,string ManifestSha256,long Bytes,string Scope);
    private static string Local(string raw)
    {
        UntamedOperationCoordinator.ResourceKey(raw);string path=Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar);
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked backup metadata refused");
        return path;
    }
    private static bool Overlap(string a,string b)=>a.Equals(b,StringComparison.OrdinalIgnoreCase)||
        a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
        b.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static (LocalMap Map,string Managed,string Quarantine) Paths(ClusterService cluster,Guid mapId)
    {
        var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId&&mapId!=Guid.Empty)??
            throw new UnauthorizedAccessException("Backup collection is not a registered local map");
        string managed=Local(Path.Combine(cluster.Store.Root,"local-backups",mapId.ToString("N"))),
            quarantine=Local(Path.Combine(cluster.Store.Root,"backup-quarantine",mapId.ToString("N")));
        foreach(var local in cluster.Config.LocalMaps)
        {
            string installation=Local(local.InstallationPath);
            if(Overlap(managed,installation)||Overlap(quarantine,installation))
                throw new InvalidDataException("Managed backup/quarantine overlaps a registered installation");
        }
        return(map,managed,quarantine);
    }
    private static string Point(string managed,string package)
    {
        string full=Local(package);
        if(!string.Equals(Path.GetDirectoryName(full),managed,StringComparison.OrdinalIgnoreCase)||
           full.EndsWith(".incomplete",StringComparison.OrdinalIgnoreCase)||!Directory.Exists(full))
            throw new UnauthorizedAccessException("Only a complete direct child of this managed collection is eligible");
        return full;
    }
    private static string Hash(string path)
    {using var file=new FileStream(Local(path),FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexString(SHA256.HashData(file));}
    private static BackupManifest VerifyPoint(string point,Guid map)
    {
        Local(point);string manifest=Local(Path.Combine(point,"manifest.json"));
        if(new FileInfo(manifest).Length>8*1024*1024)throw new InvalidDataException("Oversized backup manifest");
        return TestBackupPackage.Verify(point,map);
    }
    private static void Durable(string path,ReadOnlySpan<byte> bytes)
    {using var file=new FileStream(Local(path),FileMode.CreateNew,FileAccess.Write,FileShare.None);file.Write(bytes);file.Flush(true);}

    public static RetentionPlan Preview(ClusterService cluster,Guid mapId,RetentionRule rule)
    {
        var paths=Paths(cluster,mapId);var records=new List<RetainedBackup>();
        if(!Directory.Exists(paths.Managed))return BackupRetention.Preview(records,rule,DateTimeOffset.UtcNow);
        string[] directories=Directory.GetDirectories(paths.Managed);
        if(directories.Length>500)throw new InvalidDataException("Backup catalog exceeds bounded automatic retention scope");
        foreach(string dir in directories)
        {
            bool valid=false,protect=true;DateTimeOffset created=DateTimeOffset.UtcNow;
            long bytes=0;string scope="unverified";
            try
            {
                Local(dir);created=new DirectoryInfo(dir).CreationTimeUtc;
                protect=File.Exists(Local(Path.Combine(dir,".protected")));
                Point(paths.Managed,dir);var manifest=VerifyPoint(dir,mapId);
                created=manifest.CreatedUtc;bytes=manifest.Files.Sum(x=>x.Size);scope=manifest.Scope;valid=true;
            }
            catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException){}
            records.Add(new(mapId,mapId,dir,created,bytes,protect,valid,scope));
        }
        // This is the fixed, per-map manual collection; no arbitrary caller Job
        // or caller-supplied Verified/Eligible flag authorizes a move.
        return BackupRetention.Preview(records,rule,DateTimeOffset.UtcNow);
    }

    public static BackupQuarantineResult Quarantine(ClusterService cluster,Guid mapId,
        string package,RetentionRule rule)=>QuarantineCore(cluster,mapId,package,rule,null);
    internal static BackupQuarantineResult QuarantineCore(ClusterService cluster,Guid mapId,
        string package,RetentionRule rule,Action? afterMove)
    {
        var paths=Paths(cluster,mapId);string source=Point(paths.Managed,package);
        using var lease=cluster.Operations.AcquireMany(new[]{paths.Map.InstallationPath,cluster.Store.Root},UntamedOperationKind.Backup);
        var plan=Preview(cluster,mapId,rule);
        var candidate=plan.Items.SingleOrDefault(x=>x.Backup.Package.Equals(source,StringComparison.OrdinalIgnoreCase));
        if(candidate==null||!candidate.Eligible)throw new UnauthorizedAccessException("Current authoritative retention policy keeps this point");
        var manifest=VerifyPoint(source,mapId);
        if(File.Exists(Path.Combine(source,".protected")))throw new UnauthorizedAccessException("Backup point was protected after preview");
        Guid operation=Guid.NewGuid();string transaction=Local(Path.Combine(paths.Quarantine,operation.ToString("N")));
        string destination=Path.Combine(transaction,"point");
        var intent=new Intent(1,mapId,operation,Local(cluster.Store.Root),Path.GetFileName(source),
            Hash(Path.Combine(source,"manifest.json")),manifest.Files.Sum(x=>x.Size),manifest.Scope);
        lease.MarkMutationStarted();Directory.CreateDirectory(transaction);
        Durable(Path.Combine(transaction,"intent.dpapi"),ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(intent),null,DataProtectionScope.CurrentUser));
        VerifyIntentPoint(source,intent);
        Directory.Move(source,destination);
        afterMove?.Invoke();
        VerifyIntentPoint(destination,intent);
        Durable(Path.Combine(transaction,"quarantined"),JsonSerializer.SerializeToUtf8Bytes(new{operation,mapId,State="quarantined"}));
        lease.Complete();return new(operation,transaction,destination);
    }
    private static Intent ReadIntent(string transaction,ClusterService cluster,Guid mapId)
    {
        var paths=Paths(cluster,mapId);string root=Local(transaction);
        if(!string.Equals(Path.GetDirectoryName(root),paths.Quarantine,StringComparison.OrdinalIgnoreCase)||
           !Guid.TryParseExact(Path.GetFileName(root),"N",out Guid operation))
            throw new UnauthorizedAccessException("Quarantine transaction is not owned by this collection");
        string file=Local(Path.Combine(root,"intent.dpapi"));
        if(new FileInfo(file).Length>65536)throw new InvalidDataException("Oversized quarantine intent");
        Intent? intent;
        try{intent=JsonSerializer.Deserialize<Intent>(ProtectedData.Unprotect(File.ReadAllBytes(file),null,DataProtectionScope.CurrentUser));}
        catch(CryptographicException){throw new InvalidDataException("Quarantine operator binding cannot be verified");}
        if(intent==null||intent.Schema!=1||intent.Map!=mapId||intent.Operation!=operation||
           intent.Profile!=Local(cluster.Store.Root)||string.IsNullOrWhiteSpace(intent.SourceName)||
           Path.GetFileName(intent.SourceName)!=intent.SourceName)
            throw new InvalidDataException("Quarantine operation identity mismatch");
        return intent;
    }
    private static void VerifyIntentPoint(string point,Intent intent)
    {
        var manifest=VerifyPoint(point,intent.Map);
        if(Hash(Path.Combine(point,"manifest.json"))!=intent.ManifestSha256||
           manifest.Files.Sum(x=>x.Size)!=intent.Bytes||manifest.Scope!=intent.Scope)
            throw new InvalidDataException("Quarantined backup changed since intent was sealed");
    }
    public static string RestoreToCatalog(ClusterService cluster,Guid mapId,string transaction)
    {
        var paths=Paths(cluster,mapId);string root=Local(transaction);
        var intent=ReadIntent(root,cluster,mapId);string point=Local(Path.Combine(root,"point"));
        string destination=Local(Path.Combine(paths.Managed,intent.SourceName));
        if(Path.GetDirectoryName(destination)!=paths.Managed||Directory.Exists(destination)||File.Exists(destination))
            throw new IOException("Original backup name is occupied; restore never overwrites an existing point");
        // Explicit recovery takes the same OS leases using its own durable
        // record, preserving an interrupted quarantine's original NeedsOperator.
        using var lease=new UntamedOperationCoordinator(Path.Combine(root,"restore-operations"))
            .AcquireMany(new[]{paths.Map.InstallationPath,cluster.Store.Root},UntamedOperationKind.Restore);
        VerifyIntentPoint(point,intent);lease.MarkMutationStarted();Directory.CreateDirectory(paths.Managed);
        Directory.Move(point,destination);VerifyIntentPoint(destination,intent);
        Durable(Path.Combine(root,"restored"),JsonSerializer.SerializeToUtf8Bytes(new{intent.Operation,mapId,State="restored-to-backup-catalog"}));
        lease.Complete();return destination;
    }
    public static IReadOnlyList<BackupQuarantineEntry> InspectQuarantine(ClusterService cluster,Guid mapId)
    {
        var paths=Paths(cluster,mapId);if(!Directory.Exists(paths.Quarantine))return Array.Empty<BackupQuarantineEntry>();
        var rows=new List<BackupQuarantineEntry>();
        foreach(string directory in Directory.GetDirectories(paths.Quarantine).Take(501))
        {
            if(rows.Count>=500)throw new InvalidDataException("Quarantine catalog exceeds inspection limit");
            string point=Path.Combine(directory,"point");string state="invalid-operator-review";
            try
            {
                var intent=ReadIntent(directory,cluster,mapId);
                if(Directory.Exists(point)){VerifyIntentPoint(point,intent);state="verified-quarantine";}
                else if(File.Exists(Path.Combine(directory,"restored")))state="restored-to-catalog";
            }
            catch(Exception e) when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){}
            rows.Add(new(directory,point,state));
        }
        return rows;
    }
    public static void Protect(ClusterService cluster,Guid mapId,string package,bool protect)
    {
        var paths=Paths(cluster,mapId);string point=Point(paths.Managed,package);
        using var lease=cluster.Operations.AcquireMany(new[]{paths.Map.InstallationPath,cluster.Store.Root},UntamedOperationKind.Configuration);
        VerifyPoint(point,mapId);string marker=Local(Path.Combine(point,".protected"));
        if(File.Exists(marker)==protect){lease.Complete();return;}
        lease.MarkMutationStarted();
        if(protect)Durable(marker,JsonSerializer.SerializeToUtf8Bytes(new{mapId,State="retention-protected"}));
        else File.Delete(marker);
        lease.Complete();
    }
}
