using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace UntamedManager.Core;

/// <summary>Stopped installation clone to a previously absent local directory.
/// The original is never renamed, deleted or changed. Publish only after
/// comparing the complete file inventory and source/copy hashes.</summary>
public static class InstallationRelocation
{
    public sealed record Entry(string Relative,bool Directory,long Bytes);
    public sealed record Plan(string Source,string Destination,Entry[] Entries,long Bytes,long FreeBytes);
    private static bool Overlap(string a,string b)=>a.Equals(b,StringComparison.OrdinalIgnoreCase)||
        a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
        b.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static string Root(string path)
    {
        _=UntamedOperationCoordinator.ResourceKey(path);
        string full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(full.Equals(Path.GetPathRoot(full),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("UM-MOVE-DRIVE-ROOT");
        return full;
    }
    private static void Absent(string path)
    {
        try{_=File.GetAttributes(path);}
        catch(FileNotFoundException){return;}
        catch(DirectoryNotFoundException){return;}
        throw new IOException("UM-MOVE-TARGET-EXISTS");
    }
    private static Entry[] Inventory(string root)
    {
        var entries=new List<Entry>();long bytes=0;
        void Walk(string dir,int depth)
        {
            if(depth>64)throw new InvalidDataException("UM-MOVE-PATH-DEPTH");
            _=UntamedOperationCoordinator.ResourceKey(dir);
            foreach(string full in Directory.EnumerateFileSystemEntries(dir))
            {
                var attrs=File.GetAttributes(full);
                if((attrs&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("UM-MOVE-LINKED-FILE");
                string relative=Path.GetRelativePath(root,full);
                if(relative.Contains('~')||relative.Contains(':')||relative.Split(Path.DirectorySeparatorChar).Any(x=>x.EndsWith('.')||x.EndsWith(' ')))
                    throw new InvalidDataException("UM-MOVE-ALIAS");
                bool directory=(attrs&FileAttributes.Directory)!=0;
                long length=directory?0:new FileInfo(full).Length;
                bytes=checked(bytes+length);
                entries.Add(new(relative,directory,length));
                if(entries.Count>100_000||bytes>2L*1024*1024*1024*1024)throw new InvalidDataException("UM-MOVE-RESOURCE-LIMIT");
                if(directory)Walk(full,depth+1);
            }
        }
        Walk(root,0);
        return entries.OrderBy(x=>x.Relative,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static Plan Preview(string source,string destination)
    {
        source=Root(source);destination=Root(destination);
        if(Overlap(source,destination))throw new InvalidDataException("UM-MOVE-PATH-OVERLAP");
        if(!Directory.Exists(source)||!Directory.Exists(Path.GetDirectoryName(destination)))
            throw new DirectoryNotFoundException("UM-MOVE-DIRECTORY-MISSING");
        Absent(destination);
        var entries=Inventory(source);
        long bytes=entries.Sum(x=>x.Bytes);
        long free=new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
        if(bytes>free||Math.Max(64L*1024*1024,bytes/10)>free-bytes)throw new IOException("UM-MOVE-NOT-ENOUGH-SPACE");
        return new(source,destination,entries,bytes,free);
    }
    private static string Hash(string file)
    {
        _=UntamedOperationCoordinator.ResourceKey(Path.GetDirectoryName(file)!);
        if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("UM-MOVE-LINKED-FILE");
        using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    public static void CopyVerified(Plan approved,Action verifyStopped)
    {
        verifyStopped();
        var current=Preview(approved.Source,approved.Destination);
        if(!current.Entries.SequenceEqual(approved.Entries))throw new InvalidOperationException("UM-MOVE-SOURCE-CHANGED");
        string stage=approved.Destination+".obsidian-incomplete-"+Guid.NewGuid().ToString("N");
        _=Root(stage);Absent(stage);Directory.CreateDirectory(stage);
        // An interrupted copy is retained under its unique staging name;
        // it cannot be mistaken for the approved installation.
        foreach(var e in current.Entries.Where(x=>x.Directory))
        {
            string path=Path.Combine(stage,e.Relative);_=UntamedOperationCoordinator.ResourceKey(path);
            Directory.CreateDirectory(path);
        }
        var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var e in current.Entries.Where(x=>!x.Directory))
        {
            verifyStopped();
            string original=Path.Combine(current.Source,e.Relative),copy=Path.Combine(stage,e.Relative);
            _=UntamedOperationCoordinator.ResourceKey(Path.GetDirectoryName(original)!);
            _=UntamedOperationCoordinator.ResourceKey(Path.GetDirectoryName(copy)!);
            if((File.GetAttributes(original)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("UM-MOVE-LINKED-FILE");
            using(var input=new FileStream(original,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=new FileStream(copy,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {if(input.Length!=e.Bytes)throw new IOException("UM-MOVE-SOURCE-CHANGED");input.CopyTo(output);output.Flush(true);}
            string digest=Hash(copy);
            if(Hash(original)!=digest)throw new IOException("UM-MOVE-HASH-MISMATCH");
            hashes.Add(e.Relative,digest);
        }
        if(!Inventory(stage).SequenceEqual(current.Entries)||!Inventory(current.Source).SequenceEqual(current.Entries))
            throw new IOException("UM-MOVE-INVENTORY-CHANGED");
        foreach(var e in hashes)
            if(Hash(Path.Combine(current.Source,e.Key))!=e.Value||Hash(Path.Combine(stage,e.Key))!=e.Value)
                throw new IOException("UM-MOVE-HASH-MISMATCH");
        verifyStopped();
        Absent(current.Destination);
        Directory.Move(stage,current.Destination);
        if(!Inventory(current.Destination).SequenceEqual(current.Entries))
            throw new IOException("UM-MOVE-PUBLISHED-INVENTORY");
    }
}
