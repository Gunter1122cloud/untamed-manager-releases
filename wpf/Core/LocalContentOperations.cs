using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;
public static class LocalContentOperations
{
    private static readonly Regex ModsParameter =
        new(@"(?<!\S)-mods=(?<ids>[^ \t""]+)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    public static string ModifyMods(string existing,IEnumerable<string> input,bool add)
    {
        existing??="";
        var requested=input.Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();
        if(requested.Length==0)throw new ArgumentException("At least one mod ID required");
        if(requested.Any(x=>!Regex.IsMatch(x,@"\A[0-9]{5,12}\z")))
            throw new InvalidDataException("CurseForge mod IDs must be 5-12 digits");
        var matches=ModsParameter.Matches(existing);
        if(matches.Count>1)throw new InvalidDataException("Multiple -mods parameters found; manual review required");
        var mods=new List<string>();
        if(matches.Count==1)
        {
            string body=matches[0].Groups["ids"].Value;
            if(body.Split(',',StringSplitOptions.RemoveEmptyEntries).Any(x=>!Regex.IsMatch(x,@"\A[0-9]{5,12}\z")))
                throw new InvalidDataException("Unknown -mods value; refusing to corrupt launch arguments");
            mods.AddRange(body.Split(',',StringSplitOptions.RemoveEmptyEntries));
        }
        if(add){foreach(var id in requested)if(!mods.Contains(id,StringComparer.Ordinal))mods.Add(id);}
        else mods.RemoveAll(x=>requested.Contains(x,StringComparer.Ordinal));
        var without=matches.Count==1?existing.Remove(matches[0].Index,matches[0].Length).Trim():existing.Trim();
        return mods.Count==0?without:
            (without.Length==0?"":without+" ")+"-mods="+string.Join(",",mods);
    }
    public static string InstallLocalPluginFolder(LocalMap map,string sourceFolder)
    {
        if(string.IsNullOrWhiteSpace(map.InstallationPath)||!Directory.Exists(map.InstallationPath))
            throw new InvalidOperationException("Local map installation root unavailable");
        if(!Directory.Exists(sourceFolder))throw new DirectoryNotFoundException(sourceFolder);
        var source=new DirectoryInfo(Path.GetFullPath(sourceFolder));
        string name=source.Name;
        if(!Regex.IsMatch(name,@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,79}\z") ||
            name.Contains("Kova",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe plugin folder name");
        var dlls=source.GetFiles("*.dll",SearchOption.TopDirectoryOnly);
        if(dlls.Length==0)throw new InvalidDataException("Selected plugin folder has no root .dll file");
        string baseDir=Path.GetFullPath(Path.Combine(map.InstallationPath,
            "ShooterGame","Binaries","Win64","ArkApi","Plugins"));
        string target=Path.Combine(baseDir,name);
        string root=Path.GetFullPath(map.InstallationPath).TrimEnd(Path.DirectorySeparatorChar)
                    +Path.DirectorySeparatorChar;
        if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Plugin target escapes map installation");
        // Avoid copying through symbolic links/junctions.
        CheckNoLinks(source);
        Directory.CreateDirectory(baseDir);
        string? backup=null;
        try{
            if(Directory.Exists(target))
            {
                backup=target+".untamed-backup-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N")[..6];
                Directory.Move(target,backup);
            }
            CopyTree(source.FullName,target);
            return target;
        }catch{
            if(Directory.Exists(target))Directory.Delete(target,true);
            if(backup!=null&&Directory.Exists(backup))Directory.Move(backup,target);
            throw;
        }
    }
    private static void CheckNoLinks(DirectoryInfo dir)
    {
        if((dir.Attributes & FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Symlink/junction plugin folder rejected");
        foreach(var file in dir.GetFiles())
            if((file.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked plugin file rejected");
        foreach(var child in dir.GetDirectories())CheckNoLinks(child);
    }
    private static void CopyTree(string source,string dest)
    {
        Directory.CreateDirectory(dest);
        foreach(var file in Directory.EnumerateFiles(source))
            File.Copy(file,Path.Combine(dest,Path.GetFileName(file)),false);
        foreach(var sub in Directory.EnumerateDirectories(source))
            CopyTree(sub,Path.Combine(dest,Path.GetFileName(sub)));
    }
}
