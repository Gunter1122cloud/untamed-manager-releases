using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

// UI-independent recurring dispatcher for read-only inspection jobs ONLY.
// Every slot is claimed durably BEFORE execution; no missed or uncertain
// operation is replayed after a crash, reboot or host clock adjustment.
public sealed record RecurringJobReceipt(Guid Job,Guid Map,DateTimeOffset SlotUtc,
    DateTimeOffset ObservedUtc,string Status,string Code);
public sealed class RecurringReadOnlyRunner
{
    private sealed class Cursor
    {
        public Guid Job { get; set; }
        public string Signature { get; set; }="";
        public DateTimeOffset NextDueUtc { get; set; }
    }
    private sealed class Store
    {
        public List<Cursor> Cursors { get; set; }=new();
        public List<RecurringJobReceipt> Receipts { get; set; }=new();
    }
    private readonly string file,mutexName;
    public RecurringReadOnlyRunner(string root)
    {
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\"))
            throw new InvalidDataException("Local recurring runner root required");
        var full=Path.GetFullPath(root);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Recurring runner requires local fixed disk");
        for(var d=new DirectoryInfo(full);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked recurring runner root rejected");
        Directory.CreateDirectory(full);
        file=Path.Combine(full,"recurring-readonly-v1.json");
        mutexName="Local\\Untamed-RecurringReadOnly-"+Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())));
    }
    private Store ReadStore()
    {
        if(!File.Exists(file))return new Store();
        if(new FileInfo(file).Length>1024*1024 ||
           (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Unsafe recurring runner journal");
        var data=JsonSerializer.Deserialize<Store>(File.ReadAllText(file))
            ??throw new InvalidDataException("Invalid recurring runner journal");
        if(data.Cursors==null||data.Receipts==null||
           data.Cursors.Count>1000||data.Receipts.Count>3000||
           data.Cursors.Any(x=>x.Job==Guid.Empty)||
           data.Cursors.Select(x=>x.Job).Distinct().Count()!=data.Cursors.Count)
            throw new InvalidDataException("Corrupt recurring runner journal");
        return data;
    }
    private void WriteStore(Store s)
    {
        s.Receipts=s.Receipts.TakeLast(2000).ToList();
        string tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(output,s);output.Flush(true);}
            File.Move(tmp,file,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    private static bool Supported(OperationalTaskKind kind)
        =>kind is OperationalTaskKind.Observation or OperationalTaskKind.ModCheck
            or OperationalTaskKind.ApiCheck or OperationalTaskKind.SteamCheck;
    private static string Signature(RecurringSchedule j)
        =>j.Map.ToString("N")+"|"+j.Kind+"|"+j.Frequency+"|"+
          j.LocalClock.Ticks+"|"+j.WeekDay+"|"+j.IntervalMinutes+"|"+
          j.Enabled+"|"+j.NoticeRequired;
    public RecurringJobReceipt[] Tick(IEnumerable<RecurringSchedule> schedules,
        IReadOnlySet<Guid> owned,DateTimeOffset now,
        Func<RecurringSchedule,bool> maintenance,
        Action<RecurringSchedule> readOnlyAction)
    {
        if(schedules==null||owned==null||maintenance==null||readOnlyAction==null)
            throw new ArgumentNullException(nameof(schedules));
        var jobs=schedules.ToArray();
        if(jobs.Length>500||jobs.Any(j=>j==null||j.Id==Guid.Empty||j.Map==Guid.Empty)||
           jobs.Select(x=>x.Id).Distinct().Count()!=jobs.Length)
            throw new InvalidDataException("Invalid recurring job ownership or duplicate IDs");
        using var gate=new Mutex(false,mutexName);
        bool held=false;
        try
        {
            try{held=gate.WaitOne(TimeSpan.FromSeconds(5));}
            catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("Recurring runner is busy");
            var s=ReadStore();
            var results=new List<RecurringJobReceipt>();
            foreach(var job in jobs.OrderBy(x=>x.Id))
            {
                string signature=Signature(job);
                var cursor=s.Cursors.SingleOrDefault(x=>x.Job==job.Id);
                // New/edited/disabled jobs need a new future slot. Never
                // auto-catch-up tasks configured before first Supervisor tick.
                if(cursor==null||cursor.Signature!=signature)
                {
                    if(cursor==null){cursor=new Cursor{Job=job.Id};s.Cursors.Add(cursor);}
                    cursor.Signature=signature;
                    cursor.NextDueUtc=OperationalScheduler.Next(job,now).DueUtc;
                    WriteStore(s);
                    continue;
                }
                if(!job.Enabled||cursor.NextDueUtc>now)continue;
                var due=cursor.NextDueUtc;
                // Subsequent recurrence is computed from its previous slot,
                // not the current clock. Skipped slots cannot replay.
                cursor.NextDueUtc=OperationalScheduler.Next(job,due).DueUtc;
                if(cursor.NextDueUtc<=due)
                    throw new InvalidDataException("Non-advancing recurring job cursor");
                string status=!owned.Contains(job.Map)?"foreign-map-blocked":
                    !Supported(job.Kind)?"live-operation-disabled":
                    maintenance(job)?"maintenance-blocked":
                    now-due>TimeSpan.FromMinutes(5)?"missed-no-catchup":"claimed";
                var receipt=new RecurringJobReceipt(job.Id,job.Map,due,now,
                    status,status=="claimed"?"read-only-start":"read-only-blocked");
                s.Receipts.Add(receipt);WriteStore(s);results.Add(receipt);
                if(status!="claimed")continue;
                string outcome="succeeded",code="read-only-complete";
                try{readOnlyAction(job);}
                catch(Exception){outcome="failed-not-retried";code="read-only-exception";}
                var final=new RecurringJobReceipt(job.Id,job.Map,due,now,outcome,code);
                s.Receipts.Add(final);WriteStore(s);results.Add(final);
            }
            // Active journal has bounded memory; removed jobs are never run.
            s.Cursors.RemoveAll(c=>!jobs.Any(j=>j.Id==c.Job));
            WriteStore(s);
            return results.ToArray();
        }
        finally{if(held)gate.ReleaseMutex();}
    }
    public RecurringJobReceipt[] Recent(int max=100)
    {
        if(max<1||max>500)throw new ArgumentOutOfRangeException(nameof(max));
        using var gate=new Mutex(false,mutexName);
        bool held=false;
        try
        {
            try{held=gate.WaitOne(TimeSpan.FromSeconds(5));}
            catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("Recurring runner busy");
            return ReadStore().Receipts.TakeLast(max).ToArray();
        }
        finally{if(held)gate.ReleaseMutex();}
    }
}
