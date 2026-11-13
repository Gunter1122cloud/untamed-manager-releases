using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using UntamedManager.Core;

// Native NTFS/Windows hard-link regression for the ORIGINAL Legacy source
// read() nlink==1 guard. All paths are isolated under test TEMP; no Legacy,
// live ASA, shared transfer root, original writer or RCON is accessed.
internal static class LegacyStoreOriginalLinkTests
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFile,string existingFile,IntPtr security);

    private static void Check(bool pass,string reason)
    {
        if(!pass)throw new InvalidOperationException(
            "ORIGINAL LEGACY SHARED LINK REGRESSION FAILED: "+reason);
        Console.WriteLine("PASS original Legacy shared file links: "+reason);
    }
    private static bool Denies(Action operation)
    {
        try {operation();return false;}
        catch(Exception ex) when(ex is InvalidDataException or IOException or
            UnauthorizedAccessException){return true;}
    }
    public static void Run(string root)
    {
        if(!OperatingSystem.IsWindows())
            throw new InvalidOperationException("NTFS file-link test requires Windows");
        string folder=Path.Combine(root,"isolated-legacy-source-hardlink");
        Directory.CreateDirectory(folder);
        string original=Path.Combine(folder,"operation.json");
        string link=Path.Combine(folder,"aliased-operation.json");
        byte[] expected=System.Text.Encoding.UTF8.GetBytes(
            "{\"schema\":1,\"session\":\"synthetic-not-real\"}");
        File.WriteAllBytes(original,expected);
        Check(LegacyStoreV1LiveReadGate.ReadShared(original).SequenceEqual(expected),
            "ordinary single-link original source file is readable");
        Check(CreateHardLink(link,original,IntPtr.Zero),
            "Windows creates an ordinary NTFS hard link for the synthetic fixture");
        Check((File.GetAttributes(original)&FileAttributes.ReparsePoint)==0 &&
              Denies(()=>LegacyStoreV1LiveReadGate.ReadShared(original))&&
              Denies(()=>LegacyStoreV1LiveReadGate.ReadShared(link)),
            "both hard-link names are rejected despite absent symlink/reparse flags");
        File.Delete(link);
        Check(LegacyStoreV1LiveReadGate.ReadShared(original).SequenceEqual(expected),
            "removing only synthetic extra link restores exact original nlink=1 guard");
        File.WriteAllBytes(original,Array.Empty<byte>());
        Check(Denies(()=>LegacyStoreV1LiveReadGate.ReadShared(original)),
            "empty original shared coordination evidence is never considered valid");
    }
}
