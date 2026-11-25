using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

/// <summary>
/// A bounded, read-only observation tied to the CURRENT Windows process-start
/// timestamp, never an authorization to move characters or edit Store files.
/// Log evidence is supplemental to independently verified PID and RCON.
/// </summary>
internal static class AsaRuntimeLogEvidence
{
    internal sealed record Proof(bool JoinAdvertised,bool ApiInitialized,bool PluginsInitialized);

    private static readonly Regex ApiTime=new(
        @"(?<ts>\d{2}/\d{2}/\d{2} \d{2}:\d{2}) \[API\]",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);

    internal static Proof Inspect(LocalMap map,ProcessIdentity server,
        ProcessIdentity? loader)
    {
        bool advertised=false,apiInitialized=false,pluginsInitialized=false;
        try
        {
            // New map registrations do not necessarily populate LogFilePath.
            // ASA still writes its canonical ShooterGame.log there, exactly
            // as used by the existing real SaveWorld witness.
            // Do not change/persist the operator's saved map configuration.
            var logMap=string.IsNullOrWhiteSpace(map.LogFilePath)?
                new LocalMap{
                    Id=map.Id,InstallationPath=map.InstallationPath,
                    LogFilePath=Path.Combine(map.InstallationPath,
                        "ShooterGame","Saved","Logs","ShooterGame.log")
                }:map;
            // LocalLogCenter enforces local-root ownership, rejects symlinks,
            // and reads at most 1 MiB with no ASA process writes.
            var snapshot=LocalLogCenter.Read(logMap,1_048_576,4000);
            var session=AsaHistoricalLogReader.InspectLines(
                snapshot.Lines.Select(l=>l.Content),null);
            DateTimeOffset started=server.StartedUtc.ToUniversalTime();
            advertised=session.OpenUtc is { } opened &&
                session.AdvertisingUtc is { } ready &&
                opened>=started.AddMinutes(-2) &&
                opened<=started.AddMinutes(2) &&
                ready>=started.AddSeconds(-2) &&
                ready<=DateTimeOffset.UtcNow.AddMinutes(2) &&
                snapshot.LastModifiedUtc>=started.AddSeconds(-2) &&
                !session.ShutdownSequenceConfirmed &&
                !session.LogClosed;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException)
        {
            // No matching current-session ASA log: not advertised, NOT a failed
            // process, RCON or credential claim.
        }

        if(loader!=null && map.StartWithAsaApi)
        {
            var api=InspectCurrentApiLogs(map,server,loader);
            apiInitialized=api.ApiInitialized;
            pluginsInitialized=api.PluginsInitialized;
        }
        return new(advertised,apiInitialized,pluginsInitialized);
    }

    // ASA API's logger normally writes ArkApi_<ASA PID>*.log rather than
    // a single ArkApi.log. The PID comes from GetCurrentProcessId() inside
    // AsaApi.dll, so it identifies the exact ASA child, not the Loader parent.
    // StaticLogPath/older builds may still produce ArkApi.log.
    private static (bool ApiInitialized,bool PluginsInitialized) InspectCurrentApiLogs(
        LocalMap map,ProcessIdentity server,ProcessIdentity loader)
    {
        string root=Path.GetFullPath(map.InstallationPath);
        string bin=Path.Combine(root,"ShooterGame","Binaries","Win64");
        string[] folders={
            Path.Combine(bin,"logs"),
            Path.Combine(root,"ShooterGame","Win64","logs"),
            Path.Combine(bin,"ArkApi","logs"),
            Path.Combine(root,"ShooterGame","Win64","ArkApi","logs")
        };
        string prefix="ArkApi_"+server.Pid.ToString(CultureInfo.InvariantCulture);
        var pidLogs=new System.Collections.Generic.List<string>();
        var legacyLogs=new System.Collections.Generic.List<string>();
        foreach(string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if(!Directory.Exists(folder))continue;
                foreach(string path in Directory.EnumerateFiles(folder,"ArkApi*.log",
                    SearchOption.TopDirectoryOnly))
                {
                    string name=Path.GetFileName(path);
                    if(name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&
                       (name.Length==prefix.Length+4||
                        name[prefix.Length]=='_'||name[prefix.Length]=='.'))
                        pidLogs.Add(path);
                    else if(string.Equals(name,"ArkApi.log",StringComparison.OrdinalIgnoreCase))
                        legacyLogs.Add(path);
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            { }
        }
        // If an exact-PID log exists, NEVER fall back to a different PID's
        // file or a stale generic log. Read all current PID rotations in
        // chronological order because a busy server may rotate during boot.
        string[] files=(pidLogs.Count>0?pidLogs:legacyLogs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path=>{
                try{
                    var info=new FileInfo(path);
                    return info.Exists&&info.Length is >=1 and <=100_000_000&&
                        (info.Attributes&FileAttributes.ReparsePoint)==0&&
                        info.LastWriteTimeUtc>=loader.StartedUtc.UtcDateTime.AddSeconds(-2);
                }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                    ArgumentException or NotSupportedException){return false;}
            })
            .OrderBy(path=>File.GetLastWriteTimeUtc(path))
            .ThenBy(path=>Path.GetFileName(path),StringComparer.OrdinalIgnoreCase)
            .Take(8).ToArray();
        bool loaded=false,hooks=false,pluginsComplete=false;
        bool apiInitialized=false,pluginsInitialized=false;
        foreach(string log in files)
        {
            try
            {
                for(DirectoryInfo? node=new(Path.GetDirectoryName(log)!);
                    node!=null;node=node.Parent)
                    if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                        throw new InvalidDataException("Linked API log path");
                foreach(string line in Tail(log,1_048_576))
                {
                    bool isLoad=line.Contains("API was successfully loaded",
                        StringComparison.Ordinal);
                    bool isHooks=line.Contains("Initialized hooks",
                        StringComparison.Ordinal);
                    bool isPluginsComplete=line.Contains("Loaded all plugins",
                        StringComparison.Ordinal);
                    bool sessionStart=line.TrimEnd().EndsWith("Loading...",
                        StringComparison.Ordinal);
                    if(!isLoad&&!isHooks&&!isPluginsComplete&&!sessionStart)continue;
                    var match=ApiTime.Match(line);
                    if(!match.Success||
                       !DateTime.TryParseExact(match.Groups["ts"].Value,
                        "MM/dd/yy HH:mm",CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal|DateTimeStyles.AdjustToUniversal,
                        out var when))continue;
                    var at=new DateTimeOffset(when,TimeSpan.Zero);
                    if(at<loader.StartedUtc.AddMinutes(-2)||
                       at>DateTimeOffset.UtcNow.AddMinutes(2))continue;
                    if(sessionStart)
                    {
                        loaded=false;hooks=false;pluginsComplete=false;
                        apiInitialized=false;pluginsInitialized=false;
                    }
                    else if(isLoad)
                    {
                        if(loaded){hooks=false;pluginsComplete=false;}
                        loaded=true;
                    }
                    else if(isHooks)
                    {
                        if(hooks){loaded=false;pluginsComplete=false;}
                        hooks=true;
                    }
                    if(isPluginsComplete)pluginsComplete=true;
                    apiInitialized=loaded&&hooks;
                    pluginsInitialized=apiInitialized&&pluginsComplete;
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or NotSupportedException)
            {
                // A missing, unreadable or suspicious log is not positive evidence.
            }
        }
        return(apiInitialized,pluginsInitialized);
    }

    private static string[] Tail(string path,int bytes)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.ReadWrite|FileShare.Delete);
        long offset=Math.Max(0,file.Length-bytes);
        file.Position=offset;
        using var reader=new StreamReader(file,new UTF8Encoding(false,false),
            detectEncodingFromByteOrderMarks:true);
        if(offset>0)reader.ReadLine();
        // Keep only the final 4,000 lines without O(n²) List.RemoveAt(0)
        // shifts on a densely written 1 MiB API log.
        var lines=new System.Collections.Generic.Queue<string>(4000);
        while(reader.ReadLine() is { } line)
        {
            if(line.Length>8192 || line.Contains('\0'))
                throw new InvalidDataException("Invalid API log line");
            if(lines.Count==4000)lines.Dequeue();
            lines.Enqueue(line);
        }
        return lines.ToArray();
    }
}
