using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UntamedManager.Core;

public enum VaultFindingCode
{
    MissingLocalHost, SessionMismatch, ConfigMismatch, ConfigHashMismatch,
    MapAssignment, SelectedMaps, LocalLock, LocalStore, BackupMismatch,
    IncompleteCoordination, RunningMarker, CancelledMarker, MutationLock
}
public sealed record VaultFinding(VaultFindingCode Code,string Message);
public sealed record VaultHostStatus(int Number,int Maps,bool LocalEvidence,
    bool SessionMatches,bool ConfigHashMatches,bool LockMatches,bool ActiveMaps);
public sealed record ClusterVaultArchiveReport(
    int VerifiedFiles,int Maps,int Peers,
    bool SessionConsistent,bool ConfigHashesVerified,
    bool LocalHostMapRecordsActive,bool CompleteTwoHostEvidence,
    string[] MapNames,string[] Warnings)
{
    public VaultFinding[] Findings {get;init;}=System.Array.Empty<VaultFinding>();
    public VaultHostStatus[] Hosts {get;init;}=System.Array.Empty<VaultHostStatus>();
    public bool HistoricalConsistent {get;init;}
    public bool ProductionUseAllowed=>false;
}

public static class ClusterVaultArchiveInspector
{
    internal const string SharedPrefix=VaultProtocol.ArchiveCoordinationPrefix;
    private static readonly string[] LocalNames={
        "cluster-v1.json","cluster-store-v1.json",
        "shared-store-device-v1.json","shared-store-lock-v1.json"};
    public static ClusterVaultArchiveReport Inspect(string archivePath)
    {
        if(string.IsNullOrWhiteSpace(archivePath)||!archivePath.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VAULT_INPUT: helyi ZIP-másolat szükséges");
        using var snapshot=new VaultReadSnapshot();
        var bytes=snapshot.Read(archivePath,32_000_000);
        return InspectFiles(VaultReadSnapshot.Archive(bytes));
    }

    public static ClusterVaultArchiveReport InspectPairedCopies(string coordinationArchive,string localMetadataArchive)
    {
        if(string.IsNullOrWhiteSpace(coordinationArchive)||string.IsNullOrWhiteSpace(localMetadataArchive)||
           !coordinationArchive.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)||
           !localMetadataArchive.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VAULT_INPUT: két helyi ZIP-másolat szükséges");
        using var snapshot=new VaultReadSnapshot();
        var files=VaultReadSnapshot.Archive(snapshot.Read(coordinationArchive,32_000_000));
        var local=VaultReadSnapshot.LocalMetadataArchive(snapshot.Read(localMetadataArchive,4_500_000));
        foreach(var entry in local)
            if(!files.TryAdd("SUPPLIED-LOCAL/"+entry.Key,entry.Value))
                throw new InvalidDataException("VAULT_LOCAL_ARCHIVE: ütköző helyi metadata");
        // Never writes/re-packs either original ZIP. Device identity, not this
        // internal prefix, determines the peer association in the one validator.
        return InspectFiles(files);
    }

    // One validator for ZIP snapshots and explicitly copied local folders.
    // Metadata paths remain strings: never open an embedded root or share.
    internal static ClusterVaultArchiveReport InspectFiles(
        IReadOnlyDictionary<string,byte[]> files)
    {
        var findings=new List<VaultFinding>();
        void Issue(VaultFindingCode code,string message)
        {
            if(!findings.Any(x=>x.Code==code&&x.Message==message))findings.Add(new(code,message));
        }
        using var operation=VaultReadSnapshot.Json(files,SharedPrefix+"operation.json");
        var op=operation.RootElement;
        string session=Text(op,"session"),sharedPath=Text(op,"path");
        if(!Schema(op)||!Guid.TryParse(session,out var sessionId)||
           Text(op,"cluster")!=VaultProtocol.ClusterId||!sharedPath.StartsWith(@"\\")||
           sharedPath.Split('\\',StringSplitOptions.RemoveEmptyEntries).Length<2)
            throw new InvalidDataException("VAULT_SCHEMA: ismeretlen koordináció");
        var peerIds=Strings(Required(op,"peers"));
        if(peerIds.Length!=2||peerIds.Any(x=>!Guid.TryParse(x,out _))||
           peerIds.Select(Guid.Parse).Distinct().Count()!=2)
            throw new InvalidDataException("VAULT_PEERS: két különböző peer szükséges");
        var locals=new Dictionary<Guid,string>();
        foreach(string key in files.Keys.Where(x=>x.EndsWith("/shared-store-device-v1.json",StringComparison.OrdinalIgnoreCase)))
        {
            using var d=VaultReadSnapshot.Json(files,key);
            if(!Schema(d.RootElement)||!Guid.TryParse(Text(d.RootElement,"id"),out var id)||
               !peerIds.Select(Guid.Parse).Contains(id)||!locals.TryAdd(id,key[..^"shared-store-device-v1.json".Length]))
                throw new InvalidDataException("VAULT_DEVICE: ismételt vagy idegen host");
        }
        var allIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allRoots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allMaps=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names=new List<string>();
        var hosts=new List<VaultHostStatus>();
        bool sessions=true,hashes=true,active=true;
        for(int index=0;index<peerIds.Length;index++)
        {
            string peerId=peerIds[index];
            string peerKey=SharedPrefix+peerId+".json";
            if(!files.ContainsKey(peerKey))
            {
                Issue(VaultFindingCode.IncompleteCoordination,"Hiányzik az egyik peer másolata.");
                sessions=false;hashes=false;active=false;
                hosts.Add(new(index+1,0,false,false,false,false,false));continue;
            }
            using var peerDoc=VaultReadSnapshot.Json(files,peerKey);
            var peer=peerDoc.RootElement;
            bool sessionOk=SameGuid(Text(peer,"session"),sessionId)&&SameGuid(Text(peer,"id"),Guid.Parse(peerId));
            if(!sessionOk)Issue(VaultFindingCode.SessionMismatch,"Eltérő peer-azonosító vagy munkamenet.");
            if(Text(peer,"phase")!="done")
                Issue(VaultFindingCode.IncompleteCoordination,"A peer művelete nincs lezárva.");
            var config=Required(peer,"config");
            bool hashOk=StoreV1Fingerprint.Matches(config,Text(peer,"configHash"));
            if(!hashOk)Issue(VaultFindingCode.ConfigHashMismatch,"A peer konfigurációlenyomata eltér.");
            if(Text(config,"id")!=VaultProtocol.ClusterId||Text(config,"mode")!="shared"||
               !Equal(Text(config,"path"),sharedPath))
                Issue(VaultFindingCode.ConfigMismatch,"Eltérő cluster, mód vagy közös útvonal.");
            var members=Elements(Required(peer,"members"));
            var configMembers=Required(config,"members");
            if(configMembers.ValueKind!=JsonValueKind.Array||
               !OrderedEqual(configMembers,Required(peer,"members")))
                Issue(VaultFindingCode.MapAssignment,"Eltérő beágyazott mapkiosztás.");
            var memberIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var member in members)
            {
                string id=Text(member,"id"),map=Text(member,"map"),root=Text(member,"root");
                if(!Guid.TryParseExact(id,"D",out _)||!memberIds.Add(id)||!allIds.Add(id)||
                   !SafeMap(map)||!allMaps.Add(map)||!LocalRoot(root)||!allRoots.Add(peerId+":"+NormalRoot(root)))
                    Issue(VaultFindingCode.MapAssignment,"Hiányos, ismételt vagy hibás map/azonosító/gyökér.");
                names.Add(SafeMap(map)?map:"Ismeretlen map");
            }
            if(members.Length!=5)Issue(VaultFindingCode.MapAssignment,"Hostonként pontosan öt map szükséges.");
            string[] selected=Strings(Required(peer,"selected"));
            if(selected.Length!=5||selected.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=5||
               !memberIds.SetEquals(selected))
                Issue(VaultFindingCode.SelectedMaps,"A kiválasztás nem fedi le az öt saját mapot.");
            string backup=Text(Required(peer,"backup"),"id");
            if(!Guid.TryParse(backup,out _))
                Issue(VaultFindingCode.BackupMismatch,"Hiányzik az érvényes peer-mentésazonosító.");
            bool local=locals.TryGetValue(Guid.Parse(peerId),out var prefix);
            bool lockOk=false,localActive=false;
            if(!local)Issue(VaultFindingCode.MissingLocalHost,"Az egyik host négy helyi metadatafájlja hiányzik.");
            else if(LocalNames.Any(n=>!files.ContainsKey(prefix+n)))
                Issue(VaultFindingCode.LocalLock,"Hiányos helyi konfiguráció, Store vagy zárolás.");
            else
            {
                using var lockDoc=VaultReadSnapshot.Json(files,prefix+"shared-store-lock-v1.json");
                using var configDoc=VaultReadSnapshot.Json(files,prefix+"cluster-v1.json");
                using var storeDoc=VaultReadSnapshot.Json(files,prefix+"cluster-store-v1.json");
                var locked=lockDoc.RootElement;
                bool localSession=SameGuid(Text(locked,"session"),sessionId);
                sessionOk &= localSession;
                lockOk=Schema(locked)&&localSession&&
                    Text(locked,"configHash").Equals(Text(peer,"configHash"),StringComparison.OrdinalIgnoreCase)&&
                    Strings(Required(locked,"selected")).SequenceEqual(selected,StringComparer.OrdinalIgnoreCase)&&
                    OrderedEqual(Required(locked,"config"),config)&&OrderedEqual(configDoc.RootElement,config);
                bool localHash=StoreV1Fingerprint.Matches(Required(locked,"config"),Text(locked,"configHash"))&&
                    StoreV1Fingerprint.Matches(configDoc.RootElement,Text(peer,"configHash"));
                hashOk &= localHash;
                if(!localHash)Issue(VaultFindingCode.ConfigHashMismatch,"A helyi konfigurációlenyomat eltér.");
                if(!lockOk)Issue(VaultFindingCode.LocalLock,"A helyi konfiguráció, kiválasztás vagy zár eltér.");
                var store=storeDoc.RootElement;
                var mapRecords=Required(store,"maps");
                localActive=Schema(store)&&mapRecords.ValueKind==JsonValueKind.Object&&
                    mapRecords.EnumerateObject().Count()==5;
                if(mapRecords.ValueKind==JsonValueKind.Object)
                foreach(var record in mapRecords.EnumerateObject())
                {
                    var member=members.FirstOrDefault(m=>Equal(Text(m,"id"),record.Name));
                    bool recordOk=member.ValueKind!=JsonValueKind.Undefined&&
                        Text(record.Value,"phase")=="active"&&
                        SameGuid(Text(record.Value,"sharedSession"),sessionId)&&
                        Text(record.Value,"backupId")==backup&&Text(record.Value,"map")==Text(member,"map")&&
                        Equal(Text(record.Value,"root"),Text(member,"root"));
                    localActive &= recordOk;
                    if(Text(record.Value,"backupId")!=backup)
                        Issue(VaultFindingCode.BackupMismatch,"A helyi Store és a peer mentésazonosítója eltér.");
                }
                if(!localActive)Issue(VaultFindingCode.LocalStore,"A helyi Store-mapok nem igazolt ACTIVE állapotúak.");
            }
            sessions &= sessionOk;hashes &= hashOk;
            // Active records describe supplied hosts only. Missing host evidence
            // is separately and unconditionally excluded from full consistency.
            if(local)active &= localActive;
            hosts.Add(new(index+1,members.Length,local,sessionOk,hashOk,lockOk,localActive));
        }
        if(allIds.Count!=10||allMaps.Count!=10)
            Issue(VaultFindingCode.MapAssignment,"Nem igazolt a tíz különböző map.");
        string attempted=SharedPrefix+"attempted.json";
        if(!files.ContainsKey(attempted))
            Issue(VaultFindingCode.IncompleteCoordination,"Hiányzik a lezárt művelet próbálkozási jelzője.");
        else
        {
            using var attempt=VaultReadSnapshot.Json(files,attempted);
            if(!SameGuid(Text(attempt.RootElement,"session"),sessionId))
            {sessions=false;Issue(VaultFindingCode.SessionMismatch,"Eltérő próbálkozási munkamenet.");}
        }
        foreach(var marker in new[]{
            ("running.json",VaultFindingCode.RunningMarker,"Futó vagy félbeszakadt Store-művelet jelzője."),
            ("cancelled.json",VaultFindingCode.CancelledMarker,"Megszakított Store-művelet jelzője."),
            ("mutation.lock",VaultFindingCode.MutationLock,"Store-módosítási zár található.")})
            if(files.Keys.Any(k=>k.Equals(SharedPrefix+marker.Item1,StringComparison.OrdinalIgnoreCase)||
                k.StartsWith(SharedPrefix+marker.Item1+"/",StringComparison.OrdinalIgnoreCase)))
                Issue(marker.Item2,marker.Item3);
        bool complete=hosts.Count==2&&hosts.All(h=>h.LocalEvidence)&&
            !findings.Any(f=>f.Code==VaultFindingCode.LocalLock);
        bool historical=complete&&findings.Count==0;
        // Partial historical evidence may have internally consistent sessions,
        // but never two-host or production acceptance.
        bool consistent=sessions&&!findings.Any(f=>f.Code!=VaultFindingCode.MissingLocalHost);
        var warnings=findings.Select(f=>f.Message).Append(
            "TÖRTÉNETI MÁSOLAT: élő állapot, karaktertranszfer és kizárólagos írójog nem igazolt.").ToArray();
        return new(files.Count(x=>x.Value.Length>0&&x.Key!="manifest.json"),names.Count,
            hosts.Count,consistent,hashes,active&&locals.Count>0,complete,names.ToArray(),warnings)
        {Findings=findings.ToArray(),Hosts=hosts.ToArray(),HistoricalConsistent=historical};
    }
    private static JsonElement Required(JsonElement e,string name)=>
        e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var value)?value:
            throw new InvalidDataException("VAULT_SCHEMA: kötelező szerkezeti mező hiányzik");
    private static JsonElement[] Elements(JsonElement e)=>e.ValueKind==JsonValueKind.Array?
        e.EnumerateArray().ToArray():throw new InvalidDataException("VAULT_SCHEMA: hibás lista");
    private static string[] Strings(JsonElement e)=>Elements(e).Select(x=>
        x.ValueKind==JsonValueKind.String?x.GetString()??"":"").ToArray();
    private static string Text(JsonElement e,string key)=>VaultReadSnapshot.Text(e,key);
    private static bool Schema(JsonElement e)=>e.ValueKind==JsonValueKind.Object&&
        e.TryGetProperty("schema",out var s)&&s.ValueKind==JsonValueKind.Number&&s.TryGetInt32(out int n)&&n==1;
    private static bool SameGuid(string text,Guid id)=>Guid.TryParse(text,out var parsed)&&parsed==id;
    private static bool Equal(string a,string b)=>a.Equals(b,StringComparison.OrdinalIgnoreCase);
    private static bool OrderedEqual(JsonElement a,JsonElement b)=>
        StoreV1Fingerprint.Compute(Wrap(a))==StoreV1Fingerprint.Compute(Wrap(b));
    private static JsonElement Wrap(JsonElement e)=>JsonSerializer.SerializeToElement(new{value=e});
    private static bool SafeMap(string map)=>map.Length is >0 and <=48&&
        map.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-' or '.' or ' ');
    private static bool LocalRoot(string root)=>root.Length>3&&char.IsAsciiLetter(root[0])&&
        root[1]==':'&&(root[2]=='\\'||root[2]=='/')&&
        !root[3..].Replace('\\','/').TrimEnd('/').Split('/').Any(s=>
            s.Length==0||s is "." or ".."||s.EndsWith('.')||s.EndsWith(' '))&&
        !root.Skip(2).Any(c=>c<32||c is ':' or '<' or '>' or '"' or '|' or '?' or '*');
    private static string NormalRoot(string root)=>root.Replace('/','\\').TrimEnd('\\');
}

