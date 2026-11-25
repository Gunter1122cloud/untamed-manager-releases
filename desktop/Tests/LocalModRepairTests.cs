using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;
internal static class LocalModRepairTests
{
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"obsidian-mod-repair-"+Guid.NewGuid().ToString("N"));
        try{
            string tree=Path.Combine(root,"ShooterGame","Mods","83374");
            string one=Path.Combine(tree,"123456_1"),other=Path.Combine(tree,"123457_1");
            Directory.CreateDirectory(one);Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(one,"synthetic.pak"),"test");
            File.WriteAllText(Path.Combine(other,"unrelated.pak"),"keep");
            var map=new LocalMap{Id=Guid.NewGuid(),Name="Synthetic",InstallationPath=root};
            var folders=LocalModRepair.Inspect(root,"123456");
            if(folders.Length!=1)throw new InvalidOperationException("Unexpected mod scope");
            int moved=LocalModRepair.Quarantine(map,"123456",folders,()=>{},new UntamedOperationCoordinator());
            if(moved!=1||Directory.Exists(one)||!File.Exists(Path.Combine(other,"unrelated.pak"))||
                Directory.GetFiles(Path.Combine(root,".obsidian-mod-quarantine"),"synthetic.pak",SearchOption.AllDirectories).Length!=1)
                throw new InvalidOperationException("Mod repair changed unrelated files or failed to retain original");
            Console.WriteLine("PASS mod repair: exact directory quarantined, unrelated mod and original bytes retained");
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
