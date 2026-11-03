using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

// Read-only Kova 1.8.3 shared-store-v1 state contract over three EXPLICITLY
// copied local folders: a shared coordination copy and both local lock copies.
// The reader NEVER creates, modifies, migrates or deletes a Store record.
// "Historical consistent" must never unlock LiveKovaStoreWrite or ASA start.
public sealed record KovaStoreV1CopyObservation(
    Guid Session,string Cluster,string[] MapNames,string[] DeviceIds,
    bool HistoricalConsistent,string[] Problems,string[] InspectedSha256,
    string Warning);

public static class KovaStoreV1CopyReader
{
    private const string Disclaimer="Historical copied Store-v1 coordination only; "+
        "live peer liveness, JS configHash canonical parity, character/tribe data "+
        "and 1:1 application compatibility NOT proven.";
    private sealed record PeerDetails(string Id,string ConfigHash,string[] Maps,
        string[] Selected,bool Done,string[] Issues);

    public static KovaStoreV1CopyObservation Inspect(
        string coordinationCopy,string hpLocalCopy,string ryzenLocalCopy)
    {
        string shared=Root(coordinationCopy),hp=Root(hpLocalCopy),ryzen=Root(ryzenLocalCopy);
        if(new[]{shared,hp,ryzen}.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=3)
            throw new InvalidDataException("Shared and both local Kova snapshot roots must be distinct");
        using var operation=Json(shared,"operation.json");
        var op=operation.RootElement;
        if(Int(op,"schema")!=1 || !Guid.TryParse(Str(op,"session"),out var session)||
           Str(op,"cluster")!="KovaCluster")
            throw new InvalidDataException("Unknown Kova Store-v1 operation contract");
        string clusterPath=Str(op,"path");
        if(string.IsNullOrWhiteSpace(clusterPath))
            throw new InvalidDataException("Store operation cluster path missing");
        var peers=Array(op,"peers").EnumerateArray()
            .Select(x=>x.ValueKind==JsonValueKind.String?x.GetString()??"":"").ToArray();
        if(peers.Length!=2 ||peers.Any(x=>!Guid.TryParse(x,out _)) ||
           peers[0].Equals(peers[1],StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Store operation requires two different device IDs");

        var sha=new List<string>{Digest(Path.Combine(shared,"operation.json"))};
        var issues=new List<string>();
        PeerDetails ReadPeer(string localRoot)
        {
            using var device=Json(localRoot,"shared-store-device-v1.json");
            var localDevice=device.RootElement;
            if(Int(localDevice,"schema")!=1 ||
               !Guid.TryParse(Str(localDevice,"id"),out var deviceId))
                throw new InvalidDataException("Invalid Kova Store device identity");
            string key=deviceId.ToString();
            if(!peers.Any(x=>Guid.Parse(x)==deviceId))
                throw new InvalidDataException("Local Store device is not in operation peer list");
            using var locked=Json(localRoot,"shared-store-lock-v1.json");
            var localLock=locked.RootElement;
            if(Int(localLock,"schema")!=1 ||
               !Guid.TryParse(Str(localLock,"session"),out var localSession)||
               localSession!=session)
                issues.Add("Local lock session does not match operation");
            string localHash=Str(localLock,"configHash");
            if(localHash.Length!=64 || localHash.Any(x=>!Uri.IsHexDigit(x)))
                issues.Add("Malformed local configHash");
            var localSelected=Ids(Array(localLock,"selected"));
            using var peer=Json(shared,key+".json");
            var p=peer.RootElement;
            if(!Guid.TryParse(Str(p,"session"),out var peerSession)||
               peerSession!=session||
               !Guid.TryParse(Str(p,"id"),out var peerId)||peerId!=deviceId)
                issues.Add("Peer identity/session not bound to operation");
            bool done=Str(p,"phase")=="done";
            if(!done)issues.Add("Peer is not in done phase");
            string peerHash=Str(p,"configHash");
            if(!string.Equals(localHash,peerHash,StringComparison.OrdinalIgnoreCase))
                issues.Add("Peer and local lock configHash differ");
            var config=Required(p,"config");
            if(Str(config,"id")!="KovaCluster" ||
               Str(config,"mode")!="shared" ||
               !Str(config,"path").Equals(clusterPath,
                   StringComparison.OrdinalIgnoreCase))
                issues.Add("Peer config differs from operation cluster/mode/path");
            var lockConfig=Required(localLock,"config");
            if(Str(lockConfig,"id")!=Str(config,"id") ||
               Str(lockConfig,"mode")!=Str(config,"mode") ||
               !Str(lockConfig,"path").Equals(Str(config,"path"),
                   StringComparison.OrdinalIgnoreCase))
                issues.Add("Local lock config differs from peer config");

            var members=Array(p,"members").EnumerateArray().ToArray();
            var memberIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mapNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var m in members)
            {
                string id=Str(m,"id"),name=Str(m,"map"),root=Str(m,"root");
                if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(name)||
                   string.IsNullOrWhiteSpace(root)||!memberIds.Add(id)||!mapNames.Add(name))
                    issues.Add("Duplicate/incomplete peer map membership");
            }
            if(members.Length!=5)
                issues.Add("Peer map membership must contain exactly five maps");
            var selected=Ids(Array(p,"selected"));
            if(selected.Length!=5 || selected.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=5||
               selected.Any(id=>!memberIds.Contains(id)) ||
               !selected.SequenceEqual(localSelected,StringComparer.OrdinalIgnoreCase))
                issues.Add("Peer selected maps differ from local lock/owned members");
            if(config.TryGetProperty("members",out var configMembers) &&
               configMembers.ValueKind==JsonValueKind.Array)
            {
                var fromConfig=configMembers.EnumerateArray().Select(m=>(
                    Id:Str(m,"id"),Map:Str(m,"map"),Root:Str(m,"root"))).ToArray();
                var fromPeer=members.Select(m=>(
                    Id:Str(m,"id"),Map:Str(m,"map"),Root:Str(m,"root"))).ToArray();
                if(!fromConfig.SequenceEqual(fromPeer))
                    issues.Add("Peer member list differs from embedded config members");
            }
            else issues.Add("Missing config.members array");
            sha.Add(Digest(Path.Combine(localRoot,"shared-store-device-v1.json")));
            sha.Add(Digest(Path.Combine(localRoot,"shared-store-lock-v1.json")));
            sha.Add(Digest(Path.Combine(shared,key+".json")));
            return new(key,peerHash,mapNames.ToArray(),selected,done,issues.ToArray());
        }
        var first=ReadPeer(hp);
        var second=ReadPeer(ryzen);
        var total=first.Maps.Concat(second.Maps).ToArray();
        if(total.Length!=10 ||total.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=10)
            issues.Add("Store peer map collision, missing or duplicate map");
        if(!string.Equals(first.ConfigHash,second.ConfigHash,StringComparison.OrdinalIgnoreCase))
            issues.Add("Peer config hashes differ (may reflect different valid per-host configs)");
        // Do not require the two local config hashes to equal each other: Kova
        // host configs contain different members. Distinct configs are valid.
        issues.RemoveAll(x=>x.StartsWith("Peer config hashes differ",
            StringComparison.Ordinal));
        if(File.Exists(Path.Combine(shared,"running.json")) ||
           Directory.Exists(Path.Combine(shared,"mutation.lock")) ||
           File.Exists(Path.Combine(shared,"cancelled.json")))
            issues.Add("Interrupted/running/cancelled Store coordination marker exists");
        if(peers.Any(id=>!new[]{first.Id,second.Id}.Contains(
            Guid.Parse(id).ToString(),StringComparer.OrdinalIgnoreCase)))
            issues.Add("Peer ID mismatch between device copies and operation");
        return new(session,"KovaCluster",total,
            new[]{first.Id,second.Id},issues.Count==0,
            issues.ToArray(),sha.ToArray(),Disclaimer);
    }

    private static string Root(string dir)
    {
        if(string.IsNullOrWhiteSpace(dir)||!Path.IsPathFullyQualified(dir)||dir.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit copied local Kova Store folder required");
        string full=Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Kova Store copies must be on fixed disk");
        for(var node=new DirectoryInfo(full);node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked Store copy path rejected");
        if(!Directory.Exists(full))
            throw new DirectoryNotFoundException("Missing copied Kova Store folder");
        return full;
    }
    private static JsonDocument Json(string dir,string name)
    {
        string path=Path.Combine(dir,name);
        if(!File.Exists(path)||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0 ||
           new FileInfo(path).Length is < 2 or > 1_048_576)
            throw new InvalidDataException("Missing/oversized/linked copied Store JSON: "+name);
        return JsonDocument.Parse(File.ReadAllText(path),new JsonDocumentOptions{
            MaxDepth=36,AllowTrailingCommas=false});
    }
    private static string Digest(string path)
    {
        using var f=File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }
    private static JsonElement Required(JsonElement e,string name)
        =>e.ValueKind==JsonValueKind.Object &&e.TryGetProperty(name,out var v)?
            v:throw new InvalidDataException("Required Store v1 key missing: "+name);
    private static JsonElement Array(JsonElement e,string name)
    {
        var v=Required(e,name);
        return v.ValueKind==JsonValueKind.Array?v:
            throw new InvalidDataException("Store v1 list missing: "+name);
    }
    private static string Str(JsonElement e,string key)
        =>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&
          v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    private static int Int(JsonElement e,string key)
        =>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&
          v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var n)?n:-1;
    private static string[] Ids(JsonElement a)
        =>a.EnumerateArray().Select(e=>e.ValueKind==JsonValueKind.String?
            e.GetString()??"":"").ToArray();
}
