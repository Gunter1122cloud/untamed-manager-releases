using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UntamedManager.Core;

// No services, sockets, process launches, RCON, SQL, live paths or API calls.
// Optional private ZIP checks are local only and print no private metadata.
int passed=0;
void Check(bool ok,string label)
{
    if(!ok)throw new Exception("FAIL "+label);
    passed++;Console.WriteLine("PASS "+label);
}
string Sha(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
var options=new JsonSerializerOptions{Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
string Serialize(object value)=>JsonSerializer.Serialize(value,options);
string Fingerprint(string value)
{
    using var doc=JsonDocument.Parse(value);return StoreV1Fingerprint.Compute(doc.RootElement);
}
string shared="SHARED/.kova-store-v1/";
string session=Guid.NewGuid().ToString();
string[] peers={Guid.NewGuid().ToString(),Guid.NewGuid().ToString()};
var original=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
original[shared+"operation.json"]=Serialize(new{schema=1,session,cluster="KovaCluster",
    path=@"\\192.0.2.1\test-only",peers});
original[shared+"attempted.json"]=Serialize(new{session});
for(int host=0;host<2;host++)
{
    var members=Enumerable.Range(0,5).Select(i=>new{id=Guid.NewGuid().ToString(),
        root=@"D:\synthetic\host"+host+"\\map"+i,map="Map_"+host+"_"+i}).ToArray();
    var selected=members.Select(m=>m.id).ToArray();
    var config=new{id="KovaCluster",mode="shared",path=@"\\192.0.2.1\test-only",
        backupPath=@"D:\synthetic\backups",members};
    string hash=Fingerprint(Serialize(config)),backup=Guid.NewGuid().ToString();
    original[shared+peers[host]+".json"]=Serialize(new{session,id=peers[host],phase="done",
        config,configHash=hash,members,selected,backup=new{id=backup}});
    string prefix="HOST"+host+"/";
    original[prefix+"cluster-v1.json"]=Serialize(config);
    original[prefix+"shared-store-device-v1.json"]=Serialize(new{schema=1,id=peers[host]});
    original[prefix+"shared-store-lock-v1.json"]=Serialize(new{schema=1,session,
        config,configHash=hash,selected});
    original[prefix+"cluster-store-v1.json"]=Serialize(new{schema=1,maps=members.ToDictionary(
        m=>m.id,m=>new{m.root,m.map,phase="active",backupId=backup,sharedSession=session})});
}
string root=Path.Combine(Path.GetTempPath(),"Untamed-StoreV1-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Archive(Dictionary<string,string> items,bool directoryLock=false,bool badManifest=false)
{
    string file=Path.Combine(root,Guid.NewGuid()+".zip");
    using var zip=ZipFile.Open(file,ZipArchiveMode.Create);
    foreach(var item in items)
    {
        var entry=zip.CreateEntry(item.Key,CompressionLevel.Optimal);
        using var stream=entry.Open();stream.Write(Encoding.UTF8.GetBytes(item.Value));
    }
    if(directoryLock)zip.CreateEntry(shared+"mutation.lock/");
    var manifest=items.Select(item=>new{Path=item.Key,Bytes=Encoding.UTF8.GetByteCount(item.Value),
        SHA256=badManifest?new string('0',64):Sha(Encoding.UTF8.GetBytes(item.Value))}).ToArray();
    using(var writer=new StreamWriter(zip.CreateEntry("manifest.json").Open(),new UTF8Encoding(false)))
        writer.Write(Serialize(manifest));
    return file;
}
void Edit(Dictionary<string,string> items,string key,Action<JsonNode> change)
{
    var node=JsonNode.Parse(items[key])!;change(node);items[key]=node.ToJsonString(options);
}
void Rehash(Dictionary<string,string> items,string key)
{
    Edit(items,key,node=>node["configHash"]=Fingerprint(node["config"]!.ToJsonString(options)));
}
void Negative(string label,Action<Dictionary<string,string>> change,VaultFindingCode? expected=null)
{
    var copy=new Dictionary<string,string>(original,StringComparer.OrdinalIgnoreCase);change(copy);
    string file=Archive(copy);string before=Sha(File.ReadAllBytes(file));
    bool denied=false;
    try
    {
        var report=ClusterVaultArchiveInspector.Inspect(file);
        denied=!report.HistoricalConsistent&&!report.ProductionUseAllowed&&
            (expected==null||report.Findings.Any(f=>f.Code==expected));
    }
    catch(InvalidDataException){denied=expected==null;}
    Check(denied,label);
    Check(before==Sha(File.ReadAllBytes(file)),label+" preserves source bytes");
}
try
{
    ClusterVaultTests.Run(root);
    ManagerMigrationArchiveTests.Run(root);
    string good=Archive(original),before=Sha(File.ReadAllBytes(good));
    var full=ClusterVaultArchiveInspector.Inspect(good);
    Check(full.HistoricalConsistent&&full.CompleteTwoHostEvidence&&full.Maps==10&&
        full.Hosts.Length==2&&full.Hosts.All(h=>h.LockMatches&&h.ActiveMaps),"complete 5+5 historical metadata");
    Check(!full.ProductionUseAllowed,"historical success never grants production or writer rights");
    Check(before==Sha(File.ReadAllBytes(good)),"full inspection preserves original ZIP");
    string first=shared+peers[0]+".json",second=shared+peers[1]+".json";
    var samePaths=new Dictionary<string,string>(original,StringComparer.OrdinalIgnoreCase);
    var firstPeer=JsonNode.Parse(samePaths[first])!;
    Edit(samePaths,second,n=>{
        for(int i=0;i<5;i++)
        {
            var value=firstPeer["members"]![i]!["root"]!.DeepClone();
            n["members"]![i]!["root"]=value.DeepClone();
            n["config"]!["members"]![i]!["root"]=value.DeepClone();
        }
    });
    Rehash(samePaths,second);
    var updatedPeer=JsonNode.Parse(samePaths[second])!;
    samePaths["HOST1/cluster-v1.json"]=updatedPeer["config"]!.ToJsonString(options);
    Edit(samePaths,"HOST1/shared-store-lock-v1.json",n=>{
        n["config"]=updatedPeer["config"]!.DeepClone();n["configHash"]=updatedPeer["configHash"]!.DeepClone();
    });
    Edit(samePaths,"HOST1/cluster-store-v1.json",n=>{
        foreach(var m in updatedPeer["members"]!.AsArray())
            n["maps"]![m!["id"]!.GetValue<string>()]!["root"]=m["root"]!.DeepClone();
    });
    Check(ClusterVaultArchiveInspector.Inspect(Archive(samePaths)).HistoricalConsistent,
        "identical local path strings on different physical peers are valid");
    Negative("asymmetric one-host package",c=>{
        foreach(string k in c.Keys.Where(k=>k.StartsWith("HOST0/")).ToArray())c.Remove(k);
    },VaultFindingCode.MissingLocalHost);
    Negative("missing peer",c=>c.Remove(first),VaultFindingCode.IncompleteCoordination);
    Negative("missing local lock",c=>c.Remove("HOST0/shared-store-lock-v1.json"),VaultFindingCode.LocalLock);
    Negative("wrong peer session",c=>Edit(c,first,n=>n["session"]=Guid.NewGuid().ToString()),VaultFindingCode.SessionMismatch);
    Negative("wrong lock session",c=>Edit(c,"HOST0/shared-store-lock-v1.json",n=>n["session"]=Guid.NewGuid().ToString()),VaultFindingCode.LocalLock);
    Negative("wrong config hash",c=>Edit(c,first,n=>n["configHash"]=new string('0',64)),VaultFindingCode.ConfigHashMismatch);
    Negative("JSON field reorder changes fingerprint",c=>Edit(c,first,n=>{
        var cfg=n["config"]!.AsObject();var id=cfg["id"]!.DeepClone();cfg.Remove("id");cfg.Add("id",id);
    }),VaultFindingCode.ConfigHashMismatch);
    Negative("missing selected",c=>Edit(c,first,n=>n.AsObject().Remove("selected")));
    Negative("duplicate selected",c=>Edit(c,first,n=>n["selected"]![1]=n["selected"]![0]!.DeepClone()),VaultFindingCode.SelectedMaps);
    Negative("wrong map root",c=>Edit(c,"HOST0/cluster-store-v1.json",n=>
        n["maps"]!.AsObject().First().Value!["root"]=@"D:\other"),VaultFindingCode.LocalStore);
    Negative("Windows root path alias",c=>{
        Edit(c,first,n=>{n["members"]![0]!["root"]=@"D:\synthetic\alias.";
            n["config"]!["members"]![0]!["root"]=@"D:\synthetic\alias.";});Rehash(c,first);
    },VaultFindingCode.MapAssignment);
    Negative("wrong backup identity",c=>Edit(c,"HOST0/cluster-store-v1.json",n=>
        n["maps"]!.AsObject().First().Value!["backupId"]=Guid.NewGuid().ToString()),VaultFindingCode.BackupMismatch);
    Negative("missing peer backup",c=>Edit(c,first,n=>n.AsObject().Remove("backup")));
    Negative("interrupted transfer metadata",c=>Edit(c,"HOST0/cluster-store-v1.json",n=>
        n["maps"]!.AsObject().First().Value!["phase"]="review"),VaultFindingCode.LocalStore);
    Negative("incomplete coordination",c=>Edit(c,first,n=>n["phase"]="backed"),VaultFindingCode.IncompleteCoordination);
    Negative("cancelled operation",c=>c[shared+"cancelled.json"]=Serialize(new{session}),VaultFindingCode.CancelledMarker);
    Negative("running operation",c=>c[shared+"running.json"]=Serialize(new{session}),VaultFindingCode.RunningMarker);
    Negative("file mutation lock",c=>c[shared+"mutation.lock"]="locked",VaultFindingCode.MutationLock);
    var locked=ClusterVaultArchiveInspector.Inspect(Archive(original,directoryLock:true));
    Check(!locked.HistoricalConsistent&&locked.Findings.Any(f=>f.Code==VaultFindingCode.MutationLock),"empty directory mutation lock");
    Negative("missing attempted marker",c=>c.Remove(shared+"attempted.json"),VaultFindingCode.IncompleteCoordination);
    Negative("attempted session mismatch",c=>Edit(c,shared+"attempted.json",n=>n["session"]=Guid.NewGuid().ToString()),VaultFindingCode.SessionMismatch);
    Negative("cross-host duplicate map IDs",c=>{
        var id=JsonNode.Parse(c[first])!["members"]![0]!["id"]!.DeepClone();
        Edit(c,second,n=>{n["members"]![0]!["id"]=id.DeepClone();n["config"]!["members"]![0]!["id"]=id.DeepClone();});
        Rehash(c,second);
    },VaultFindingCode.MapAssignment);
    Negative("duplicate map roots",c=>{
        Edit(c,first,n=>{n["members"]![1]!["root"]=n["members"]![0]!["root"]!.DeepClone();
            n["config"]!["members"]![1]!["root"]=n["members"]![0]!["root"]!.DeepClone();});Rehash(c,first);
    },VaultFindingCode.MapAssignment);
    Negative("duplicate device assignment",c=>c["HOST1/shared-store-device-v1.json"]=c["HOST0/shared-store-device-v1.json"]);
    Negative("traversal member",c=>c["../outside.json"]="{}");
    Negative("normalized case collision",c=>c["host0\\CLUSTER-v1.JSON"]=c["HOST0/cluster-v1.json"]);
    Negative("file directory collision",c=>c["HOST0"]="{}");
    Negative("Windows trailing dot alias",c=>c["HOST0./other.json"]="{}");
    Negative("duplicate JSON key",c=>c[shared+"attempted.json"]="{\"session\":\""+session+"\",\"session\":\""+session+"\"}");
    Negative("malformed JSON",c=>c[shared+"attempted.json"]="{oops");
    Negative("decompression ratio limit",c=>c["oversized-padding.txt"]=new string('x',100_000));
    Negative("entry size limit",c=>c["oversized-padding.txt"]=new string('x',2_000_001));
    Negative("invalid manifest input type",c=>Edit(c,shared+"operation.json",n=>n["schema"]="one"));
    Negative("unsafe display name is redacted",c=>Edit(c,first,n=>n["members"]![0]!["map"]="https://secret.invalid/token"),VaultFindingCode.MapAssignment);
    bool badManifest=false;try{ClusterVaultArchiveInspector.Inspect(Archive(original,badManifest:true));}
    catch(InvalidDataException){badManifest=true;}Check(badManifest,"tampered manifest rejected");
    string linkZip=Archive(original);
    using(var zip=ZipFile.Open(linkZip,ZipArchiveMode.Update))
        zip.GetEntry(shared+"attempted.json")!.ExternalAttributes=unchecked((int)0xA1FF0000);
    bool linked=false;try{ClusterVaultArchiveInspector.Inspect(linkZip);}catch(InvalidDataException){linked=true;}
    Check(linked,"ZIP symbolic link entry rejected");
    string reordered="{\"2\":\"two\",\"1\":\"one\",\"z\":\"end\",\"01\":\"leading\"}";
    string js="{\"1\":\"one\",\"2\":\"two\",\"z\":\"end\",\"01\":\"leading\"}";
    Check(Fingerprint(reordered)==Sha(Encoding.UTF8.GetBytes(js)),"JS array-index property ordering");
    string unicode="{\"id\":\"árvíz 😀\",\"path\":\"\\\\host\\\\share\"}";
    Check(Fingerprint(unicode)==Sha(Encoding.UTF8.GetBytes(unicode)),"UTF-8 and supplementary Unicode parity");
    Check(Fingerprint("{\"a\":\"1\",\"b\":\"2\"}")!=Fingerprint("{\"b\":\"2\",\"a\":\"1\"}"),"ordinary key insertion order preserved");
    bool numbers=false;try{Fingerprint("{\"unknown\":1}");}catch(InvalidDataException){numbers=true;}
    Check(numbers,"unknown numeric config schema fails closed");

    // Create only synthetic copied folders. A repeat read models application
    // restart; no original paths or production data are consulted.
    string coord=Path.Combine(root,"shared"),h0=Path.Combine(root,"host0"),h1=Path.Combine(root,"host1");
    foreach(string dir in new[]{coord,h0,h1})Directory.CreateDirectory(dir);
    // Restore only synthetic metadata from the already verified ZIP into
    // freshly created test folders. No embedded destination is followed.
    var restoreFiles=VaultReadSnapshot.Archive(File.ReadAllBytes(good));
    foreach(var item in restoreFiles.Where(x=>x.Key!="manifest.json"))
    {
        string folder=item.Key.StartsWith(shared)?coord:item.Key.StartsWith("HOST0/")?h0:h1;
        File.WriteAllBytes(Path.Combine(folder,item.Key.Split('/').Last()),item.Value);
    }
    var hashes=Directory.GetFiles(root,"*.json",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Sha(File.ReadAllBytes(p)));
    var copy=VaultCompatibilityReader.Inspect(coord,h0,h1);
    Check(copy.HistoricalConsistent&&copy.Report!.CompleteTwoHostEvidence,"synthetic ZIP metadata test-copy restore uses same full validator");
    Check(VaultCompatibilityReader.Inspect(coord,h1,h0).HistoricalConsistent,"folder order does not override device identity");
    Check(VaultCompatibilityReader.Inspect(coord,h0,h1).HistoricalConsistent,"read after restart preserves recognition");
    Check(hashes.All(x=>Sha(File.ReadAllBytes(x.Key))==x.Value),"all copied metadata source bytes preserved");
    using(var snapshot=new VaultReadSnapshot())
    {
        string p=Path.Combine(coord,"operation.json");snapshot.Read(p,1_048_576);
        bool exclusive=false;try{using var writer=new FileStream(p,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);}
        catch(IOException){exclusive=true;}Check(exclusive,"snapshot excludes concurrent Windows writers");
    }
    File.Delete(Path.Combine(h0,"cluster-store-v1.json"));
    Check(!VaultCompatibilityReader.Inspect(coord,h0,h1).HistoricalConsistent,"lost local metadata after interruption blocks success");
    bool missingShare=false;try{VaultCompatibilityReader.Inspect(Path.Combine(root,"unavailable-copy"),h0,h1);}
    catch(InvalidDataException){missingShare=true;}Check(missingShare,"unavailable copied coordination fails closed");
    bool unc=false;try{ClusterVaultArchiveInspector.Inspect(@"\\192.0.2.1\not-contacted\input.zip");}
    catch(InvalidDataException){unc=true;}Check(unc,"SMB input refused before connection");
    foreach(string file in args)
    {
        string privateBefore=Sha(File.ReadAllBytes(file));
        if(Path.GetFileName(file).Contains("Manager",StringComparison.OrdinalIgnoreCase))
        {
            var manager=UntamedManagerArchiveInspector.Inspect(Path.GetFullPath(file));
            Check(manager.VerifiedFiles==39&&manager.LocalMaps==5&&manager.SharedMaps==10&&
                manager.SharedSessionConsistent&&manager.PeerConfigHashesVerified&&
                !manager.ProductionImportAllowed,"private Manager reference: common Store validation and no import rights");
            Check(privateBefore==Sha(File.ReadAllBytes(file)),"private Manager reference byte preservation");
            continue;
        }
        var report=ClusterVaultArchiveInspector.Inspect(Path.GetFullPath(file));
        Check(report.Maps==10&&report.Peers==2&&report.ConfigHashesVerified&&
            report.SessionConsistent&&report.LocalHostMapRecordsActive&&
            !report.CompleteTwoHostEvidence&&!report.ProductionUseAllowed,
            "private historical reference: partial two-host evidence correctly classified");
        Check(privateBefore==Sha(File.ReadAllBytes(file)),"private reference byte preservation");
    }
    Console.WriteLine("STORE-V1 PASS: "+passed+" assertions; no live-system operations.");
}
finally
{
    // This exact random directory was created above for these tests only.
    Directory.Delete(root,true);
}
