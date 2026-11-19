using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using UntamedManager.Core;

namespace UntamedDesktop;

// Closed DTO: never serialize raw bridge responses, command lines, paths,
// exception text, passwords, role assertions or approval tokens.
internal sealed class DiscordOperationEvents
{
    internal sealed record Event(long Sequence,string EventId,Guid OperationId,Guid[] MapIds,
        string Operation,string Origin,string? ActorId,DateTimeOffset EventUtc,string Phase,
        string Result,string Evidence,DateTimeOffset? ScheduledUtc=null,Guid? NativeJobId=null);
    internal sealed record Page(int Version,string HostId,Guid? Epoch,long OldestSequence,long LatestSequence,
        string State,bool Gap,bool More,string? NextCursor,Event[] Events);
    private sealed record Journal(Guid Epoch,string HostId,long Latest,List<Event> Events);
    private sealed record Origin(string Name,Guid? Id,string? Actor,Guid[]? Maps,string? Action,bool Suppress);
    private static readonly AsyncLocal<Origin?> Current=new();
    private sealed class Scope(Origin? previous):IDisposable
    {
        public void Dispose()=>Current.Value=previous;
    }
    internal static IDisposable EnterOrigin(string name,Guid? id=null,string? actor=null,Guid[]? maps=null,string? action=null,bool suppress=false)
    {
        var previous=Current.Value;Current.Value=new(name,id,actor,maps,action,suppress);return new Scope(previous);
    }
    internal sealed class NativeRun : IDisposable
    {
        private readonly DiscordOperationEvents stream;
        private readonly Guid id,native;
        private readonly Guid[] maps;
        private readonly string operation,origin;
        private readonly string? actor;
        private readonly bool suppress;
        private readonly HashSet<string> emitted=new(StringComparer.Ordinal);
        internal NativeRun(ClusterService cluster,Guid native,Guid[] maps,string operation)
        {
            stream=new(cluster);this.native=native;
            var source=Current.Value;id=source?.Id??native;
            this.maps=source?.Maps??maps;this.operation=source?.Action??operation;
            origin=source?.Name??"unknown";actor=source?.Actor;suppress=source?.Suppress==true;
        }
        public void Dispose()
        {
            lock(emitted)
                if(emitted.Contains("started")&&!emitted.Contains("terminal"))Emit("terminal","uncertain","NATIVE_EXECUTION_OUTCOME_UNVERIFIED");
        }
        internal void Emit(string phase,string result,string evidence)
        {
            if(suppress)return;
            lock(emitted)
            {
                if(!emitted.Add(phase))return;
                stream.Record(id,maps,operation,origin,actor,phase,result,evidence,DateTimeOffset.UtcNow,nativeJob:native);
            }
        }
    }
    private const int Retain=4096,MaxBytes=8_000_000;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly Regex Code=new(@"\A[A-Za-z0-9_-]{1,240}\z",RegexOptions.CultureInvariant);
    private readonly string file,gapFile,mutex,hostId;
    internal DiscordOperationEvents(ClusterService cluster):this(cluster.Store.Root,cluster.Config.LocalHostId){}
    internal DiscordOperationEvents(string root,string hostId)
    {
        this.hostId=hostId;
        file=Path.Combine(root,"discord-operation-events-v1.json");
        gapFile=file+".gap";
        mutex=@"Global\Untamed-DiscordEvents-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
    }
    private T Locked<T>(Func<T> action)
    {
        using var gate=new Mutex(false,mutex);
        bool held;try{held=gate.WaitOne(TimeSpan.FromSeconds(3));}catch(AbandonedMutexException){held=true;}
        if(!held)throw new IOException("UM-DC-EVENTS-BUSY");
        try
        {
            for(var d=new DirectoryInfo(Path.GetDirectoryName(file)!);d!=null;d=d.Parent)
                if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("UM-DC-EVENTS-LINK");
            foreach(var path in new[]{file,gapFile})
                if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("UM-DC-EVENTS-LINK");
            return action();
        }
        finally{gate.ReleaseMutex();}
    }
    private Journal? Load()
    {
        if(!File.Exists(file))return null;
        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length is <2 or >MaxBytes)throw new InvalidDataException("UM-DC-EVENTS-CORRUPT");
        var journal=JsonSerializer.Deserialize<Journal>(stream,Json)??throw new InvalidDataException("UM-DC-EVENTS-CORRUPT");
        if(journal.Epoch==Guid.Empty||journal.HostId!=hostId||journal.Latest<0||journal.Events==null||journal.Events.Count>Retain||
           journal.Events.Where((e,i)=>e==null||e.Sequence!=journal.Latest-journal.Events.Count+i+1||e.OperationId==Guid.Empty||
               e.EventId!=journal.Epoch.ToString("N")+":"+e.Sequence||!Valid(e)).Any())
            throw new InvalidDataException("UM-DC-EVENTS-CORRUPT");
        return journal;
    }
    private static bool Valid(Event e)=>e.MapIds is {Length:>=1 and <=25}&&e.MapIds.All(x=>x!=Guid.Empty)&&
        e.MapIds.Distinct().Count()==e.MapIds.Length&&Code.IsMatch(e.Operation)&&
        e.Origin is "scheduler" or "discord" or "manager" or "unknown"&&
        (e.ActorId==null||(e.ActorId.Length is >=15 and <=25&&e.ActorId.All(char.IsAsciiDigit)))&&
        Code.IsMatch(e.Phase)&&Code.IsMatch(e.Result)&&Code.IsMatch(e.Evidence);
    private void Save(Journal journal)
    {
        string tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var s=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(s,journal,Json);
                if(s.Length>MaxBytes)throw new IOException("UM-DC-EVENTS-LIMIT");
                s.Flush(true);
            }
            File.Move(tmp,file,true);
        }
        finally {if(File.Exists(tmp))File.Delete(tmp);}
    }
    // Telemetry cannot replay an operation or change its native outcome.
    // A failed publication poisons completeness explicitly. If even the gap
    // marker cannot be saved, the process-local flag remains visible.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,bool> MemoryGaps=new();
    internal void Record(Guid id,Guid[] maps,string operation,string origin,string? actor,
        string phase,string result,string evidence,DateTimeOffset utc,DateTimeOffset? scheduled=null,Guid? nativeJob=null)
    {
        try
        {
            Locked(()=>{
                var current=Load()??new Journal(Guid.NewGuid(),hostId,0,new());
                // An acknowledgement returning after a completed async worker is not a new phase.
                if(current.Events.Any(x=>x.OperationId==id&&x.Phase=="terminal"))return true;
                if(current.Events.Any(x=>x.OperationId==id&&x.Phase==phase&&x.Result==result&&x.Evidence==evidence))return true;
                var next=checked(current.Latest+1);
                var e=new Event(next,current.Epoch.ToString("N")+":"+next,id,maps,operation,origin,actor,
                    utc,phase,result,string.IsNullOrEmpty(evidence)?"NO_ADDITIONAL_EVIDENCE":Code.IsMatch(evidence)?evidence:"EVIDENCE_NOT_EXPORTABLE",scheduled,nativeJob);
                if(!Valid(e))throw new InvalidDataException("UM-DC-EVENTS-FIELDS");
                current.Events.Add(e);
                if(current.Events.Count>Retain)current.Events.RemoveAt(0);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                Save(current with{Latest=next});
                return true;
            });
        }
        catch
        {
            MemoryGaps[file]=true;
            try{Locked(()=>{using var f=new FileStream(gapFile,FileMode.Create,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);f.WriteByte(1);f.Flush(true);return true;});}catch{}
        }
    }
    internal Page Read(string? cursor,int limit)
    {
        if(limit is <1 or >100)throw new InvalidDataException("UM-DC-EVENTS-LIMIT");
        return Locked(()=>{
            bool gap=MemoryGaps.ContainsKey(file)||File.Exists(gapFile);
            var journal=Load();
            if(journal==null)return new Page(1,hostId,null,0,0,cursor is null or "empty"?"notInitialized":"journalChanged",gap||cursor is not (null or "empty"),false,cursor is null or "empty"?"empty":null,Array.Empty<Event>());
            long oldest=journal.Events.Count==0?journal.Latest+1:journal.Events[0].Sequence;
            string Make(long n)=>journal.Epoch.ToString("N")+":"+n;
            // Null means baseline at the current high-water mark. No old
            // events are represented as new alerts. "begin" explicitly opts in.
            long after=journal.Latest;
            if(cursor=="begin")after=oldest-1;
            else if(cursor=="empty")
            {
                if(oldest>1)return new Page(1,hostId,journal.Epoch,oldest,journal.Latest,"cursorInvalid",true,false,null,Array.Empty<Event>());
                after=0;
            }
            else if(cursor!=null)
            {
                var parts=cursor.Split(':');
                if(parts.Length!=2||!Guid.TryParseExact(parts[0],"N",out var epoch)||!long.TryParse(parts[1],out after)||after<0)
                    throw new InvalidDataException("UM-DC-EVENTS-CURSOR");
                if(epoch!=journal.Epoch||after>journal.Latest||after<oldest-1)
                    return new Page(1,hostId,journal.Epoch,oldest,journal.Latest,"cursorInvalid",true,false,null,Array.Empty<Event>());
            }
            var events=journal.Events.Where(x=>x.Sequence>after).Take(limit).ToArray();
            long last=events.LastOrDefault()?.Sequence??after;
            return new Page(1,hostId,journal.Epoch,oldest,journal.Latest,gap?"publicationGap":"ok",gap,last<journal.Latest,Make(last),events);
        });
    }
}
