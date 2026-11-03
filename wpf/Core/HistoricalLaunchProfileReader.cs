using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

// Historical, read-only preview of an existing five-map Manager archive.
// No passwords, paths, tokens, process IDs or schedules are exported.
// This is never a live server profile import or automatic launch.
public sealed record HistoricalLaunchProfile(
    Guid Id,string MapId,int GamePort,int QueryPort,
    int EnabledMods,int PassiveMods,bool ExistingStoreActive,
    string ArgumentsPreview);
public sealed record HistoricalLaunchBundle(
    HistoricalLaunchProfile[] Maps,bool ReadyForLiveControl,string Warning);

public static class HistoricalLaunchProfileReader
{
    public static HistoricalLaunchBundle Inspect(string copiedZip)
    {
        var verified=UntamedManagerArchiveInspector.Inspect(copiedZip);
        if(verified.VerifiedFiles!=39)
            throw new InvalidDataException("Historical Manager archive is incomplete");
        using var zip=ZipFile.OpenRead(copiedZip);
        using var launch=Read(zip,"Kova/launch-profiles-v1.json");
        using var world=Read(zip,"Kova/cluster-store-v1.json");
        using var cluster=Read(zip,"Kova/cluster-v1.json");
        if(Number(launch.RootElement,"version")!=1 ||
           Number(world.RootElement,"schema")!=1 ||
           Str(cluster.RootElement,"id")!="KovaCluster" ||
           Str(cluster.RootElement,"mode")!="shared")
            throw new InvalidDataException("Unsupported historical launch contract");
        var local=Required(world.RootElement,"maps");
        var members=Required(cluster.RootElement,"members").EnumerateArray().ToArray();
        var rows=Required(launch.RootElement,"rows").EnumerateArray().ToArray();
        if(rows.Length!=5 ||members.Length!=5 ||local.EnumerateObject().Count()!=5)
            throw new InvalidDataException("Expected five local map profiles");
        var seen=new HashSet<Guid>();
        var ports=new HashSet<int>();
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previews=new List<HistoricalLaunchProfile>();
        foreach(var row in rows)
        {
            if(!Guid.TryParse(Str(row,"id"),out var id)||!seen.Add(id))
                throw new InvalidDataException("Missing or duplicated map profile identity");
            var cfg=Required(row,"launch");
            string map=Str(cfg,"map");
            if(!Regex.IsMatch(map,@"\A[A-Za-z][A-Za-z0-9_]{1,63}\z")||
               !names.Add(map))
                throw new InvalidDataException("Ambiguous historical map ID");
            int game=Number(cfg,"gamePort"),query=Number(cfg,"queryPort");
            if(game is < 1024 or > 65535||query is < 1024 or > 65535 ||
               !ports.Add(game)||!ports.Add(query))
                throw new InvalidDataException("Historical map port collision");
            var enabled=ModIds(Required(cfg,"mods"));
            var passive=ModIds(Required(cfg,"passiveMods"));
            if(enabled.Distinct(StringComparer.Ordinal).Count()!=enabled.Length ||
               passive.Distinct(StringComparer.Ordinal).Count()!=passive.Length ||
               passive.Except(enabled,StringComparer.Ordinal).Any())
                throw new InvalidDataException("Invalid passive mod subset");
            var member=members.SingleOrDefault(m=>Str(m,"id")==id.ToString());
            if(member.ValueKind==JsonValueKind.Undefined ||
               Str(member,"map")!=map ||Str(member,"root")!=Str(row,"root"))
                throw new InvalidDataException("Launch profile and Store member mismatch");
            if(!local.TryGetProperty(id.ToString(),out var state)||
               Str(state,"phase")!="active" ||
               Str(state,"map")!=map ||
               Str(state,"root")!=Str(row,"root") ||
               !Guid.TryParse(Str(state,"sharedSession"),out _))
                throw new InvalidDataException("Historical Store state not active");
            // The original launch format has an enabled list and a passive
            // subset. The canonical output places passive first, once.
            var ordered=passive.Concat(enabled.Except(passive,StringComparer.Ordinal))
                .ToArray();
            string preview=map+"? -mods="+string.Join(",",ordered)+
                (passive.Length>0?" -passivemods="+string.Join(",",passive):"")+
                " -usestore";
            previews.Add(new(id,map,game,query,ordered.Length,passive.Length,
                true,preview));
        }
        return new(previews.ToArray(),false,
            "Történeti profiladatok. Nincs Store-migráció, SaveComplete, "+
            "éles mapindítás vagy importálási jogosultság.");
    }

    private static JsonDocument Read(ZipArchive zip,string name)
    {
        var entry=zip.Entries.SingleOrDefault(e=>
            e.FullName.Replace('\\','/').Equals(name,StringComparison.OrdinalIgnoreCase));
        if(entry==null || entry.Length is < 2 or > 1_000_000)
            throw new InvalidDataException("Missing or invalid historical profile record");
        using var stream=entry.Open();
        return JsonDocument.Parse(stream,new JsonDocumentOptions{MaxDepth=40});
    }
    private static JsonElement Required(JsonElement e,string name)=>
        e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var value)?
            value:throw new InvalidDataException("Historical field missing");
    private static string Str(JsonElement e,string name)=>
        e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var value)&&
        value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    private static int Number(JsonElement e,string name)=>
        e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var value)&&
        value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out int n)?n:-1;
    private static string[] ModIds(JsonElement array)
    {
        if(array.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("Historical mod list must be an array");
        var result=array.EnumerateArray().Select(x=>x.ValueKind switch{
            JsonValueKind.String=>x.GetString()??"",
            JsonValueKind.Number when x.TryGetInt64(out var i)=>i.ToString(),
            _=>""}).ToArray();
        if(result.Length>500 ||result.Any(x=>!Regex.IsMatch(x,@"\A[0-9]{5,12}\z")))
            throw new InvalidDataException("Invalid mod ID in historical archive");
        return result;
    }
}
