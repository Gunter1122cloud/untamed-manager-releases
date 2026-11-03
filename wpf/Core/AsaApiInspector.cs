using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record ApiBinary(string RelativePath,bool Present,bool ValidPe64,
    string? Sha256,string? Version);
public sealed record AsaApiInspection(string Installation,bool Complete,
    IReadOnlyList<ApiBinary> Binaries,bool ConfigValid,bool PluginsDirectoryPresent,
    string? LoaderRuntimeState,string Summary);

public static class AsaApiInspector
{
    public static AsaApiInspection Inspect(string installation,ProcessCheck? mapState=null)
    {
        if(string.IsNullOrWhiteSpace(installation)||!Path.IsPathFullyQualified(installation)||
           installation.StartsWith(@"\\"))
            throw new InvalidDataException("ASA API inspection requires explicit local installation");
        string root=Path.GetFullPath(installation);
        for(var d=new DirectoryInfo(root);d!=null;d=d.Parent)
            if(d.Exists && (d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked ASA installation root rejected");
        string binary=Path.Combine(root,"ShooterGame","Binaries","Win64");
        var files=new[]{"AsaApiLoader.exe",Path.Combine("ArkApi","AsaApi.dll")};
        var result=new List<ApiBinary>();
        foreach(var rel in files)
        {
            string path=Path.Combine(binary,rel);
            if(!File.Exists(path))
            {
                result.Add(new(rel,false,false,null,null));continue;
            }
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked ASA API binary");
            var info=new FileInfo(path);
            if(info.Length<128 || info.Length>128L*1024*1024)
            {result.Add(new(rel,true,false,null,null));continue;}
            bool valid=IsPe64(path);
            using var stream=File.OpenRead(path);
            var hash=Convert.ToHexString(SHA256.HashData(stream));
            string? version=null;
            try{version=FileVersionInfo.GetVersionInfo(path).FileVersion;}catch{}
            result.Add(new(rel,true,valid,hash,version));
        }
        bool config=false;
        string configFile=Path.Combine(binary,"config.json");
        if(File.Exists(configFile) && new FileInfo(configFile).Length<=1_000_000 &&
           (File.GetAttributes(configFile)&FileAttributes.ReparsePoint)==0)
        {
            try
            {
                using var json=JsonDocument.Parse(File.ReadAllText(configFile));
                config=json.RootElement.ValueKind==JsonValueKind.Object;
            }
            catch(JsonException){config=false;}
        }
        bool plugins=Directory.Exists(Path.Combine(binary,"ArkApi","Plugins"));
        bool complete=result.All(x=>x.Present&&x.ValidPe64)&&config&&plugins;
        return new(root,complete,result,config,plugins,
            mapState==null?"Process state not supplied":
                mapState.Running && mapState.LoaderIdentity!=null?"Loader process observed":
                mapState.Running?"ASA running; API loader identity unverified":
                mapState.ConfirmedStopped?"Server confirmed stopped":"Unknown process state",
            complete?"API files present and PE64-shaped; live module loading NOT proven":
            "Missing/invalid API binary, config or plugin directory; no automatic repair");
    }

    public static bool IsPe64(string path)
    {
        if(!File.Exists(path))return false;
        using var stream=File.OpenRead(path);
        if(stream.Length<0x90)return false;
        using var reader=new BinaryReader(stream);
        if(reader.ReadUInt16()!=0x5A4D)return false; // MZ
        stream.Position=0x3c;
        int pe=reader.ReadInt32();
        if(pe<0x40 || (long)pe+26>stream.Length)return false;
        stream.Position=pe;
        if(reader.ReadUInt32()!=0x4550)return false; // PE\0\0
        if(reader.ReadUInt16()!=0x8664)return false; // AMD64 only
        stream.Position=pe+24; // optional header
        return reader.ReadUInt16()==0x20b; // PE32+
    }
}
