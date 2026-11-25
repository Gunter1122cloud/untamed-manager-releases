using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;
public static class MapModIdentity
{
    public static int ParseProject(string raw)
    {
        if(!Regex.IsMatch(raw,@"\A[1-9][0-9]{4,9}\z")||!int.TryParse(raw,out int id)||id<10000)
            throw new InvalidDataException("UM-INITIAL-MAP-MOD-ID");
        return id;
    }
    public static void DemandMapProject(CurseForgeMod metadata,int project)
    {
        if(metadata.Id!=project||metadata.IsAvailable==false||
           !metadata.Categories.Any(x=>x.Equals("Maps",StringComparison.OrdinalIgnoreCase)||x.Equals("Map",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UM-INITIAL-MAP-MOD-CATEGORY");
    }
    public static string RemasterSwitch(string level,string project)
    {
        if(string.IsNullOrEmpty(project))return "";
        _=ParseProject(project);
        return AsaSteamMapCatalog.IsOfficialLevel(level)?" -MapModID="+project:"";
    }
    public static string BuildInitialArguments(string level,string session,int game,int query,string project)
    {
        string baseline=AsaSteamMapCatalog.LaunchArgumentsForLevel(level,session,game,query);
        if(string.IsNullOrEmpty(project))return baseline;
        return baseline+RemasterSwitch(level,project)+" -mods="+project;
    }
    public static void DemandLaunch(LocalMap map)
    {
        if(string.IsNullOrEmpty(map.MapModProjectId))return;
        _=ParseProject(map.MapModProjectId);
        if(map.HistoricalStoreProtected)throw new InvalidDataException("UM-MAP-MOD-HISTORICAL-SOURCE-PINNED");
        string level=AsaSaveLayoutResolver.MapToken(map);
        var enabled=ModStateController.Read(map);
        if(!enabled.Active.Contains(map.MapModProjectId,StringComparer.Ordinal))
            throw new InvalidDataException("UM-MAP-MOD-MUST-BE-ACTIVE");
        string unquoted=Regex.Replace(map.Arguments??"",@"""[^""]*""","");
        var flags=Regex.Matches(unquoted,@"(?i)(?<!\S)-MapModID(?:=([^\s]+))?(?=\s|$)");
        bool remaster=AsaSteamMapCatalog.IsOfficialLevel(level);
        if(remaster?(flags.Count!=1||flags[0].Groups[1].Value!=map.MapModProjectId):flags.Count!=0)
            throw new InvalidDataException("UM-MAP-MOD-REMASTER-FLAG");
    }
}
