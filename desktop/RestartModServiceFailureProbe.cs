using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Bounded, read-only diagnosis of the CURRENT ASA startup session, never
/// a reason to delete mods or mutate Store. Only a same-process-era log-open
/// marker can attribute serverUnreachable to the attempted restart.
/// </summary>
internal static class RestartModServiceFailureProbe
{
    internal const string TransientCurseForge=
        "UM-RESTART-CURSEFORGE-SERVER-UNREACHABLE";
    internal const string RequiredModsFailed=
        "UM-RESTART-REQUESTED-MODS-FAILED";

    internal static string? Classify(IReadOnlyList<string> lines,
        DateTimeOffset attemptedStartUtc)
    {
        if(lines==null||lines.Count==0||lines.Count>4000||
           attemptedStartUtc==default)return null;
        var report=AsaHistoricalLogReader.InspectLines(lines,null);
        if(report.OpenUtc is not { } opened ||
           opened<attemptedStartUtc.AddMinutes(-2)||
           opened>attemptedStartUtc.AddMinutes(2))
            return null; // old run's mod failure MUST NOT be attributed here
        int last=-1;
        for(int i=0;i<lines.Count;i++)
            if(lines[i].Contains("Log file open",StringComparison.Ordinal))
                last=i;
        if(last<0)return null;
        var current=lines.Skip(last+1);
        bool failed=current.Any(x=>x.Contains(
            "Requested mods failed to load on server.",
            StringComparison.OrdinalIgnoreCase));
        if(!failed)return null;
        bool unreachable=current.Any(x=>x.Contains("serverUnreachable",
            StringComparison.OrdinalIgnoreCase));
        return unreachable?TransientCurseForge:RequiredModsFailed;
    }

    internal static string? Inspect(LocalMap map,
        DateTimeOffset attemptedStartUtc)
    {
        try
        {
            if(map==null||map.Id==Guid.Empty||
               string.IsNullOrWhiteSpace(map.InstallationPath))return null;
            var logMap=string.IsNullOrWhiteSpace(map.LogFilePath)?
                new LocalMap{Id=map.Id,InstallationPath=map.InstallationPath,
                    LogFilePath=Path.Combine(map.InstallationPath,
                        "ShooterGame","Saved","Logs","ShooterGame.log")}:map;
            var snapshot=LocalLogCenter.Read(logMap,1_048_576,4000);
            if(snapshot.LastModifiedUtc<attemptedStartUtc.AddMinutes(-2))
                return null;
            return Classify(snapshot.Lines.Select(x=>x.Content).ToArray(),
                attemptedStartUtc);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {return null;}
    }
}
