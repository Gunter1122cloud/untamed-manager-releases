using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UntamedManager.Core;

// Portable non-secret Manager preferences. Import never grants local map
// ownership, changes executable paths, Store/SQL/RCON credentials or launches ASA.
public sealed record SafeMapPreference(Guid Id,string Name,int WatchdogIntervalSeconds,
    bool MonitorRcon);
public sealed record SafeProfileBundle(int Version,string HostId,DateTimeOffset ExportedUtc,
    SafeMapPreference[] Maps);
public sealed record SafeProfilePreview(bool Allowed,int MatchedMaps,string Reason);

public static class LocalProfileTransfer
{
    public static string Export(ManagerConfig config)
    {
        if(config==null || string.IsNullOrWhiteSpace(config.LocalHostId))
            throw new InvalidDataException("Explicit Manager identity required");
        var bundle=new SafeProfileBundle(1,config.LocalHostId,DateTimeOffset.UtcNow,
            config.LocalMaps.Select(map=>new SafeMapPreference(map.Id,map.Name,
                map.WatchdogIntervalSeconds,map.MonitorRcon)).ToArray());
        return JsonSerializer.Serialize(bundle,new JsonSerializerOptions{WriteIndented=true});
    }

    public static SafeProfilePreview Preview(ManagerConfig current,string json)
    {
        if(current==null || string.IsNullOrWhiteSpace(json) || json.Length>200000)
            return new(false,0,"Config preview must be a bounded local file");
        SafeProfileBundle? source;
        try{source=JsonSerializer.Deserialize<SafeProfileBundle>(json);}
        catch(JsonException){return new(false,0,"Malformed preferences JSON");}
        if(source==null || source.Version!=1 || source.HostId!=current.LocalHostId ||
            source.Maps==null)return new(false,0,"Host identity or version mismatch");
        if(source.Maps.Select(x=>x.Id).Distinct().Count()!=source.Maps.Length ||
            source.Maps.Any(x=>x.Id==Guid.Empty || string.IsNullOrWhiteSpace(x.Name) ||
                x.WatchdogIntervalSeconds<5 || x.WatchdogIntervalSeconds>3600))
            return new(false,0,"Duplicate/unsafe map preferences");
        if(source.Maps.Length!=current.LocalMaps.Count ||
            source.Maps.Any(item=>!current.LocalMaps.Any(map=>map.Id==item.Id &&
                map.Name==item.Name)))
            return new(false,0,"The bundle must match registered local map ownership exactly");
        return new(true,source.Maps.Length,
            "Only Watchdog interval and read-only RCON monitoring preferences will be imported");
    }

    public static void Apply(ManagerConfig current,string json)
    {
        var preview=Preview(current,json);
        if(!preview.Allowed)throw new InvalidDataException(preview.Reason);
        var source=JsonSerializer.Deserialize<SafeProfileBundle>(json)!;
        foreach(var change in source.Maps)
        {
            var map=current.LocalMaps.Single(x=>x.Id==change.Id);
            map.WatchdogIntervalSeconds=change.WatchdogIntervalSeconds;
            map.MonitorRcon=change.MonitorRcon;
        }
    }

    public static void ExportToFile(ManagerConfig config,string path)
    {
        if(string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
           !path.EndsWith(".json",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a fully qualified JSON destination");
        string full=Path.GetFullPath(path);
        if(File.Exists(full))throw new IOException("Preferences export destination already exists");
        File.WriteAllText(full,Export(config));
    }
}
