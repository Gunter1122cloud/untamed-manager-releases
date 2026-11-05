using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using UntamedManager.Core;

// Synthetic Store-v1 metadata ONLY. No supplied/private archive bytes are
// committed, copied to GitHub or executed; all test data is generated in TEMP.
public static class ClusterVaultTests
{
    private static readonly JsonSerializerOptions Compact=new(){
        Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping,WriteIndented=false};

    private static void Assert(bool ok,string label)
    {
        if(!ok)throw new Exception("INTEGRATION TEST FAILED: "+label);
        Console.WriteLine("PASS v1.6 Vault "+label);
    }

    public static void Run(string root)
    {
        string dir=Path.Combine(root,"v16-vault-zip");
        Directory.CreateDirectory(dir);
        var mapHp=new[]{"Valguero_WP","Astraeos_WP","Extinction_WP","Genesis_WP","TheIsland_WP"};
        var mapRyzen=new[]{"TheCenter_WP","LostColony_WP","Ragnarok_WP","Aberration_WP","ScorchedEarth_WP"};
        string deviceHp=Guid.NewGuid().ToString(),deviceRyzen=Guid.NewGuid().ToString();
        string session=Guid.NewGuid().ToString(),backupId=Guid.NewGuid().ToString();
        string hpBackupId=Guid.NewGuid().ToString();
        string sharedPath=@"\\192.0.2.1\synthetic";
        var idsHp=mapHp.Select(_=>Guid.NewGuid().ToString()).ToArray();
        var idsRyzen=mapRyzen.Select(_=>Guid.NewGuid().ToString()).ToArray();
        var membersHp=mapHp.Select((map,i)=>new {
            id=idsHp[i],map,root=@"D:\synthetic\h"+i}).ToArray();
        var membersRyzen=mapRyzen.Select((map,i)=>new {
            id=idsRyzen[i],map,root=@"C:\synthetic\r"+i}).ToArray();
        var configHp=new {id="KovaCluster",mode="shared",path=sharedPath,
            backupPath=@"D:\synthetic\backup",members=membersHp};
        var configRyzen=new {id="KovaCluster",mode="shared",path=sharedPath,
            backupPath=@"C:\synthetic\backup",members=membersRyzen};
        string ConfigHash<T>(T value)=>Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(value,Compact))).ToLowerInvariant();
        string hHash=ConfigHash(configHp),rHash=ConfigHash(configRyzen);
        var items=new Dictionary<string,string>{
            ["Ryzen-Kova/cluster-v1.json"]=JsonSerializer.Serialize(configRyzen,Compact),
            ["Ryzen-Kova/shared-store-device-v1.json"]=JsonSerializer.Serialize(
                new{schema=1,id=deviceRyzen},Compact),
            ["Ryzen-Kova/shared-store-lock-v1.json"]=JsonSerializer.Serialize(
                new{schema=1,session,config=configRyzen,configHash=rHash,
                    selected=idsRyzen},Compact),
            ["Ryzen-Kova/cluster-store-v1.json"]=JsonSerializer.Serialize(new{
                schema=1,maps=idsRyzen.Select((id,i)=>(id,i)).ToDictionary(x=>x.id,
                    x=>(object)new{root=membersRyzen[x.i].root,map=membersRyzen[x.i].map,
                        phase="active",backupId,sharedSession=session,
                        preparedAt="2026-11-03T18:00:00Z"})},Compact),
            ["SHARED/.kova-store-v1/operation.json"]=JsonSerializer.Serialize(
                new{schema=1,session,cluster="KovaCluster",path=sharedPath,
                    peers=new[]{deviceHp,deviceRyzen}},Compact),
            ["SHARED/.kova-store-v1/"+deviceHp+".json"]=JsonSerializer.Serialize(
                new{session,id=deviceHp,phase="done",config=configHp,configHash=hHash,
                    members=membersHp,selected=idsHp,
                    backup=new{id=hpBackupId}},Compact),
            ["SHARED/.kova-store-v1/"+deviceRyzen+".json"]=JsonSerializer.Serialize(
                new{session,id=deviceRyzen,phase="done",config=configRyzen,configHash=rHash,
                    members=membersRyzen,selected=idsRyzen,
                    backup=new{id=backupId}},Compact),
            ["SHARED/.kova-store-v1/attempted.json"]=JsonSerializer.Serialize(
                new{session},Compact)
        };
        string CreateArchive(string name,Dictionary<string,string> entries)
        {
            string file=Path.Combine(dir,name);
            var metadata=entries.Select(x=>new{
                Source="Synthetic fixture only",Path=x.Key.Replace('/','\\'),
                SHA256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(x.Value)))
            }).ToArray();
            using(var zip=ZipFile.Open(file,ZipArchiveMode.Create))
            {
                void Add(string path,string value)
                {
                    var e=zip.CreateEntry(path.Replace('/','\\'),CompressionLevel.Optimal);
                    using var writer=new StreamWriter(e.Open(),new UTF8Encoding(false));
                    writer.Write(value);
                }
                foreach(var item in entries)Add(item.Key,item.Value);
                Add("manifest.json",JsonSerializer.Serialize(metadata));
            }
            return file;
        }

        // The complementary HP reference is four local metadata JSONs only.
        // Neither ZIP contains live credentials, profiles, transfer contents
        // or real host identifiers; all bytes are synthesized under TEMP.
        var hpLocal=new Dictionary<string,string>{
            ["cluster-v1.json"]=JsonSerializer.Serialize(configHp,Compact),
            ["shared-store-device-v1.json"]=JsonSerializer.Serialize(
                new{schema=1,id=deviceHp},Compact),
            ["shared-store-lock-v1.json"]=JsonSerializer.Serialize(
                new{schema=1,session,config=configHp,configHash=hHash,selected=idsHp},Compact),
            ["cluster-store-v1.json"]=JsonSerializer.Serialize(new{
                schema=1,maps=idsHp.Select((id,i)=>(id,i)).ToDictionary(x=>x.id,
                    x=>(object)new{root=membersHp[x.i].root,map=membersHp[x.i].map,
                        phase="active",backupId=hpBackupId,sharedSession=session,
                        preparedAt="2026-11-03T18:00:00Z"})},Compact)
        };
        string hpCopy=Path.Combine(dir,"Synthetic-HP-Local-v1.zip");
        using(var hpZip=ZipFile.Open(hpCopy,ZipArchiveMode.Create))
            foreach(var file in hpLocal)
            {
                var entry=hpZip.CreateEntry(file.Key,CompressionLevel.Optimal);
                using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false));
                writer.Write(file.Value);
            }
        string original=CreateArchive("Untamed-Cluster-Vault.zip",items);
        string before=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
        var result=ClusterVaultArchiveInspector.Inspect(original);
        Assert(result.VerifiedFiles==8&&result.Maps==10&&result.Peers==2&&
            result.SessionConsistent&&result.ConfigHashesVerified&&
            result.LocalHostMapRecordsActive&&!result.CompleteTwoHostEvidence,
            "ZIP manifest, peer session, ten map IDs and live-ineligible safety flags");
        string originalHpSha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hpCopy)));
        var together=ClusterVaultArchiveInspector.InspectPairedCopies(original,hpCopy);
        Assert(together.VerifiedFiles==12 && together.Peers==2 && together.Maps==10 &&
            together.CompleteTwoHostEvidence && together.ConfigHashesVerified &&
            together.SessionConsistent && together.LocalHostMapRecordsActive &&
            !together.ProductionUseAllowed,
            "pairing genuine-shaped eight-file coordination and four-file HP copy proves only historical metadata, never production ownership");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)))==before &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hpCopy)))==originalHpSha,
            "paired historical ZIP inspection changes neither copied source");
        var invalidHp=Path.Combine(dir,"Synthetic-HP-Incomplete.zip");
        using(var hpMissing=ZipFile.Open(invalidHp,ZipArchiveMode.Create))
            foreach(var file in hpLocal.Where(x=>x.Key!="shared-store-lock-v1.json"))
            {
                var entry=hpMissing.CreateEntry(file.Key,CompressionLevel.Optimal);
                using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false));
                writer.Write(file.Value);
            }
        bool missingLockDenied=false;
        try{ClusterVaultArchiveInspector.InspectPairedCopies(original,invalidHp);}
        catch(InvalidDataException){missingLockDenied=true;}
        Assert(missingLockDenied,
            "incomplete second host local Store metadata cannot become a complete pair");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)))==before,
            "Vault inspection is byte-preserving and does not extract or modify input");
        Assert(result.MapNames.Distinct(StringComparer.OrdinalIgnoreCase).Count()==10,
            "Unique 5+5 map inventory is preserved across both hosts");
        var tampered=new Dictionary<string,string>(items);
        tampered["SHARED/.kova-store-v1/operation.json"]+=" ";
        string badZip=CreateArchive("tampered-consistent.zip",tampered);
        // This archive's *internal* inventory is consistent; session is still
        // parsable. A separate test below corrupts ZIP bytes after creation.
        var originalBytes=File.ReadAllBytes(badZip);
        int off=Array.IndexOf(originalBytes,(byte)'K');
        // A missing required field should always be rejected, regardless of
        // whether the internally provided SHA-256 list is re-created.
        var missing=new Dictionary<string,string>(items);
        missing.Remove("SHARED/.kova-store-v1/attempted.json");
        string missingZip=CreateArchive("missing.json.zip",missing);
        bool missingRefused=false;
        try{missingRefused=!ClusterVaultArchiveInspector.Inspect(missingZip).SessionConsistent;}
        catch(InvalidDataException){missingRefused=true;}
        Assert(missingRefused,"Incomplete Vault ZIP is rejected");
        var malicious=new Dictionary<string,string>(items);
        malicious["SHARED/.kova-store-v1/operation.json"]=
            malicious["SHARED/.kova-store-v1/operation.json"].Replace(
                "KovaCluster","UnknownCluster",StringComparison.Ordinal);
        string wrongSchema=CreateArchive("unknown-contract.zip",malicious);
        bool mismatchRefused=false;
        try{ClusterVaultArchiveInspector.Inspect(wrongSchema);}
        catch(InvalidDataException){mismatchRefused=true;}
        Assert(mismatchRefused,"Unsupported source schema cannot be relabeled as a valid Vault");
        var peerError=new Dictionary<string,string>(items);
        peerError["SHARED/.kova-store-v1/"+deviceRyzen+".json"]=
            peerError["SHARED/.kova-store-v1/"+deviceRyzen+".json"].Replace(
                rHash,new string('0',64),StringComparison.Ordinal);
        string corruptedPeer=CreateArchive("wrong-peer-hash.zip",peerError);
        var warning=ClusterVaultArchiveInspector.Inspect(corruptedPeer);
        Assert(!warning.ConfigHashesVerified&&!warning.SessionConsistent,
            "Peer hash mismatch is reported and not considered transfer-compatible");
    }
}

