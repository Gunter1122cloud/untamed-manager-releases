using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UntamedManager.Core;

// Local copied inputs only. Windows handles exclude writes/deletes for the
// whole inspection; no path from metadata is ever opened or resolved.
internal sealed class VaultReadSnapshot : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,
            WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out HandleInfo info);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
    private readonly List<FileStream> handles=new();
    public static string LocalPath(string path,bool directory=false)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
           path.StartsWith(@"\\")||path.StartsWith("//",StringComparison.Ordinal))
            throw new InvalidDataException("VAULT_INPUT: helyi másolat szükséges");
        string full=Path.GetFullPath(path);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("VAULT_INPUT: helyi fix meghajtó szükséges");
        FileSystemInfo? node=directory?new DirectoryInfo(full):new FileInfo(full);
        for(;node!=null;node=node is FileInfo f?f.Directory:((DirectoryInfo)node).Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("VAULT_INPUT: hivatkozott útvonal tiltva");
        return full;
    }
    public byte[] Read(string path,int limit)
    {
        string full=LocalPath(path);
        var stream=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);
        handles.Add(stream);
        var final=new StringBuilder(32768);
        if(!OperatingSystem.IsWindows()||
           !GetFileInformationByHandle(stream.SafeFileHandle,out var info)||info.Links!=1||
           (info.Attributes&(uint)FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("VAULT_INPUT: hivatkozott vagy nem igazolt fájl");
        uint length=GetFinalPathNameByHandle(stream.SafeFileHandle,final,(uint)final.Capacity,0);
        string resolved=final.ToString();
        if(resolved.StartsWith(@"\\?\"))resolved=resolved[4..];
        if(length==0||length>=final.Capacity||!resolved.Equals(full,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VAULT_CHANGED: a megnyitott fájlútvonal eltér");
        LocalPath(full);
        if(stream.Length<1||stream.Length>limit)
            throw new InvalidDataException("VAULT_LIMIT: érvénytelen bemeneti méret");
        var bytes=new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if(stream.ReadByte()!=-1)
            throw new InvalidDataException("VAULT_CHANGED: változó bemenet");
        return bytes;
    }
    public static string EntryName(string name)
    {
        string key=name.Replace('\\','/');
        if(key.StartsWith('/')||key.EndsWith('/')||key.Contains(':')||
           key.Split('/').Any(x=>x.Length==0||x is "." or ".."||
               x.EndsWith('.')||x.EndsWith(' ')||x.Any(c=>c<32)||
               x.IndexOfAny(new[]{'<','>','"','|','?','*'})>=0))
            throw new InvalidDataException("VAULT_PATH: nem biztonságos archívumútvonal");
        return key;
    }
    public static Dictionary<string,byte[]> LocalMetadataArchive(byte[] bytes)
    {
        string[] names={"cluster-v1.json","cluster-store-v1.json",
            "shared-store-device-v1.json","shared-store-lock-v1.json"};
        using var input=new MemoryStream(bytes,false);
        using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        if(zip.Entries.Count!=names.Length)
            throw new InvalidDataException("VAULT_LOCAL_ARCHIVE: pontosan négy helyi metadatafájl szükséges");
        var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            string key=EntryName(entry.FullName);
            int kind=(entry.ExternalAttributes>>16)&0xF000;
            if(!names.Contains(key,StringComparer.OrdinalIgnoreCase)||kind==0xA000||
               (entry.ExternalAttributes&0x410)!=0||entry.Length<1||entry.Length>1_048_576||
               entry.CompressedLength==0||entry.Length>entry.CompressedLength*200)
                throw new InvalidDataException("VAULT_LOCAL_ARCHIVE: tiltott helyi metadata-bejegyzés");
            using var source=entry.Open();
            var data=new byte[checked((int)entry.Length)];source.ReadExactly(data);
            if(source.ReadByte()!=-1||!files.TryAdd(key,data))
                throw new InvalidDataException("VAULT_LOCAL_ARCHIVE: ismételt vagy csonka metadata");
        }
        if(names.Any(n=>!files.ContainsKey(n)))
            throw new InvalidDataException("VAULT_LOCAL_ARCHIVE: hiányzó helyi metadata");
        // These original local copies have no producer manifest. Their bytes
        // are read under the snapshot handle and their schema/session/config
        // hashes are checked against the manifested coordination archive.
        return files;
    }
    public static Dictionary<string,byte[]> Archive(byte[] bytes,string? unlistedReadme=null)
    {
        using var input=new MemoryStream(bytes,false);
        using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        if(zip.Entries.Count is < 9 or > 128)
            throw new InvalidDataException("VAULT_LIMIT: ismeretlen fájlszám");
        var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        long total=0;
        foreach(var entry in zip.Entries)
        {
            // A mutation lock may be a directory. Its presence is evidence,
            // including an empty directory; it is never extracted or removed.
            bool directory=entry.FullName.EndsWith('/')||entry.FullName.EndsWith('\\');
            string key=EntryName(directory?entry.FullName.TrimEnd('/','\\'):entry.FullName);
            int kind=(entry.ExternalAttributes>>16)&0xF000;
            if(kind==0xA000||(entry.ExternalAttributes&0x400)!=0||
               entry.Length<0||entry.Length>2_000_000||
               (!directory&&(entry.Length<1||entry.CompressedLength==0||
                   entry.Length>entry.CompressedLength*200))||
               (directory&&(!key.EndsWith("/mutation.lock",StringComparison.OrdinalIgnoreCase)||entry.Length!=0)))
                throw new InvalidDataException("VAULT_ENTRY: tiltott archívumbejegyzés");
            total=checked(total+entry.Length);
            if(total>16_000_000)throw new InvalidDataException("VAULT_LIMIT: kibontási korlát");
            using var source=entry.Open();
            var data=new byte[checked((int)entry.Length)];
            source.ReadExactly(data);
            if(source.ReadByte()!=-1||!files.TryAdd(key,data))
                throw new InvalidDataException("VAULT_ENTRY: ismételt vagy csonka bejegyzés");
        }
        foreach(string key in files.Keys)
            for(int at=key.IndexOf('/');at>=0;at=key.IndexOf('/',at+1))
                if(files.ContainsKey(key[..at]))
                    throw new InvalidDataException("VAULT_PATH: fájl/könyvtár ütközés");
        using var manifest=Json(files,"manifest.json");
        if(manifest.RootElement.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("VAULT_MANIFEST: hibás fájlleltár");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in manifest.RootElement.EnumerateArray())
        {
            string key=EntryName(Text(row,"Path"));
            string hash=Text(row,"SHA256");
            if(key.Equals("manifest.json",StringComparison.OrdinalIgnoreCase)||
               !seen.Add(key)||!files.TryGetValue(key,out var data)||data.Length==0||
               hash.Length!=64||hash.Any(c=>!Uri.IsHexDigit(c))||
               !Convert.ToHexString(SHA256.HashData(data)).Equals(hash,StringComparison.OrdinalIgnoreCase)||
               (row.TryGetProperty("Bytes",out var size)&&(size.ValueKind!=JsonValueKind.Number||!size.TryGetInt64(out long n)||n!=data.Length)))
                throw new InvalidDataException("VAULT_MANIFEST: hash vagy méret eltérés");
        }
        if(unlistedReadme!=null&&(!files.ContainsKey(unlistedReadme)||seen.Contains(unlistedReadme)))
            throw new InvalidDataException("VAULT_MANIFEST: hiányzó vagy ismételt leíró");
        if(seen.Count!=files.Count(x=>x.Value.Length>0)-1-(unlistedReadme==null?0:1))
            throw new InvalidDataException("VAULT_MANIFEST: hiányos fájlleltár");
        return files;
    }
    public static JsonDocument Json(IReadOnlyDictionary<string,byte[]> files,string name)
    {
        if(!files.TryGetValue(name,out var bytes))
            throw new InvalidDataException("VAULT_MISSING: hiányzó metadata");
        try
        {
            // Historical exports can contain a UTF-8 BOM. It is not part of JSON.
            int offset=bytes.Length>=3&&bytes[0]==239&&bytes[1]==187&&bytes[2]==191?3:0;
            var doc=JsonDocument.Parse(bytes.AsMemory(offset),new JsonDocumentOptions{MaxDepth=48});
            try{Unique(doc.RootElement);return doc;}catch{doc.Dispose();throw;}
        }
        catch(JsonException){throw new InvalidDataException("VAULT_JSON: hibás JSON");}
    }
    private static void Unique(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in e.EnumerateObject())
            {
                if(!names.Add(p.Name))throw new InvalidDataException("VAULT_JSON: ismételt JSON-kulcs");
                Unique(p.Value);
            }
        }
        else if(e.ValueKind==JsonValueKind.Array)foreach(var item in e.EnumerateArray())Unique(item);
    }
    public static string Text(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&
        e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    public void Dispose(){foreach(var handle in handles)handle.Dispose();}
}
