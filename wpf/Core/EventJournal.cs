using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace UntamedManager.Core;
public sealed record MonitorEvent(Guid Id,DateTimeOffset Utc,string Host,Guid? Map,string Severity,string Code);
public sealed class EventJournal(string root)
{
    private string PathName=>Path.Combine(root,"events.jsonl");
    public void Append(string host,Guid? map,string severity,string code)
    {
        if(code.Length>100||code.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'&&c!='_'))throw new ArgumentException("Only event codes may be logged");
        Directory.CreateDirectory(root);
        using var gate=new Mutex(false,"Local\\Untamed-Events-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(root).ToUpperInvariant()))));
        bool entered=false;
        try
        {
            try{entered=gate.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("Event journal busy");
            using var file=new FileStream(PathName,FileMode.Append,FileAccess.Write,FileShare.Read);
            byte[] data=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new MonitorEvent(Guid.NewGuid(),DateTimeOffset.UtcNow,host,map,severity,code))+"\n");
            file.Write(data);file.Flush(true);
        }
        finally{if(entered)gate.ReleaseMutex();}
    }
    public MonitorEvent[] Read(string? host=null,Guid? map=null,string? severity=null,DateTimeOffset? since=null)
    {
        if(!File.Exists(PathName))return Array.Empty<MonitorEvent>();
        var events=new List<MonitorEvent>();
        using var file=new FileStream(PathName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        using var reader=new StreamReader(file);
        while(reader.ReadLine() is {} line)
        {
            try
            {
                var e=JsonSerializer.Deserialize<MonitorEvent>(line);
                if(e!=null&&(host==null||e.Host==host)&&(map==null||e.Map==map)&&(severity==null||e.Severity==severity)&&(since==null||e.Utc>=since))events.Add(e);
            }catch(JsonException){} // An interrupted last record is never a successful event.
        }
        return events.TakeLast(500).Reverse().ToArray();
    }
}
