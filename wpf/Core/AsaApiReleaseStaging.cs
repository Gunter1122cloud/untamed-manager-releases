using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// T13: fetch official ArkServerApi release bytes, verify the publisher SHA-256
// and ZIP path safety, then seal an isolated download. NO API DLL/EXE is loaded,
// extracted into ASA, activated, or treated as compatible with live servers.
public sealed record AsaApiStagedRelease(int Schema,string Tag,string AssetName,
    string OfficialSha256,long Bytes,DateTimeOffset VerifiedUtc)
{
    public bool LiveInstallationAllowed=>false;
}

public static class AsaApiReleaseStaging
{
    private const string FileName="release.zip";
    private const string IndexName="release-manifest.json";
    private const string CompleteName="complete";

    private static string Lab(string target)
    {
        if(string.IsNullOrWhiteSpace(target)||!Path.IsPathFullyQualified(target)||
           target.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("ASA API isolated target must be a local absolute directory");
        string root=Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if(!string.Equals(Path.GetDirectoryName(root),temp,StringComparison.OrdinalIgnoreCase)||
           !Path.GetFileName(root).StartsWith("Untamed-ApiLab-",StringComparison.Ordinal)||
           new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new UnauthorizedAccessException("Only a fresh named TEMP API lab directory is allowed");
        for(var p=new DirectoryInfo(temp);p!=null;p=p.Parent)
            if(p.Exists&&(p.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked API lab ancestor rejected");
        if(Directory.Exists(root)&&(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked API lab target rejected");
        return root;
    }
    private static string Hash(string file)
    {
        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void InspectZip(string archive)
    {
        using var zip=ZipFile.OpenRead(archive);
        if(zip.Entries.Count is <1 or >2000)
            throw new InvalidDataException("Release ZIP has invalid entry count");
        long expanded=0;int binaries=0;
        var names=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            string name=entry.FullName;
            if(string.IsNullOrWhiteSpace(name)||name.StartsWith('/')||
               name.Contains('\\')||name.Contains(':')||
               name.Split('/').Any(p=>p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
                throw new InvalidDataException("Unsafe API ZIP path");
            int kind=(entry.ExternalAttributes>>16)&0xF000;
            if(kind==0xA000||
               ((FileAttributes)entry.ExternalAttributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked release ZIP member rejected");
            if(name.EndsWith('/'))continue;
            if(!names.Add(name))throw new InvalidDataException("Duplicate API ZIP member");
            expanded=checked(expanded+entry.Length);
            if(expanded>1024L*1024*1024 || entry.Length>256L*1024*1024)
                throw new InvalidDataException("API ZIP extraction size would exceed safety bound");
            if(name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||
               name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))
                binaries++;
        }
        if(binaries==0)throw new InvalidDataException("API ZIP contains no binary payload");
    }
    private static void Write(string path,string text)
    {
        using var fs=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new StreamWriter(fs,new System.Text.UTF8Encoding(false),1024,leaveOpen:true);
        writer.Write(text);writer.Flush();fs.Flush(true);
    }
    private static AsaApiStagedRelease VerifyInternal(string folder,bool internalStage)
    {
        string root=internalStage?Lab(folder[..^".incomplete".Length])+".incomplete":Lab(folder);
        if(!Directory.Exists(root)||Directory.GetFiles(root).Length!=3||
           Directory.GetDirectories(root).Length!=0)
            throw new InvalidDataException("API release archive is incomplete");
        string manifest=Path.Combine(root,IndexName),
            data=Path.Combine(root,FileName),complete=Path.Combine(root,CompleteName);
        if(!File.Exists(manifest)||!File.Exists(data)||!File.Exists(complete)||
           new FileInfo(manifest).Length is <20 or >4096)
            throw new InvalidDataException("Missing API staging integrity metadata");
        var state=JsonSerializer.Deserialize<AsaApiStagedRelease>(File.ReadAllText(manifest)) ??
            throw new InvalidDataException("Invalid API release manifest");
        if(state.Schema!=1||state.LiveInstallationAllowed||
           !string.Equals(File.ReadAllText(complete).Trim(),state.OfficialSha256,
               StringComparison.OrdinalIgnoreCase)||
           state.Bytes<=0||state.Bytes>512L*1024*1024||
           new FileInfo(data).Length!=state.Bytes||
           !string.Equals(Hash(data),state.OfficialSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("API release hash, schema or staging seal differs");
        InspectZip(data);
        return state;
    }
    public static AsaApiStagedRelease Verify(string destination)=>
        VerifyInternal(destination,false);

    public static async Task<AsaApiStagedRelease> StageAsync(
        HttpClient http,AsaApiReleaseInfo release,AsaApiReleaseAsset selected,
        string destination,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(selected);
        ct.ThrowIfCancellationRequested();
        if(!release.Assets.Contains(selected)||
           selected.OfficialSha256==null||selected.OfficialSha256.Length!=64||
           selected.OfficialSha256.Any(c=>!Uri.IsHexDigit(c))||
           !selected.Name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)||
           selected.Bytes is <1 or >512L*1024*1024)
            throw new InvalidDataException("Explicit official SHA-256 ZIP asset required");
        if(!Uri.TryCreate(selected.Url,UriKind.Absolute,out var source)||
           source.Scheme!=Uri.UriSchemeHttps||
           !source.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||
           !source.AbsolutePath.StartsWith(
               "/ArkServerApi/AsaApi/releases/download/"+release.Tag+"/",
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Nonofficial or mismatched release URL rejected");
        string target=Lab(destination),stage=target+".incomplete";
        if(Directory.Exists(target)||Directory.Exists(stage)||
           File.Exists(target)||File.Exists(stage))
            throw new IOException("API release destination or earlier partial stage exists");
        long free=new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
        if(free<selected.Bytes+64L*1024*1024)
            throw new IOException("Insufficient disk space for an isolated API package");
        Directory.CreateDirectory(stage);
        string data=Path.Combine(stage,FileName);
        using var request=new HttpRequestMessage(HttpMethod.Get,source);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)
            throw new IOException("Official API asset HTTP request failed");
        if(response.Content.Headers.ContentLength.HasValue &&
           response.Content.Headers.ContentLength.Value!=selected.Bytes)
            throw new InvalidDataException("Official API asset length differs");
        await using(var output=new FileStream(data,FileMode.CreateNew,
            FileAccess.Write,FileShare.None))
        await using(var input=await response.Content.ReadAsStreamAsync(ct))
        {
            byte[] buffer=new byte[65536];long total=0;int count;
            while((count=await input.ReadAsync(buffer,ct))!=0)
            {
                total=checked(total+count);
                if(total>selected.Bytes)throw new InvalidDataException("Oversized API asset stream");
                await output.WriteAsync(buffer.AsMemory(0,count),ct);
            }
            if(total!=selected.Bytes)throw new InvalidDataException("Truncated API release bytes");
            output.Flush(true);
        }
        if(!string.Equals(Hash(data),selected.OfficialSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Official API release publisher SHA-256 mismatch");
        InspectZip(data);
        var state=new AsaApiStagedRelease(1,release.Tag,selected.Name,
            selected.OfficialSha256.ToUpperInvariant(),selected.Bytes,DateTimeOffset.UtcNow);
        Write(Path.Combine(stage,IndexName),JsonSerializer.Serialize(state));
        Write(Path.Combine(stage,CompleteName),state.OfficialSha256);
        VerifyInternal(stage,true);
        Directory.Move(stage,target);
        return Verify(target);
    }
}
