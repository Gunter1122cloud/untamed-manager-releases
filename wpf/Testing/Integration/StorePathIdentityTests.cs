using System;
using System.IO;
using UntamedManager.Core;

public static class StorePathIdentityTests
{
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new Exception("Store path identity regression failed: "+reason);
        Console.WriteLine("PASS store-path-identity "+reason);
    }

    public static void Run(string root)
    {
        string baseRoot=Path.Combine(root,"store-path-identity-fixture");
        string a=Path.Combine(baseRoot,"store-a");
        string b=Path.Combine(baseRoot,"store-b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        string sentinel=Path.Combine(a,"untamed-test-sentinel.txt");
        File.WriteAllText(sentinel,"original fixture bytes");
        byte[] original=File.ReadAllBytes(sentinel);
        var self=StorePathIdentity.CompareDirectories(a,a);
        Check(self.SameFileSystemObject&&!self.BothUncPaths&&!self.AliasEvidence,
            "same local NTFS directory is proven but not promoted to cross-host UNC proof");
        var different=StorePathIdentity.CompareDirectories(a,b);
        Check(!different.SameFileSystemObject&&!different.AliasEvidence,
            "two different directories cannot be mistaken for aliases");
        var missing=StorePathIdentity.CompareDirectories(a,Path.Combine(baseRoot,"missing"));
        Check(!missing.SameFileSystemObject&&!missing.AliasEvidence,
            "missing directory cannot be used as historical Store alias evidence");
        var file=StorePathIdentity.CompareDirectories(a,sentinel);
        Check(!file.AliasEvidence,"a regular file cannot impersonate a Store directory");
        var unqualified=StorePathIdentity.CompareDirectories(a,"relative-store-dir");
        Check(!unqualified.AliasEvidence,"relative path is not accepted as ownership evidence");
        Check(original.AsSpan().SequenceEqual(File.ReadAllBytes(sentinel)),
            "filesystem identity comparison leaves all Store fixture bytes unchanged");
    }
}
