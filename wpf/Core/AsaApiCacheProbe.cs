using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Read-only ASA API cache metadata observation. The official ArkBaseApi
/// computes SHA-256 of ArkAscendedServer.exe, reads ArkApi/Cache/cached_key.cache
/// (legacy raw hash or version-1 JSON), and then validates cached_offsets and
/// cached_bitfields. The native API alone downloads/validates/activates caches.
/// No cache is created, downloaded, renamed or deleted by this probe.
/// </summary>
internal static class AsaApiCacheProbe
{
    internal sealed record Observation(string State,string ExecutableSha256,
        bool KeyMatchesExecutable,bool CandidateFilesPresent)
    {
        // Matching a key and observing candidate files is NOT equivalent
        // to the native API's serialized map validation or runtime readiness.
        public bool RuntimeCertified=>false;
    }

    private static bool HexHash(string? text)=>
        text is {Length:64}&&text.All(Uri.IsHexDigit);

    private static bool SafeGeneration(string relative)
    {
        if(relative.Length is <80 or >200 ||
           !relative.StartsWith("generations/",StringComparison.Ordinal) ||
           relative.Contains('\\')||relative.Contains("..",StringComparison.Ordinal))
            return false;
        string name=relative["generations/".Length..];
        var parts=name.Split('-');
        return parts.Length==4&&HexHash(parts[0])&&
            parts.Skip(1).All(p=>p.Length is >0 and <=20&&
                p.All(char.IsAsciiDigit));
    }

    private static bool PlainFile(string path,long maxBytes)
    {
        if(!File.Exists(path))return false;
        var info=new FileInfo(path);
        if((info.Attributes&FileAttributes.ReparsePoint)!=0||
           info.Length is <=0 || info.Length>maxBytes)return false;
        for(var parent=info.Directory;parent!=null;parent=parent.Parent)
            if(parent.Exists&&(parent.Attributes&FileAttributes.ReparsePoint)!=0)
                return false;
        return true;
    }

    internal static Observation Inspect(string installation,string serverExe)
    {
        if(string.IsNullOrWhiteSpace(installation)||
           string.IsNullOrWhiteSpace(serverExe)||
           !Path.IsPathFullyQualified(installation)||
           !Path.IsPathFullyQualified(serverExe))
            throw new InvalidDataException("UM-API-CACHE-LOCAL-ROOT-REQUIRED");
        string root=Path.GetFullPath(installation);
        string expected=Path.Combine(root,"ShooterGame","Binaries",
            "Win64","ArkAscendedServer.exe");
        if(!Path.GetFullPath(serverExe).Equals(expected,
            StringComparison.OrdinalIgnoreCase)||!PlainFile(expected,4L*1024*1024*1024))
            throw new InvalidDataException("UM-API-CACHE-EXE-NOT-REGISTERED");
        string hash;
        using(var stream=File.OpenRead(expected))
            hash=Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        Observation Result(string state,bool match=false,bool files=false)=>
            new(state,hash,match,files);
        string cache=Path.Combine(root,"ShooterGame","Binaries",
            "Win64","ArkApi","Cache");
        string key=Path.Combine(cache,"cached_key.cache");
        if(!File.Exists(key))return Result("UM-API-CACHE-NO-LOCAL-KEY");
        if(!PlainFile(key,8192))return Result("UM-API-CACHE-KEY-INVALID");
        string raw=File.ReadAllText(key).Trim();
        string knownHash="";
        string relative="";
        if(HexHash(raw))
            knownHash=raw.ToLowerInvariant();
        else
        {
            try
            {
                using var json=JsonDocument.Parse(raw);
                if(json.RootElement.ValueKind!=JsonValueKind.Object||
                   !json.RootElement.TryGetProperty("version",out var version)||
                   !version.TryGetInt32(out var ver)||ver!=1||
                   !json.RootElement.TryGetProperty("executable_hash",out var keyHash)||
                   keyHash.ValueKind!=JsonValueKind.String||
                   !HexHash(keyHash.GetString()))
                    return Result("UM-API-CACHE-KEY-INVALID");
                knownHash=keyHash.GetString()!.ToLowerInvariant();
                if(json.RootElement.TryGetProperty("cache_directory",out var folder))
                {
                    if(folder.ValueKind!=JsonValueKind.String)
                        return Result("UM-API-CACHE-KEY-INVALID");
                    relative=folder.GetString()??"";
                    if(relative.Length>0&&!SafeGeneration(relative))
                        return Result("UM-API-CACHE-KEY-INVALID");
                }
            }
            catch(JsonException){return Result("UM-API-CACHE-KEY-INVALID");}
        }
        if(!knownHash.Equals(hash,StringComparison.Ordinal))
            return Result("UM-API-CACHE-EXE-HASH-MISMATCH");
        string selected=relative.Length==0?cache:Path.Combine(cache,
            relative.Replace('/',Path.DirectorySeparatorChar));
        if(!PlainFile(Path.Combine(selected,"cached_offsets.cache"),2L*1024*1024*1024)||
           !PlainFile(Path.Combine(selected,"cached_bitfields.cache"),2L*1024*1024*1024))
            return Result("UM-API-CACHE-FILES-NOT-VERIFIED",true);
        return Result("UM-API-CACHE-HASH-BOUND-CANDIDATE",true,true);
    }
}
