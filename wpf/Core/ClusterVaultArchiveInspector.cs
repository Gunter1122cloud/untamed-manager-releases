using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace UntamedManager.Core;

// Untamed Cluster Vault: import/inspect an existing third-party Store-v1
// ZIP *without* extracting or writing it. Protocol literals below are required
// solely to recognize the original on-disk format. Vault does NOT claim
// ownership of the format or assert transfer compatibility.
public sealed record ClusterVaultArchiveReport(
    int VerifiedFiles,int Maps,int Peers,
    bool SessionConsistent,bool ConfigHashesVerified,
    bool LocalHostMapRecordsActive,bool CompleteTwoHostEvidence,
    string[] MapNames,string[] Warnings);

public static class ClusterVaultArchiveInspector
{
    private const string Operation="SHARED/.kova-store-v1/operation.json";
    private const string SharedPrefix="SHARED/.kova-store-v1/";
    private const string LocalPrefix="Ryzen-Kova/";
    private static readonly JsonSerializerOptions CompactJson=new(){
        Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented=false};

    public static ClusterVaultArchiveReport Inspect(string archivePath)
    {
        if(string.IsNullOrWhiteSpace(archivePath) ||
           !Path.IsPathFullyQualified(archivePath) ||archivePath.StartsWith(@"\\")||
           !archivePath.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cluster Vault requires a copied local ZIP file");
        string full=Path.GetFullPath(archivePath);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Cluster Vault ZIP must be on a local fixed disk");
        for(var d=new DirectoryInfo(Path.GetDirectoryName(full)!);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked Vault input directory rejected");
        var file=new FileInfo(full);
        if(!file.Exists||file.Length is < 100 or > 32_000_000 ||
           (file.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Invalid or oversized Vault archive");

        using var zip=ZipFile.OpenRead(full);
        if(zip.Entries.Count is < 9 or > 64)
            throw new InvalidDataException("Unexpected Vault archive contents");
        var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        long expanded=0;
        foreach(var entry in zip.Entries)
        {
            string key=entry.FullName.Replace('\\','/');
            if(key.StartsWith('/')||key.EndsWith('/')||key.Contains(':')||
               key.Contains("//",StringComparison.Ordinal)||
               key.Split('/').Any(x=>x is "" or "." or "..")||
               entry.Length is < 2 or > 2_000_000 ||
               entry.CompressedLength==0 ||
               entry.Length>entry.CompressedLength*200 ||
               ((entry.ExternalAttributes>>16)&0xF000)==0xA000)
                throw new InvalidDataException("Unsafe Vault archive entry");
            expanded=checked(expanded+entry.Length);
            if(expanded>16_000_000)
                throw new InvalidDataException("Vault archive decompression limit exceeded");
            using var stream=entry.Open();
            using var buffer=new MemoryStream();
            stream.CopyTo(buffer);
            if(buffer.Length!=entry.Length||!files.TryAdd(key,buffer.ToArray()))
                throw new InvalidDataException("Duplicate or incomplete Vault ZIP member");
        }

        using var inventory=Document(files,"manifest.json");
        if(inventory.RootElement.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("Vault inventory must be an array");
        var members=inventory.RootElement.EnumerateArray().ToArray();
        if(members.Length!=files.Count-1 || members.Length!=8)
            throw new InvalidDataException("Vault manifest must cover eight metadata files");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in members)
        {
            string path=Field(item,"Path").Replace('\\','/');
            string hash=Field(item,"SHA256");
            if(path=="manifest.json"||!seen.Add(path)||
               !files.TryGetValue(path,out var bytes)||
               hash.Length!=64||hash.Any(c=>!Uri.IsHexDigit(c))||
               !Convert.ToHexString(SHA256.HashData(bytes))
                   .Equals(hash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Vault archive SHA-256 inventory mismatch");
        }

        using var operation=Document(files,Operation);
        var op=operation.RootElement;
        string session=Field(op,"session");
        if(Number(op,"schema")!=1 ||!Guid.TryParse(session,out var sessionGuid) ||
           Field(op,"cluster")!="KovaCluster")
            throw new InvalidDataException("Unsupported stored cluster coordination schema");
        string sharePath=Field(op,"path");
        if(string.IsNullOrWhiteSpace(sharePath))
            throw new InvalidDataException("Missing shared coordination root");
        var peers=Sequence(Required(op,"peers"));
        if(peers.Length!=2 ||peers.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=2||
           peers.Any(x=>!Guid.TryParse(x,out _)))
            throw new InvalidDataException("Exactly two Vault peers required");

        var warnings=new List<string>();
        var mapNames=new List<string>();
        var peerDocs=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
        bool peerPhases=true,configHashes=true;
        foreach(string peerId in peers)
        {
            using var source=Document(files,SharedPrefix+peerId+".json");
            var p=source.RootElement;
            if(!Guid.TryParse(Field(p,"session"),out var peerSession)||
               peerSession!=sessionGuid ||
               !Guid.TryParse(Field(p,"id"),out var ownId)||
               ownId!=Guid.Parse(peerId))
                warnings.Add("A peer munkamenete vagy azonosítója nem egyezik");
            if(Field(p,"phase")!="done")peerPhases=false;
            var config=Required(p,"config");
            if(Field(config,"id")!="KovaCluster"||
               Field(config,"mode")!="shared"||
               !string.Equals(Field(config,"path"),sharePath,
                   StringComparison.OrdinalIgnoreCase))
                warnings.Add("Eltérő peer-konfiguráció");
            string calculated=Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(config,CompactJson)));
            if(!calculated.Equals(Field(p,"configHash"),StringComparison.OrdinalIgnoreCase))
                configHashes=false;
            var fromConfig=Required(config,"members").EnumerateArray().ToArray();
            var fromPeer=Required(p,"members").EnumerateArray().ToArray();
            if(fromConfig.Length!=5 ||fromPeer.Length!=5 ||
               !JsonSerializer.Serialize(fromConfig,CompactJson)
                   .Equals(JsonSerializer.Serialize(fromPeer,CompactJson),
                       StringComparison.Ordinal))
                warnings.Add("Hiányzó vagy eltérő peer-maplista");
            var selected=Sequence(Required(p,"selected"));
            var ids=fromPeer.Select(m=>Field(m,"id")).ToArray();
            if(selected.Length!=5||selected.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=5||
               !selected.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(ids))
                warnings.Add("A kiválasztott mapok és a peer-tagok nem egyeznek");
            mapNames.AddRange(fromPeer.Select(m=>Field(m,"map")));
            peerDocs.Add(peerId,p.Clone());
        }
        if(!peerPhases)warnings.Add("Nem minden peer lezárt állapotú");
        if(!configHashes)warnings.Add("A konfigurációk SHA-256 lenyomata eltér");
        if(mapNames.Count!=10 ||
           mapNames.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=10 ||
           mapNames.Any(string.IsNullOrWhiteSpace))
            warnings.Add("A 10 map kiosztása hiányos vagy ismétlődik");

        using var device=Document(files,LocalPrefix+"shared-store-device-v1.json");
        using var locked=Document(files,LocalPrefix+"shared-store-lock-v1.json");
        using var clusterConfig=Document(files,LocalPrefix+"cluster-v1.json");
        using var localStore=Document(files,LocalPrefix+"cluster-store-v1.json");
        string localId=Field(device.RootElement,"id");
        bool localValid=Number(device.RootElement,"schema")==1 &&
            peerDocs.TryGetValue(localId,out var localPeer) &&
            Number(locked.RootElement,"schema")==1 &&
            Guid.TryParse(Field(locked.RootElement,"session"),out var lockSession) &&
            lockSession==sessionGuid;
        if(!localValid)warnings.Add("A helyi Vault peer/zár munkamenete nem egyezik");
        bool localActive=true;
        if(localValid)
        {
            var peer=localPeer;
            if(!Field(locked.RootElement,"configHash")
                   .Equals(Field(peer,"configHash"),StringComparison.OrdinalIgnoreCase) ||
               !JsonSerializer.Serialize(Required(locked.RootElement,"config"),CompactJson)
                   .Equals(JsonSerializer.Serialize(Required(peer,"config"),CompactJson),
                       StringComparison.Ordinal)||
               !JsonSerializer.Serialize(clusterConfig.RootElement,CompactJson)
                   .Equals(JsonSerializer.Serialize(Required(peer,"config"),CompactJson),
                       StringComparison.Ordinal)||
               !Sequence(Required(locked.RootElement,"selected"))
                   .SequenceEqual(Sequence(Required(peer,"selected")),
                       StringComparer.OrdinalIgnoreCase))
                warnings.Add("A helyi konfiguráció és a peer-zár eltér");
            var mapObject=Required(localStore.RootElement,"maps");
            var localMembers=Required(peer,"members").EnumerateArray().ToArray();
            if(Number(localStore.RootElement,"schema")!=1 ||
               mapObject.ValueKind!=JsonValueKind.Object ||
               mapObject.EnumerateObject().Count()!=5)
                localActive=false;
            else
            {
                string backupId=Field(Required(peer,"backup"),"id");
                foreach(var m in mapObject.EnumerateObject())
                {
                    var candidate=localMembers.FirstOrDefault(x=>Field(x,"id")==m.Name);
                    if(candidate.ValueKind==JsonValueKind.Undefined||
                       Field(m.Value,"phase")!="active"||
                       !Guid.TryParse(Field(m.Value,"sharedSession"),out var mapSession)||
                       mapSession!=sessionGuid ||
                       Field(m.Value,"backupId")!=backupId||
                       Field(m.Value,"map")!=Field(candidate,"map")||
                       !Field(m.Value,"root").Equals(Field(candidate,"root"),
                           StringComparison.OrdinalIgnoreCase))
                        localActive=false;
                }
            }
        }
        else localActive=false;
        if(!localActive)warnings.Add("A helyi mapok mentett Vault-állapota nem aktív");
        using var attempted=Document(files,SharedPrefix+"attempted.json");
        if(!Guid.TryParse(Field(attempted.RootElement,"session"),out var attemptSession)||
           attemptSession!=sessionGuid)
            warnings.Add("A mentett próbálkozás munkamenete eltér");

        // This package does not contain the other host's four local records.
        // Do not infer their absence on the real host from a selected archive.
        warnings.Add("A másik host helyi metaadatai és az élő állapot nem igazoltak");
        return new(members.Length,mapNames.Count,peers.Length,
            warnings.Count==1 && peerPhases,configHashes,localActive,false,
            mapNames.ToArray(),warnings.ToArray());
    }

    private static JsonDocument Document(Dictionary<string,byte[]> files,string name)
        =>files.TryGetValue(name,out var data)?
            JsonDocument.Parse(data,new JsonDocumentOptions{MaxDepth=48}):
            throw new InvalidDataException("Hiányzó Vault-metadata fájl");
    private static JsonElement Required(JsonElement element,string name)
        =>element.ValueKind==JsonValueKind.Object&&
          element.TryGetProperty(name,out var v)?v:
          throw new InvalidDataException("A Vault-metadata mezője hiányzik");
    private static string Field(JsonElement element,string key)
        =>element.ValueKind==JsonValueKind.Object&&
          element.TryGetProperty(key,out var v)&&
          v.ValueKind==JsonValueKind.String ? v.GetString()??"" : "";
    private static int Number(JsonElement element,string key)
        =>element.ValueKind==JsonValueKind.Object&&
          element.TryGetProperty(key,out var v)&&
          v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var n)?n:-1;
    private static string[] Sequence(JsonElement e)
        =>e.ValueKind==JsonValueKind.Array?
          e.EnumerateArray().Select(x=>x.ValueKind==JsonValueKind.String?
              x.GetString()??"":"").ToArray():
          throw new InvalidDataException("A Vault-metaadat listája hiányzik");
}
