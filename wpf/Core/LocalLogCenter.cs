using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UntamedManager.Core;

public enum LocalLogLevel { Info, Warning, Error }

public sealed record LocalLogLine(long Number,LocalLogLevel Level,string Content);
public sealed record LocalLogSnapshot(Guid MapId,string FileName,long TotalBytes,
    DateTimeOffset LastModifiedUtc,bool Truncated,IReadOnlyList<LocalLogLine> Lines);

// Only a bounded TAIL of an explicitly registered LOCAL ASA log is read.
// Historical log text never certifies a process identity, RCON, SaveComplete,
// ASA health or live Store/SQL writes. No directories are created or modified.
public static class LocalLogCenter
{
    public static LocalLogSnapshot Read(LocalMap map,int maxBytes=262_144,int maxLines=1600)
    {
        if(map is null || map.Id==Guid.Empty)
            throw new InvalidDataException("Explicit local map identity required");
        if(maxBytes<512||maxBytes>1_048_576||maxLines<1||maxLines>4000)
            throw new ArgumentOutOfRangeException(nameof(maxBytes),"Bounded log limits required");
        var root=FixedDirectory(map.InstallationPath);
        var log=map.LogFilePath??"";
        if(string.IsNullOrWhiteSpace(log)||!Path.IsPathFullyQualified(log)||
           log.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("Local ASA log path required");
        var full=Path.GetFullPath(log);
        if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Log file must belong to the selected local installation");
        if(Path.GetExtension(full).ToLowerInvariant() is not (".log" or ".txt"))
            throw new InvalidDataException("Only local .log/.txt text files are supported");
        string relative=Path.GetRelativePath(root,full);
        if(relative.Split(Path.DirectorySeparatorChar).Any(part=>
            part.Length==0||part=="."||part==".."||part.Contains(':')))
            throw new InvalidDataException("Invalid relative log path");
        CheckNoLinks(root,full);
        var file=new FileInfo(full);
        if(!file.Exists || (file.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new FileNotFoundException("Local log is unavailable");
        if(file.Length>50L*1024*1024*1024)
            throw new InvalidDataException("Unexpectedly large log file");
        var lines=new List<LocalLogLine>();
        using(var stream=new FileStream(full,FileMode.Open,FileAccess.Read,
            FileShare.ReadWrite|FileShare.Delete,4096,FileOptions.SequentialScan))
        {
            long length=stream.Length;
            long offset=Math.Max(0,length-maxBytes);
            stream.Seek(offset,SeekOrigin.Begin);
            using var reader=new StreamReader(stream,new UTF8Encoding(false,false),
                detectEncodingFromByteOrderMarks:true,bufferSize:4096,leaveOpen:false);
            if(offset>0)reader.ReadLine(); // partial first line must not be displayed
            long row=0;
            while(reader.ReadLine() is { } line)
            {
                row++;
                if(line.Length>8192)line=line[..8192]+" [LINE TRUNCATED]";
                if(line.IndexOf('\0')>=0)
                    throw new InvalidDataException("Binary data is not an ASA log");
                string safe=UntamedPresentation.Redact(line);
                lines.Add(new(row,LevelOf(safe),safe));
                if(lines.Count>maxLines)lines.RemoveAt(0);
            }
            return new(map.Id,relative,length,new DateTimeOffset(file.LastWriteTimeUtc,TimeSpan.Zero),
                offset>0||row>maxLines,lines.ToArray());
        }
    }
    public static IReadOnlyList<LocalLogLine> Filter(LocalLogSnapshot snapshot,
        string? query,LocalLogLevel? level=null,int maxResults=1000)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if(maxResults<1||maxResults>4000)throw new ArgumentOutOfRangeException(nameof(maxResults));
        if((query?.Length??0)>128)throw new InvalidDataException("Log search is too long");
        return snapshot.Lines.Where(l=>
            (!level.HasValue||l.Level==level) &&
            (string.IsNullOrWhiteSpace(query)||
             l.Content.Contains(query.Trim(),StringComparison.OrdinalIgnoreCase)))
            .TakeLast(maxResults).ToArray();
    }

    private static LocalLogLevel LevelOf(string s)
    {
        if(s.Contains("error",StringComparison.OrdinalIgnoreCase) ||
           s.Contains("fatal",StringComparison.OrdinalIgnoreCase) ||
           s.Contains("exception",StringComparison.OrdinalIgnoreCase) ||
           s.Contains("failed",StringComparison.OrdinalIgnoreCase))
            return LocalLogLevel.Error;
        if(s.Contains("warning",StringComparison.OrdinalIgnoreCase) ||
           s.Contains("warn",StringComparison.OrdinalIgnoreCase) ||
           s.Contains("timeout",StringComparison.OrdinalIgnoreCase))
            return LocalLogLevel.Warning;
        return LocalLogLevel.Info;
    }
    private static string FixedDirectory(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
           path.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("Explicit local ASA installation required");
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Local fixed drive required");
        for(var dir=new DirectoryInfo(full);dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked installation directory refused");
        return full;
    }
    private static void CheckNoLinks(string root,string full)
    {
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(full)!);
            dir!=null;dir=dir.Parent)
        {
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked log directory refused");
            if(dir.FullName.Equals(root,StringComparison.OrdinalIgnoreCase))break;
        }
        var file=new FileInfo(full);
        if(file.Exists&&(file.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked log file refused");
    }
}
