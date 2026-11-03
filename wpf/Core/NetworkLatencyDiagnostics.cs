using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// Manual ICMP-only diagnostics. Does not modify WAN, DNS, server processes,
// router port forwards or network settings. ICMP filtering is NOT packet loss
// proof on an ASA server connection, and ping is not an ASA travel benchmark.
public sealed record LatencyReport(string Destination,int Samples,
    int Responses,double LossPercent,double? MedianMs,double? P95Ms,
    double? MaxMs,double? MeanAbsoluteJitterMs,string Comment);

public static class NetworkLatencyDiagnostics
{
    public static bool ValidTarget(string? text)
    {
        if(string.IsNullOrWhiteSpace(text)||text!=text.Trim()||
           !IPAddress.TryParse(text,out var address) ||
           address.AddressFamily!=AddressFamily.InterNetwork ||
           address.ToString()!=text)
            return false;
        var b=address.GetAddressBytes();
        // Explicit IPv4 targets only, no DNS hostnames, loopback, multicast,
        // zero network or 255.255.255.255 broadcast.
        return b[0]!=0 &&b[0]!=127 &&b[0]<224 &&
            !(b[0]==169&&b[1]==254) &&
            !(b[0]==255 &&b[1]==255&&b[2]==255&&b[3]==255);
    }

    public static LatencyReport Analyze(string target,
        IReadOnlyList<double?> elapsed)
    {
        if(!ValidTarget(target)||elapsed==null||
           elapsed.Count is < 3 or > 100||
           elapsed.Any(x=>x.HasValue&&(x.Value<0 ||
               double.IsNaN(x.Value)||double.IsInfinity(x.Value)||x.Value>60_000)))
            throw new InvalidDataException("Invalid latency diagnostic input");
        var good=elapsed.Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();
        if(good.Length==0)
            return new(target,elapsed.Count,0,100,null,null,null,null,
                "No replies; ICMP may be blocked. No conclusion about ASA availability.");
        var sorted=good.OrderBy(x=>x).ToArray();
        double percentile(double p){
            var slot=(sorted.Length-1)*p;
            int lo=(int)Math.Floor(slot),hi=(int)Math.Ceiling(slot);
            return sorted[lo]+(sorted[hi]-sorted[lo])*(slot-lo);
        }
        double? jitter=good.Length>=2 ?
            good.Skip(1).Select((x,i)=>Math.Abs(x-good[i])).Average():
            null;
        return new(target,elapsed.Count,good.Length,
            100.0*(elapsed.Count-good.Length)/elapsed.Count,
            percentile(.5),percentile(.95),sorted[^1],jitter,
            "Diagnostic ICMP RTT only; compare with router and external uplink. "+
            "Not a guaranteed game/cluster-transfer latency estimate.");
    }

    public static async Task<LatencyReport> MeasureAsync(
        string target,int samples=12,
        CancellationToken ct=default)
    {
        if(!ValidTarget(target)||samples is < 3 or > 30)
            throw new ArgumentOutOfRangeException(nameof(target),
                "Choose a concrete IPv4 address and 3..30 ICMP samples");
        var readings=new List<double?>(samples);
        using var ping=new Ping();
        for(int i=0;i<samples;i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply=await ping.SendPingAsync(target,1200);
                readings.Add(reply.Status==IPStatus.Success?
                    reply.RoundtripTime:null);
            }
            catch(Exception e) when(e is PingException or SocketException)
            {readings.Add(null);}
            if(i<samples-1)await Task.Delay(120,ct);
        }
        return Analyze(target,readings);
    }
}
