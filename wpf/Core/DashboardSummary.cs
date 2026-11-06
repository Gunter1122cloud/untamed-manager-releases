using System;
using System.Globalization;

namespace UntamedManager.Core;

// A dashboard must never turn historical audit numbers into live telemetry.
// Counts are derived solely from local profile maps and observed LAN snapshots.
public sealed record DashboardSummary
{
    public int LocalMaps { get; }
    public int? RemoteMaps { get; }
    public int KnownModAssignments { get; }
    public int ReadableLocalModMaps { get; }
    public bool RemoteFresh { get; }

    public DashboardSummary(int localMaps,int? remoteMaps,int knownModAssignments,
        int readableLocalModMaps,bool remoteFresh)
    {
        if(localMaps<0 || remoteMaps is <0 || knownModAssignments<0 ||
           readableLocalModMaps<0 || readableLocalModMaps>localMaps ||
           (remoteFresh && remoteMaps is null))
            throw new ArgumentOutOfRangeException(nameof(localMaps),"Inconsistent dashboard observation");
        LocalMaps=localMaps;
        RemoteMaps=remoteMaps;
        KnownModAssignments=knownModAssignments;
        ReadableLocalModMaps=readableLocalModMaps;
        RemoteFresh=remoteFresh;
    }

    public int MapCount => checked(LocalMaps+RemoteMaps.GetValueOrDefault());
    public int HostCount => RemoteMaps.HasValue?2:1;
    public int FreshLanPeers => RemoteMaps.HasValue && RemoteFresh?1:0;
    public string ModCountDisplay => LocalMaps==0?"0" :
        ReadableLocalModMaps==0?"—" :
        KnownModAssignments.ToString(CultureInfo.InvariantCulture)+
        (ReadableLocalModMaps<LocalMaps?"+":"");
    public string ModCoverageDisplay => LocalMaps==0?"Nincs helyi map":
        ReadableLocalModMaps.ToString(CultureInfo.InvariantCulture)+" / "+
        LocalMaps.ToString(CultureInfo.InvariantCulture)+" helyi map olvasható";
}
