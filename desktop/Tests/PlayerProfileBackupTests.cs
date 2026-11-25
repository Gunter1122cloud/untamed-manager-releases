using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

internal static class PlayerProfileBackupTests
{
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"obsidian-player-profiles-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string install=Path.Combine(root,"server");
            var map=new LocalMap{Id=Guid.NewGuid(),Name="Astraeos",InstallationPath=install,Arguments="Astraeos_WP"};
            string world=Path.Combine(install,"ShooterGame","Saved","SavedArks","Astraeos_WP");
            Directory.CreateDirectory(world);
            File.WriteAllText(Path.Combine(world,"test.arkprofile"),"profile bytes");
            File.WriteAllText(Path.Combine(world,"Astraeos_WP.ark"),"world bytes");
            File.WriteAllText(Path.Combine(world,"test.arktribe"),"tribe bytes");
            var stopped=new ProcessCheck(false,"synthetic stopped",null,true);
            string target=Path.Combine(root,"archive");
            var saved=LocalBackupWorkflow.Create(map,stopped,target,LocalBackupScope.PlayerProfiles,checkCurrent:()=>stopped);
            if(saved.Manifest.Files.Count!=1||!saved.Manifest.Files.Single().Path.EndsWith("test.arkprofile")||
                !File.ReadAllText(Path.Combine(target,"data",saved.Manifest.Files.Single().Path)).Equals("profile bytes"))
                throw new InvalidOperationException("Profile backup selection mismatch");
            if(File.ReadAllText(Path.Combine(world,"Astraeos_WP.ark"))!="world bytes")
                throw new InvalidOperationException("Source world changed");
            Console.WriteLine("PASS profile-only backup: verified exact map profile, no world or tribe copy");
            map.Arguments="Astraeos_WP -usestore";
            bool refused=false;
            try{LocalBackupWorkflow.Preview(map,stopped,Path.Combine(root,"store"),LocalBackupScope.PlayerProfiles);}
            catch(InvalidDataException){refused=true;}
            if(!refused)throw new InvalidOperationException("Store loose profile falsely certified");
            Console.WriteLine("PASS profile-only backup: Store world must use full world backup");
        }
        finally{Directory.Delete(root,true);}
    }
}
