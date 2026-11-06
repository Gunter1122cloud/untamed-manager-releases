using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Operator-driven, read-only two-host character travel evidence for the
/// existing five-plus-five Untamed cluster. Never reads the player data into
/// the renderer, opens Kova's Store writer, or creates/modifies ASA save files.
/// The result proves only hashes/placement across four quiescent snapshots.
/// </summary>
public sealed class DesktopStoreTravelEvidenceBridge
{
    private const int Limit = 350_000;
    private const long MaxFile = 100L * 1024 * 1024;
    private const long MaxTotalBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim serial=new(1,1);
    internal sealed record Proof(string Id,string Kind,long Bytes,string Sha256);
    internal sealed record MapProof(Guid MapId,string Name,bool ConfirmedStopped,
        bool SaveDirectoryFound,bool AmbiguousLayout,int Profiles,int Tribes,
        bool ZeroByteDetected,bool Unstable,Proof[] Files,
        bool StoreMode,bool WorldPresent,long WorldBytes,bool WorldUnstable);
    internal sealed record Capture(int Schema,string HostId,DateTimeOffset Utc,
        string Nonce,MapProof[] Maps);
    public DesktopStoreTravelEvidenceBridge(ClusterService service)=>
        cluster=service??throw new ArgumentNullException(nameof(service));

    private static string MapKey(LocalMap map)=>AsaSaveLayoutResolver.MapToken(map);
    private static string SafeRoot(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
           path.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-TRAVEL-LOCAL-FIXED-DISK-ONLY");
        string root=Path.GetFullPath(path);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-TRAVEL-LOCAL-FIXED-DISK-ONLY");
        for(var directory=new DirectoryInfo(root);directory!=null;directory=directory.Parent)
            if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-TRAVEL-SYMLINK-DENIED");
        return root;
    }
    private static string Hex(byte[] bytes)=>Convert.ToHexString(bytes).ToLowerInvariant();
    private static string Identity(string stem)=>Hex(SHA256.HashData(Encoding.UTF8.GetBytes(
        stem.ToUpperInvariant())));
    private MapProof Scan(LocalMap map)
    {
        var layout=AsaSaveLayoutResolver.Resolve(map);
        string folder=SafeRoot(layout.WorldDirectory);
        bool missing=!Directory.Exists(folder);
        // Reject conflicting per-map save roots even if both maps have valid
        // map tokens. Never silently scan another map's flat SavedArks root.
        bool ambiguous=cluster.Config.LocalMaps.Any(other=>other.Id!=map.Id&&
            !string.IsNullOrWhiteSpace(other.InstallationPath)&&
            string.Equals(AsaSaveLayoutResolver.Resolve(other).WorldDirectory,
                folder,StringComparison.OrdinalIgnoreCase));
        var before=cluster.ObserveLocalProcessesReadOnly(map);
        bool quiescent=!before.Running&&before.ConfirmedStopped&&
            before.Identity==null&&before.LoaderIdentity==null;
        var proofs=new List<Proof>();
        bool zero=false,unstable=false;
        // The official ASA 57.12 -usestore format embeds player and tribe
        // records inside the world .ark. Loose profiles may be stale or empty.
        // We inspect world existence/size/stability WITHOUT hashing the huge
        // world (the manager must remain low I/O on live hardware).
        bool storeMode=KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map);
        bool worldPresent=false,worldUnstable=false;
        long worldBytes=0;
        string world=layout.WorldFile;
        long total=0;
        if(!missing&&!ambiguous)
        {
            foreach(string file in Directory.EnumerateFiles(folder,"*",SearchOption.TopDirectoryOnly)
                .Where(file=>file.EndsWith(".arkprofile",StringComparison.OrdinalIgnoreCase)||
                    file.EndsWith(".arktribe",StringComparison.OrdinalIgnoreCase))
                .OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))
            {
                if(proofs.Count>=1500)throw new InvalidDataException("UM-TRAVEL-TOO-MANY-FILES");
                var attributes=File.GetAttributes(file);
                if((attributes&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("UM-TRAVEL-SYMLINK-DENIED");
                var start=new FileInfo(file);
                string extension=Path.GetExtension(file).ToLowerInvariant();
                string kind=extension==".arkprofile"?"profile":"tribe";
                if(start.Length==0)zero=true;
                if(start.Length>MaxFile||total+start.Length>MaxTotalBytes)
                    throw new InvalidDataException("UM-TRAVEL-SNAPSHOT-TOO-LARGE");
                total+=start.Length;
                string sha="";
                using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,
                    FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.SequentialScan))
                    sha=Hex(SHA256.HashData(stream));
                var end=new FileInfo(file);
                if(start.Length!=end.Length||start.LastWriteTimeUtc!=end.LastWriteTimeUtc)
                    unstable=true;
                proofs.Add(new(Identity(Path.GetFileNameWithoutExtension(file)),kind,start.Length,sha));
            }
        }
        if(storeMode&&!missing&&!ambiguous && File.Exists(world))
        {
            if((File.GetAttributes(world)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-TRAVEL-WORLD-SYMLINK");
            var first=new FileInfo(world);
            worldBytes=first.Length;
            worldPresent=worldBytes>0;
            var touched=first.LastWriteTimeUtc;
            var second=new FileInfo(world);
            worldUnstable=first.Length!=second.Length||touched!=second.LastWriteTimeUtc;
        }
        var after=cluster.ObserveLocalProcessesReadOnly(map);
        if(quiescent&&(after.Running||!after.ConfirmedStopped||
            after.Identity!=null||after.LoaderIdentity!=null))unstable=true;
        if(worldUnstable)unstable=true;
        return new(map.Id,map.Name,quiescent&&!unstable,!missing,ambiguous,
            proofs.Count(p=>p.Kind=="profile"),proofs.Count(p=>p.Kind=="tribe"),
            zero,unstable,proofs.ToArray(),storeMode,worldPresent,
            worldBytes,worldUnstable);
    }
    private Capture CaptureLocal()
    {
        var maps=cluster.Config.LocalMaps;
        if(maps.Count!=5||maps.Select(m=>m.Id).Distinct().Count()!=5||
           maps.Any(m=>m.Id==Guid.Empty))
            throw new InvalidOperationException("UM-TRAVEL-REQUIRES-FIVE-OWNED-MAPS");
        if(!Guid.TryParse(cluster.Config.LocalHostId,out var host)||host==Guid.Empty)
            throw new InvalidDataException("UM-TRAVEL-HOST-ID-INVALID");
        return new(2,host.ToString("D"),DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"),maps.Select(Scan).ToArray());
    }
    private static Capture Read(string json)
    {
        if(json.Length is <20 or >Limit)throw new InvalidDataException("UM-TRAVEL-INVALID-EVIDENCE");
        var snapshot=JsonSerializer.Deserialize<Capture>(json,Json)??
            throw new InvalidDataException("UM-TRAVEL-INVALID-EVIDENCE");
        if(snapshot.Schema!=2||!Guid.TryParse(snapshot.HostId,out var host)||host==Guid.Empty||
           !Guid.TryParseExact(snapshot.Nonce,"N",out var nonce)||nonce==Guid.Empty||
           snapshot.Maps?.Length!=5||snapshot.Maps.Select(m=>m.MapId).Distinct().Count()!=5||
           snapshot.Maps.Any(m=>m.MapId==Guid.Empty||m.Files==null||
               m.Files.Length>1500||m.WorldBytes<0||
               (m.StoreMode&&m.WorldPresent!=(m.WorldBytes>0))||
               m.Files.Any(p=>p.Bytes<0||
                   p.Kind is not ("profile" or "tribe")||
                   p.Id.Length!=64||p.Sha256.Length!=64||
                   !p.Id.All(Uri.IsHexDigit)||!p.Sha256.All(Uri.IsHexDigit)))||
           snapshot.Utc>DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("UM-TRAVEL-INVALID-EVIDENCE");
        return snapshot;
    }
    private static object Compare(string beforeHp,string beforeRyzen,
        string afterHp,string afterRyzen,string sourceMap,string destinationMap,string profileId)
    {
        var a=Read(beforeHp);var b=Read(beforeRyzen);
        var c=Read(afterHp);var d=Read(afterRyzen);
        if(a.HostId==b.HostId||a.HostId!=c.HostId||b.HostId!=d.HostId ||
           a.Nonce==c.Nonce||b.Nonce==d.Nonce||
           a.Utc>=c.Utc||b.Utc>=d.Utc||
           !a.Maps.Select(x=>x.MapId).OrderBy(x=>x).SequenceEqual(
               c.Maps.Select(x=>x.MapId).OrderBy(x=>x))||
           !b.Maps.Select(x=>x.MapId).OrderBy(x=>x).SequenceEqual(
               d.Maps.Select(x=>x.MapId).OrderBy(x=>x)))
            throw new InvalidDataException("UM-TRAVEL-IDENTITY-OR-EPOCH-MISMATCH");
        var names=a.Maps.Concat(b.Maps).Select(x=>x.MapId).ToArray();
        var before=a.Maps.Concat(b.Maps).ToArray();
        var after=c.Maps.Concat(d.Maps).ToArray();
        bool storeInvolved=before.Concat(after).Any(m=>m.StoreMode);
        bool explicitEmbedded=storeInvolved&&profileId=="embedded";
        if(names.Distinct().Count()!=10 ||
           !Guid.TryParse(sourceMap,out var from)||!Guid.TryParse(destinationMap,out var to)||
           from==to||!names.Contains(from)||!names.Contains(to)||
           !(explicitEmbedded||(profileId.Length==64&&profileId.All(Uri.IsHexDigit))))
            throw new InvalidDataException("UM-TRAVEL-INVALID-SELECTION");
        bool healthy=before.Concat(after).All(m=>m.ConfirmedStopped&&
            m.SaveDirectoryFound&&!m.AmbiguousLayout&&!m.Unstable&&
            (m.StoreMode ? m.WorldPresent&&!m.WorldUnstable :
                !m.ZeroByteDetected));
        var beforeSource=before.Single(m=>m.MapId==from).Files
            .Where(p=>p.Kind=="profile"&&p.Id==profileId).ToArray();
        var afterTarget=after.Single(m=>m.MapId==to).Files
            .Where(p=>p.Kind=="profile"&&p.Id==profileId).ToArray();
        var afterAll=after.SelectMany(m=>m.Files.Where(p=>
            p.Kind=="profile"&&p.Id==profileId).Select(p=>m.MapId)).ToArray();
        bool beforeOnSource=beforeSource.Length==1&&beforeSource[0].Bytes>0;
        bool afterOnTarget=afterTarget.Length==1&&afterTarget[0].Bytes>0;
        bool unique=afterAll.Length==1&&afterAll[0]==to;
        int zeroBytes=before.Concat(after).Sum(m=>m.Files.Count(p=>p.Bytes==0));
        var findings=new List<string>();
        if(!healthy)findings.Add("Leállított map, érvényes világmentés, fájlkönyvtár vagy stabil fájlállapot nem igazolt.");
        if(storeInvolved)findings.Add("ASA -usestore: a karakter/tribe a .ark világban lehet; külön .arkprofile elhelyezkedés nem bizonyítja az átutazást. Játékbeli visszabelépés kötelező.");
        if(!storeInvolved)
        {
            if(!beforeOnSource)findings.Add("A karakter az induló mapon nem szerepel egyetlen érvényes fájlként.");
            if(!afterOnTarget)findings.Add("A célmapon hiányzik az érvényes karakterfájl.");
            if(!unique)findings.Add("A karakter nem egyetlen célmapon szerepel: hiány vagy duplikáció.");
        }
        // File placement alone does not prove biological/imprint/tribe data
        // semantics, MariaDB transactions, Store-v1 exclusive multiwriter,
        // user join or real ASA SaveComplete. Keep the production gate closed.
        return new{
            // 'passed' means loose-file placement ONLY for non-Store mode.
            // Never infer embedded survivor continuity from .ark timestamps.
            passed=!storeInvolved&&healthy&&beforeOnSource&&afterOnTarget&&unique,
            storeModeDetected=storeInvolved,
            embeddedSurvivorVerified=false,gameClientRejoinVerified=false,
            worldFilesPresentForStore=storeInvolved&&before.Concat(after)
                .Where(m=>m.StoreMode).All(m=>m.WorldPresent&&!m.WorldUnstable),
            maps=10,hosts=2,beforeOnSource,afterOnTarget,uniqueDestination=unique,
            zeroByteFiles=zeroBytes,
            findings=findings.ToArray(),
            liveStoreWriterCertified=false,asaSaveCompleteCertified=false,
            productionUnlockAllowed=false,
            evidence=storeInvolved?
                "STORE_V1_EMBEDDED_WORLD_METADATA_NO_CHARACTER_CONTENT_PROOF":
                "FOUR_QUIESCENT_HOST_FILE_HASH_SNAPSHOTS_ONLY"
        };
    }
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4*Limit+8192)
                return Error(null,"UM-TRAVEL-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            if(data.ValueKind!=JsonValueKind.Object||
               !data.TryGetProperty("id",out var i)||i.ValueKind!=JsonValueKind.String||
               !Guid.TryParse(i.GetString(),out var req)||req==Guid.Empty||
               !data.TryGetProperty("method",out var m)||m.ValueKind!=JsonValueKind.String)
                return Error(null,"UM-TRAVEL-INVALID-MESSAGE");
            id=req.ToString("D");
            string? method=m.GetString();
            if(method is not ("travel.capture" or "travel.compare"))
                return Error(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="travel.capture")
                {
                    var snapshot=await Task.Run(CaptureLocal);
                    string json=JsonSerializer.Serialize(snapshot,Json);
                    return Ok(id,new{
                        snapshot=json,hostId=snapshot.HostId,
                        maps=snapshot.Maps.Select(x=>new{
                            mapId=x.MapId,mapName=x.Name,
                            x.ConfirmedStopped,x.SaveDirectoryFound,x.AmbiguousLayout,
                            profileCount=x.Profiles,tribeCount=x.Tribes,
                            x.ZeroByteDetected,x.Unstable,
                            x.StoreMode,x.WorldPresent,x.WorldBytes,x.WorldUnstable,
                            embeddedSurvivorVerified=false,
                            profileFiles=x.Files.Where(f=>f.Kind=="profile").Select(f=>
                                new{id=f.Id,bytes=f.Bytes}).ToArray()
                        }).ToArray(),
                        complete=snapshot.Maps.All(m=>m.ConfirmedStopped&&
                            m.SaveDirectoryFound&&!m.AmbiguousLayout&&
                            !m.Unstable &&
                            (m.StoreMode?m.WorldPresent&&!m.WorldUnstable:
                                !m.ZeroByteDetected)),
                        writerCertified=false,readOnly=true,
                        evidence="LOCAL_QUIESCENT_FILE_HASH_SNAPSHOT_ONLY"
                    });
                }
                string Required(string key)
                {
                    if(!data.TryGetProperty(key,out var value)||
                       value.ValueKind!=JsonValueKind.String)
                        throw new InvalidDataException("UM-TRAVEL-MISSING-EVIDENCE");
                    return value.GetString()??"";
                }
                return Ok(id,Compare(Required("beforeA"),Required("beforeB"),
                    Required("afterA"),Required("afterB"),Required("sourceMap"),
                    Required("destinationMap"),Required("profileId")));
            }
            finally{serial.Release();}
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or JsonException)
        {return Error(id,"UM-TRAVEL-EVIDENCE-UNVERIFIED");}
    }
    private static string Ok(string id,object payload)=>
        JsonSerializer.Serialize(new{id,ok=true,data=payload},Json);
    private static string Error(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
}
