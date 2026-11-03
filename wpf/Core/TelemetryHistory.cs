using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record TelemetrySample(Guid Map,DateTimeOffset ObservedUtc,
    double? CpuPercent,long? MemoryBytes,int? Players,double? RconLatencyMs,
    double? NetworkBytesPerSecond,string ProcessStatus,bool IsStale);

public sealed class TelemetryHistory
{
    private readonly string root;
    public TelemetryHistory(string localProfile)
    {
        if(string.IsNullOrWhiteSpace(localProfile) ||
           !Path.IsPathFullyQualified(localProfile) || localProfile.StartsWith(@"\\"))
            throw new InvalidDataException("Local telemetry profile required");
        var full=Path.GetFullPath(localProfile);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Telemetry must stay on fixed local disk");
        for(var d=new DirectoryInfo(full);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked telemetry path rejected");
        root=Path.Combine(full,"telemetry-history");
        Directory.CreateDirectory(root);
    }
    private string FileFor(Guid map)
    {
        if(map==Guid.Empty)throw new InvalidDataException("Map ID missing");
        return Path.Combine(root,map.ToString("N")+".jsonl");
    }
    public void Observe(MapObservation observation,DateTimeOffset now)
    {
        if(observation==null || observation.Map==Guid.Empty)
            throw new InvalidDataException("Map telemetry identity required");
        if(observation.ObservedUtc>now.AddSeconds(5) ||
           now-observation.ObservedUtc>TimeSpan.FromSeconds(30))
            return; // never treat stale observations as live samples
        string file=FileFor(observation.Map);
        var lines=File.Exists(file)?File.ReadAllLines(file):Array.Empty<string>();
        var entries=new List<TelemetrySample>();
        foreach(var line in lines.TakeLast(2000))
            try{var e=JsonSerializer.Deserialize<TelemetrySample>(line);
                if(e!=null && e.Map==observation.Map &&
                   now-e.ObservedUtc<TimeSpan.FromDays(7) &&
                   e.ObservedUtc<=now.AddSeconds(5))entries.Add(e);}
            catch(JsonException){}
        if(entries.Count>0 &&
           observation.ObservedUtc-entries[^1].ObservedUtc<TimeSpan.FromSeconds(30))
            return;
        entries.Add(new(observation.Map,observation.ObservedUtc,
            observation.CpuPercent,observation.MemoryBytes,observation.Players,
            null,null,observation.Error,false));
        string temp=file+".tmp";
        File.WriteAllLines(temp,entries.TakeLast(2000).Select(JsonSerializer.Serialize));
        File.Move(temp,file,true);
    }
    public IReadOnlyList<TelemetrySample> Read(Guid map,DateTimeOffset now,int max=500)
    {
        if(max<1||max>2000)throw new ArgumentOutOfRangeException(nameof(max));
        string path=FileFor(map);
        if(!File.Exists(path))return Array.Empty<TelemetrySample>();
        return File.ReadLines(path).TakeLast(max)
            .Select(line=>{
                try{return JsonSerializer.Deserialize<TelemetrySample>(line);}
                catch(JsonException){return null;}
            }).Where(x=>x!=null && x.Map==map)
            .Select(x=>x! with {IsStale=now-x!.ObservedUtc>TimeSpan.FromSeconds(30)})
            .ToArray();
    }
}
