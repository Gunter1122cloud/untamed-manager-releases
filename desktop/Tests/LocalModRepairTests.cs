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
            string primary=Path.Combine(root,"ShooterGame","Binaries","Win64","ShooterGame","Mods","83374");
            string bare=Path.Combine(primary,"123456"),bareSecondary=Path.Combine(tree,"123456");
            string versionedPrimary=Path.Combine(primary,"123456_999");
            string near=Path.Combine(primary,"1234567"),invalid=Path.Combine(tree,"123456_backup");
            string[] selected={one,bare,bareSecondary,versionedPrimary};
            string[] retained={other,near,invalid};
            foreach(string dir in selected)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir,"synthetic.pak"),dir);
            }
            foreach(string dir in retained)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir,"unrelated.pak"),"keep");
            }
            var map=new LocalMap{Id=Guid.NewGuid(),Name="Synthetic",InstallationPath=root};
            var folders=LocalModRepair.Inspect(root,"123456");
            if(folders.Length!=selected.Length||
               !folders.Select(x=>Path.Combine(root,x.Relative)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(selected))
                throw new InvalidOperationException("Unexpected mod scope");
            int moved=LocalModRepair.Quarantine(map,"123456",folders,()=>{},new UntamedOperationCoordinator());
            var preserved=Directory.GetFiles(Path.Combine(root,".obsidian-mod-quarantine"),"synthetic.pak",SearchOption.AllDirectories);
            if(moved!=selected.Length||selected.Any(Directory.Exists)||
                retained.Any(dir=>File.ReadAllText(Path.Combine(dir,"unrelated.pak"))!="keep")||
                preserved.Length!=selected.Length||
                !preserved.Select(File.ReadAllText).ToHashSet().SetEquals(selected)||
                LocalModRepair.Inspect(root,"123456").Length!=0)
                throw new InvalidOperationException("Mod repair changed unrelated files or failed to retain original");
            Console.WriteLine("PASS mod repair: bare project and numeric file-ID folders quarantined in both layouts, unrelated mods and original bytes retained");
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
