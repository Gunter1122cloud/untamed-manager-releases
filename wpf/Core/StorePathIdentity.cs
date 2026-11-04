using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UntamedManager.Core;

// T05: read-only, fail-closed Windows filesystem identity check for the HP/Ryzen
// UNC alias discrepancy. Never modifies the historical Store config/hash/protocol.
// A local test match is not a two-machine production ownership certificate.
public sealed record StorePathIdentityDecision(bool SameFileSystemObject,
    bool BothUncPaths,bool AliasEvidence, string Reason);

public static class StorePathIdentity
{
    const uint ReadAttributes=0x80;
    const uint ShareReadWriteDelete=0x7;
    const uint OpenExisting=3;
    const uint BackupSemantics=0x02000000;
    const int FileIdInfoClass=18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=16)]
        public byte[] FileId;
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="CreateFileW",SetLastError=true)]
    private static extern SafeFileHandle OpenDirectory(string path,uint access,
        uint share,IntPtr security,uint disposition,uint flags,IntPtr template);

    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,
        int infoClass,out FileIdInfo info,uint size);

    private static bool Linked(string path)
    {
        for(DirectoryInfo? dir=new(path);dir!=null;dir=dir.Parent)
        {
            if(!dir.Exists)return true;
            if((dir.Attributes&FileAttributes.ReparsePoint)!=0)return true;
        }
        return false;
    }

    private static string Normalized(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
           path.StartsWith(@"\\?\",StringComparison.Ordinal)||
           path.StartsWith(@"\\.\",StringComparison.Ordinal))
            throw new InvalidDataException("A fully qualified, ordinary directory path is required");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
    }

    private static FileIdInfo ReadDirectoryIdentity(string path)
    {
        using var handle=OpenDirectory(path,ReadAttributes,ShareReadWriteDelete,
            IntPtr.Zero,OpenExisting,BackupSemantics,IntPtr.Zero);
        if(handle.IsInvalid)
            throw new IOException("Directory metadata handle could not be opened");
        if(!GetFileInformationByHandleEx(handle,FileIdInfoClass,out var id,
            (uint)Marshal.SizeOf<FileIdInfo>()) ||
           id.FileId==null||id.FileId.Length!=16||
           id.FileId.All(b=>b==0))
            throw new IOException("Filesystem did not supply a stable directory identifier");
        return id;
    }

    public static StorePathIdentityDecision CompareDirectories(string first,string second)
    {
        if(!OperatingSystem.IsWindows())
            return new(false,false,false,"Windows filesystem identity API required");
        try
        {
            string a=Normalized(first),b=Normalized(second);
            bool bothUnc=a.StartsWith(@"\\",StringComparison.Ordinal)&&
                         b.StartsWith(@"\\",StringComparison.Ordinal);
            if(!Directory.Exists(a)||!Directory.Exists(b)||Linked(a)||Linked(b))
                return new(false,bothUnc,false,"Missing, inaccessible or reparse-linked Store directory");
            var left=ReadDirectoryIdentity(a);
            var right=ReadDirectoryIdentity(b);
            bool same=left.VolumeSerialNumber==right.VolumeSerialNumber &&
                left.FileId.SequenceEqual(right.FileId);
            if(!same)return new(false,bothUnc,false,"Different verified filesystem directory IDs");
            return new(true,bothUnc,bothUnc,
                bothUnc?"UNC paths resolve to the same Windows filesystem directory ID":
                        "Same directory in local test only; cross-host UNC ownership unverified");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or
                ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return new(false,false,false,"Filesystem identity could not be established safely");
        }
    }
}
