using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace UntamedManager.Core;

/// <summary>
/// Fail-closed OS port preflight for a stopped local ASA installation.
/// Does not reserve ports; the exact process owner is still checked during
/// lifecycle and RCON operations. Prevents a new server from launching over
/// an already-bound UDP game/query port or TCP RCON port.
/// </summary>
public enum AsaLocalPortAvailability { Available, InUse, Unverified }
public static class AsaLocalPortPreflight
{
    public static bool CanStart(LocalMap map,IEnumerable<IPEndPoint>? udp=null,
        IEnumerable<IPEndPoint>? tcp=null)=>
        Inspect(map,udp,tcp)==AsaLocalPortAvailability.Available;

    public static AsaLocalPortAvailability Inspect(LocalMap map,
        IEnumerable<IPEndPoint>? udp=null,IEnumerable<IPEndPoint>? tcp=null)
    {
        if(map==null||map.GamePort is <1024 or >65535||
           map.QueryPort is <1024 or >65535||
           map.GamePort==map.QueryPort||
           map.RconPort is <0 or >65535||
           (map.RconPort>0 && map.RconPort<1024))
            return AsaLocalPortAvailability.Unverified;
        try
        {
            var ip=IPGlobalProperties.GetIPGlobalProperties();
            var busyUdp=(udp??ip.GetActiveUdpListeners())
                .Select(x=>x.Port).ToHashSet();
            var busyTcp=(tcp??ip.GetActiveTcpListeners())
                .Select(x=>x.Port).ToHashSet();
            return busyUdp.Contains(map.GamePort)||
                busyUdp.Contains(map.QueryPort)||
                (map.RconPort!=0&&busyTcp.Contains(map.RconPort))
                ? AsaLocalPortAvailability.InUse
                : AsaLocalPortAvailability.Available;
        }
        catch(Exception ex) when(ex is NetworkInformationException or
            InvalidOperationException or UnauthorizedAccessException)
        {
            // Lack of OS evidence must not be misreported as an occupied port.
            return AsaLocalPortAvailability.Unverified;
        }
    }
}
