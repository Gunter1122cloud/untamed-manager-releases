using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

/// <summary>
/// Offline validation of the official downloaded ZIP. Never extracts arbitrary
/// paths, scripts or symlinks. A signature cannot be inferred from a SHA-256
/// calculated locally; transport is restricted separately to Valve HTTPS.
/// </summary>
public static class SteamCmdZipValidator
{
    public static async Task<byte[]> ExtractExecutableAsync(Stream input,
        CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if(!input.CanRead || !input.CanSeek || input.Length is <=0 or >50_000_000)
            throw new InvalidDataException("SteamCMD ZIP length is invalid");
        using var archive=new ZipArchive(input,ZipArchiveMode.Read,leaveOpen:true);
        if(archive.Entries.Count is <1 or >64)
            throw new InvalidDataException("SteamCMD ZIP contains too many entries");
        foreach(var entry in archive.Entries)
        {
            string name=entry.FullName.Replace('\\','/');
            if(name.StartsWith('/') || name.Contains("//") ||
               name.Split('/').Any(part=>part is "." or "..") ||
               name.Contains(':') || name.Any(char.IsControl))
                throw new InvalidDataException("SteamCMD ZIP has an unsafe entry name");
            // Unix symlink mode 0120000 must not be silently accepted.
            int mode=(entry.ExternalAttributes>>16)&0xF000;
            if(mode==0xA000)
                throw new InvalidDataException("SteamCMD ZIP contains a link");
        }
        var candidates=archive.Entries.Where(e=>
            e.FullName.Equals("steamcmd.exe",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(candidates.Length!=1 || candidates[0].Length is <=0 or >50_000_000)
            throw new InvalidDataException("Exactly one root steamcmd.exe is required");
        using var output=new MemoryStream();
        await using(var file=candidates[0].Open())
        {
            var buffer=new byte[81920];
            int count;
            while((count=await file.ReadAsync(buffer,ct))>0)
            {
                if(output.Length+count>50_000_000)
                    throw new InvalidDataException("SteamCMD executable expands beyond limit");
                await output.WriteAsync(buffer.AsMemory(0,count),ct);
            }
        }
        var bytes=output.ToArray();
        if(bytes.Length<2 || bytes[0]!=(byte)'M' || bytes[1]!=(byte)'Z')
            throw new InvalidDataException("SteamCMD payload is not a Windows PE executable");
        return bytes;
    }
}
