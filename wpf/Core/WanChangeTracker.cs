using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// Opt-in read-only public-IP/DDNS checks. No router, No-IP or ASA mutation.
// Two independent HTTPS providers and two time-separated samples are required.
public sealed record WanProbe(DateTimeOffset Utc,string? Primary,string? Secondary,
    string[] ResolvedAddresses);
public sealed record WanAssessment(string State,string? Baseline,string? Candidate,
    bool ConfirmedChange,bool DdnsMatches,int ConsecutiveSamples,string Message);

public sealed class WanChangeTracker
{
    private string? baseline,candidate;
    private int streak;
    private DateTimeOffset? previousSample;
    public WanAssessment Observe(WanProbe probe)
    {
        if(probe==null)throw new ArgumentNullException(nameof(probe));
        string? primary=ValidPublicIpv4(probe.Primary),secondary=ValidPublicIpv4(probe.Secondary);
        if(primary==null || secondary==null || primary!=secondary)
        {
            candidate=null;streak=0;previousSample=null;
            return new("Uncertain",baseline,null,false,false,0,
                "Independent WAN IP providers unavailable or disagree; no recovery allowed");
        }
        string observed=primary;
        bool ddns=probe.ResolvedAddresses!=null &&
            probe.ResolvedAddresses.Any(x=>ValidPublicIpv4(x)==observed);
        if(baseline!=null && observed==baseline)
        {
            candidate=null;streak=0;previousSample=null;
            return new("Stable",baseline,null,false,ddns,0,
                ddns?"WAN IP and DDNS agree":"WAN stable but DDNS is not aligned");
        }
        if(candidate!=observed || previousSample==null ||
            probe.Utc-previousSample.Value>TimeSpan.FromMinutes(10) ||
            probe.Utc<=previousSample.Value)
        {
            candidate=observed;streak=1;previousSample=probe.Utc;
            return new(baseline==null?"Learning":"Pending",baseline,candidate,false,ddns,streak,
                "Await another independent time-separated IP measurement");
        }
        if(probe.Utc-previousSample.Value<TimeSpan.FromSeconds(30))
            return new(baseline==null?"Learning":"Pending",baseline,candidate,false,ddns,streak,
                "Second IP measurement must be at least 30 seconds later");
        previousSample=probe.Utc;
        streak=Math.Min(2,streak+1);
        if(streak>=2)
        {
            bool changed=baseline!=null && baseline!=observed;
            baseline=observed;candidate=null;streak=0;previousSample=null;
            return new(changed?"Changed":"Stable",baseline,null,changed,ddns,2,
                changed?(ddns?"Confirmed IP change and aligned DDNS; notification only":
                    "Confirmed WAN change; DDNS not aligned; no server restart"):
                    "WAN baseline confirmed; notification only");
        }
        return new("Pending",baseline,candidate,false,ddns,streak,
            "Await second IP sample");
    }
    private static string? ValidPublicIpv4(string? raw)
    {
        if(raw==null || raw.Length>15 || raw.Trim()!=raw ||
           !IPAddress.TryParse(raw,out var address) ||
           address.AddressFamily!=AddressFamily.InterNetwork || address.ToString()!=raw)
            return null;
        byte[] b=address.GetAddressBytes();
        if(b[0]==0 || b[0]==10 || b[0]==127 || b[0]>=224 ||
           b[0]==169&&b[1]==254 || b[0]==172&&b[1]>=16&&b[1]<=31 ||
           b[0]==192&&b[1]==168 || b[0]==100&&b[1]>=64&&b[1]<=127 ||
           b[0]==192&&b[1]==0&&b[2]==0 || b[0]==198&&b[1]>=18&&b[1]<=19 ||
           b[0]==192&&b[1]==0&&b[2]==2 || b[0]==198&&b[1]==51&&b[2]==100 ||
           b[0]==203&&b[1]==0&&b[2]==113)
            return null;
        return raw;
    }
}

public static class ReadOnlyWanProbe
{
    // Only called after an explicit user click. The checker never alters DDNS
    // records, router port forwards, configs, or ASA process state.
    public static async Task<WanProbe> CaptureAsync(HttpClient http,CancellationToken ct=default)
    {
        if(http==null)throw new ArgumentNullException(nameof(http));
        static async Task<string?> Get(HttpClient client,string url,CancellationToken cancel)
        {
            try
            {
                using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,cancel);
                if(!response.IsSuccessStatusCode || response.Content.Headers.ContentLength>64)return null;
                var value=(await response.Content.ReadAsStringAsync(cancel)).Trim();
                return value.Length>20?null:value;
            }
            catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException)
            {return null;}
        }
        var first=await Get(http,"https://api.ipify.org",ct);
        var second=await Get(http,"https://checkip.amazonaws.com",ct);
        string[] dns=Array.Empty<string>();
        try
        {
            dns=(await Dns.GetHostAddressesAsync("untamedkingdom.ddns.net",ct))
                .Where(x=>x.AddressFamily==AddressFamily.InterNetwork)
                .Select(x=>x.ToString()).ToArray();
        }
        catch(Exception ex) when(ex is SocketException or OperationCanceledException){}
        return new(DateTimeOffset.UtcNow,first,second,dns);
    }
}
