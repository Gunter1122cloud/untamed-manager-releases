using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;
internal static class MapModIdentityTests
{
    internal static void Run()
    {
        const string project="123456";
        var map=new LocalMap{Name="Island Remaster",MapModProjectId=project,
            Arguments=MapModIdentity.BuildInitialArguments("TheIsland_WP","synthetic",7777,27015,project)};
        MapModIdentity.DemandLaunch(map);
        if(!map.Arguments.Contains("-MapModID=123456 -mods=123456"))throw new InvalidOperationException("Remaster missing linked project");
        var custom=new LocalMap{Name="Custom",MapModProjectId=project,
            Arguments=MapModIdentity.BuildInitialArguments("Custom_WP","synthetic",7797,27035,project)};
        MapModIdentity.DemandLaunch(custom);
        if(custom.Arguments.Contains("-MapModID")||!ModStateController.Read(custom).Active.Contains(project))
            throw new InvalidOperationException("Custom map arguments invalid");
        bool refused=false;
        try{new ModDraftSession(map).Stage(project,ModMode.Disabled);}catch(InvalidOperationException){refused=true;}
        if(!refused)throw new InvalidOperationException("Linked map project disabled");
        var metadata=new CurseForgeMod(123456,"Map","",null,null,null){Categories=new[]{"Maps"},IsAvailable=true};
        MapModIdentity.DemandMapProject(metadata,123456);
        refused=false;try{MapModIdentity.DemandMapProject(metadata with {Categories=new[]{"Structures"}},123456);}catch(InvalidDataException){refused=true;}
        if(!refused)throw new InvalidOperationException("Non-map project accepted");
        Console.WriteLine("PASS map identity: custom/official remaster, category, active map pack guard");
    }
}
