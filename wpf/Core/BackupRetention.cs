using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

public sealed record RetainedBackup(Guid Job,Guid Map,string Package,DateTimeOffset CreatedUtc,
    long Bytes,bool Protected,bool Verified,string Scope);
public sealed record RetentionRule(int KeepLatest,TimeSpan MaxAge,long MaxBytes);
public sealed record RetentionChoice(RetainedBackup Backup,bool Eligible,string Reason);
public sealed record RetentionPlan(IReadOnlyList<RetentionChoice> Items,
    long EstimatedReclaimBytes,int Candidates);

// Never touch live ASA SavedArks, Legacy Store, SQL or another task's points.
// Retention only moves separately managed, SHA-verified backup packages into a
// distinct quarantine; irreversible deletion is a separate manual operation.
public static class BackupRetention
{
    public static RetentionPlan Preview(IEnumerable<RetainedBackup> source,
        RetentionRule rule,DateTimeOffset now)
    {
        if(source==null || rule==null || rule.KeepLatest<1 || rule.KeepLatest>10000 ||
           rule.MaxAge<=TimeSpan.Zero || rule.MaxAge>TimeSpan.FromDays(3650) ||
           rule.MaxBytes<0)
            throw new InvalidDataException("Invalid backup retention rule");
        var records=source.ToArray();
        if(records.Any(x=>x.Job==Guid.Empty||x.Map==Guid.Empty||x.Bytes<0||
            x.CreatedUtc>now.AddMinutes(5)) ||
           records.GroupBy(x=>x.Package,StringComparer.OrdinalIgnoreCase).Any(x=>x.Count()>1))
            throw new InvalidDataException("Invalid/double-counted backup records");
        var result=new List<RetentionChoice>();
        foreach(var job in records.GroupBy(x=>(x.Job,x.Map)))
        {
            var ordered=job.OrderByDescending(x=>x.CreatedUtc)
                .ThenByDescending(x=>x.Package,StringComparer.OrdinalIgnoreCase).ToArray();
            // Corrupt/newer points must never push the last known-good restore
            // point out of KeepLatest. KeepLatest counts VERIFIED points too.
            var verifiedToKeep=ordered.Where(x=>x.Verified).Take(rule.KeepLatest)
                .Select(x=>x.Package).ToHashSet(StringComparer.OrdinalIgnoreCase);
            long size=ordered.Sum(x=>x.Bytes);
            for(int i=0;i<ordered.Length;i++)
            {
                var backup=ordered[i];
                bool retained=i<rule.KeepLatest || i==0 || verifiedToKeep.Contains(backup.Package)||backup.Protected ||
                    !backup.Verified;
                bool old=now-backup.CreatedUtc>rule.MaxAge;
                bool overSize=rule.MaxBytes>0 && size>rule.MaxBytes;
                bool due=!retained && (old || overSize || i>=rule.KeepLatest);
                if(due)size-=backup.Bytes;
                result.Add(new(backup,due,due?"eligible-managed-point":
                    backup.Protected?"protected":
                    !backup.Verified?"unverified-blocked":"keep-latest"));
            }
        }
        return new(result,result.Where(x=>x.Eligible).Sum(x=>x.Backup.Bytes),
            result.Count(x=>x.Eligible));
    }

    internal static string Quarantine(RetentionChoice choice,string managedRoot,
        string quarantineRoot,Guid ownedJob,Guid ownedMap)
    {
        if(choice==null||!choice.Eligible ||
           choice.Backup.Job!=ownedJob || choice.Backup.Map!=ownedMap ||
           choice.Backup.Protected || !choice.Backup.Verified)
            throw new UnauthorizedAccessException("Unowned or protected backup cannot be quarantined");
        string root=FixedLocal(managedRoot),quarantine=FixedLocal(quarantineRoot);
        string package=FixedLocal(choice.Backup.Package);
        if(!IsInside(root,package)||IsInside(root,quarantine)||
           IsInside(quarantine,root)||root.Equals(quarantine,StringComparison.OrdinalIgnoreCase) ||
           !Directory.Exists(package))
            throw new InvalidDataException("Backup and quarantine roots must be disjoint");
        // A real manifest and map identity are re-verified immediately before
        // moving. Caller-provided 'Verified=true' is not authorization.
        var manifest=TestBackupPackage.Verify(package,ownedMap);
        if(!manifest.Scope.Equals(choice.Backup.Scope,StringComparison.Ordinal))
            throw new InvalidDataException("Backup scope changed since retention preview");
        long bytes=manifest.Files.Sum(x=>x.Size);
        if(bytes!=choice.Backup.Bytes)
            throw new InvalidDataException("Backup bytes changed since preview");
        var time=new DirectoryInfo(package).CreationTimeUtc;
        if(!Directory.Exists(quarantine))
            Directory.CreateDirectory(quarantine);
        string destination=Path.Combine(quarantine,ownedJob.ToString("N")+"-"+
            ownedMap.ToString("N")+"-"+Guid.NewGuid().ToString("N"));
        Directory.Move(package,destination);
        return destination;
    }
    private static string FixedLocal(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
           path.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit local fixed-disk path required");
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Backup retention cannot cross network drives");
        for(var d=new DirectoryInfo(full);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked backup path rejected");
        return full;
    }
    private static bool IsInside(string parent,string child)
        =>child.StartsWith(parent+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
