using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace UntamedDesktop;

/// <summary>
/// Read-only, explicitly requested inventory of possible legacy Kova
/// controllers and autostart. Negative findings are never proof of exclusive
/// ownership. Does not stop/start/reconfigure Kova, ASA, services or tasks.
/// Returns only counts and evidence gaps, never command lines/task XML.
/// </summary>
public static class KovaLegacySupervisorReadOnlyInventory
{
    public sealed record Report(
        bool ReadOnly,bool ProcessInventoryComplete,bool ServiceInventoryComplete,
        bool ScheduledTaskInventoryComplete,int SuspectedProcessCount,
        int SuspectedServiceCount,int SuspectedScheduledTaskCount,
        bool KovaWatchdogDisabledVerified,bool ExclusiveHandoffVerified,
        string[] EvidenceGaps,string Evidence);

    private static bool LooksLikeKova(string? value) =>
        !string.IsNullOrWhiteSpace(value)&&
        (value.Contains("Kova",StringComparison.OrdinalIgnoreCase)||
         value.Contains("shared-store.mjs",StringComparison.OrdinalIgnoreCase)||
         value.Contains("local-manager.cjs",StringComparison.OrdinalIgnoreCase)||
         value.Contains("agent/agent.mjs",StringComparison.OrdinalIgnoreCase));

    public static Report Assess(
        IReadOnlyList<string?> processFacts,
        IReadOnlyList<string?> serviceFacts,
        IReadOnlyList<string?> scheduledTaskFacts,
        bool processesComplete,bool servicesComplete,bool scheduledTasksComplete)
    {
        ArgumentNullException.ThrowIfNull(processFacts);
        ArgumentNullException.ThrowIfNull(serviceFacts);
        ArgumentNullException.ThrowIfNull(scheduledTaskFacts);
        int processCount=processFacts.Count(LooksLikeKova);
        int serviceCount=serviceFacts.Count(LooksLikeKova);
        int taskCount=scheduledTaskFacts.Count(LooksLikeKova);
        var gaps=new List<string>();
        if(!processesComplete)gaps.Add("PROCESS_INVENTORY_INCOMPLETE");
        if(!servicesComplete)gaps.Add("SERVICE_INVENTORY_INCOMPLETE");
        if(!scheduledTasksComplete)gaps.Add("TASK_SCHEDULER_INVENTORY_INCOMPLETE");
        if(processCount+serviceCount+taskCount>0)
            gaps.Add("LEGACY_KOVA_CONTROLLER_OR_AUTORUN_CANDIDATE");
        gaps.Add("IN_APP_KOVA_WATCHDOG_AND_SCHEDULE_STATE_UNCERTIFIED");
        gaps.Add("NO_EXCLUSIVE_STORE_WRITER_LEASE_OR_TAKEOVER_CERTIFICATE");
        return new(true,processesComplete,servicesComplete,
            scheduledTasksComplete,processCount,serviceCount,taskCount,
            KovaWatchdogDisabledVerified:false,ExclusiveHandoffVerified:false,
            gaps.ToArray(),
            "LOCAL_WINDOWS_PROCESS_SERVICE_TASKS_READ_ONLY_NOT_HANDOFF_PROOF");
    }

    private static void InspectFolder(dynamic folder,List<string?> rows,ref int seen,int depth)
    {
        if(depth>16)throw new InvalidDataException("TASK_FOLDER_DEPTH_LIMIT");
        foreach(dynamic task in folder.GetTasks(1))
        {
            if(++seen>5000)throw new InvalidDataException("TASK_COUNT_LIMIT");
            string name=(string)task.Name;
            string xml=(string)task.Xml;
            if(xml.Length>300000)throw new InvalidDataException("TASK_XML_LIMIT");
            // The task action may contain secrets. Never return or log XML.
            rows.Add(LooksLikeKova(name)||LooksLikeKova(xml)?"Kova":"unrelated");
        }
        foreach(dynamic child in folder.GetFolders(0))
            InspectFolder(child,rows,ref seen,depth+1);
    }

    public static Report InspectLocal()
    {
        var processes=new List<string?>();
        var services=new List<string?>();
        var tasks=new List<string?>();
        bool processesComplete=false,servicesComplete=false,tasksComplete=false;
        try
        {
            using var search=new ManagementObjectSearcher(
                "SELECT Name, CommandLine FROM Win32_Process");
            search.Options.Timeout=TimeSpan.FromSeconds(5);
            using var result=search.Get();
            foreach(ManagementBaseObject item in result)
            {
                if(processes.Count>=5000)throw new InvalidDataException("PROCESS_LIMIT");
                string name=item["Name"]?.ToString()??"";
                string args=item["CommandLine"]?.ToString()??"";
                processes.Add(LooksLikeKova(name)||LooksLikeKova(args)?"Kova":"unrelated");
            }
            processesComplete=true;
        }
        catch(Exception) { /* Incomplete is not proof of absence. */ }
        try
        {
            using var search=new ManagementObjectSearcher(
                "SELECT Name, DisplayName, PathName FROM Win32_Service");
            search.Options.Timeout=TimeSpan.FromSeconds(5);
            using var result=search.Get();
            foreach(ManagementBaseObject item in result)
            {
                if(services.Count>=5000)throw new InvalidDataException("SERVICE_LIMIT");
                string facts=(item["Name"]?.ToString()??"")+" "+
                    (item["DisplayName"]?.ToString()??"")+" "+
                    (item["PathName"]?.ToString()??"");
                services.Add(LooksLikeKova(facts)?"Kova":"unrelated");
            }
            servicesComplete=true;
        }
        catch(Exception) { }
        object? service=null;
        try
        {
            // Task Scheduler 2 COM API: GetTasks/GetFolders, no mutations.
            var kind=Type.GetTypeFromProgID("Schedule.Service");
            if(kind==null)throw new NotSupportedException();
            service=Activator.CreateInstance(kind);
            if(service==null)throw new NotSupportedException();
            dynamic api=service;
            api.Connect();
            int seen=0;
            InspectFolder(api.GetFolder("\\"),tasks,ref seen,0);
            tasksComplete=true;
        }
        catch(Exception) { }
        finally
        {
            if(service!=null&&Marshal.IsComObject(service))
                try{Marshal.FinalReleaseComObject(service);}catch(Exception){}
        }
        return Assess(processes,services,tasks,processesComplete,
            servicesComplete,tasksComplete);
    }
}
