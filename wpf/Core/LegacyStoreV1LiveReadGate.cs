using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Read-only port of the ORIGINAL completed shared-store.mjs guard.
/// The legacy Store remains authoritative. No new session, conversion,
/// shared-directory creation, lock deletion, JSON write or implicit retry.
/// A ready result means the existing two-peer ACTIVE/DONE source state can
/// authorize an explicitly approved normal start in the original protocol;
/// it does not attest a survivor's in-game identity or a MariaDB snapshot.
/// </summary>
public static class LegacyStoreV1LiveReadGate
{
    public sealed record Proof(bool CompletedOriginalSourceGuard,
        bool LocalActive,bool BothPeersDone,bool SharedSessionMatches,
        bool NoPendingConversion,bool NoMutationMarker,
        bool LocalMapBindingMatches,bool HistoricalFilesUnchanged,
        int ActiveMaps,int DonePeers,string Evidence)
    {
        public bool RejoinProven=>false;
        public bool WorldSaveCompleteProven=>false;
    }
    private static bool Str(JsonElement x,string key,string value)=>
        x.ValueKind==JsonValueKind.Object &&
        x.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String &&
        string.Equals(v.GetString(),value,StringComparison.OrdinalIgnoreCase);
    private static string Text(JsonElement x,string key)=>VaultReadSnapshot.Text(x,key);
    private static JsonElement Need(JsonElement x,string key)
    {
        if(x.ValueKind!=JsonValueKind.Object||!x.TryGetProperty(key,out var p))
            throw new InvalidDataException("UM-STORE-LIVE-SCHEMA-MISSING");
        return p;
    }
    private static string NormalRoot(string s)
    {
        if(string.IsNullOrWhiteSpace(s)||!Path.IsPathFullyQualified(s))
            throw new InvalidDataException("UM-STORE-LIVE-INVALID-ROOT");
        string full=Path.GetFullPath(s);
        if(full.StartsWith(@"\\",StringComparison.Ordinal)||full.Contains('~'))
            throw new InvalidDataException("UM-STORE-LIVE-NONLOCAL-ROOT");
        return full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
    }
    private static string SharedRoot(string s)
    {
        if(string.IsNullOrWhiteSpace(s)||!s.StartsWith(@"\\",StringComparison.Ordinal)||
           s.StartsWith(@"\\?\",StringComparison.Ordinal)||
           s.StartsWith(@"\\.\",StringComparison.Ordinal)||
           !Path.IsPathFullyQualified(s)||s.Any(char.IsControl))
            throw new InvalidDataException("UM-STORE-LIVE-SHARE-INVALID");
        string[] parts=s[2..].Split('\\',StringSplitOptions.None);
        if(parts.Length<2||parts.Any(p=>p.Length==0||p is "." or ".."||
           p.IndexOfAny(new[]{'/',':','"','*','?','<','>','|'})>=0))
            throw new InvalidDataException("UM-STORE-LIVE-SHARE-UNSAFE");
        return s.TrimEnd('\\');
    }
    private static byte[] ReadShared(string path)
    {
        var attr=File.GetAttributes(path);
        if((attr&FileAttributes.ReparsePoint)!=0||!(attr.HasFlag(FileAttributes.Archive)||
           attr.HasFlag(FileAttributes.Normal)||attr.HasFlag(FileAttributes.Hidden)||
           attr.HasFlag(FileAttributes.ReadOnly)||attr.HasFlag(FileAttributes.System)))
            throw new InvalidDataException("UM-STORE-LIVE-SHARED-FILE-INVALID");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.Read,4096,FileOptions.SequentialScan);
        if(stream.Length is <=0 or >1048576)
            throw new InvalidDataException("UM-STORE-LIVE-SHARED-SIZE");
        byte[] data=new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        if(stream.ReadByte()!=-1)
            throw new InvalidDataException("UM-STORE-LIVE-SHARED-CHANGED");
        return data;
    }
    private static JsonDocument Json(byte[] raw)
    {
        var content=new Dictionary<string,byte[]>(StringComparer.Ordinal){{"shared.json",raw}};
        return VaultReadSnapshot.Json(content,"shared.json");
    }
    private static bool Missing(string path)=>!File.Exists(path)&&!Directory.Exists(path);
    private static void DemandUnchanged(string path,byte[] expected)
    {
        byte[] current=ReadShared(path);
        if(!CryptographicOperations.FixedTimeEquals(
               SHA256.HashData(current),SHA256.HashData(expected)))
            throw new IOException("UM-STORE-LIVE-SHARED-CHANGED");
    }

    /// <summary>
    /// Explicit operator-initiated probe against the existing ORIGINAL Kova
    /// local profile and its configured UNC root. No other remote URI or
    /// path can be supplied by WebView2. Never used by periodic dashboard.
    /// </summary>
    public static Proof InspectOriginalActive(LocalMap map,string originalLocalProfile)
    {
        if(map==null||!map.HistoricalStoreProtected||map.Id==Guid.Empty||
           string.IsNullOrWhiteSpace(map.HistoricalMapKey))
            throw new UnauthorizedAccessException("UM-STORE-LIVE-HISTORICAL-MAP-ONLY");
        var report=LegacyStoreV1LocalReader.Inspect(originalLocalProfile);
        if(!report.LocalSnapshotReady||report.Maps.Length!=5)
            throw new InvalidDataException("UM-STORE-LIVE-LOCAL-NOT-ACTIVE");
        using var held=new VaultReadSnapshot();
        string local=VaultReadSnapshot.LocalPath(originalLocalProfile,true);
        byte[] clusterRaw=held.Read(Path.Combine(local,"cluster-v1.json"),1048576);
        byte[] deviceRaw=held.Read(Path.Combine(local,"shared-store-device-v1.json"),1048576);
        byte[] lockRaw=held.Read(Path.Combine(local,"shared-store-lock-v1.json"),1048576);
        byte[] storeRaw=held.Read(Path.Combine(local,"cluster-store-v1.json"),1048576);
        var files=new Dictionary<string,byte[]>{
            ["cluster.json"]=clusterRaw,["device.json"]=deviceRaw,
            ["lock.json"]=lockRaw,["store.json"]=storeRaw};
        using var cDoc=VaultReadSnapshot.Json(files,"cluster.json");
        using var dDoc=VaultReadSnapshot.Json(files,"device.json");
        using var lDoc=VaultReadSnapshot.Json(files,"lock.json");
        using var sDoc=VaultReadSnapshot.Json(files,"store.json");
        var cluster=cDoc.RootElement;var device=dDoc.RootElement;
        var localLock=lDoc.RootElement;var store=sDoc.RootElement;
        string deviceId=Text(device,"id");
        string session=Text(localLock,"session");
        string path=SharedRoot(Text(cluster,"path"));
        if(!Guid.TryParse(deviceId,out _)||!Guid.TryParse(session,out _)||
           !Str(cluster,"mode","shared")||
           !Str(cluster,"id",VaultProtocol.ClusterId)||
           !Str(Need(localLock,"config"),"id",VaultProtocol.ClusterId)||
           !Str(Need(localLock,"config"),"path",path)||
           !StoreV1Fingerprint.Matches(Need(localLock,"config"),Text(localLock,"configHash")))
            throw new InvalidDataException("UM-STORE-LIVE-LOCAL-LOCK-MISMATCH");
        string share=Path.Combine(path,".kova-store-v1");
        if(!Directory.Exists(share)||
           (File.GetAttributes(share)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-STORE-LIVE-SHARED-DIRECTORY-INVALID");
        string operation=Path.Combine(share,"operation.json");
        byte[] operationRaw=ReadShared(operation);
        using var opDoc=Json(operationRaw);
        var op=opDoc.RootElement;
        var peers=Need(op,"peers");
        if(peers.ValueKind!=JsonValueKind.Array||peers.GetArrayLength()!=2||
           !Str(op,"session",session)||
           !Str(op,"cluster",VaultProtocol.ClusterId)||
           !Str(op,"path",path)||
           Need(op,"schema").GetInt32()!=1)
            throw new InvalidDataException("UM-STORE-LIVE-OPERATION-INVALID");
        var peerIds=peers.EnumerateArray().Select(x=>x.GetString()??"").ToArray();
        if(peerIds.Any(x=>!Guid.TryParse(x,out _))||
           peerIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=2||
           !peerIds.Contains(deviceId,StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-STORE-LIVE-PEERS-INVALID");
        var members=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int done=0;byte[][] peerBytes=new byte[2][];
        for(int i=0;i<2;i++)
        {
            string peerFile=Path.Combine(share,peerIds[i]+".json");
            peerBytes[i]=ReadShared(peerFile);
            using var peerDoc=Json(peerBytes[i]);var peer=peerDoc.RootElement;
            var config=Need(peer,"config");
            var owned=Need(peer,"members");
            var selected=Need(peer,"selected");
            if(!Str(peer,"session",session)||
               !Str(peer,"id",peerIds[i])||
               !Str(peer,"phase","done")||
               !Str(config,"id",VaultProtocol.ClusterId)||
               !Str(config,"mode","shared")||
               !Str(config,"path",path)||
               !StoreV1Fingerprint.Matches(config,Text(peer,"configHash"))||
               owned.ValueKind!=JsonValueKind.Array||owned.GetArrayLength()!=5||
               selected.ValueKind!=JsonValueKind.Array||selected.GetArrayLength()!=5||
               Need(config,"members").GetRawText()!=owned.GetRawText())
                throw new InvalidDataException("UM-STORE-LIVE-PEER-DONE-INVALID");
            var ids=owned.EnumerateArray().Select(x=>Text(x,"id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(ids.Count!=5||selected.EnumerateArray().Any(x=>
               !ids.Contains(x.GetString()??"")))
                throw new InvalidDataException("UM-STORE-LIVE-PEER-SELECTED-MISMATCH");
            foreach(var item in owned.EnumerateArray())
                if(!members.Add(Text(item,"map")))
                    throw new InvalidDataException("UM-STORE-LIVE-DUPLICATE-MAP");
            done++;
        }
        var records=Need(store,"maps");
        if(records.ValueKind!=JsonValueKind.Object||
           !records.TryGetProperty(map.Id.ToString("D"),out var record)||
           !Str(record,"phase","active")||
           !Str(record,"map",map.HistoricalMapKey)||
           !Str(record,"sharedSession",session)||
           !NormalRoot(Text(record,"root")).Equals(
               NormalRoot(map.InstallationPath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-STORE-LIVE-MAP-OWNERSHIP-MISMATCH");
        string running=Path.Combine(share,"running.json");
        string mutation=Path.Combine(share,"mutation.lock");
        string cancelled=Path.Combine(share,"cancelled.json");
        string attempted=Path.Combine(share,"attempted.json");
        if(!Missing(running)||!Missing(mutation)||!Missing(cancelled))
            throw new InvalidOperationException("UM-STORE-LIVE-RECOVERY-MARKER-REQUIRES-OPERATOR");
        byte[] attemptedRaw=ReadShared(attempted);
        using(var attemptedDoc=Json(attemptedRaw))
            if(!Str(attemptedDoc.RootElement,"session",session))
                throw new InvalidDataException("UM-STORE-LIVE-ATTEMPTED-SESSION-MISMATCH");
        DemandUnchanged(operation,operationRaw);
        for(int i=0;i<2;i++)
            DemandUnchanged(Path.Combine(share,peerIds[i]+".json"),peerBytes[i]);
        DemandUnchanged(attempted,attemptedRaw);
        if(!Missing(running)||!Missing(mutation)||!Missing(cancelled))
            throw new InvalidOperationException("UM-STORE-LIVE-SHARED-MARKER-CHANGED");
        // Proof covers original active coordination only. Launch/client
        // acceptance is a separate operation; no source bytes changed.
        return new(true,true,done==2,true,true,true,true,true,
            members.Count,done,"ORIGINAL_LIVE_TWO_HOST_ACTIVE_DONE_SHARED_GUARD_READ_ONLY");
    }
}
