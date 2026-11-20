using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UntamedManager.Core;

/// <summary>Bounded, read-only local CurseForge payload evidence. A manifest
/// reference is not proof that an Unreal package mounts or a server is ready.
/// No downloads, reparse traversal, archive extraction or level-name guessing.</summary>
public static class SupplementalMapContentProbe
{
    public sealed record Evidence(string State,string[] PresentProjects,string[] UnverifiedProjects,
        string[] LevelManifestProjects,string Fingerprint);

    public static Evidence Inspect(string root,string level,IReadOnlyList<string> orderedMods)
    {
        var mods=orderedMods.Distinct(StringComparer.Ordinal).ToArray();
        if(mods.Any(x=>string.IsNullOrEmpty(x)||x.Length>12||!x.All(char.IsAsciiDigit)))
            throw new InvalidDataException("UM-STORE-EXTRA-MOD-ID-INVALID");
        var present=new HashSet<string>(StringComparer.Ordinal);
        var levels=new HashSet<string>(StringComparer.Ordinal);
        var tags=new List<string>();
        bool uncertain=false;
        string folder=Path.Combine(root,"ShooterGame","Binaries","Win64","ShooterGame","Mods","83374");
        try
        {
            for(var d=new DirectoryInfo(folder);d!=null;d=d.Parent)
                if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("Linked mod path");
            if(Directory.Exists(folder))
            {
                int entries=0;
                foreach(var project in Directory.EnumerateDirectories(folder).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))
                {
                    if(++entries>2048)throw new IOException("Mod inventory limit");
                    string name=Path.GetFileName(project);
                    string[] parts=name.Split('_');
                    if(parts.Length!=2||!mods.Contains(parts[0],StringComparer.Ordinal)||
                       parts[1].Length is <1 or >20||!parts[1].All(char.IsAsciiDigit))continue;
                    if((File.GetAttributes(project)&FileAttributes.ReparsePoint)!=0)
                    {uncertain=true;continue;}
                    var stack=new Stack<(string Path,int Depth)>();
                    stack.Push((project,0));
                    var packages=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool manifestLevel=false;
                    int visited=0;
                    while(stack.Count>0)
                    {
                        var item=stack.Pop();
                        if(item.Depth>8)throw new IOException("Mod directory depth limit");
                        foreach(var entry in Directory.EnumerateFileSystemEntries(item.Path))
                        {
                            if(++visited>10000)throw new IOException("Mod inventory entry limit");
                            var attr=File.GetAttributes(entry);
                            if((attr&FileAttributes.ReparsePoint)!=0){uncertain=true;continue;}
                            if((attr&FileAttributes.Directory)!=0){stack.Push((entry,item.Depth+1));continue;}
                            var info=new FileInfo(entry);
                            string ext=info.Extension.ToLowerInvariant();
                            if(info.Length>0&&ext is ".pak" or ".utoc" or ".ucas")
                            {
                                packages.Add(Path.ChangeExtension(entry,null)+"|"+ext);
                                tags.Add(name+"/"+Path.GetRelativePath(project,entry)+":"+info.Length+":"+info.LastWriteTimeUtc.Ticks);
                            }
                            if(!info.Name.StartsWith("Manifest_UFSFiles_",StringComparison.OrdinalIgnoreCase)||
                               !info.Name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase)||
                               info.Length is <=0 or >8_000_000)continue;
                            byte[] bytes;
                            using(var input=new FileStream(entry,FileMode.Open,FileAccess.Read,FileShare.Read))
                            {
                                if(input.Length is <=0 or >8_000_000)throw new IOException("Mod manifest size changed");
                                bytes=new byte[(int)input.Length];input.ReadExactly(bytes);
                            }
                            tags.Add(name+"/"+Path.GetRelativePath(project,entry)+":"+Convert.ToHexString(SHA256.HashData(bytes)));
                            foreach(var line in Encoding.UTF8.GetString(bytes).Split('\n'))
                            {
                                string asset=line.Trim().Split('\t')[0].Trim().Replace('\\','/');
                                if(Path.GetFileName(asset).Equals(level+".umap",StringComparison.OrdinalIgnoreCase))
                                    manifestLevel=true;
                            }
                        }
                    }
                    // Require a non-empty PAK, or a matching IoStore index/data pair.
                    bool payload=packages.Any(x=>x.EndsWith("|.pak",StringComparison.OrdinalIgnoreCase)||
                        (x.EndsWith("|.utoc",StringComparison.OrdinalIgnoreCase)&&packages.Contains(x[..^6]+"|.ucas")));
                    if(payload){present.Add(parts[0]);if(manifestLevel)levels.Add(parts[0]);}
                }
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {uncertain=true;}
        string state=uncertain?"inventory-incomplete":levels.Count>0?"level-manifest-and-payload-found":
            mods.Length==0?"no-mod-projects-declared":"level-content-unverified";
        string fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n",new[]{state,level,string.Join(",",mods)}.Concat(tags.OrderBy(x=>x,StringComparer.Ordinal))))));
        return new(state,mods.Where(present.Contains).ToArray(),mods.Where(x=>!present.Contains(x)).ToArray(),
            mods.Where(levels.Contains).ToArray(),fingerprint);
    }
}
