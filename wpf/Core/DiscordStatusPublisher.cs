using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// Optional Discord *outbound* monitor-only connector: never accepts commands,
// players' identities, RCON passwords, admin secrets or raw process arguments.
// No background posting or automatic retry of ambiguous/failed POSTs.
public sealed record DiscordPostReceipt(string MessageId,DateTimeOffset SentUtc,int Maps);
public static class DiscordStatusPublisher
{
    private static readonly Regex Route=new(
        @"\A/api(?:/v10)?/webhooks/[0-9]{16,22}/[A-Za-z0-9_-]{25,200}\z",
        RegexOptions.CultureInvariant|RegexOptions.Compiled);

    public static bool ValidWebhook(string? value)
    {
        if(value==null||value.Length is < 65 or > 300||
           !Uri.TryCreate(value,UriKind.Absolute,out var uri))
            return false;
        return uri.Scheme=="https" &&
            uri.Host=="discord.com" &&
            uri.IsDefaultPort &&
            uri.UserInfo.Length==0 &&
            uri.Fragment.Length==0 &&
            uri.Query.Length==0 &&
            Route.IsMatch(uri.AbsolutePath) &&
            string.Equals(value,"https://discord.com"+uri.AbsolutePath,
                StringComparison.Ordinal);
    }

    public static string Compose(SupervisorSnapshot snapshot,
        IReadOnlySet<Guid> ownedMaps,DateTimeOffset now)
    {
        if(snapshot==null ||ownedMaps==null||
           snapshot.Maps==null||snapshot.Maps.Length is < 1 or > 10 ||
           !StatusFreshness.IsFresh(snapshot.ObservedUtc,now)||
           snapshot.Maps.Any(x=>x==null ||x.Map==Guid.Empty ||
               !ownedMaps.Contains(x.Map) ||
               !StatusFreshness.IsFresh(x.ObservedUtc,now))||
           snapshot.Maps.Select(x=>x.Map).Distinct().Count()!=snapshot.Maps.Length)
            throw new InvalidOperationException("No current, verified, locally owned Supervisor snapshot");
        var lines=new List<string>{
            "🦖 **UNTAMED KINGDOM · helyi szerverstátusz**",
            "Hitelesített Supervisor-mérés: "+
                snapshot.ObservedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture)+" UTC",
            "A távoli host állapotát ez az üzenet nem igazolja."
        };
        foreach(var map in snapshot.Maps)
        {
            // Map names are presentation data, not commands. Strip Discord
            // mentions/markdown and line breaks even if a local name is tampered.
            string safe=new(map.Name.Where(x=>char.IsLetterOrDigit(x) ||
                x==' '||x=='-'||x=='.').Take(45).ToArray());
            if(string.IsNullOrWhiteSpace(safe))safe="Névtelen map";
            string state=map.Running switch{
                true=>"FUT",false=>"LEÁLLÍTVA",_=>"ISMERETLEN"};
            string players=map.Players is >= 0 and <= 500 ?
                map.Players.Value.ToString(CultureInfo.InvariantCulture):
                "ismeretlen";
            lines.Add("• "+safe+" — "+state+
                " · játékosok: "+players+
                (map.Maintenance?" · karbantartás":""));
        }
        var content=string.Join("\n",lines);
        if(content.Length>1950)
            throw new InvalidOperationException("Status payload exceeds safe Discord content length");
        return content;
    }

    public static async Task<DiscordPostReceipt> SendAsync(
        HttpClient http,string webhook,SupervisorSnapshot snapshot,
        IReadOnlySet<Guid> ownedMaps,
        CancellationToken ct=default)
    {
        if(http==null)throw new ArgumentNullException(nameof(http));
        if(!ValidWebhook(webhook))
            throw new InvalidDataException("Only exact Discord HTTPS webhook addresses are accepted");
        string content=Compose(snapshot,ownedMaps,DateTimeOffset.UtcNow);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var uri=new Uri(webhook+"?wait=true");
        using var request=new HttpRequestMessage(HttpMethod.Post,uri);
        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue("UntamedManager","1.6"));
        request.Content=new StringContent(JsonSerializer.Serialize(new{
            username="Untamed Kingdom",
            content,
            allowed_mentions=new{parse=Array.Empty<string>()}
        }),Encoding.UTF8,"application/json");
        // Explicitly disable redirect following on the dedicated HttpClient
        // supplied by the caller. Never send a webhook token cross-domain.
        using var reply=await http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        if(reply.StatusCode==HttpStatusCode.TooManyRequests)
            throw new IOException("Discord rate-limited this status message; no automatic retry");
        if(reply.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new UnauthorizedAccessException("Discord rejected the configured webhook");
        if(reply.StatusCode!=HttpStatusCode.OK)
            throw new IOException("Discord did not confirm a saved message: "+(int)reply.StatusCode);
        if(reply.Content.Headers.ContentLength>64_000)
            throw new InvalidDataException("Oversized Discord message acknowledgement");
        var json=await reply.Content.ReadAsStringAsync(timeout.Token);
        if(json.Length>64_000)throw new InvalidDataException("Oversized Discord response body");
        using var document=JsonDocument.Parse(json);
        if(!document.RootElement.TryGetProperty("id",out var id) ||
           id.ValueKind!=JsonValueKind.String ||
           !Regex.IsMatch(id.GetString()??"",@"\A[0-9]{16,22}\z"))
            throw new IOException("Discord did not return a confirmed message ID");
        return new(id.GetString()!,DateTimeOffset.UtcNow,snapshot.Maps.Length);
    }
}
