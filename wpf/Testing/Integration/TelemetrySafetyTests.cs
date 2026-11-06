using System.Text.Json;
using UntamedManager.Core;

public static class TelemetrySafetyTests
{
    static void Check(bool value,string reason)
    {if(!value)throw new Exception("Telemetry safety: "+reason);Console.WriteLine("PASS telemetry-safety "+reason);}
    public static async Task Run(string root)
    {
        string profile=Path.Combine(root,"telemetry-safety");Guid map=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        var first=new TelemetryHistory(profile);var stale=new TelemetryHistory(profile);
        var sample=new MapObservation(map,"fixture",true,"known","unknown",double.NaN,-1,null,now,-1,false,
            "unknown","unknown","unknown","password=must-never-persist");
        await Task.WhenAll(Task.Run(()=>first.Observe(sample,now)),Task.Run(()=>stale.Observe(sample,now)));
        var read=first.Read(map,now).Single();
        Check(read.CpuPercent==null&&read.MemoryBytes==null&&read.Players==null&&read.ProcessStatus=="running",
            "invalid/nonfinite measurements stay unknown rather than zero and cannot break JSON persistence");
        string path=Path.Combine(profile,"telemetry-history",map.ToString("N")+".jsonl");
        Check(!File.ReadAllText(path).Contains("password="),"raw process/RCON error text is excluded from telemetry records");
        Check(stale.Read(map,now).Count==1,"two stale instances cannot duplicate the same sample during concurrent writes");
        var future=new TelemetrySample(map,now.AddDays(1),34,1024,5,0,0,"token=legacy-secret",false);
        var negative=new TelemetrySample(map,now,-1,-1,-1,-1,-1,"password=legacy-secret",false);
        File.WriteAllLines(path,new[]{JsonSerializer.Serialize(negative),JsonSerializer.Serialize(future)});
        var cleaned=first.Read(map,now).Single();
        Check(cleaned.CpuPercent==null&&cleaned.RconLatencyMs==null&&cleaned.NetworkBytesPerSecond==null&&
            cleaned.ProcessStatus=="unknown","legacy invalid values and secret-bearing status are sanitized; future rows are excluded");
        var next=now.AddSeconds(31);first.Observe(sample with {ObservedUtc=next,CpuPercent=0,MemoryBytes=0,Players=0},next);
        var series=stale.Read(map,next);
        Check(series.Count==2&&series[^1].CpuPercent==0&&series[^1].Players==0&&series[^1].MemoryBytes==0&&
            !File.ReadAllText(path).Contains("legacy-secret"),"actual measured zeros remain valid; future history cannot suppress a fresh sample");
        Check(first.Read(map,next.AddMinutes(1)).All(x=>x.IsStale),"historical samples cannot masquerade as current values");
        using(var oversized=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None))oversized.SetLength(5*1024*1024);
        bool blocked=false;try{first.Read(map,now);}catch(IOException){blocked=true;}
        Check(blocked&&new FileInfo(path).Length==5*1024*1024,"oversized history is rejected before reading or truncating it");
        var config=JsonSerializer.Deserialize<ManagerConfig>("{}")!;
        Check(!config.TelemetryHistoryEnabled,"legacy/default configuration does not silently opt into persistent telemetry sampling");
    }
}
