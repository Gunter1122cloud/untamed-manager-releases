using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace UntamedManager.Core;

// The probe queries the local Windows machine only. No remote host, command,
// network socket, Store, server executable or configuration is involved.
public sealed record LocalHostResourceSnapshot(
    double? CpuPercent,ulong? UsedMemoryKiB,ulong? TotalMemoryKiB,DateTimeOffset MeasuredUtc)
{
    public string Display(string hostName)
    {
        string cpu=CpuPercent.HasValue
            ?CpuPercent.Value.ToString("0",CultureInfo.InvariantCulture)+"%":"nem mérhető";
        string memory=UsedMemoryKiB.HasValue && TotalMemoryKiB is >0
            ?(UsedMemoryKiB.Value/1048576d).ToString("0.0",CultureInfo.InvariantCulture)+"/"+
             (TotalMemoryKiB.Value/1048576d).ToString("0.0",CultureInfo.InvariantCulture)+" GiB"
            :"nem mérhető";
        return hostName+" · CPU: "+cpu+" · RAM: "+memory+
            " · helyi Windows-mérés ("+MeasuredUtc.ToLocalTime().ToString("HH:mm:ss")+")";
    }
}
public static class LocalHostResourceProbe
{
    public static LocalHostResourceSnapshot Read()
    {
        double? cpu=null;
        ulong? used=null,total=null;
        if(!OperatingSystem.IsWindows())
            return new(cpu,used,total,DateTimeOffset.UtcNow);
        try
        {
            var samples=new List<double>();
            using var searcher=new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
            using var entries=searcher.Get();
            foreach(ManagementBaseObject entry in entries)
            {
                try{
                    if(entry["LoadPercentage"]!=null &&
                        double.TryParse(Convert.ToString(entry["LoadPercentage"],CultureInfo.InvariantCulture),
                            NumberStyles.Float,CultureInfo.InvariantCulture,out var value) &&
                        value>=0 && value<=100)samples.Add(value);
                } finally {entry.Dispose();}
            }
            if(samples.Count>0)cpu=samples.Average();
        }
        catch(Exception) { /* unavailable WMI must remain unavailable, never 0% */ }
        try
        {
            using var searcher=new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            using var entries=searcher.Get();
            foreach(ManagementBaseObject entry in entries)
            {
                try
                {
                    if(entry["TotalVisibleMemorySize"]==null||entry["FreePhysicalMemory"]==null)continue;
                    ulong t=Convert.ToUInt64(entry["TotalVisibleMemorySize"],CultureInfo.InvariantCulture);
                    ulong free=Convert.ToUInt64(entry["FreePhysicalMemory"],CultureInfo.InvariantCulture);
                    if(t>0 && free<=t){total=t;used=t-free;break;}
                }finally{entry.Dispose();}
            }
        }
        catch(Exception) { /* no assumption about RAM if WMI is blocked */ }
        return new(cpu,used,total,DateTimeOffset.UtcNow);
    }
}
