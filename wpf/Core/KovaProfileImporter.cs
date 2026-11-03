using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record KovaImportedMod(string Id,ModMode Mode);
public sealed record KovaImportedMap(string SourceId,string Name,string Root,
    string MapCode,int GamePort,int QueryPort,IReadOnlyList<KovaImportedMod> Mods,
    string StorePhase,bool SafeToStart);
public sealed record KovaProfileReport(string SourceHash,int Version,
    IReadOnlyList<KovaImportedMap> Maps,bool ReadOnly,bool StoreCompatible,
    IReadOnlyList<string> Warnings,string SourcePath);

// Read-only Kova 1.8.3 profile v1 reader. Produces an inert migration
// proposal, NEVER claims Store binary compatibility or starts a server.
// Accepted input is a user-selected, locally copied JSON export; not live
// Kova localStorage, remote SMB or Store coordination folder.
public static class KovaProfileImporter
{
    public static KovaProfileReport InspectCopy(string profileJson,string? storeJson=null)
    {
        string profile=FixedCopy(profileJson);
        byte[] raw=ReadBounded(profile,20_000_000);
        string digest=Convert.ToHexString(SHA256.HashData(raw));
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||
           Num(root,"version")!=1||
           !root.TryGetProperty("profiles",out var profiles)||
           profiles.ValueKind!=JsonValueKind.Array||
           profiles.GetArrayLength()>1000)
            throw new InvalidDataException("Kova Manager v1 profile export required");
        var store=ReadStoreCopy(storeJson);
        var result=new List<KovaImportedMap>();
        var ids=new HashSet<string>(StringComparer.Ordinal);
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports=new HashSet<int>();
        var warnings=new List<string>();
        foreach(var profileEntry in profiles.EnumerateArray())
        {
            if(profileEntry.ValueKind!=JsonValueKind.Object ||
               !Bool(profileEntry,"localManaged"))continue;
            var id=Str(profileEntry,"id");
            var path=Str(profileEntry,"installPath");
            if(string.IsNullOrWhiteSpace(id)||id.Length>80 ||
               id.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'&&c!='-') ||
               string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path) ||
               path.StartsWith(@"\\"))
                throw new InvalidDataException("Invalid or nonlocal Kova profile map binding");
            var normalized=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if(!ids.Add(id) || !paths.Add(normalized))
                throw new InvalidDataException("Duplicate Kova profile ID or server root");
            var name=Str(profileEntry,"map")??id;
            var code=Str(profileEntry,"mapId")??"";
            int game=Num(profileEntry,"gamePort"),query=Num(profileEntry,"queryPort");
            if(game<0||game>65535||query<0||query>65535 ||
               game>0&&!ports.Add(game) || query>0&&!ports.Add(query))
                throw new InvalidDataException("Kova profile port collision or invalid port");
            if(!profileEntry.TryGetProperty("mods",out var modList) ||
               modList.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("Kova profile missing explicit mod list");
            var mods=new List<KovaImportedMod>();
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in modList.EnumerateArray())
            {
                if(item.ValueKind!=JsonValueKind.Object || !item.TryGetProperty("id",out var itemId))
                    throw new InvalidDataException("Invalid Kova mod item");
                string? modId=itemId.ValueKind==JsonValueKind.String?itemId.GetString():
                    itemId.ValueKind==JsonValueKind.Number?itemId.GetRawText():null;
                if(modId==null || modId.Length<5 || modId.Length>12 ||
                   modId.Any(c=>c<'0'||c>'9') || !seen.Add(modId))
                    throw new InvalidDataException("Invalid or duplicate Kova mod ID");
                var mode=Str(item,"mode") switch{
                    null or "active"=>ModMode.Active,"passive"=>ModMode.Passive,
                    "disabled"=>ModMode.Disabled,
                    _=>throw new InvalidDataException("Unknown Kova mod mode")
                };
                mods.Add(new(modId,mode));
            }
            string phase=store.TryGetValue(id,out var val)?val:"unknown";
            if(phase!="active")warnings.Add("Store activation unverified for local profile "+id);
            result.Add(new(id,name,normalized,code,game,query,mods,phase,false));
        }
        if(result.Count==0)warnings.Add("No locally managed profile found");
        if(storeJson==null)warnings.Add("Kova Store metadata not supplied; active sessions unverified");
        if(!Convert.ToHexString(SHA256.HashData(ReadBounded(profile,20_000_000)))
            .Equals(digest,StringComparison.Ordinal))
            throw new IOException("Kova profile changed during read");
        return new(digest,1,result,true,false,warnings,profile);
    }

    public static ManagerConfig BuildInertLocalDraft(KovaProfileReport report,string localHostId)
    {
        if(report==null||!report.ReadOnly||string.IsNullOrWhiteSpace(localHostId)||
           report.Maps.Count==0)
            throw new InvalidDataException("Explicit host and inspected Kova copy required");
        var result=new ManagerConfig{LocalHostId=localHostId,LocalHostName="Imported offline preview",
            ListenPort=0,PeerAddress="",PeerPort=0};
        foreach(var row in report.Maps)
        {
            var idBytes=SHA256.HashData(Encoding.UTF8.GetBytes(localHostId+":"+row.SourceId+
                ":"+row.Root.ToUpperInvariant()));
            var map=new LocalMap{
                Id=new Guid(idBytes.AsSpan(0,16)),Name=row.Name,
                InstallationPath=row.Root,
                ExecutablePath="",ApiLoaderPath="",
                // Never construct a launch command or copy Kova secrets.
                Arguments="",GamePort=row.GamePort,QueryPort=row.QueryPort,
                AutoStart=false,MonitorRcon=false,Maintenance=true,
                ModChangesPending=true,
                DisabledModIds=row.Mods.Select(x=>x.Id).ToList()
            };
            result.LocalMaps.Add(map);
        }
        // Important: no Store session, passwords or peer credentials are copied.
        return result;
    }

    private static Dictionary<string,string> ReadStoreCopy(string? file)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        if(file==null)return result;
        string path=FixedCopy(file);
        using var doc=JsonDocument.Parse(ReadBounded(path,1_000_000));
        var root=doc.RootElement;
        if(root.ValueKind!=JsonValueKind.Object || Num(root,"schema")!=1||
           !root.TryGetProperty("maps",out var maps)||
           maps.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("Unknown Kova Store-v1 copy schema");
        foreach(var item in maps.EnumerateObject())
        {
            var phase=Str(item.Value,"phase");
            if(phase is not ("prepared" or "review" or "active") ||
               Str(item.Value,"root")==null || Str(item.Value,"map")==null ||
               Str(item.Value,"backupId")==null ||
               !DateTimeOffset.TryParse(Str(item.Value,"preparedAt"),out _))
                throw new InvalidDataException("Corrupt Kova Store-map metadata");
            result[item.Name]=phase!;
        }
        return result;
    }
    private static string FixedCopy(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
           path.StartsWith(@"\\") || !Path.GetExtension(path).Equals(".json",
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only local JSON copy may be inspected");
        var full=Path.GetFullPath(path);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("No network shares or mapped drives");
        for(var d=new DirectoryInfo(Path.GetDirectoryName(full)!);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked Kova copy path rejected");
        if(!File.Exists(full)||(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Missing or linked Kova copy");
        return full;
    }
    private static byte[] ReadBounded(string path,int max)
    {
        var file=new FileInfo(path);
        if(file.Length>max || file.Length==0)
            throw new InvalidDataException("Kova copy exceeds safe size");
        return File.ReadAllBytes(path);
    }
    private static string? Str(JsonElement root,string field)
        =>root.ValueKind==JsonValueKind.Object&&root.TryGetProperty(field,out var el)&&
          el.ValueKind==JsonValueKind.String?el.GetString():null;
    private static int Num(JsonElement root,string field)
    {
        if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty(field,out var value))
            return 0;
        if(value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out var numeric))
            return numeric;
        if(value.ValueKind==JsonValueKind.String &&
           int.TryParse(value.GetString(),out var textNumber))return textNumber;
        return -1;
    }
    private static bool Bool(JsonElement root,string field)
        =>root.ValueKind==JsonValueKind.Object&&root.TryGetProperty(field,out var el)&&
          el.ValueKind==JsonValueKind.True;
}
