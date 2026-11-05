using System;
using System.Linq;
using UntamedManager.Core;

public static class ModLoadOrderPreservationTests
{
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException("MOD LOAD ORDER: "+reason);
        Console.WriteLine("PASS mod load order: "+reason);
    }
    public static void Run()
    {
        const string initial="-port=7777 -mods=928548,930170,942024 -passivemods=930170";
        var map=new LocalMap{Id=Guid.NewGuid(),Name="Synthetic Interleaved Map",
            Arguments=initial,DisabledModIds=new(){"950914"}};
        var loaded=ModStateController.Read(map);
        Check(loaded.EnabledOrder.SequenceEqual(new[]{"928548","930170","942024"}) &&
            loaded.Active.SequenceEqual(new[]{"928548","942024"}) &&
            loaded.Passive.SequenceEqual(new[]{"930170"}),
            "read preserves interleaved -mods order independently of passive subset");

        var passiveToActive=ModStateController.Preview(map,
            new ModEdit(map.Id,"930170",ModMode.Active));
        Check(passiveToActive.NewArguments==
            "-port=7777 -mods=928548,930170,942024" &&
            passiveToActive.Updated.EnabledOrder.SequenceEqual(loaded.EnabledOrder),
            "changing Passive to Active leaves full launch order untouched");

        var activeToPassive=ModStateController.Preview(map,
            new ModEdit(map.Id,"928548",ModMode.Passive));
        Check(activeToPassive.NewArguments==
            "-port=7777 -mods=928548,930170,942024 -passivemods=930170,928548",
            "changing Active to Passive does not move it ahead of other mods");

        var unchanged=ModStateController.Preview(map,
            new ModEdit(map.Id,"942024",ModMode.Active));
        Check(!unchanged.Changed && unchanged.NewArguments==initial,
            "reselecting an existing mod without position is order-stable no-op");

        var appended=ModStateController.Preview(map,
            new ModEdit(map.Id,"950914",ModMode.Active));
        Check(appended.NewArguments.Contains(
            "-mods=928548,930170,942024,950914",StringComparison.Ordinal) &&
            appended.Updated.Disabled.Count==0,
            "enabling disabled mod appends without reordering existing active/passive mods");

        var explicitMove=ModStateController.Preview(map,
            new ModEdit(map.Id,"942024",ModMode.Active,0));
        Check(explicitMove.Updated.Active.SequenceEqual(new[]{"942024","928548"}) &&
            explicitMove.Updated.EnabledOrder.SequenceEqual(
                new[]{"942024","928548","930170"}),
            "explicit within-mode reorder moves only chosen mod and retains other order");

        var disabled=ModStateController.Preview(map,
            new ModEdit(map.Id,"928548",ModMode.Disabled));
        Check(disabled.Updated.EnabledOrder.SequenceEqual(new[]{"930170","942024"}) &&
            disabled.Updated.Disabled.SequenceEqual(new[]{"950914","928548"}),
            "disabling one mod does not reorder remaining enabled mods");

        Check(map.Arguments==initial && map.DisabledModIds!.SequenceEqual(new[]{"950914"}),
            "all previews remain read-only; original map and disabled list are untouched");
    }
}
