using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

public sealed record TelemetrySample(Guid Map,DateTimeOffset ObservedUtc,
    double? CpuPercent,long? MemoryBytes,int? Players,double? RconLatencyMs,
    double? NetworkBytesPerSecond,string ProcessStatus,bool IsStale);

public sealed class TelemetryHistory
{
    private readonly string root,mutexName;
    public TelemetryHistory(string localProfile)
    {
        UntamedOperationCoordinator.ResourceKey(localProfile);
        root=Path.Combine(Path.GetFullPath(localProfile),"telemetry-history");
        UntamedOperationCoordinator.ResourceKey(root);Directory.CreateDirectory(root);
        mutexName="Global\\Untamed-Telemetry-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
    }
    private string FileFor(Guid map)
    {
        if(map==Guid.Empty)throw new InvalidDataException("Map telemetry identity missing");
        UntamedOperationCoordinator.ResourceKey(root);
        string file=Path.Combine(root,map.ToString("N")+".jsonl");
        if(File.Exists(file)&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked telemetry file refused");
        return file;
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex=new Mutex(false,mutexName);bool entered=false;
        try
        {
            try{entered=mutex.WaitOne(TimeSpan.FromSeconds(2));}catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("Telemetry journal busy");
            return action();
        }
        finally{if(entered)mutex.ReleaseMutex();}
    }
    private static double? Finite(double? value,double maximum)=>
        value.HasValue&&double.IsFinite(value.Value)&&value>=0&&value<=maximum?value:null;
    private static TelemetrySample Sanitize(TelemetrySample sample,DateTimeOffset now)=>sample with {
        CpuPercent=Finite(sample.CpuPercent,100),
        MemoryBytes=sample.MemoryBytes>=0?sample.MemoryBytes:null,
        Players=sample.Players>=0?sample.Players:null,
        RconLatencyMs=Finite(sample.RconLatencyMs,600_000),
        NetworkBytesPerSecond=Finite(sample.NetworkBytesPerSecond,long.MaxValue),
        ProcessStatus=sample.ProcessStatus is "running" or "stopped" or "unknown"?sample.ProcessStatus:"unknown",
        IsStale=now-sample.ObservedUtc>TimeSpan.FromSeconds(30)
    };
    private static List<TelemetrySample> ReadRecords(string file,Guid map,DateTimeOffset now)
    {
        var entries=new List<TelemetrySample>();if(!File.Exists(file))return entries;
        using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>4*1024*1024)throw new IOException("Telemetry journal exceeds bounded reader size");
        using var reader=new StreamReader(input,new UTF8Encoding(false,true),true);
        int lines=0;string? line;
        while((line=reader.ReadLine())!=null)
        {
            if(++lines>4000||line.Length>4096)throw new IOException("Telemetry record bounds exceeded");
            try
            {
                var value=JsonSerializer.Deserialize<TelemetrySample>(line);
                if(value!=null&&value.Map==map&&value.ObservedUtc<=now.AddSeconds(5)&&now-value.ObservedUtc<TimeSpan.FromDays(7))
                    entries.Add(Sanitize(value,now));
            }
            catch(JsonException){}
        }
        return entries.OrderBy(x=>x.ObservedUtc).TakeLast(2000).ToList();
    }
    public void Observe(MapObservation observation,DateTimeOffset now)
    {
        if(observation==null||observation.Map==Guid.Empty)throw new InvalidDataException("Local telemetry identity required");
        if(observation.ObservedUtc>now.AddSeconds(5)||now-observation.ObservedUtc>TimeSpan.FromSeconds(30))return;
        Locked(()=>{
            string file=FileFor(observation.Map);var entries=ReadRecords(file,observation.Map,now);
            if(entries.Count>0&&observation.ObservedUtc-entries[^1].ObservedUtc<TimeSpan.FromSeconds(30))return false;
            entries.Add(Sanitize(new(observation.Map,observation.ObservedUtc,observation.CpuPercent,
                observation.MemoryBytes,observation.Players,null,null,
                observation.Running==true?"running":observation.Running==false?"stopped":"unknown",false),now));
            string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var writer=new StreamWriter(output,new UTF8Encoding(false),1024,true))
                {
                    foreach(var entry in entries.TakeLast(2000))writer.WriteLine(JsonSerializer.Serialize(entry));
                    writer.Flush();output.Flush(true);
                }
                FileFor(observation.Map);File.Move(temp,file,true);
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
            return true;
        });
    }
    public IReadOnlyList<TelemetrySample> Read(Guid map,DateTimeOffset now,int max=500)
    {
        if(max<1||max>2000)throw new ArgumentOutOfRangeException(nameof(max));
        return Locked(()=>ReadRecords(FileFor(map),map,now).TakeLast(max).ToArray());
    }
}
