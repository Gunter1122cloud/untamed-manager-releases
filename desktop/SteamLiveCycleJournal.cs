using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedDesktop;

/// <summary>
/// Manager-local, byte-hashed operation journal for an explicit local ASA
/// SaveWorld -> DoExit -> SteamCMD -> Start maintenance sequence. Not a
/// Store-v1 writer/lock or another ASA backup format.
/// </summary>
internal static class SteamLiveCycleJournal
{
    internal sealed record Record(Guid JobId,Guid MapId,string State,
        string Stage,string ProfileSha256,string InitialBuild,
        DateTimeOffset ChangedUtc);
    private sealed record Envelope(int Schema,Record Value,string Sha256);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private const string Name="steam-live-cycle-v1.json";
    private const int Limit=16*1024;
    private static string Folder(string profile)
    {
        if(string.IsNullOrWhiteSpace(profile)||!Path.IsPathFullyQualified(profile)||
           profile.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-LOCAL-PROFILE");
        string folder=Path.Combine(Path.GetFullPath(profile),"maintenance-journals");
        for(DirectoryInfo? d=new(folder);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-LINKED-PATH");
        return folder;
    }
    private static void Validate(Record value)
    {
        if(value==null||value.JobId==Guid.Empty||value.MapId==Guid.Empty||
           value.State is not ("queued" or "running" or "completed" or "needsOperator")||
           value.Stage is null||value.Stage.Length is <1 or >120||
           value.ProfileSha256 is null||value.ProfileSha256.Length!=64||
           !value.ProfileSha256.All(Uri.IsHexDigit)||
           value.InitialBuild is null||value.InitialBuild.Length is <1 or >30||
           value.InitialBuild.Any(ch=>!char.IsAsciiDigit(ch)))
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-INVALID");
    }
    private static string Hash(Record row)=>Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row,Json))));
    internal static Record? Load(string profile)
    {
        string file=Path.Combine(Folder(profile),Name);
        if(!File.Exists(file))return null;
        if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||
           new FileInfo(file).Length is <40 or >Limit)
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-SIZE");
        using var doc=JsonDocument.Parse(File.ReadAllBytes(file));
        var value=doc.RootElement.Deserialize<Envelope>(Json)??
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-INVALID");
        if(value.Schema!=1||value.Value==null||
           value.Sha256?.Length!=64||!value.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-INVALID");
        Validate(value.Value);
        if(!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(Hash(value.Value)),
            Convert.FromHexString(value.Sha256)))
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-HASH-MISMATCH");
        return value.Value;
    }
    internal static void Save(string profile,Record row)
    {
        Validate(row);
        string folder=Folder(profile);
        Directory.CreateDirectory(folder);
        if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-LINKED-FOLDER");
        string dest=Path.Combine(folder,Name);
        if(File.Exists(dest)&&(File.GetAttributes(dest)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-LINKED-FILE");
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,row,Hash(row)),Json);
        if(bytes.Length>Limit)
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-TOO-LARGE");
        string tmp=Path.Combine(folder,".steam-live-"+Guid.NewGuid().ToString("N")+".pending");
        try
        {
            using(var stream=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,
                FileShare.None,4096,FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk:true);
            }
            File.Move(tmp,dest,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    internal static FileStream AcquireLease(string profile)
    {
        string folder=Folder(profile);
        Directory.CreateDirectory(folder);
        string lease=Path.Combine(folder,"steam-live-cycle.lease");
        if(File.Exists(lease)&&(File.GetAttributes(lease)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-LINKED-LEASE");
        try
        {
            return new FileStream(lease,FileMode.OpenOrCreate,
                FileAccess.ReadWrite,FileShare.None,4096,FileOptions.WriteThrough);
        }
        catch(IOException){throw new IOException("UM-STEAM-LIVE-CYCLE-LEASE-BUSY");}
    }
    internal static void ArchivePrevious(string profile)
    {
        string folder=Folder(profile),old=Path.Combine(folder,Name);
        if(!File.Exists(old))return;
        var record=Load(profile)??
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-MISSING");
        string history=Path.Combine(folder,"history");
        Directory.CreateDirectory(history);
        if((File.GetAttributes(history)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-STEAM-LIVE-JOURNAL-LINKED-HISTORY");
        string target=Path.Combine(history,record.JobId.ToString("N")+".json");
        if(File.Exists(target))throw new IOException("UM-STEAM-LIVE-JOURNAL-ARCHIVED");
        File.Move(old,target);
    }
}
