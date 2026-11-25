using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

internal static class LocalLogCenterBoundedTests
{
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"obsidian-log-budget-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file=Path.Combine(root,"ShooterGame.log");
            File.WriteAllText(file,new string('x',8192)+"\nServerAdminPassword=synthetic-secret\n[ASA] Error: latest\n");
            var map=new LocalMap{Id=Guid.NewGuid(),Name="Synthetic log",InstallationPath=root,LogFilePath=file};
            byte[] before=File.ReadAllBytes(file);
            var tail=LocalLogCenter.Read(map,512,10);
            if(!tail.Truncated||tail.Lines.Count!=2||
                !tail.Lines.Last().Content.Contains("latest")||
                tail.Lines.Any(l=>l.Content.Contains("synthetic-secret"))||
                !before.SequenceEqual(File.ReadAllBytes(file)))
                throw new InvalidOperationException("Bounded log tail lost scope, redaction or read-only invariant");
            Console.WriteLine("PASS native log center: byte-bounded partial-line skip and secret redaction");
            File.WriteAllBytes(file,Array.Empty<byte>());
            var empty=LocalLogCenter.Read(map,512,10);
            if(empty.Lines.Count!=0||empty.TotalBytes!=0)
                throw new InvalidOperationException("Empty log produced invented lines");
            Console.WriteLine("PASS native log center: empty file returns no rows");
        }
        finally{Directory.Delete(root,true);}
    }
}
