using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;
public enum ScheduledKind { Observation, BackupIntegrity, Notification, Maintenance, AutoStart }
public sealed class ScheduledTask
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid Map { get; set; }
    public ScheduledKind Kind { get; set; }
    public DateTimeOffset DueUtc { get; set; }
    public DateTimeOffset? UntilUtc { get; set; }
    public bool Enabled { get; set; }=true;
    public string BackupTestPackage { get; set; }="";
}
public sealed record TaskReceipt(Guid Task,DateTimeOffset DueUtc,string Status,int Attempts);
public sealed class PersistentScheduler
{
    private const int MaxReceipts=20000;
    private readonly string root,path,mutexName;
    public PersistentScheduler(string root)
    {
        UntamedOperationCoordinator.ResourceKey(root);
        this.root=Path.GetFullPath(root);Directory.CreateDirectory(this.root);
        path=Path.Combine(this.root,"scheduler-state.json");
        mutexName="Global\\Untamed-PersistentSchedule-"+
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        Locked(Read);
    }
    private List<TaskReceipt> Read()
    {
        UntamedOperationCoordinator.ResourceKey(root);
        if(!File.Exists(path))return new();
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||new FileInfo(path).Length>8*1024*1024)
            throw new IOException("Unsafe scheduler state");
        var records=JsonSerializer.Deserialize<List<TaskReceipt>>(File.ReadAllText(path))??
            throw new IOException("Invalid scheduler state");
        if(records.Count>MaxReceipts||records.Any(x=>x==null||x.Task==Guid.Empty||
            x.Attempts is <0 or >1)||records.GroupBy(x=>(x.Task,x.DueUtc)).Any(g=>g.Count()!=1))
            throw new IOException("Invalid or duplicate scheduler claims");
        return records;
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex=new Mutex(false,mutexName);bool entered=false;
        try
        {
            try{entered=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("Scheduler claim journal busy");
            return action();
        }
        finally{if(entered)mutex.ReleaseMutex();}
    }
    private void Save(List<TaskReceipt> records)
    {
        UntamedOperationCoordinator.ResourceKey(root);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(file,records);file.Flush(true);}
            if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Linked scheduler journal rejected");
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public TaskReceipt[] Receipts=>Locked(()=>Read().ToArray());
    private static ScheduledTask Copy(ScheduledTask task)=>new(){Id=task.Id,Map=task.Map,Kind=task.Kind,
        DueUtc=task.DueUtc,UntilUtc=task.UntilUtc,Enabled=task.Enabled,BackupTestPackage=task.BackupTestPackage};

    public async Task RunDue(IEnumerable<ScheduledTask> tasks,IReadOnlySet<Guid> owned,DateTimeOffset now,
        Func<ScheduledTask,Task> execute,Func<Guid,bool>? maintenance=null)
    {
        ArgumentNullException.ThrowIfNull(tasks);ArgumentNullException.ThrowIfNull(owned);ArgumentNullException.ThrowIfNull(execute);
        var input=tasks.Take(501).ToArray();
        if(input.Length>500||input.Any(x=>x==null||x.Id==Guid.Empty)||
           input.GroupBy(x=>x.Id).Any(g=>g.Count()!=1))throw new InvalidDataException("Invalid scheduler batch");
        var jobs=input.Select(Copy).Where(x=>x.Enabled&&x.DueUtc<=now).OrderBy(x=>x.DueUtc).ToArray();
        foreach(var task in jobs)
        {
            // Re-read and atomically claim under an OS mutex, even for stale
            // instances. The thread-affine mutex is never held across await.
            bool claimed=Locked(()=>{
                var receipts=Read();
                if(receipts.Any(x=>x.Task==task.Id&&x.DueUtc==task.DueUtc))return false;
                if(receipts.Count>=MaxReceipts)throw new IOException("Scheduler claim capacity reached; no tombstones evicted");
                string status=!Enum.IsDefined(task.Kind)?"invalid-kind-blocked":
                    !owned.Contains(task.Map)?"foreign-map-blocked":
                    task.Kind==ScheduledKind.AutoStart?"autostart-disabled":
                    maintenance?.Invoke(task.Map)==true?"maintenance-blocked":
                    task.UntilUtc.HasValue&&task.UntilUtc.Value<=now?"expired-no-auto-retry":
                    now-task.DueUtc>TimeSpan.FromMinutes(5)?"missed-no-catchup":"claimed";
                receipts.Add(new(task.Id,task.DueUtc,status,0));Save(receipts);
                return status=="claimed";
            });
            if(!claimed)continue;
            string outcome="succeeded";
            // A callback cannot rewrite the identity of its durable claim.
            try{await execute(Copy(task));}catch(Exception){outcome="failed-no-auto-retry";}
            Locked(()=>{
                var receipts=Read();int index=receipts.FindIndex(x=>x.Task==task.Id&&x.DueUtc==task.DueUtc);
                if(index<0||receipts[index].Status!="claimed")throw new IOException("Scheduler claim changed during execution");
                receipts[index]=new(task.Id,task.DueUtc,outcome,1);Save(receipts);return true;
            });
        }
    }
}
