using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

// Read-only historical observation. Log text is NOT a process identity,
// RCON readiness, SaveComplete, or permission to start/stop a server.
public sealed record AsaHistoricalEvidence(
    bool LogOpened,bool ServerStarted,bool JoinAdvertised,
    bool ApiLoaded,bool ApiHooksInitialized,bool ApiSameStartWindow,
    int ApiPluginLines,bool ClosingRequested,bool LogClosed,
    bool ShutdownSequenceConfirmed,TimeSpan? StartedToAdvertising,
    DateTimeOffset? OpenUtc,DateTimeOffset? StartedUtc,
    DateTimeOffset? AdvertisingUtc,DateTimeOffset? ClosingUtc,
    bool LiveControlReady=false,bool SaveCompleted=false);

public static class AsaHistoricalLogReader
{
    private static readonly Regex AsaTimestamp=new(
        @"\[(?<ts>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\d{3})\]",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex ApiTimestamp=new(
        @"(?<ts>\d{2}/\d{2}/\d{2} \d{2}:\d{2}) \[API\]",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);

    private sealed class Session
    {
        public DateTimeOffset? Open,Started,Advertised,Closing,Closed;
    }

    public static AsaHistoricalEvidence InspectFiles(string asaPath,string? apiPath)
        =>InspectLines(ReadLocalCopy(asaPath),string.IsNullOrWhiteSpace(apiPath)?
            null:ReadLocalCopy(apiPath));

    public static AsaHistoricalEvidence InspectLines(
        IEnumerable<string> asaLines,IEnumerable<string>? apiLines)
    {
        if(asaLines==null)throw new ArgumentNullException(nameof(asaLines));
        Session? session=null;
        int count=0;
        foreach(string line in asaLines)
        {
            if(++count>100_000 || line==null || line.Length>16_384)
                throw new InvalidDataException("Oversized historical ASA log");
            var matched=AsaTimestamp.Match(line);
            if(!matched.Success ||!DateTime.TryParseExact(
                   matched.Groups["ts"].Value,"yyyy.MM.dd-HH.mm.ss:fff",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,
                   out var timestamp))continue;
            var when=new DateTimeOffset(timestamp,TimeSpan.Zero);
            if(line.Contains("Log file open",StringComparison.Ordinal))
            {
                // A second open starts a NEW session. Never join evidence
                // from two different server starts.
                session=new Session{Open=when};
                continue;
            }
            if(session==null ||when<session.Open)continue;
            if(line.Contains("has successfully started!",StringComparison.Ordinal))
            {
                if(session.Started==null)session.Started=when;
            }
            else if(line.Contains("Server has completed startup and is now advertising for join",
                        StringComparison.Ordinal))
            {
                if(session.Started!=null && when>=session.Started &&
                   session.Advertised==null)session.Advertised=when;
            }
            else if(line.Contains("Closing by request",StringComparison.Ordinal))
                session.Closing ??= when;
            else if(line.Contains("Log file closed",StringComparison.Ordinal))
                session.Closed ??= when;
        }

        bool apiLoaded=false,hooks=false;
        int plugins=0;
        var apiTimes=new List<DateTimeOffset>();
        if(apiLines!=null)
        {
            count=0;
            foreach(string line in apiLines)
            {
                if(++count>100_000 || line==null || line.Length>16_384)
                    throw new InvalidDataException("Oversized historical API log");
                if(line.Contains("Initialized hooks",StringComparison.Ordinal))
                    hooks=true;
                if(line.Contains("API was successfully loaded",StringComparison.Ordinal))
                    apiLoaded=true;
                if(line.Contains("Loaded plugin ",StringComparison.Ordinal))
                    plugins++;
                var match=ApiTimestamp.Match(line);
                if(match.Success && DateTime.TryParseExact(match.Groups["ts"].Value,
                   "MM/dd/yy HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,
                   out var date))
                {
                    try
                    {
                        var tz=TimeZoneInfo.FindSystemTimeZoneById(
                            "Central European Standard Time");
                        DateTime utc=TimeZoneInfo.ConvertTimeToUtc(
                            DateTime.SpecifyKind(date,DateTimeKind.Unspecified),tz);
                        apiTimes.Add(new DateTimeOffset(utc,TimeSpan.Zero));
                    }
                    catch(ArgumentException) { /* ambiguous or invalid clock */ }
                }
            }
        }
        bool apiSameStart=session?.Open is { } opened &&apiLoaded &&hooks &&
            apiTimes.Count>0 && apiTimes.Any(t=>Math.Abs((t-opened).TotalMinutes)<=5);
        bool shutdown=session?.Closing is { } close &&
            session.Closed is { } closed &&closed>=close;
        TimeSpan? startup=session?.Advertised is { } ready &&
            session.Started is { } started?ready-started:null;
        return new(session?.Open!=null,session?.Started!=null,
            session?.Advertised!=null,apiLoaded,hooks,apiSameStart,
            plugins,session?.Closing!=null,session?.Closed!=null,
            shutdown,startup,session?.Open,session?.Started,
            session?.Advertised,session?.Closing);
    }

    private static IEnumerable<string> ReadLocalCopy(string path)
    {
        if(string.IsNullOrWhiteSpace(path) ||!Path.IsPathFullyQualified(path) ||
           path.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("Choose a local copied log file");
        string full=Path.GetFullPath(path);
        var parent=Path.GetDirectoryName(full)!;
        var root=Path.GetPathRoot(full)!;
        if(new DriveInfo(root).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Historical log must be on a fixed drive");
        for(var dir=new DirectoryInfo(parent);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked historical log path refused");
        if(!full.EndsWith(".txt",StringComparison.OrdinalIgnoreCase) &&
           !full.EndsWith(".log",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected a copied TXT or LOG file");
        var file=new FileInfo(full);
        if(!file.Exists ||file.Length is < 16 or > 4_000_000 ||
           (file.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Missing or oversized historical log");
        return File.ReadLines(full);
    }
}
