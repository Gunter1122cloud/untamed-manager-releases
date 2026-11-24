using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UntamedDesktop;

/// <summary>
/// One map's durable local restart-outage receipt. This never attempts
/// recovery by itself, writes no ASA/Store bytes and can be read by the
/// existing Discord notification worker after a Manager restart.
/// </summary>
internal sealed record ScheduledRestartOutage(
    int Schema,Guid RunId,Guid MapId,string MapName,
    DateTimeOffset OutageStartedUtc,DateTimeOffset UpdatedUtc,
    string Error,int Attempts,string NextStep,DateTimeOffset? RecoveredUtc)
{
    internal bool Offline=>RecoveredUtc==null;
}
internal static class ScheduledRestartOutageLedger
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly Regex Code=new(
        @"\A[A-Za-z0-9_-]{1,140}\z",RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static string FileFor(string profile,Guid mapId)
    {
        if(mapId==Guid.Empty||string.IsNullOrWhiteSpace(profile)||
           !Path.IsPathFullyQualified(profile)||
           profile.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-SCHEDULE-OUTAGE-LOCAL-PROFILE");
        string root=Path.GetFullPath(profile);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-SCHEDULE-OUTAGE-FIXED-DISK");
        for(var d=new DirectoryInfo(root);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-SCHEDULE-OUTAGE-LINK");
        return Path.Combine(root,"schedule-restart-outages-v1",
            mapId.ToString("N")+".json");
    }
    private static string Clean(string raw,int max)=>
        new string(raw.Where(c=>!char.IsControl(c)&&c!='@'&&c!='`')
            .Take(max).ToArray());
    internal static ScheduledRestartOutage? Read(string profile,Guid mapId)
    {
        string path=FileFor(profile,mapId);
        if(!File.Exists(path))return null;
        var file=new FileInfo(path);
        if((file.Attributes&FileAttributes.ReparsePoint)!=0||
           file.Length is <4 or >8192)
            throw new InvalidDataException("UM-SCHEDULE-OUTAGE-JOURNAL-INVALID");
        var row=JsonSerializer.Deserialize<ScheduledRestartOutage>(
            File.ReadAllBytes(path),Json);
        if(row==null||row.Schema!=1||row.RunId==Guid.Empty||
           row.MapId!=mapId||row.MapName.Length is <1 or >100||
           row.Error.Length is <1 or >140||!Code.IsMatch(row.Error)||
           row.Attempts is <1 or >4||
           row.NextStep.Length is <1 or >180||
           row.OutageStartedUtc==default||row.UpdatedUtc<row.OutageStartedUtc)
            throw new InvalidDataException("UM-SCHEDULE-OUTAGE-IDENTITY");
        return row;
    }
    internal static ScheduledRestartOutage Record(string profile,Guid mapId,
        Guid runId,string name,DateTimeOffset outageUtc,string error,
        int attempts,string nextStep,DateTimeOffset? recovered=null)
    {
        if(mapId==Guid.Empty||runId==Guid.Empty||outageUtc==default||
           attempts is <1 or >4||!Code.IsMatch(error))
            throw new InvalidDataException("UM-SCHEDULE-OUTAGE-RECEIPT");
        string path=FileFor(profile,mapId),folder=Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        for(var d=new DirectoryInfo(folder);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-SCHEDULE-OUTAGE-LINK");
        string cleanName=Clean(name,100),cleanNext=Clean(nextStep,180);
        if(cleanName.Length==0||cleanNext.Length==0)
            throw new InvalidDataException("UM-SCHEDULE-OUTAGE-FIELDS");
        var row=new ScheduledRestartOutage(1,runId,mapId,cleanName,
            outageUtc,DateTimeOffset.UtcNow,error,attempts,cleanNext,recovered);
        string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var fs=new FileStream(tmp,FileMode.CreateNew,
                FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {JsonSerializer.Serialize(fs,row,Json);fs.Flush(true);}
            File.Move(tmp,path,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
        return row;
    }
}
