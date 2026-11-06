using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Prevent cross-product updates: the WPF Manager and Obsidian preview have
/// different Inno Setup AppIds and executable names. Their releases must
/// NEVER be interpreted as interchangeable.
/// </summary>
public static class ObsidianReleaseCatalog
{
    public const string Repository="Gunter1122cloud/untamed-manager-releases";
    public const string Installer="Untamed_Obsidian_Desktop_Preview_Setup.exe";
    private static readonly Regex TagRegex=new(
        @"\Av(?<version>\d+\.\d+\.\d+)-obsidian(?:-preview\.(?<preview>[1-9]\d*))?\z",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly HttpClient Client=ClientFactory();

    private static HttpClient ClientFactory()
    {
        var result=new HttpClient{Timeout=TimeSpan.FromSeconds(22)};
        result.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("UntamedObsidian","1.4"));
        result.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return result;
    }
    private static (Version version,int preview)? Order(string tag)
    {
        var m=TagRegex.Match(tag);
        if(!m.Success||!Version.TryParse(m.Groups["version"].Value,out var version))
            return null;
        int preview=m.Groups["preview"].Success?
            int.Parse(m.Groups["preview"].Value):-1;
        return(version,preview);
    }
    public static UpdateRelease? Select(string json,UpdateChannel channel)
    {
        if(json.Length>5_000_000)throw new InvalidDataException("Oversized release manifest");
        using var doc=JsonDocument.Parse(json);
        if(doc.RootElement.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("Invalid release catalog");
        var found=new List<(Version v,int preview,UpdateRelease item)>();
        foreach(var item in doc.RootElement.EnumerateArray())
        {
            if(item.ValueKind!=JsonValueKind.Object||
               !item.TryGetProperty("draft",out var draft) ||
               draft.ValueKind!=JsonValueKind.False||
               !item.TryGetProperty("prerelease",out var pre)||
               pre.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
               !item.TryGetProperty("tag_name",out var tagValue)||
               tagValue.ValueKind!=JsonValueKind.String ||
               !item.TryGetProperty("assets",out var assets)||
               assets.ValueKind!=JsonValueKind.Array)continue;
            string tag=tagValue.GetString()??"";
            var order=Order(tag);
            if(order==null)continue;
            bool preview=order.Value.preview>=0;
            if(preview!=pre.GetBoolean() ||
               (preview&&channel==UpdateChannel.Stable))continue;
            var exactAssets=assets.EnumerateArray().Where(asset=>
                asset.ValueKind==JsonValueKind.Object&&
                asset.TryGetProperty("name",out var name)&&
                name.GetString()==Installer).ToArray();
            if(exactAssets.Length!=1)continue;
            var a=exactAssets[0];
            string url=a.TryGetProperty("browser_download_url",out var download)?
                download.GetString()??"":"";
            string expected=$"https://github.com/{Repository}/releases/download/{tag}/{Installer}";
            string digest=a.TryGetProperty("digest",out var digestValue)?
                digestValue.GetString()??"":"";
            if(url!=expected || !Regex.IsMatch(digest,
                @"\Asha256:[a-fA-F0-9]{64}\z",RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))continue;
            string body=item.TryGetProperty("body",out var notes)&&
                notes.ValueKind==JsonValueKind.String?notes.GetString()??"":"";
            found.Add((order.Value.version,order.Value.preview,
                new(order.Value.version,tag,body,url,digest[7..].ToLowerInvariant(),Installer)));
        }
        return found.OrderByDescending(x=>x.v).ThenByDescending(x=>x.preview)
            .Select(x=>x.item).FirstOrDefault();
    }
    public static bool IsTrusted(UpdateRelease release) =>
        Order(release.Tag)!=null &&
        release.AssetName==Installer &&
        release.AssetUrl==$"https://github.com/{Repository}/releases/download/{release.Tag}/{Installer}" &&
        Regex.IsMatch(release.Sha256,@"\A[a-fA-F0-9]{64}\z",
            RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));

    public static bool IsNewer(UpdateRelease release,Version current,string installedTag)
    {
        var incoming=Order(release.Tag);
        if(incoming==null)return false;
        var old=Order(installedTag);
        if(old==null)
            return incoming.Value.version>current ||
                (incoming.Value.version==current && incoming.Value.preview>0);
        int v=incoming.Value.version.CompareTo(old.Value.version);
        return v>0||(v==0&&incoming.Value.preview>old.Value.preview);
    }
    public static async Task<UpdateRelease?> CheckAsync(UpdateChannel channel,
        CancellationToken ct=default)
    {
        using var response=await Client.GetAsync(
            "https://api.github.com/repos/"+Repository+"/releases?per_page=50",ct);
        response.EnsureSuccessStatusCode();
        return Select(await response.Content.ReadAsStringAsync(ct),channel);
    }
}
