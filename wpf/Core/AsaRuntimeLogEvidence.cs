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
    internal sealed record Proof(bool JoinAdvertised,bool ApiInitialized);

    private static readonly Regex ApiTime=new(
        @"(?<ts>\d{2}/\d{2}/\d{2} \d{2}:\d{2}) \[API\]",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);

    internal static Proof Inspect(LocalMap map,ProcessIdentity server,
        ProcessIdentity? loader)
    {
        bool advertised=false,apiInitialized=false;
        try
        {
            // LocalLogCenter enforces local-root ownership, rejects symlinks,
            // and reads at most 1 MiB with no ASA process writes.
            var snapshot=LocalLogCenter.Read(map,1_048_576,4000);
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
            // The officially documented API log may be emitted from either
            // of these supported layouts. Never recurse into plugin folders.
            foreach(string relative in new[]{
                Path.Combine("ShooterGame","Win64","logs","ArkApi.log"),
                Path.Combine("ShooterGame","Binaries","Win64","logs","ArkApi.log")})
            {
                try
                {
                    string root=Path.GetFullPath(map.InstallationPath);
                    string log=Path.Combine(root,relative);
                    for(DirectoryInfo? node=new(Path.GetDirectoryName(log)!);
                        node!=null;node=node.Parent)
                        if(node.Exists && (node.Attributes&FileAttributes.ReparsePoint)!=0)
                            throw new InvalidDataException("Linked API log path");
                    var file=new FileInfo(log);
                    if(!file.Exists ||file.Length is <1 or >100_000_000 ||
                        (file.Attributes&FileAttributes.ReparsePoint)!=0 ||
                        file.LastWriteTimeUtc<loader.StartedUtc.UtcDateTime.AddSeconds(-2))
                        continue;
                    // The timestamp *on the positive load line*, not merely
                    // the file's LastWriteTime, must be from this loader run.
                    foreach(string line in Tail(log,1_048_576))
                    {
                        if(!line.Contains("API was successfully loaded",
                            StringComparison.Ordinal))continue;
                        var match=ApiTime.Match(line);
                        if(!match.Success ||
                           !DateTime.TryParseExact(match.Groups["ts"].Value,
                            "MM/dd/yy HH:mm",CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeLocal|DateTimeStyles.AdjustToUniversal,
                            out var when))continue;
                        var at=new DateTimeOffset(when,TimeSpan.Zero);
                        if(at>=loader.StartedUtc.AddMinutes(-2) &&
                           at<=DateTimeOffset.UtcNow.AddMinutes(2))
                        {
                            apiInitialized=true;break;
                        }
                    }
                    if(apiInitialized)break;
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                    InvalidDataException or ArgumentException or NotSupportedException)
                {
                    // Missing, unreadable or suspicious API log is NOT a
                    // successful runtime initialization witness.
                }
            }
        }
        return new(advertised,apiInitialized);
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
        var lines=new System.Collections.Generic.List<string>();
        while(reader.ReadLine() is { } line)
        {
            if(line.Length>8192 || line.Contains('\0'))
                throw new InvalidDataException("Invalid API log line");
            if(lines.Count>=4000)lines.RemoveAt(0);
            lines.Add(line);
        }
        return lines.ToArray();
    }
}
