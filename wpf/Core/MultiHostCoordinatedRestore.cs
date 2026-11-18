using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

// Coordinated OFFLINE preparation: all selected cluster owners use one
// sealed sandbox and one immutable batch identity. Never publishes to a
// remote machine, registered installation, transfer root or Store writer.
// A crash leaves individual verified packages and a review-only journal.
public static class MultiHostCoordinatedRestore
{
    public sealed record Plan(Guid BatchId,string Sandbox,string Parent,
        MultiHostHostRestoreStaging.Plan[] Hosts,string IdentitySha256,
        int Maps,int Clusters,long Bytes);
    public sealed record HostState(string HostId,string DirectoryName,string State,
        int Maps,int Components,long Bytes);
    public sealed record Receipt(int Schema,Guid TransactionId,Guid BatchId,
        string PlanSha256,string State,DateTimeOffset UpdatedUtc,HostState[] Hosts,
        bool ProductionRestoreAllowed=false,bool RemoteWritesAllowed=false);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Digest(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Journal(Plan p)=>Path.Combine(p.Parent,"coordinated-restore-"+p.BatchId.ToString("N")+".json");
    private static bool Exists(string path)=>File.Exists(path)||Directory.Exists(path);
    private static bool Overlap(string a,string b)
    {
        a=Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        b=Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return a.Equals(b,StringComparison.OrdinalIgnoreCase)||
            a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            b.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    private static void FullCoverage(MultiHostRestoreSandboxManifest manifest,MultiHostBackupOwnershipSnapshot ownership)
    {
        var clusters=manifest.Clusters.Select(c=>c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required=ownership.Maps.Where(m=>m.ClusterId!=null&&clusters.Contains(m.ClusterId)).ToArray();
        if(required.Length!=manifest.Maps.Length||required.Any(m=>!manifest.Maps.Any(x=>
            x.MapId==m.MapId&&x.HostId.Equals(m.HostId,StringComparison.OrdinalIgnoreCase)&&
            x.ClusterId.Equals(m.ClusterId,StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("UM-COORDINATED-RESTORE-INCOMPLETE-CLUSTER");
    }
    public static Plan Inspect(string sandbox,string parent,MultiHostBackupOwnershipSnapshot ownership,
        IReadOnlyCollection<string> protectedRoots)
    {
        _=UntamedOperationCoordinator.ResourceKey(sandbox);
        _=UntamedOperationCoordinator.ResourceKey(parent);
        if(protectedRoots.Any(p=>Overlap(p,sandbox)||Overlap(p,parent))||Overlap(sandbox,parent))
            throw new UnauthorizedAccessException("UM-COORDINATED-RESTORE-PROTECTED");
        var manifest=MultiHostClusterBackupArchive.VerifyIsolatedSandbox(sandbox);
        FullCoverage(manifest,ownership);
        var hosts=manifest.Hosts.OrderBy(h=>h.HostId,StringComparer.OrdinalIgnoreCase).Select(h=>
            MultiHostHostRestoreStaging.Inspect(sandbox,h.HostId,parent,ownership,protectedRoots)).ToArray();
        if(hosts.Length==0||hosts.Any(h=>h.BatchId!=manifest.SourceBatchId))
            throw new InvalidDataException("UM-COORDINATED-RESTORE-BATCH");
        string hash=Digest(JsonSerializer.Serialize(hosts,Json));
        var result=new Plan(manifest.SourceBatchId,Path.GetFullPath(sandbox),Path.GetFullPath(parent),
            hosts,hash,manifest.Maps.Length,manifest.Clusters.Length,hosts.Sum(h=>h.Bytes));
        if(Exists(Journal(result))||Exists(Journal(result)+".next"))
            throw new IOException("UM-COORDINATED-RESTORE-EXISTING-JOURNAL");
        if(new DriveInfo(Path.GetPathRoot(result.Parent)!).AvailableFreeSpace<
            checked(result.Bytes+64L*1024*1024))
            throw new IOException("UM-COORDINATED-RESTORE-INSUFFICIENT-SPACE");
        return result;
    }
    private static void Persist(Plan plan,Receipt row,bool first=false)
    {
        string path=Journal(plan),next=path+".next";
        _=UntamedOperationCoordinator.ResourceKey(plan.Parent);
        if(Directory.Exists(path)||(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)||
            Exists(next))throw new IOException("UM-COORDINATED-RESTORE-JOURNAL");
        using(var stream=new FileStream(next,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        {JsonSerializer.Serialize(stream,row,Json);stream.Flush(true);}
        File.Move(next,path,!first);
    }
    private static void ExactReceipt(MultiHostHostRestoreStaging.Plan plan,MultiHostRestoreSandboxManifest source,
        MultiHostHostRestoreStaging.Receipt receipt)
    {
        var expected=MultiHostHostRestoreStaging.SelectHost(source,plan.HostId);
        if(receipt.BatchId!=plan.BatchId||receipt.HostId!=plan.HostId||receipt.ProductionRestoreAllowed||
           !receipt.Components.OrderBy(x=>x.ObjectIndex).SequenceEqual(expected))
            throw new InvalidDataException("UM-COORDINATED-RESTORE-COMPONENT-MISMATCH");
    }
    public static Receipt Stage(Plan approved,MultiHostBackupOwnershipSnapshot ownership,
        IReadOnlyCollection<string> protectedRoots,bool confirm,CancellationToken ct=default,
        Action<string>? progress=null,Action<int>? afterHostForTesting=null)
    {
        if(!confirm)throw new UnauthorizedAccessException("UM-COORDINATED-RESTORE-APPROVAL");
        ct.ThrowIfCancellationRequested();
        using var lease=MultiHostOfflineArchiveRecovery.AcquireLease(Journal(approved));
        var plan=Inspect(approved.Sandbox,approved.Parent,ownership,protectedRoots);
        if(plan.BatchId!=approved.BatchId||plan.IdentitySha256!=approved.IdentitySha256||
            !plan.Hosts.SequenceEqual(approved.Hosts))
            throw new InvalidDataException("UM-COORDINATED-RESTORE-PLAN-CHANGED");
        var source=MultiHostClusterBackupArchive.VerifyIsolatedSandbox(plan.Sandbox);
        var row=new Receipt(1,Guid.NewGuid(),plan.BatchId,plan.IdentitySha256,"prepared",DateTimeOffset.UtcNow,
            plan.Hosts.Select(x=>new HostState(x.HostId,x.StageDirectoryName,"queued",x.Maps,x.Components,x.Bytes)).ToArray());
        Persist(plan,row,true);
        void Update(int index,string state,string phase)
        {
            if(index>=0)row.Hosts[index]=row.Hosts[index] with{State=state};
            row=row with{State=phase,UpdatedUtc=DateTimeOffset.UtcNow};
            Persist(plan,row);
        }
        try
        {
            for(int i=0;i<plan.Hosts.Length;i++)
            {
                ct.ThrowIfCancellationRequested();
                var host=plan.Hosts[i];
                Update(i,"copying","staging");
                progress?.Invoke("COORDINATED_HOST_"+(i+1)+"_OF_"+plan.Hosts.Length);
                var result=MultiHostHostRestoreStaging.Stage(host,ownership,protectedRoots,ct);
                ExactReceipt(host,source,result);
                Update(i,"verified","staging");
                afterHostForTesting?.Invoke(i+1);
            }
            ct.ThrowIfCancellationRequested();
            // Rehash every final host package plus the original sandbox before
            // the terminal receipt. Same-batch IDs alone do not prove equality.
            var current=MultiHostClusterBackupArchive.VerifyIsolatedSandbox(plan.Sandbox);
            if(current.SourceBatchId!=source.SourceBatchId||
                !current.Components.SequenceEqual(source.Components))
                throw new InvalidDataException("UM-COORDINATED-RESTORE-SOURCE-CHANGED");
            foreach(var host in plan.Hosts)
            {
                ct.ThrowIfCancellationRequested();
                ExactReceipt(host,current,MultiHostHostRestoreStaging.Verify(Path.Combine(plan.Parent,host.StageDirectoryName)));
            }
            Update(-1,"verified","offline-ready");return row;
        }
        catch
        {
            try{Update(-1,"","needs-review");}catch{}
            // Never remove a completed/partial package or replay an interrupted
            // copy. The adjacent journal records all completed host stages.
            throw;
        }
    }
}
