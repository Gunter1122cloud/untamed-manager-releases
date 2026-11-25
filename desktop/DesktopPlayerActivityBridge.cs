using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>Host-local, opt-in activity sampling independent of WebView navigation.
//// Only authenticated fixed ListPlayers reads. Never writes to ASA or shared Store.</summary>
public sealed class DesktopPlayerActivityBridge : IDisposable
{
    private sealed record Player(string Id,string Name);
    private sealed record Sample(long Utc,string Epoch,Player[] Players);
    public sealed record ActivityRecord(Guid MapId,string Id,string Name,long FirstSeen,long LastSeen,double Seconds,int Samples);
    private sealed record Saved(bool Enabled,ActivityRecord[] Records);
    private readonly object gate=new();
    private readonly string? stateFile;
    private readonly Func<string,Task<string>> local,historical;
    private readonly Dictionary<Guid,Sample> previous=new();
    private readonly Dictionary<Guid,(int Failures,long After)> backoff=new();
    private ActivityRecord[] records=Array.Empty<ActivityRecord>();
    private bool enabled,disposed,storageError;
    private long generation;
    private int polling;
    private const long MaxGap=90_000,Retention=30L*86_400_000;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly Regex Row=new(@"^(\d+)\.\s+(.+),\s*([A-Za-z0-9]{16,64})$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static readonly Regex Id=new(@"^[a-z0-9]{16,64}$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public DesktopPlayerActivityBridge(string? profileRoot,Func<string,Task<string>> local,Func<string,Task<string>> historical)
    {
        this.local=local;this.historical=historical;
        if(profileRoot==null)return;
        stateFile=Path.Combine(Path.GetFullPath(profileRoot),"player-activity.dpapi");
        try
        {
            SafePath();
            if(!File.Exists(stateFile))return;
            using var input=new FileStream(stateFile,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(input.Length is <1 or >4_000_000)throw new InvalidDataException();
            byte[] bytes=new byte[(int)input.Length];input.ReadExactly(bytes);
            var saved=JsonSerializer.Deserialize<Saved>(SecureStore.UnprotectBytes(bytes),Json)??throw new InvalidDataException();
            records=Clean(saved.Records??Array.Empty<ActivityRecord>(),Now());
            enabled=saved.Enabled;
        }
        catch{enabled=false;storageError=true;}
    }
    private void SafePath()
    {
        if(stateFile==null)return;
        for(var d=new DirectoryInfo(Path.GetDirectoryName(stateFile)!);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();
        if(File.Exists(stateFile)&&(File.GetAttributes(stateFile)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();
    }
    private static ActivityRecord[] Clean(IEnumerable<ActivityRecord> source,long now)=>source
        .Where(r=>r!=null&&r.MapId!=Guid.Empty&&r.Id!=null&&Id.IsMatch(r.Id)&&r.Name!=null&&r.Name.Length<=120&&
            !r.Name.Any(char.IsControl)&&r.FirstSeen>=0&&r.FirstSeen<=r.LastSeen&&r.LastSeen<=now+5000&&now-r.LastSeen<=Retention&&
            double.IsFinite(r.Seconds)&&r.Seconds>=0&&r.Seconds<=(r.LastSeen-r.FirstSeen)/1000d+1&&r.Samples>=1)
        .OrderByDescending(r=>r.LastSeen).DistinctBy(r=>(r.MapId,r.Id)).Take(1000).ToArray();
    private void Save(bool nextEnabled,ActivityRecord[] next)
    {
        if(stateFile==null)return;
        SafePath();Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);SafePath();
        byte[] bytes=SecureStore.ProtectBytes(JsonSerializer.SerializeToUtf8Bytes(new Saved(nextEnabled,next),Json));
        if(bytes.Length>4_000_000)throw new InvalidDataException();
        string temp=stateFile+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {output.Write(bytes);output.Flush(true);}
            SafePath();File.Move(temp,stateFile,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private object Status()=>new{enabled,records=Clean(records,Now()),storageError,persistent=stateFile!=null};
    public Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >2048)throw new InvalidDataException();
            using var doc=JsonDocument.Parse(raw);var root=doc.RootElement;
            id=root.GetProperty("id").GetGuid().ToString("D");
            string? method=root.GetProperty("method").GetString();
            lock(gate)
            {
                if(disposed)throw new InvalidOperationException();
                if(method=="activity.status")return Task.FromResult(JsonSerializer.Serialize(new{id,ok=true,data=Status()},Json));
                if(!root.TryGetProperty("confirm",out var confirm)||confirm.ValueKind!=JsonValueKind.True)
                    throw new InvalidOperationException();
                bool nextEnabled=method=="activity.setEnabled"?root.GetProperty("enabled").GetBoolean():enabled;
                var next=method=="activity.clear"?Array.Empty<ActivityRecord>():Clean(records,Now());
                if(method is not ("activity.setEnabled" or "activity.clear")||storageError&&method!="activity.clear")
                    throw new InvalidOperationException();
                // Clear on a damaged file is explicit and disables collection.
                if(storageError)nextEnabled=false;
                Save(nextEnabled,next);
                enabled=nextEnabled;records=next;storageError=false;
                generation++;previous.Clear();backoff.Clear();
                return Task.FromResult(JsonSerializer.Serialize(new{id,ok=true,data=Status()},Json));
            }
        }
        catch{return Task.FromResult(JsonSerializer.Serialize(new{id,ok=false,error="UM-PLAYER-ACTIVITY-UNAVAILABLE"},Json));}
    }
    private static Player[]? Parse(string raw)
    {
        if(raw.Length>32_768)return null;
        string text=raw.Trim();
        if(text.Equals("No Players Connected",StringComparison.OrdinalIgnoreCase)||text.Equals("No Players Connected.",StringComparison.OrdinalIgnoreCase))
            return Array.Empty<Player>();
        var lines=text.Split('\n').Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();
        if(lines.Length is <1 or >100)return null;
        var result=new List<Player>();var ids=new HashSet<string>(StringComparer.Ordinal);
        int first=-1;
        for(int i=0;i<lines.Length;i++)
        {
            var m=Row.Match(lines[i]);
            if(!m.Success||!int.TryParse(m.Groups[1].Value,out int n))return null;
            if(i==0)first=n;
            string name=m.Groups[2].Value,id=m.Groups[3].Value.ToLowerInvariant();
            if(first is not (0 or 1)||n!=first+i||name.Length>120||name.Any(char.IsControl)||!ids.Add(id))return null;
            result.Add(new(id,name));
        }
        return result.ToArray();
    }
    private static Sample? ReadSample(string raw,Guid mapId)
    {
        using var doc=JsonDocument.Parse(raw);var root=doc.RootElement;
        if(!root.GetProperty("ok").GetBoolean())return null;
        var d=root.GetProperty("data");
        if(d.GetProperty("mapId").GetGuid()!=mapId||d.GetProperty("command").GetString()!="ListPlayers"||
           !d.GetProperty("readOnly").GetBoolean())return null;
        long utc=d.GetProperty("observedUtc").GetDateTimeOffset().ToUnixTimeMilliseconds();
        long now=Now();
        if(utc>now+5000||now-utc>60_000)return null;
        int pid=d.GetProperty("processPid").GetInt32();
        string? started=d.GetProperty("processStartedUtc").GetString();
        if(pid<=0||!DateTimeOffset.TryParse(started,out var start)||start.ToUnixTimeMilliseconds()>utc)return null;
        var players=Parse(d.GetProperty("response").GetString()??"");
        return players==null?null:new(utc,pid+":"+start.UtcTicks,players);
    }
    private void Observe(Guid mapId,Sample current)
    {
        var next=Clean(records,current.Utc).ToDictionary(r=>(r.MapId,r.Id));
        previous.TryGetValue(mapId,out var prior);
        long delta=prior==null?0:current.Utc-prior.Utc;
        if(delta<=0&&prior!=null)return;
        bool continuous=prior!=null&&delta<=MaxGap&&delta>0&&prior.Epoch==current.Epoch;
        var ids=continuous?prior!.Players.Select(p=>p.Id).ToHashSet():new HashSet<string>();
        foreach(var p in current.Players)
        {
            next.TryGetValue((mapId,p.Id),out var old);
            if(old!=null&&old.LastSeen>=current.Utc)continue;
            double seconds=old!=null&&continuous&&old.LastSeen==prior!.Utc&&ids.Contains(p.Id)?delta/1000d:0;
            next[(mapId,p.Id)]=new(mapId,p.Id,p.Name,old?.FirstSeen??current.Utc,current.Utc,(old?.Seconds??0)+seconds,
                old==null?1:old.Samples==int.MaxValue?int.MaxValue:old.Samples+1);
        }
        records=Clean(next.Values,current.Utc);previous[mapId]=current;
    }
    public async Task PollAsync(IEnumerable<(Guid MapId,bool Historical)> eligible)
    {
        if(Interlocked.Exchange(ref polling,1)!=0)return;
        try
        {
            (Guid MapId,bool Historical)[] maps;long captured;
            lock(gate)
            {
                if(!enabled||disposed||storageError)return;
                captured=generation;
                maps=eligible.Where(m=>m.MapId!=Guid.Empty).DistinctBy(m=>m.MapId).Take(50).ToArray();
                var active=maps.Select(m=>m.MapId).ToHashSet();
                foreach(var old in previous.Keys.Where(k=>!active.Contains(k)).ToArray())previous.Remove(old);
                foreach(var old in backoff.Keys.Where(k=>!active.Contains(k)).ToArray())backoff.Remove(old);
                maps=maps.Where(m=>!backoff.TryGetValue(m.MapId,out var b)||b.After<=Now()).ToArray();
            }
            foreach(var batch in maps.Chunk(4))
            {
                lock(gate){if(disposed||!enabled||generation!=captured)return;}
                var results=await Task.WhenAll(batch.Select(async m=>{
                    Sample? sample=null;
                    try
                    {
                        string request=JsonSerializer.Serialize(new{id=Guid.NewGuid(),method=m.Historical?"historical.rconPlayers":"rcon.listPlayers",mapId=m.MapId},Json);
                        sample=ReadSample(await (m.Historical?historical:local)(request),m.MapId);
                    }
                    catch{}
                    return (m.MapId,Sample:sample);
                }));
                lock(gate)
                {
                    if(disposed||!enabled||generation!=captured)return;
                    bool changed=false;
                    foreach(var result in results)
                    {
                        if(result.Sample==null)
                        {
                            previous.Remove(result.MapId);
                            int failures=Math.Min(4,backoff.TryGetValue(result.MapId,out var b)?b.Failures+1:1);
                            backoff[result.MapId]=(failures,Now()+Math.Min(300_000,30_000*(1<<failures)));
                        }
                        else
                        {
                            backoff.Remove(result.MapId);
                            Observe(result.MapId,result.Sample);changed=true;
                        }
                    }
                    if(changed)
                    {
                        try{Save(enabled,records);}
                        catch{storageError=true;enabled=false;previous.Clear();return;}
                    }
                }
            }
        }
        finally{Interlocked.Exchange(ref polling,0);}
    }
    public void BreakContinuity(){lock(gate){generation++;previous.Clear();}}
    public void Dispose(){lock(gate){disposed=true;generation++;previous.Clear();}}
}
