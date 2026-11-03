using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

public enum OperationalTaskKind
{
    Observation,Notification,BackupIntegrity,BackupSnapshot,ServerStart,ServerStop,
    ServerRestart,WildDinoWipe,ModCheck,ApiCheck,SteamCheck,Maintenance
}
public enum ScheduleFrequency { Daily,Weekly,IntervalMinutes }
public sealed class RecurringSchedule
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid Map { get; set; }
    public OperationalTaskKind Kind { get; set; }
    public ScheduleFrequency Frequency { get; set; }=ScheduleFrequency.Daily;
    public TimeSpan LocalClock { get; set; }=new(7,30,0);
    public DayOfWeek WeekDay { get; set; }=DayOfWeek.Sunday;
    public int IntervalMinutes { get; set; }=60;
    public DateTimeOffset? LastDueUtc { get; set; }
    public bool Enabled { get; set; }=false; // deliberate opt-in, even for templates
    public bool NoticeRequired { get; set; }=true;
}
public sealed record ScheduleDue(Guid Job,Guid Map,OperationalTaskKind Kind,
    DateTimeOffset DueUtc,string Zone,string Status,bool AutoExecutionPermitted);
public static class OperationalScheduler
{
    public const string ZoneId="Europe/Budapest";
    private static readonly TimeZoneInfo Zone=ResolveZone();
    private static TimeZoneInfo ResolveZone()
    {
        try{return TimeZoneInfo.FindSystemTimeZoneById(ZoneId);}
        catch(TimeZoneNotFoundException)
        {return TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");}
    }
    public static ScheduleDue Next(RecurringSchedule job,DateTimeOffset afterUtc,
        bool owned=true,bool maintenance=false)
    {
        if(job==null||job.Map==Guid.Empty||job.Id==Guid.Empty ||
           !Enum.IsDefined(job.Kind)||!Enum.IsDefined(job.Frequency) ||
           job.LocalClock<TimeSpan.Zero||job.LocalClock>=TimeSpan.FromDays(1))
            throw new InvalidDataException("Invalid recurring schedule identity/settings");
        DateTimeOffset utc=afterUtc.ToUniversalTime();
        DateTimeOffset candidate;
        if(job.Frequency==ScheduleFrequency.IntervalMinutes)
        {
            if(job.IntervalMinutes<5 || job.IntervalMinutes>10080)
                throw new InvalidDataException("Schedule interval out of bounds");
            DateTimeOffset origin=job.LastDueUtc?.ToUniversalTime()??utc;
            candidate=origin.AddMinutes(job.IntervalMinutes);
            if(candidate<=utc)
            {
                long elapsed=(long)((utc-origin).TotalMinutes/job.IntervalMinutes);
                candidate=origin.AddMinutes((elapsed+1)*job.IntervalMinutes);
            }
        }
        else
        {
            var local=TimeZoneInfo.ConvertTime(utc,Zone).DateTime;
            candidate=DateTimeOffset.MaxValue;
            for(int day=0;day<=9;day++)
            {
                var date=local.Date.AddDays(day);
                if(job.Frequency==ScheduleFrequency.Weekly &&
                   date.DayOfWeek!=job.WeekDay)continue;
                var time=DateTime.SpecifyKind(date+job.LocalClock,DateTimeKind.Unspecified);
                // DST spring gap: first valid wall-clock minute after requested time.
                int shifts=0;
                while(Zone.IsInvalidTime(time)&&shifts++<120)time=time.AddMinutes(1);
                if(Zone.IsInvalidTime(time))throw new InvalidDataException("No valid DST time");
                var offset=Zone.IsAmbiguousTime(time)?
                    Zone.GetAmbiguousTimeOffsets(time).Min():Zone.GetUtcOffset(time);
                var result=new DateTimeOffset(time,offset).ToUniversalTime();
                if(result>utc){candidate=result;break;}
            }
            if(candidate==DateTimeOffset.MaxValue)
                throw new InvalidDataException("Recurring schedule cannot calculate next slot");
        }
        string state=!job.Enabled?"disabled":
            !owned?"foreign-map-blocked":
            maintenance?"maintenance-blocked":
            job.Kind is OperationalTaskKind.Observation or OperationalTaskKind.Notification or
                OperationalTaskKind.BackupIntegrity or OperationalTaskKind.ModCheck or
                OperationalTaskKind.ApiCheck or OperationalTaskKind.SteamCheck?
                "read-only-preview":
            job.NoticeRequired?"operator-and-notice-required":"operator-approval-required";
        // Even read-only checks execute through existing persistent Supervisor
        // scheduler, never through this calendar or an unattended RCON/Steam/ASA call.
        return new(job.Id,job.Map,job.Kind,candidate,ZoneId,state,false);
    }

    public static RecurringSchedule[] UntamedTemplates(IEnumerable<Guid> localMapIds)
    {
        var ids=localMapIds.Distinct().ToArray();
        if(ids.Any(x=>x==Guid.Empty))throw new InvalidDataException("Map identity missing");
        return ids.Select(x=>new RecurringSchedule{
            Map=x,Kind=OperationalTaskKind.WildDinoWipe,
            LocalClock=new(7,30,0),Frequency=ScheduleFrequency.Daily,
            Enabled=false,NoticeRequired=true}).ToArray();
    }
    public static IReadOnlyList<ScheduleDue> Upcoming(IEnumerable<RecurringSchedule> jobs,
        DateTimeOffset afterUtc,IReadOnlySet<Guid> owned)
        =>jobs.Select(x=>Next(x,afterUtc,owned.Contains(x.Map)))
            .OrderBy(x=>x.DueUtc).ThenBy(x=>x.Job).ToArray();
}
