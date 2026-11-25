using System;
using System.Buffers.Binary;
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
        if(stream.Length is <512 or >256L*1024*1024)
            throw new InvalidDataException("SteamCMD executable has an invalid size");
        // Reject text placeholders, HTML error pages and arbitrary files before
        // persisting a selected SteamCMD path. This is a bounded PE-header
        // inspection only; the executable is never launched during selection.
        byte[] dos=new byte[64];
        stream.ReadExactly(dos);
        if(
           dos[0]!=(byte)'M'||dos[1]!=(byte)'Z')
            throw new InvalidDataException("UM-STEAMCMD-NOT-WINDOWS-PE");
        int peOffset=BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3c,4));
        if(peOffset<dos.Length||peOffset>stream.Length-24)
            throw new InvalidDataException("UM-STEAMCMD-NOT-WINDOWS-PE");
        stream.Position=peOffset;
        byte[] header=new byte[24];
        stream.ReadExactly(header);
        if(
           header[0]!=(byte)'P'||header[1]!=(byte)'E'||
           header[2]!=0||header[3]!=0)
            throw new InvalidDataException("UM-STEAMCMD-NOT-WINDOWS-PE");
        ushort machine=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4,2));
        ushort sections=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6,2));
        ushort optionalHeader=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20,2));
        if(machine is not (0x014c or 0x8664 or 0xaa64)||sections==0||optionalHeader==0)
            throw new InvalidDataException("UM-STEAMCMD-NOT-WINDOWS-PE");
        stream.Position=0;
        return new(full,stream.Length,Convert.ToHexString(SHA256.HashData(stream)),
            new DateTimeOffset(file.LastWriteTimeUtc));
    }
}
