using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

/// <summary>
/// Real, byte-verified host-scoped restore staging from the EXISTING verified
/// N-host TEMP sandbox. It does not implement a second backup/archive format,
/// Kova Store writer or automatic cutover. Creates fresh, never-overwritten
/// deployment inspection packages on fixed local disks only.
/// </summary>
public static class MultiHostHostRestoreStaging
{
    private const string Prefix="Untamed-HostRestore-";
    private const string ManifestName="host-restore-stage-v1.json";
    private const string ManifestHash="host-restore-stage-v1.sha256";
    private const string Complete="complete";
    public sealed record Plan(string SourceSandbox,string Parent,string HostId,
        Guid BatchId,int Maps,int Clusters,int Components,long Bytes,
        string StageDirectoryName,string IdentitySha256);
    public sealed record Receipt(int SchemaVersion,Guid BatchId,string HostId,
        DateTimeOffset CreatedUtc,MultiHostBackupComponent[] Components,
        bool ProductionRestoreAllowed,string Evidence);

    private static string Sha(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.Read,1024*1024,FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool Under(string parent,string candidate)
    {
        parent=parent.TrimEnd(Path.DirectorySeparatorChar);
        candidate=candidate.TrimEnd(Path.DirectorySeparatorChar);
        return candidate.Equals(parent,StringComparison.OrdinalIgnoreCase)||
            candidate.StartsWith(parent+Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
    private static string SafeLocalFolder(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||
           !Path.IsPathFullyQualified(value)||
           value.StartsWith(@"\\",StringComparison.Ordinal)||
           value.StartsWith("//",StringComparison.Ordinal)||
           value.StartsWith(@"\\?\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-HOSTRESTORE-LOCAL-FIXED-ONLY");
        string full=Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        string? drive=Path.GetPathRoot(full);
        if(drive==null||new DriveInfo(drive).DriveType!=DriveType.Fixed||
           !Directory.Exists(full))
            throw new InvalidDataException("UM-HOSTRESTORE-FIXED-PARENT-REQUIRED");
        for(DirectoryInfo? ancestor=new(full);ancestor!=null;ancestor=ancestor.Parent)
            if(ancestor.Exists&&(ancestor.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-HOSTRESTORE-LINKED-PATH");
        return full;
    }
    private static void DemandDistinctParent(string sandbox,string parent,
        IReadOnlyCollection<string>? forbiddenRoots)
    {
        if(Under(parent,sandbox)||Under(sandbox,parent))
            throw new InvalidDataException("UM-HOSTRESTORE-SOURCE-OUTPUT-OVERLAP");
        foreach(string raw in forbiddenRoots??Array.Empty<string>())
        {
            if(string.IsNullOrWhiteSpace(raw))continue;
            string path=Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar);
            if(Under(path,parent)||Under(parent,path))
                throw new UnauthorizedAccessException("UM-HOSTRESTORE-PROTECTED-ROOT");
        }
    }
    private static void DemandOwnership(MultiHostRestoreSandboxManifest manifest,
        MultiHostBackupOwnershipSnapshot ownership,string hostId)
    {
        if(ownership==null||ownership.Hosts==null||
           ownership.Maps==null||ownership.Clusters==null||
           !manifest.Hosts.All(h=>ownership.Hosts.Any(owner=>
               owner.HostId.Equals(h.HostId,StringComparison.OrdinalIgnoreCase)&&
               (owner.IsLocal||owner.IsPaired)))||
           !manifest.Clusters.All(c=>ownership.Clusters.Any(owner=>
               owner.ClusterId.Equals(c.ClusterId,StringComparison.OrdinalIgnoreCase)))||
           !manifest.Maps.All(map=>ownership.Maps.Any(owner=>
               owner.MapId==map.MapId&&
               owner.HostId.Equals(map.HostId,StringComparison.OrdinalIgnoreCase)&&
               owner.ClusterId!=null&&
               owner.ClusterId.Equals(map.ClusterId,StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("UM-HOSTRESTORE-OWNERSHIP-CHANGED");
        // The existing archive may represent only one of many registered
        // clusters; unrelated hosts/maps must not be required to participate.
        // However every included owner remains pinned to CURRENT registry.
        if(manifest.Hosts.Select(x=>x.HostId)
             .Distinct(StringComparer.OrdinalIgnoreCase).Count()!=manifest.Hosts.Length||
           manifest.Maps.Select(x=>x.MapId).Distinct().Count()!=manifest.Maps.Length)
            throw new InvalidDataException("UM-HOSTRESTORE-DUPLICATE-ARCHIVE-OWNER");
        var requested=ownership.Hosts.SingleOrDefault(h=>
            string.Equals(h.HostId,hostId,StringComparison.OrdinalIgnoreCase));
        if(requested==null||!(requested.IsLocal||requested.IsPaired))
            throw new UnauthorizedAccessException("UM-HOSTRESTORE-UNPAIRED-HOST");
        if(!manifest.Maps.Any(m=>m.HostId.Equals(hostId,
            StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UM-HOSTRESTORE-HOST-WITHOUT-MAPS");
    }
    private static MultiHostBackupComponent[] SelectHost(
        MultiHostRestoreSandboxManifest manifest,string hostId)
    {
        var clusters=manifest.Maps.Where(m=>m.HostId.Equals(hostId,
            StringComparison.OrdinalIgnoreCase)).Select(m=>m.ClusterId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return manifest.Components.Where(c=>
            ((c.Kind is MultiHostBackupComponentKind.ClusterStoreV1File or
                MultiHostBackupComponentKind.ClusterTransferManifest or
                MultiHostBackupComponentKind.ClusterTransferFile) &&
                c.ClusterId!=null&&clusters.Contains(c.ClusterId)) ||
            (c.HostId.Equals(hostId,StringComparison.OrdinalIgnoreCase) &&
                c.Kind is not (MultiHostBackupComponentKind.ClusterStoreV1File or
                    MultiHostBackupComponentKind.ClusterTransferManifest or
                    MultiHostBackupComponentKind.ClusterTransferFile)))
            .OrderBy(c=>c.ObjectIndex).ToArray();
    }
    private static string Signature(MultiHostRestoreSandboxManifest manifest,
        string hostId,IReadOnlyList<MultiHostBackupComponent> components)
    {
        string canonical=manifest.SourceBatchId.ToString("N")+"|"+
            hostId.ToUpperInvariant()+"|"+
            string.Join("\n",components.Select(c=>
                c.ObjectIndex+"|"+c.Kind+"|"+c.HostId+"|"+
                c.MapId+"|"+c.ClusterId+"|"+c.RelativePath+"|"+
                c.Length+"|"+c.Sha256.ToUpperInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
    public static Plan Inspect(string sandbox,string hostId,string outputParent,
        MultiHostBackupOwnershipSnapshot ownership,
        IReadOnlyCollection<string>? protectedRoots=null)
    {
        string source=SafeLocalFolder(sandbox);
        string parent=SafeLocalFolder(outputParent);
        DemandDistinctParent(source,parent,protectedRoots);
        var manifest=MultiHostClusterBackupArchive.VerifyIsolatedSandbox(source);
        DemandOwnership(manifest,ownership,hostId);
        var exact=manifest.Hosts.Single(h=>h.HostId.Equals(hostId,
            StringComparison.OrdinalIgnoreCase)).HostId;
        var included=SelectHost(manifest,exact);
        if(included.Length==0)
            throw new InvalidDataException("UM-HOSTRESTORE-EMPTY");
        int mapCount=manifest.Maps.Count(m=>m.HostId.Equals(exact,
            StringComparison.OrdinalIgnoreCase));
        string name=Prefix+exact+"-"+manifest.SourceBatchId.ToString("N");
        string target=Path.Combine(parent,name);
        if(Directory.Exists(target)||File.Exists(target)||
           Directory.Exists(target+".incomplete")||File.Exists(target+".incomplete"))
            throw new IOException("UM-HOSTRESTORE-EXISTS-OR-INTERRUPTED");
        long bytes=0;
        foreach(var component in included)
            bytes=checked(bytes+component.Length);
        int clusters=manifest.Maps.Where(m=>m.HostId.Equals(exact,
            StringComparison.OrdinalIgnoreCase)).Select(m=>m.ClusterId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new(source,parent,exact,manifest.SourceBatchId,
            mapCount,clusters,included.Length,bytes,name,
            Signature(manifest,exact,included));
    }
    private static void Durable(string path,string value)
    {
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,
            FileShare.None,4096,FileOptions.WriteThrough);
        var data=Encoding.UTF8.GetBytes(value);
        stream.Write(data);
        stream.Flush(true);
    }
    private static void CopyChecked(string from,string to,
        MultiHostBackupComponent component,CancellationToken ct)
    {
        if((File.GetAttributes(from)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-HOSTRESTORE-LINKED-SOURCE");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using(var input=new FileStream(from,FileMode.Open,FileAccess.Read,
            FileShare.Read,1024*1024,FileOptions.SequentialScan))
        using(var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,
            FileShare.None,1024*1024,FileOptions.WriteThrough))
        {
            if(input.Length!=component.Length)
                throw new InvalidDataException("UM-HOSTRESTORE-SOURCE-LENGTH");
            byte[] buffer=new byte[128*1024];
            long count=0;
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                int n=input.Read(buffer,0,buffer.Length);
                if(n==0)break;
                count=checked(count+n);
                if(count>component.Length)
                    throw new IOException("UM-HOSTRESTORE-SOURCE-CHANGED");
                output.Write(buffer,0,n);
            }
            output.Flush(true);
            if(count!=component.Length||input.Length!=component.Length)
                throw new InvalidDataException("UM-HOSTRESTORE-PARTIAL");
        }
        if(!Sha(from).Equals(component.Sha256,StringComparison.OrdinalIgnoreCase)||
           !Sha(to).Equals(component.Sha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-HOSTRESTORE-SHA256-CHANGED");
    }
    /// <summary>
    /// Creates exactly one new host package. All shared Store/transfer payloads
    /// are copied read-only as common-cluster evidence. Do NOT launch multiple
    /// copies as independent shared writers: production cutover is not authorized.
    /// </summary>
    public static Receipt Stage(Plan approved,
        MultiHostBackupOwnershipSnapshot ownership,
        IReadOnlyCollection<string>? protectedRoots=null,
        CancellationToken ct=default,Action<int>? afterCopiedForTesting=null)
    {
        ct.ThrowIfCancellationRequested();
        var current=Inspect(approved.SourceSandbox,approved.HostId,
            approved.Parent,ownership,protectedRoots);
        if(current!=approved)
            throw new IOException("UM-HOSTRESTORE-PREVIEW-CHANGED");
        var manifest=MultiHostClusterBackupArchive.VerifyIsolatedSandbox(
            approved.SourceSandbox);
        var members=SelectHost(manifest,approved.HostId);
        string target=Path.Combine(approved.Parent,approved.StageDirectoryName);
        string partial=target+".incomplete";
        // No pre-existing destination is ever replaced, including another
        // Manager's own interrupted stage.
        Directory.CreateDirectory(partial);
        string data=Path.Combine(partial,"data");
        Directory.CreateDirectory(data);
        int copied=0;
        foreach(var component in members)
        {
            ct.ThrowIfCancellationRequested();
            string relative=MultiHostClusterBackupArchive.RestoreRelative(component);
            string source=Path.Combine(approved.SourceSandbox,"data",relative);
            string destination=Path.Combine(data,relative);
            if(!Under(data,destination))
                throw new InvalidDataException("UM-HOSTRESTORE-ESCAPE");
            CopyChecked(source,destination,component,ct);
            afterCopiedForTesting?.Invoke(++copied);
        }
        // Re-verify all source sandbox objects after copying to stop
        // same-size/timestamp source changes from being published.
        MultiHostClusterBackupArchive.VerifyIsolatedSandbox(approved.SourceSandbox);
        var receipt=new Receipt(1,approved.BatchId,approved.HostId,
            DateTimeOffset.UtcNow,members,false,
            "EXACT_HOST_AND_SHARED_CLUSTER_COPIES_FOR_OFFLINE_TEST_ONLY");
        string resultPath=Path.Combine(partial,ManifestName);
        Durable(resultPath,JsonSerializer.Serialize(receipt));
        Durable(Path.Combine(partial,ManifestHash),Sha(resultPath));
        Durable(Path.Combine(partial,Complete),approved.BatchId.ToString("N"));
        VerifyInternal(partial,allowPartial:true);
        Directory.Move(partial,target);
        return Verify(target);
    }
    private static Receipt VerifyInternal(string target,bool allowPartial)
    {
        if(!Directory.Exists(target)||
           (!allowPartial&&target.EndsWith(".incomplete",
               StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UM-HOSTRESTORE-INCOMPLETE");
        for(var p=new DirectoryInfo(target);p!=null;p=p.Parent)
            if(p.Exists&&(p.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-HOSTRESTORE-LINKED");
        string manifestFile=Path.Combine(target,ManifestName);
        string hashFile=Path.Combine(target,ManifestHash);
        string marker=Path.Combine(target,Complete);
        if(!File.Exists(manifestFile)||!File.Exists(hashFile)||
           !File.Exists(marker)||new FileInfo(manifestFile).Length is <32 or >64000000||
           !Sha(manifestFile).Equals(File.ReadAllText(hashFile).Trim(),
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-HOSTRESTORE-INVALID-MANIFEST");
        Receipt receipt=JsonSerializer.Deserialize<Receipt>(File.ReadAllText(manifestFile))??
            throw new InvalidDataException("UM-HOSTRESTORE-INVALID-RECEIPT");
        if(receipt.SchemaVersion!=1||receipt.BatchId==Guid.Empty||
           receipt.ProductionRestoreAllowed||receipt.Components==null||
           receipt.Components.Length==0||receipt.HostId.Length is <1 or >128||
           !File.ReadAllText(marker).Trim().Equals(receipt.BatchId.ToString("N"),
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-HOSTRESTORE-INVALID-SCOPE");
        var expected=receipt.Components.Select(
            MultiHostClusterBackupArchive.RestoreRelative)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string data=Path.Combine(target,"data");
        if(!Directory.Exists(data)||Directory.GetFiles(target).Length!=3||
           Directory.GetDirectories(target).Length!=1)
            throw new InvalidDataException("UM-HOSTRESTORE-EXTRA-DATA");
        var pending=new Stack<string>();
        pending.Push(data);
        while(pending.Count>0)
        {
            string current=pending.Pop();
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-HOSTRESTORE-LINKED-PAYLOAD");
            foreach(var dir in Directory.GetDirectories(current))
                pending.Push(dir);
            foreach(var file in Directory.GetFiles(current))
            {
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||
                   !actual.Add(Path.GetRelativePath(data,file)))
                    throw new InvalidDataException("UM-HOSTRESTORE-DUPLICATE-OR-LINKED");
            }
        }
        if(!expected.SetEquals(actual))
            throw new InvalidDataException("UM-HOSTRESTORE-MISSING-OR-EXTRA");
        foreach(var item in receipt.Components)
        {
            string path=Path.Combine(data,
                MultiHostClusterBackupArchive.RestoreRelative(item));
            if(new FileInfo(path).Length!=item.Length||
               !Sha(path).Equals(item.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UM-HOSTRESTORE-PAYLOAD-SHA256");
        }
        return receipt;
    }
    public static Receipt Verify(string folder)
    {
        string target=SafeLocalFolder(folder);
        if(!Path.GetFileName(target).StartsWith(Prefix,
            StringComparison.Ordinal)||Path.GetFileName(target).EndsWith(".incomplete",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-HOSTRESTORE-NAME");
        return VerifyInternal(target,allowPartial:false);
    }
}
