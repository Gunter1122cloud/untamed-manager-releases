using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

public static class LocalModInventoryTests
{
    private static void Verify(bool ok,string message)
    {
        if(!ok)throw new InvalidOperationException("LOCAL MOD INVENTORY: "+message);
        Console.WriteLine("PASS local mod inventory: "+message);
    }
    public static void Run()
    {
        const string shared="928548",other="999101",onlyDisabled="999102";
        var island=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",
            Arguments="-log -mods=928548,999101 -passivemods=999101 -port=7777",
            DisabledModIds=new(){onlyDisabled}
        };
        var rag=new LocalMap{
            Id=Guid.NewGuid(),Name="Ragnarok",
            Arguments="-mods=928548,999102 -port=7817 -passivemods=999102"
        };
        var center=new LocalMap{
            Id=Guid.NewGuid(),Name="The Center",
            Arguments="-mods=999101"
        };
        string[] before={island.Arguments,rag.Arguments,center.Arguments};
        var inventory=LocalModInventory.Read(new[]{island,rag,center});
        Verify(inventory.MapCount==3 && inventory.UnreadableMaps.Count==0 &&
            inventory.Projects.Count==3,
            "all configured local map mod states inventoried without network requests");
        Verify(inventory.SharedProjects.Count==2 &&
            inventory.SharedProjects.Any(x=>x.ProjectId==shared && x.EnabledMapCount==2) &&
            inventory.SharedProjects.Any(x=>x.ProjectId==other && x.EnabledMapCount==2),
            "shared projects count enabled maps rather than disabled-only references");
        var states=inventory.Projects.Single(x=>x.ProjectId==onlyDisabled).Assignments;
        Verify(states.Count==2 &&
            states.Any(x=>x.MapId==island.Id && x.Mode==ModMode.Disabled) &&
            states.Any(x=>x.MapId==rag.Id && x.Mode==ModMode.Passive) &&
            inventory.Projects.Single(x=>x.ProjectId==onlyDisabled).EnabledMapCount==1,
            "disabled and passive assignments remain distinguishable");
        Verify(island.Arguments==before[0] && rag.Arguments==before[1] &&
            center.Arguments==before[2],
            "inventory never rewrites -mods order, INI or local launch arguments");

        var invalid=new LocalMap{
            Id=Guid.NewGuid(),Name="Broken mods",
            Arguments="-mods=928548 -mods=999101"
        };
        var partial=LocalModInventory.Read(new[]{island,invalid,rag});
        Verify(partial.UnreadableMaps.SequenceEqual(new[]{"Broken mods"}) &&
            partial.Projects.Single(x=>x.ProjectId==shared).EnabledMapCount==2,
            "one invalid map is explicitly reported without erasing sound local states");
        bool duplicateRejected=false;
        try{_ =LocalModInventory.Read(new[]{island,island});}
        catch(InvalidDataException){duplicateRejected=true;}
        Verify(duplicateRejected,"duplicate local map identity fails closed");
    }
}
