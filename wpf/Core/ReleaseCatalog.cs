using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

public enum UpdateChannel { Stable, Preview }
public sealed record ReleaseOrder(Version Version, int? Preview) : IComparable<ReleaseOrder>
{
    public int CompareTo(ReleaseOrder? other)
    {
        if(other==null)return 1;
        int version=Version.CompareTo(other.Version);
        if(version!=0)return version;
        if(Preview==null)return other.Preview==null?0:1;
        return other.Preview==null?-1:Preview.Value.CompareTo(other.Preview.Value);
    }
    public static ReleaseOrder? Parse(string tag)
    {
        var m=Regex.Match(tag,@"\Av?(\d+)\.(\d+)\.(\d+)(?:-wpf-preview\.(\d+))?\z");
        if(!m.Success || !Version.TryParse($"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}",out var version))return null;
        int? preview=null;
        if(m.Groups[4].Success)
        { if(!int.TryParse(m.Groups[4].Value,out var n)||n<1)return null;preview=n; }
        return new(version,preview);
    }
}
public static class ReleaseCatalog
{
    public static UpdateRelease? Select(string json,string repository,UpdateChannel channel)
    {
        if(!UpdateCenter.ValidRepository(repository) || !Enum.IsDefined(channel))throw new ArgumentException("Invalid update settings");
        using var doc=JsonDocument.Parse(json);
        var candidates=new List<(ReleaseOrder order,UpdateRelease release)>();
        foreach(var item in doc.RootElement.EnumerateArray())
        {
            if(item.GetProperty("draft").GetBoolean())continue;
            string tag=item.GetProperty("tag_name").GetString()??"";
            var order=ReleaseOrder.Parse(tag);
            if(order==null)continue;
            bool preview=item.GetProperty("prerelease").GetBoolean();
            if(preview!=(order.Preview!=null) || (preview && channel==UpdateChannel.Stable))continue;
            string assetName=$"UntamedManager-{order.Version.ToString(3)}-SETUP.exe";
            var assets=item.GetProperty("assets").EnumerateArray().Where(x=>x.GetProperty("name").GetString()==assetName).ToArray();
            if(assets.Length!=1)continue;
            var asset=assets[0];
            string url=asset.GetProperty("browser_download_url").GetString()??"";
            string expected=$"https://github.com/{repository}/releases/download/{tag}/{assetName}";
            string digest=asset.TryGetProperty("digest",out var hash)?hash.GetString()??"":"";
            if(url!=expected || !Regex.IsMatch(digest,@"\Asha256:[0-9a-fA-F]{64}\z"))continue;
            candidates.Add((order,new(order.Version,tag,item.TryGetProperty("body",out var body)?body.GetString()??"":"",url,digest[7..].ToLowerInvariant(),assetName)));
        }
        return candidates.OrderByDescending(x=>x.order).Select(x=>x.release).FirstOrDefault();
    }
    public static bool IsNewer(UpdateRelease release,Version running,string installedTag)
    {
        var installed=ReleaseOrder.Parse(installedTag)??new ReleaseOrder(new Version(running.Major,running.Minor,Math.Max(0,running.Build)),null);
        return ReleaseOrder.Parse(release.Tag)?.CompareTo(installed)>0;
    }
    public static string InstalledTag
    {
        get {try{return File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"installed-release.txt")).Trim();}catch(IOException){return "";}catch(UnauthorizedAccessException){return "";} }
    }
}
