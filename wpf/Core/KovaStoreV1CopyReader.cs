using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record KovaStoreV1CopyObservation(
    Guid Session,string Cluster,string[] MapNames,string[] DeviceIds,
    bool HistoricalConsistent,string[] Problems,string[] InspectedSha256,string Warning)
{
    public ClusterVaultArchiveReport? Report {get;init;}
}

public static class KovaStoreV1CopyReader
{
    public static KovaStoreV1CopyObservation Inspect(
        string coordinationCopy,string hpLocalCopy,string ryzenLocalCopy)
    {
        var roots=new[]{coordinationCopy,hpLocalCopy,ryzenLocalCopy}
            .Select(p=>VaultReadSnapshot.LocalPath(p,true).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
        if(roots.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=3||
           roots.Any(a=>roots.Any(b=>a!=b&&a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("VAULT_INPUT: három különálló helyi másolatmappa szükséges");
        if(roots.Any(r=>!Directory.Exists(r)))
            throw new InvalidDataException("VAULT_INPUT: hiányzó másolatmappa");
        using var snapshot=new VaultReadSnapshot();
        var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        void Add(string root,string name,string key,bool required=false)
        {
            string path=Path.Combine(root,name);
            if(!File.Exists(path)&&!Directory.Exists(path))
            {
                if(required)throw new InvalidDataException("VAULT_MISSING: hiányzó koordináció");
                return;
            }
            if(Directory.Exists(path))
            {
                VaultReadSnapshot.LocalPath(path,true);
                if(name!="mutation.lock")throw new InvalidDataException("VAULT_INPUT: fájl helyett könyvtár");
                files.Add(key,System.Array.Empty<byte>());
            }
            else files.Add(key,snapshot.Read(path,1_048_576));
        }
        string shared=ClusterVaultArchiveInspector.SharedPrefix;
        Add(roots[0],"operation.json",shared+"operation.json",true);
        using var op=VaultReadSnapshot.Json(files,shared+"operation.json");
        if(!op.RootElement.TryGetProperty("peers",out var peers)||peers.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("VAULT_PEERS: hiányzó peerlista");
        var ids=peers.EnumerateArray().Select(p=>p.ValueKind==JsonValueKind.String?p.GetString()??"":"").ToArray();
        if(ids.Length!=2||ids.Any(id=>!Guid.TryParse(id,out _)))
            throw new InvalidDataException("VAULT_PEERS: érvénytelen peerlista");
        foreach(string id in ids)Add(roots[0],id+".json",shared+id+".json");
        foreach(string marker in new[]{"attempted.json","running.json","cancelled.json","mutation.lock"})
            Add(roots[0],marker,shared+marker);
        foreach(int index in new[]{1,2})
            foreach(string name in new[]{"cluster-v1.json","cluster-store-v1.json",
                "shared-store-device-v1.json","shared-store-lock-v1.json"})
                Add(roots[index],name,"HOST"+index+"/"+name);
        // Recheck marker presence before releasing any file handle. Metadata
        // copies must stay quiescent; this is not an SMB or live atomic snapshot.
        var before=files.Keys.Where(k=>k.StartsWith(shared)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var report=ClusterVaultArchiveInspector.InspectFiles(files);
        foreach(string marker in new[]{"attempted.json","running.json","cancelled.json","mutation.lock"})
        {
            string path=Path.Combine(roots[0],marker);
            if(before.Contains(shared+marker)!=(File.Exists(path)||Directory.Exists(path)))
                throw new InvalidDataException("VAULT_CHANGED: koordináció változott olvasás közben");
        }
        return new(Guid.Parse(VaultReadSnapshot.Text(op.RootElement,"session")),"KovaCluster",
            report.MapNames,ids,report.HistoricalConsistent,
            report.Findings.Select(f=>f.Code+": "+f.Message).ToArray(),
            files.Values.Where(b=>b.Length>0).Select(b=>Convert.ToHexString(SHA256.HashData(b))).ToArray(),
            "Historical copied metadata only; live Store, character/tribe consistency and writer ownership NOT proven.")
            {Report=report};
    }
}

