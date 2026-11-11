using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace UntamedManager.Core;

/// <summary>
/// Read-only SHA-256 inventory of protected ASA files. This covers the whole
/// Saved tree (worlds, characters, INI, logs and plugin state stored there)
/// and the ArkAPI plugin data, not just the selected backup scope. It never
/// follows symbolic links/reparse points. The inventory does not prove that
/// ASA SaveWorld completed; callers must separately verify stopped status.
/// </summary>
public static class SteamProtectedFiles
{
    private static readonly string[] Subtrees={
        Path.Combine("ShooterGame","Saved"),
        // Plugins alone do not cover the ASA API runtime DLL, loader
        // configuration, or disabled plugin directories. SteamCMD must not
        // silently remove or alter any of those third-party local files.
        Path.Combine("ShooterGame","Binaries","Win64","ArkApi"),
        Path.Combine("ShooterGame","Binaries","Win64","UntamedDisabledPlugins")
    };
    private static readonly string[] ExplicitFiles={
        Path.Combine("ShooterGame","Binaries","Win64","AsaApiLoader.exe")
    };
    public static IReadOnlyDictionary<string,string> Snapshot(string installRoot)
    {
        if(string.IsNullOrWhiteSpace(installRoot) ||
           !Path.IsPathFullyQualified(installRoot) ||
           installRoot.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("A local ASA installation root is required");
        var full=Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Protected ASA snapshot must stay on a fixed local disk");
        var files=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var subtree in Subtrees)
        {
            string path=Path.Combine(full,subtree);
            if(!Directory.Exists(path))continue;
            var dirs=new Stack<string>();
            dirs.Push(path);
            while(dirs.Count>0)
            {
                string directory=dirs.Pop();
                if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("A protected ASA folder is a reparse point");
                foreach(var nested in Directory.EnumerateDirectories(directory))
                    dirs.Push(nested);
                foreach(var file in Directory.EnumerateFiles(directory))
                {
                    if(files.Count>=100_000)
                        throw new InvalidDataException("Protected ASA file inventory limit exceeded");
                    if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                        throw new InvalidDataException("A protected ASA file is a reparse point");
                    var info=new FileInfo(file);
                    using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
                    if(info.Length!=input.Length)
                        throw new InvalidDataException("Protected file changed while opening");
                    string relative=Path.GetRelativePath(full,file);
                    var hash=Convert.ToHexString(SHA256.HashData(input));
                    if(info.Length!=new FileInfo(file).Length)
                        throw new InvalidDataException("Protected file changed while hashing");
                    files.Add(relative,info.Length+":"+hash);
                }
            }
        }
        foreach(var relative in ExplicitFiles)
        {
            string file=Path.Combine(full,relative);
            if(!File.Exists(file))continue;
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("A protected ASA API loader is a reparse point");
            var info=new FileInfo(file);
            using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(info.Length!=stream.Length)
                throw new InvalidDataException("ASA API loader changed while opening");
            string hash=Convert.ToHexString(SHA256.HashData(stream));
            if(info.Length!=new FileInfo(file).Length)
                throw new InvalidDataException("ASA API loader changed while hashing");
            files.Add(relative,info.Length+":"+hash);
        }
        return files;
    }
    public static bool Identical(IReadOnlyDictionary<string,string> before,
        IReadOnlyDictionary<string,string> after)=>
        before.Count==after.Count && before.All(x=>
            after.TryGetValue(x.Key,out var value) && value==x.Value);
}
