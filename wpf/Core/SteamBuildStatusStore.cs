using System;
using System.IO;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record SteamMapUpdateStatus(Guid MapId,string InstallRoot,string Branch,
    string? LocalBuild,string? AvailableBuild,AsaSteamBuildState State,
    DateTimeOffset? LastCheckedUtc,DateTimeOffset? LastUpdatedUtc,
    string Message)
{
    public static SteamMapUpdateStatus Unknown(LocalMap map)=>new(map.Id,
        map.InstallationPath,"public",null,null,AsaSteamBuildState.Unknown,
        null,null,"Nincs ellenőrzött Steam Build ID");
}

public sealed class SteamBuildStatusStore
{
    private readonly string folder;
    public SteamBuildStatusStore(string managerProfile)
    {
        if(string.IsNullOrWhiteSpace(managerProfile)||!Path.IsPathFullyQualified(managerProfile)||
           managerProfile.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("An explicit local Manager profile is required");
        folder=Path.Combine(Path.GetFullPath(managerProfile),"steam-build-status-v1");
    }
    private string PathFor(Guid mapId)
    {
        if(mapId==Guid.Empty)throw new InvalidDataException("Missing local map ID");
        return Path.Combine(folder,mapId.ToString("N")+".json");
    }
    public SteamMapUpdateStatus Read(LocalMap map)
    {
        string path=PathFor(map.Id);
        if(!File.Exists(path))return SteamMapUpdateStatus.Unknown(map);
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||
           new FileInfo(path).Length is <=0 or >16384)
            throw new InvalidDataException("Steam status journal invalid");
        SteamMapUpdateStatus? value=JsonSerializer.Deserialize<SteamMapUpdateStatus>(
            File.ReadAllText(path));
        if(value==null||value.MapId!=map.Id||
           !string.Equals(Path.GetFullPath(value.InstallRoot),
               Path.GetFullPath(map.InstallationPath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Steam status does not match local map identity");
        return value;
    }
    public void Write(LocalMap map,SteamMapUpdateStatus value)
    {
        if(value.MapId!=map.Id || !string.Equals(Path.GetFullPath(value.InstallRoot),
            Path.GetFullPath(map.InstallationPath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Steam status map/installation mismatch");
        string file=PathFor(map.Id);
        Directory.CreateDirectory(folder);
        if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked status folder refused");
        string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,value);stream.Flush(true);}
            File.Move(temp,file,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public SteamMapUpdateStatus SaveCheck(LocalMap map,AsaSteamBuildCheck check)
    {
        var old=Read(map);
        var status=new SteamMapUpdateStatus(map.Id,map.InstallationPath,check.Branch,
            check.InstalledBuild,check.AvailableBuild,check.State,check.CheckedUtc,
            old.LastUpdatedUtc,check.Explanation);
        Write(map,status);
        return status;
    }
}
