using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

// Supports the two different *sanitized audit export* shapes supplied in
// November 2026. Does not interpret or write the live .legacy-store-v1 protocol.
// A clean audit snapshot is historical evidence, NEVER an ownership/restore token.
public enum VaultAuditHost { Hp, Ryzen }
public sealed record VaultAuditSnapshot(
    VaultAuditHost Host,string ClusterId,string Mode,string SourceHash,
    string[] MapNames,int PeerCount,bool PeersDone,bool SessionsAgree,
    bool AllLocalActive,bool MarkerOperation,bool MarkerRunning,
    bool MarkerMutationLock,bool SnapshotInternallyConsistent,
    string[] Issues);
public sealed record VaultAuditPair(
    bool HistoricalSnapshotConsistent,string[] MapNames,
    string[] Issues,string SafetyStatement);

public static class VaultAuditBridge
{
    public static VaultAuditSnapshot InspectCopy(string file,VaultAuditHost host)
    {
        if(string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file) ||
            file.StartsWith(@"\\") || !file.EndsWith(".json",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Helyi, másolt Vault-audit JSON szükséges");
        string full=Path.GetFullPath(file);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("A Vault-audit csak helyi meghajtón elemezhető");
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(full)!);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Hivatkozott Vault-audit mappa nem fogadható el");
        if(!File.Exists(full) ||
            (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0 ||
            new FileInfo(full).Length is < 10 or > 2_000_000)
            throw new InvalidDataException("Hiányzó vagy nem megfelelő Vault-audit");
        byte[] data=File.ReadAllBytes(full);
        string hash=Convert.ToHexString(SHA256.HashData(data));
        using var document=JsonDocument.Parse(data,new JsonDocumentOptions{
            MaxDepth=40,AllowTrailingCommas=false,CommentHandling=JsonCommentHandling.Disallow});
        JsonElement root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("Vault-audit JSON objektum szükséges");

        string cluster=String(root,host==VaultAuditHost.Hp?"ClusterId":"ClusterID");
        string mode=String(root,"Mode");
        var problems=new List<string>();
        if(cluster!=VaultProtocol.ClusterId)problems.Add("A cluster azonosítója eltér a mentett formátumtól");
        if(!mode.Equals("shared",StringComparison.OrdinalIgnoreCase))
            problems.Add("The audit does not describe shared Store mode");

        JsonElement maps=RequiredArray(root,host==VaultAuditHost.Hp?
            "LocalStoreMaps":"StoreMaps");
        var names=new List<string>();
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool active=true;
        foreach(var item in maps.EnumerateArray())
        {
            string name=String(item,"Map");
            string profile=String(item,"Profile"),path=String(item,"Root");
            if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(profile)||
               string.IsNullOrWhiteSpace(path)||!profiles.Add(profile)||!roots.Add(path))
                problems.Add("Missing or duplicated local profile/Store map root");
            if(String(item,"Phase")!="active")active=false;
            if(host==VaultAuditHost.Hp &&
                (!Boolean(item,"Confirmed")||!Boolean(item,"SessionMatchesOperation")))
                active=false;
            if(host==VaultAuditHost.Ryzen && !Boolean(item,"HasSharedSession"))
                active=false;
            names.Add(name);
        }
        if(maps.GetArrayLength()!=5 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=5)
            problems.Add("The audit does not contain exactly five unique maps");
        if(!active)problems.Add("Local Store map(s) not proven active/session matched");

        bool peersDone=true,sessions=true;
        JsonElement peers=RequiredArray(root,host==VaultAuditHost.Hp?
            "Peers":"PhysicalPeerChecks");
        if(peers.GetArrayLength()!=2)problems.Add("Two peer entries required");
        foreach(var peer in peers.EnumerateArray())
        {
            if(String(peer,"Phase")!="done")peersDone=false;
            if(!Boolean(peer,"SessionMatches"))sessions=false;
            if(host==VaultAuditHost.Hp)
            {
                if(!Boolean(peer,"ReadOK") || Number(peer,"MemberCount")!=5 ||
                   Number(peer,"SelectedCount")!=5)
                    sessions=false;
            }
            else
            {
                if(!Boolean(peer,"ConfigHashMatches")||
                   !Boolean(peer,"PathMatches")||
                   !Boolean(peer,"SelectedAreMembers")||
                   Number(peer,"Members")!=5||Number(peer,"Selected")!=5)
                    sessions=false;
            }
        }
        if(host==VaultAuditHost.Hp)
        {
            if(Number(root,"PeerCount")!=2 || !Boolean(root,"ShareReadable") ||
               !Boolean(root,"LocalLockSessionMatches") ||
               !Boolean(root,"LocalPathMatchesOperation") ||
               !Boolean(root,"LocalClusterMatchesOperation") ||
               !Boolean(root,"SelectedLocalProfilesMatch") ||
               !Boolean(root,"LocalLockConfigIdMatches") ||
               !Boolean(root,"LocalLockConfigPathMatches"))
                sessions=false;
        }
        else if(!Boolean(root,"ReadAccessOnlyInThisAudit") ||
            !Boolean(root,"LocalConfigHashMatches")||
            !Boolean(root,"SessionAgreement"))
            sessions=false;
        if(!peersDone)problems.Add("Peer(s) are not done");
        if(!sessions)problems.Add("Session/hash/config/ownership evidence does not match");

        bool op=false,running=false,mutation=false;
        JsonElement markers=Required(root,"Markers");
        if(host==VaultAuditHost.Hp)
        {
            if(markers.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("HP-audit jelzőlistája érvénytelen");
            foreach(var marker in markers.EnumerateArray())
            {
                string name=String(marker,"Name");
                bool exists=Boolean(marker,"Exists");
                if(name.Equals("operation.json",StringComparison.OrdinalIgnoreCase))op=exists;
                if(name.Equals("running.json",StringComparison.OrdinalIgnoreCase))running=exists;
                if(name.Equals("mutation.lock",StringComparison.OrdinalIgnoreCase))mutation=exists;
            }
        }
        else
        {
            if(markers.ValueKind!=JsonValueKind.Object)
                throw new InvalidDataException("Ryzen-audit jelzőobjektuma érvénytelen");
            op=Boolean(markers,"Operation");
            running=Boolean(markers,"Running");
            mutation=Boolean(markers,"MutationLock");
        }
        if(!op || running || mutation)
            problems.Add("Missing operation marker or active running/mutation lock");
        // Source must still contain exact same bytes after parser finished.
        if(!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).Equals(hash,
            StringComparison.OrdinalIgnoreCase))
            throw new IOException("Vault-audit fájl megváltozott olvasás közben");
        return new(host,cluster,mode,hash,names.ToArray(),
            peers.GetArrayLength(),peersDone,sessions,active,op,running,mutation,
            problems.Count==0,problems.ToArray());
    }

    public static VaultAuditPair Evaluate(VaultAuditSnapshot hp,VaultAuditSnapshot ryzen)
    {
        if(hp.Host!=VaultAuditHost.Hp || ryzen.Host!=VaultAuditHost.Ryzen)
            throw new InvalidDataException("HP and Ryzen audit roles must not be swapped");
        var issues=hp.Issues.Concat(ryzen.Issues).ToList();
        if(hp.ClusterId!=ryzen.ClusterId || hp.ClusterId!=VaultProtocol.ClusterId)
            issues.Add("Host cluster ID differs");
        var maps=hp.MapNames.Concat(ryzen.MapNames).ToArray();
        if(maps.Length!=10 ||
           maps.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=10)
            issues.Add("Cluster audit does not contain ten unique map IDs");
        if(!hp.SnapshotInternallyConsistent||!ryzen.SnapshotInternallyConsistent)
            issues.Add("At least one historical audit is not internally consistent");
        return new(issues.Count==0,maps,issues.ToArray(),
            "Historical audit comparison only. Live Store session/peer liveness, transfer safety, "+
            "backup consistency and complete character-transfer compatibility are NOT proven. No Store writes permitted.");
    }

    private static JsonElement Required(JsonElement e,string property)
        =>e.ValueKind==JsonValueKind.Object && e.TryGetProperty(property,out var v)?
            v:throw new InvalidDataException("Hiányzó Vault-audit mező: "+property);
    private static JsonElement RequiredArray(JsonElement e,string name)
    {
        var x=Required(e,name);
        return x.ValueKind==JsonValueKind.Array?x:
            throw new InvalidDataException("Hibás Vault-audit lista: "+name);
    }
    private static string String(JsonElement e,string name)
    {
        if(e.ValueKind!=JsonValueKind.Object || !e.TryGetProperty(name,out var v) ||
           v.ValueKind!=JsonValueKind.String)
            return "";
        return v.GetString()??"";
    }
    private static bool Boolean(JsonElement e,string name)=>
        e.ValueKind==JsonValueKind.Object &&e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
    private static int Number(JsonElement e,string name)=>
        e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var v)&&
        v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out int n)?n:-1;
}

