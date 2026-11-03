using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace UntamedManager.Core;

// Read-only local inventory of DLLs. Never opens plugin config, SQL, Store or license data.
public sealed record PluginBinaryInfo(string Name,string Version,long Bytes,string Sha256);
public sealed record LocalPluginInfo(string PluginName,IReadOnlyList<PluginBinaryInfo> Binaries,
    bool Protected);

public static class LocalPluginInventory
{
    public static IReadOnlyList<LocalPluginInfo> Scan(LocalMap map)
    {
        if(map==null || map.Id==Guid.Empty)throw new InvalidDataException("Local map identity required");
        string installation=FixedRoot(map.InstallationPath);
        string dir=Path.Combine(installation,"ShooterGame","Binaries","Win64","ArkApi","Plugins");
        RejectLinksInParents(dir);
        if(!Directory.Exists(dir))return Array.Empty<LocalPluginInfo>();
        var output=new List<LocalPluginInfo>();
        foreach(var plugin in new DirectoryInfo(dir).GetDirectories().OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase))
        {
            if((plugin.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked plugin directory rejected");
            if(plugin.Name.StartsWith(".untamed-",StringComparison.OrdinalIgnoreCase))
                continue;
            // Do not inspect Kova plugins beyond their folder name: the Store remains protected.
            if(plugin.Name.Contains("Kova",StringComparison.OrdinalIgnoreCase))
            {
                output.Add(new(plugin.Name,Array.Empty<PluginBinaryInfo>(),true));
                continue;
            }
            var binaries=new List<PluginBinaryInfo>();
            foreach(var file in plugin.GetFiles("*.dll",SearchOption.TopDirectoryOnly))
            {
                if((file.Attributes & FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Linked plugin binary rejected");
                using var data=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);
                string hash=Convert.ToHexString(SHA256.HashData(data));
                string version="ismeretlen";
                try
                {
                    var reported=FileVersionInfo.GetVersionInfo(file.FullName).FileVersion;
                    if(!string.IsNullOrWhiteSpace(reported))version=reported;
                }
                catch(Exception e) when(e is ArgumentException or IOException or System.ComponentModel.Win32Exception)
                { /* Version resources are optional for third-party binaries. */ }
                binaries.Add(new(file.Name,version,file.Length,hash));
            }
            output.Add(new(plugin.Name,binaries,true==false));
        }
        return output;
    }
    private static string FixedRoot(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\"))
            throw new InvalidDataException("Local fixed installation path required");
        string result=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(result)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Only local fixed installations can be inspected");
        RejectLinksInParents(result);
        return result;
    }
    private static void RejectLinksInParents(string path)
    {
        for(var parent=new DirectoryInfo(Path.GetFullPath(path));parent!=null;parent=parent.Parent)
            if(parent.Exists && (parent.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Plugin path contains a linked directory");
    }
}
