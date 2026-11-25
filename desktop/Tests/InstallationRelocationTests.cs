using System;
using System.IO;
using UntamedManager.Core;

internal static class InstallationRelocationTests
{
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"obsidian-move-"+Guid.NewGuid().ToString("N"));
        try{
            string source=Path.Combine(root,"source"),target=Path.Combine(root,"target");
            Directory.CreateDirectory(Path.Combine(source,"empty"));
            File.WriteAllText(Path.Combine(source,"test.ark"),"synthetic bytes");
            var plan=InstallationRelocation.Preview(source,target);
            InstallationRelocation.CopyVerified(plan,()=>{});
            if(!Directory.Exists(Path.Combine(target,"empty"))||File.ReadAllText(Path.Combine(target,"test.ark"))!="synthetic bytes"||
               File.ReadAllText(Path.Combine(source,"test.ark"))!="synthetic bytes")
                throw new InvalidOperationException("Installation copy lost original or data");
            bool refused=false;
            try{InstallationRelocation.Preview(source,target);}catch(IOException){refused=true;}
            if(!refused)throw new InvalidOperationException("Existing target accepted");
            Console.WriteLine("PASS installation relocation: exact copy, empty directory, source retained, no overwrite");
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
