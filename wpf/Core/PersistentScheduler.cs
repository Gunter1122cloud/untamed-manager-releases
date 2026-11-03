using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private readonly string path;
    private List<TaskReceipt> receipts;
    public PersistentScheduler(string root)
    {
        Directory.CreateDirectory(root);path=Path.Combine(root,"scheduler-state.json");
        receipts=File.Exists(path)?JsonSerializer.Deserialize<List<TaskReceipt>>(File.ReadAllText(path))??throw new IOException("Invalid scheduler state"):new();
    }
    private void Save()
    {
        using(var file=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(file,receipts);file.Flush(true);}
        File.Move(path+".tmp",path,true);
    }
    public TaskReceipt[] Receipts=>receipts.ToArray();
    public async Task RunDue(IEnumerable<ScheduledTask> tasks,IReadOnlySet<Guid> owned,DateTimeOffset now,Func<ScheduledTask,Task> execute)
    {
        foreach(var task in tasks.Where(x=>x.Enabled&&x.DueUtc<=now).OrderBy(x=>x.DueUtc))
        {
            if(receipts.Any(x=>x.Task==task.Id&&x.DueUtc==task.DueUtc))continue;
            string status=!Enum.IsDefined(task.Kind)?"invalid-kind-blocked":!owned.Contains(task.Map)?"foreign-map-blocked":task.Kind==ScheduledKind.AutoStart?"autostart-disabled":"claimed";
            int index=receipts.Count;receipts.Add(new(task.Id,task.DueUtc,status,0));Save();
            if(status!="claimed")continue;
            // Claim is durable before execution. Interrupted/uncertain jobs are never retried automatically.
            try {await execute(task);receipts[index]=new(task.Id,task.DueUtc,"succeeded",1);}
            catch(Exception){receipts[index]=new(task.Id,task.DueUtc,"failed-no-auto-retry",1);}
            Save();
        }
    }
}
