using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

// TEST-ONLY state machine. It contains no reference to Process, RCON, SteamCMD,
// ILocalServerSession or production Restore. Every transition is a synthetic event.
public enum RecoveryPhase
{
    Prepared, SaveRequested, SaveProven, ExitConfirmed, StartConfirmed, Completed, Blocked
}
public sealed record RecoveryTransition(Guid Attempt,Guid Map,RecoveryPhase Phase,
    DateTimeOffset Utc,string Code);
public sealed record RecoveryScenario(Guid Map,bool Owned,bool Maintenance,ProcessCheck Initial,
    string OperationToken,DateTimeOffset CommandUtc,DateTimeOffset Now,
    IReadOnlyList<SyntheticSaveSignal> SaveSignals,
    bool FakeExitConfirmed,bool FakeStartConfirmed);
public sealed record RecoverySimulationResult(Guid Attempt,Guid Map,RecoveryPhase Phase,
    string Explanation,bool ExecutedRealActions=false);

public sealed class RecoverySimulationJournal
{
    private readonly string path;
    private readonly string mutexName;
    public RecoverySimulationJournal(string profileRoot)
    {
        if(string.IsNullOrWhiteSpace(profileRoot) ||
           !Path.IsPathFullyQualified(profileRoot) || profileRoot.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit local recovery simulation profile required");
        string full=Path.GetFullPath(profileRoot);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Recovery journal must be on fixed local disk");
        var target=Path.Combine(full,"recovery-simulation");
        for(var dir=new DirectoryInfo(target);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked recovery journal path rejected");
        Directory.CreateDirectory(target);
        path=Path.Combine(target,"transitions.json");
        mutexName="Local\\Untamed-Recovery-Synthetic-"+
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex=new Mutex(false,mutexName);
        bool held=false;
        try
        {
            try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}
            catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("Recovery journal busy");
            return action();
        }
        finally{if(held)mutex.ReleaseMutex();}
    }
    private List<RecoveryTransition> Load()
    {
        if(!File.Exists(path))return new();
        var f=new FileInfo(path);
        if(f.Length>1_000_000 || (f.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Unsafe recovery journal");
        var entries=JsonSerializer.Deserialize<List<RecoveryTransition>>(File.ReadAllText(path))
            ??throw new InvalidDataException("Recovery journal is invalid");
        if(entries.Count>10000 || entries.Any(e=>e.Map==Guid.Empty || e.Attempt==Guid.Empty ||
              !Enum.IsDefined(e.Phase)))
            throw new InvalidDataException("Recovery journal is corrupt");
        return entries;
    }
    private void Write(List<RecoveryTransition> entries)
    {
        string temp=path+".tmp";
        using(var fs=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {
            JsonSerializer.Serialize(fs,entries);
            fs.Flush(true);
        }
        File.Move(temp,path,true);
    }
    public RecoveryTransition[] Read()=>Locked(()=>Load().ToArray());
    public RecoveryTransition Begin(Guid ownedMap,DateTimeOffset now)
        =>Locked(()=>
        {
            if(ownedMap==Guid.Empty)throw new ArgumentException("Owned map required");
            var entries=Load();
            var last=entries.Where(e=>e.Map==ownedMap).LastOrDefault();
            // Crashes are NOT resumed or auto-retried. Manual clearance requires
            // a later dedicated audited workflow, not just a new Begin request.
            if(last!=null && last.Phase!=RecoveryPhase.Completed &&
               last.Phase!=RecoveryPhase.Blocked)
                throw new InvalidOperationException("Unfinished synthetic recovery blocks a new attempt");
            if(last!=null && now-last.Utc<TimeSpan.FromMinutes(5))
                throw new InvalidOperationException("Synthetic recovery cooldown active");
            int recent=entries.Count(e=>e.Map==ownedMap &&
                e.Phase==RecoveryPhase.Prepared && now-e.Utc>=TimeSpan.Zero &&
                now-e.Utc<TimeSpan.FromHours(1));
            if(recent>=3)throw new InvalidOperationException("Synthetic recovery rate limit exceeded");
            var next=new RecoveryTransition(Guid.NewGuid(),ownedMap,RecoveryPhase.Prepared,now,
                "recovery-synthetic-prepared");
            entries.Add(next);Write(entries);return next;
        });
    public RecoveryTransition Advance(Guid attempt,RecoveryPhase next,DateTimeOffset now)
        =>Locked(()=>
        {
            var entries=Load();
            var old=entries.LastOrDefault(x=>x.Attempt==attempt);
            if(old==null)throw new InvalidOperationException("Recovery attempt missing");
            var expected=old.Phase switch{
                RecoveryPhase.Prepared=>RecoveryPhase.SaveRequested,
                RecoveryPhase.SaveRequested=>RecoveryPhase.SaveProven,
                RecoveryPhase.SaveProven=>RecoveryPhase.ExitConfirmed,
                RecoveryPhase.ExitConfirmed=>RecoveryPhase.StartConfirmed,
                RecoveryPhase.StartConfirmed=>RecoveryPhase.Completed,
                _ =>RecoveryPhase.Blocked
            };
            if(next!=RecoveryPhase.Blocked && next!=expected ||
               old.Phase is RecoveryPhase.Blocked or RecoveryPhase.Completed ||
               now<old.Utc)
                throw new InvalidOperationException("Recovery journal transition rejected");
            string code=next==RecoveryPhase.Blocked?"recovery-synthetic-blocked":
                "recovery-synthetic-"+next.ToString().ToLowerInvariant();
            var newStep=new RecoveryTransition(attempt,old.Map,next,now,code);
            entries.Add(newStep);Write(entries);return newStep;
        });
}
public static class RecoverySimulator
{
    // ALL evidence is provided by FakeAsa test fixtures, not by a real ASA process.
    // The simulator never uses RCON, Process.Start/Kill, File Restore or network.
    public static RecoverySimulationResult Run(RecoverySimulationJournal journal,
        RecoveryScenario fake)
    {
        if(journal==null)throw new ArgumentNullException(nameof(journal));
        if(fake==null)throw new ArgumentNullException(nameof(fake));
        if(!fake.Owned || fake.Map==Guid.Empty)
            throw new UnauthorizedAccessException("Remote and unknown map recovery forbidden");
        if(fake.Maintenance)
            return new(Guid.Empty,fake.Map,RecoveryPhase.Blocked,"Maintenance forbids recovery");
        if(!fake.Initial.Running || fake.Initial.Identity==null ||
           fake.Initial.ConfirmedStopped)
            return new(Guid.Empty,fake.Map,RecoveryPhase.Blocked,
                "Process identity unverified or already stopped");
        var attempt=journal.Begin(fake.Map,fake.Now);
        var state=attempt;
        try
        {
            state=journal.Advance(attempt.Attempt,RecoveryPhase.SaveRequested,fake.Now);
            var proof=SaveWorldValidator.VerifySynthetic(fake.Map,
                fake.Initial.Identity,fake.OperationToken,fake.CommandUtc,
                fake.Now,fake.SaveSignals);
            if(!proof.Proven)
            {
                state=journal.Advance(attempt.Attempt,RecoveryPhase.Blocked,fake.Now);
                return new(attempt.Attempt,fake.Map,state.Phase,
                    "Synthetic SaveWorld unproven: "+proof.Reason);
            }
            state=journal.Advance(attempt.Attempt,RecoveryPhase.SaveProven,fake.Now);
            if(!fake.FakeExitConfirmed)
            {
                state=journal.Advance(attempt.Attempt,RecoveryPhase.Blocked,fake.Now);
                return new(attempt.Attempt,fake.Map,state.Phase,
                    "Synthetic exit unconfirmed; no start");
            }
            state=journal.Advance(attempt.Attempt,RecoveryPhase.ExitConfirmed,fake.Now);
            if(!fake.FakeStartConfirmed)
            {
                state=journal.Advance(attempt.Attempt,RecoveryPhase.Blocked,fake.Now);
                return new(attempt.Attempt,fake.Map,state.Phase,
                    "Synthetic start unconfirmed");
            }
            state=journal.Advance(attempt.Attempt,RecoveryPhase.StartConfirmed,fake.Now);
            state=journal.Advance(attempt.Attempt,RecoveryPhase.Completed,fake.Now);
            return new(attempt.Attempt,fake.Map,state.Phase,
                "FakeAsa lifecycle simulation completed; no real process touched");
        }
        catch
        {
            try{journal.Advance(attempt.Attempt,RecoveryPhase.Blocked,fake.Now);}catch{}
            throw;
        }
    }
}
