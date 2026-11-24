using System;
using System.IO;
using UntamedDesktop;

// File-backed incident is preserved when Manager is reopened and is not
// dependent on a Discord HTTP request or a live ASA process.
internal static class ScheduledRestartOutageLedgerTests
{
    public static void Run(string root)
    {
        string profile=Path.Combine(root,"restart-outage-persistence-test");
        Directory.CreateDirectory(profile);
        Guid mapId=Guid.NewGuid(),runId=Guid.NewGuid();
        var start=DateTimeOffset.UtcNow.AddMinutes(-7);
        var incident=ScheduledRestartOutageLedger.Record(
            profile,mapId,runId,"Astraeos",start,
            "UM-RESTART-NEW-ASA-EXITED-OR-REPLACED",1,
            "CHECK_STOPPED_FAMILY_AND_JOURNALS_THEN_MANUAL_START");
        var reopened=ScheduledRestartOutageLedger.Read(profile,mapId);
        if(reopened==null||reopened.RunId!=runId||
           reopened.MapName!="Astraeos"||reopened.Attempts!=1||
           reopened.OutageStartedUtc!=start||
           !reopened.Offline||
           !reopened.Error.Contains("NEW-ASA-EXITED",StringComparison.Ordinal))
            throw new InvalidOperationException(
                "RESTART OUTAGE: durable incident lost after Manager re-open");
        ScheduledRestartOutageLedger.Record(profile,mapId,
            incident.RunId,incident.MapName,incident.OutageStartedUtc,
            incident.Error,incident.Attempts,"NEW_INSTANCE_READY",
            DateTimeOffset.UtcNow);
        var done=ScheduledRestartOutageLedger.Read(profile,mapId);
        if(done?.Offline!=false||done.RunId!=runId)
            throw new InvalidOperationException(
                "RESTART OUTAGE: recovery lost original incident identity");
        Console.WriteLine("PASS restart outage durable notice + recovery across Manager restart");
    }
}
