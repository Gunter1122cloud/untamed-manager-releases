using System;
using System.IO;
using System.Security.Cryptography;

namespace UntamedManager.Core;

// Read-only SteamCMD prerequisite inspection. This does not execute SteamCMD, connect to
// Steam, download data, modify a server or claim any ASA build is up to date.
public sealed record SteamCmdFileStatus(string ExecutablePath,long LengthBytes,string Sha256,
    DateTimeOffset LastModifiedUtc);

public static class SteamCmdPreflight
{
    public static SteamCmdFileStatus Inspect(string executablePath)
    {
        if(string.IsNullOrWhiteSpace(executablePath) ||
           !Path.IsPathFullyQualified(executablePath) ||
           executablePath.StartsWith(@"\\"))
            throw new InvalidDataException("SteamCMD requires an explicit local executable path");
        string full=Path.GetFullPath(executablePath);
        if(!Path.GetFileName(full).Equals("steamcmd.exe",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only steamcmd.exe can be inspected");
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("SteamCMD must be on a fixed local disk");
        for(var parent=new DirectoryInfo(Path.GetDirectoryName(full)!);parent!=null;parent=parent.Parent)
            if(parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("SteamCMD path uses a junction or symlink");
        if(!File.Exists(full))throw new FileNotFoundException("SteamCMD executable not found",full);
        var file=new FileInfo(full);
        if((file.Attributes & FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked SteamCMD executable rejected");
        using var stream=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length==0)throw new InvalidDataException("SteamCMD executable is empty");
        return new(full,stream.Length,Convert.ToHexString(SHA256.HashData(stream)),
            new DateTimeOffset(file.LastWriteTimeUtc));
    }
}
