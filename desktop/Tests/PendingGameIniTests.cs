using System;
using System.IO;
using UntamedManager.Core;

internal static class PendingGameIniTests
{
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"obsidian-pending-ini-"+Guid.NewGuid().ToString("N"));
        try{
            var map=new LocalMap{Id=Guid.NewGuid(),Name="Astraeos",InstallationPath=Path.Combine(root,"server")};
            string path=Path.Combine(map.InstallationPath,"ShooterGame","Saved","Config","WindowsServer","Game.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,"[test]\r\nRate=1\r\n");
            var before=SafeConfigEditor.ReadGameDraft(path,map.InstallationPath);
            PendingGameIni.StageLocal(root,map,before,"[test]\r\nRate=2\r\n");
            if(File.ReadAllText(path)!=before.Text)throw new InvalidOperationException("Draft wrote live file");
            var stopped=new ProcessCheck(false,"synthetic stopped",null,true);
            int marks=0;
            File.WriteAllText(path,"[test]\r\nRate=3\r\n");
            if(PendingGameIni.ApplyLocal(root,map,()=>stopped,()=>marks++)||marks!=0||!File.ReadAllText(path).Contains("Rate=3"))
                throw new InvalidOperationException("Conflicting draft overwrote external edit");
            File.WriteAllText(path,before.Text);
            if(!PendingGameIni.ApplyLocal(root,map,()=>stopped,()=>marks++)||marks!=1||!File.ReadAllText(path).Contains("Rate=2"))
                throw new InvalidOperationException("Pending draft not applied once");
            if(PendingGameIni.ApplyLocal(root,map,()=>stopped,()=>marks++)||marks!=1)
                throw new InvalidOperationException("Draft replayed");
            File.WriteAllText(path,before.Text);
            if(PendingGameIni.ApplyLocal(root,map,()=>stopped,()=>marks++)||marks!=1||File.ReadAllText(path)!=before.Text)
                throw new InvalidOperationException("Consumed draft revived after external revert");
            string gus=Path.Combine(Path.GetDirectoryName(path)!,"GameUserSettings.ini");
            File.WriteAllText(gus,"[ServerSettings]\r\nTamingSpeedMultiplier=1\r\n");
            var gusBefore=SafeConfigEditor.ReadGameDraft(gus,map.InstallationPath);
            PendingGameIni.StageLocal(root,map,gusBefore,"[ServerSettings]\r\nTamingSpeedMultiplier=5\r\n");
            if(PendingGameIni.ApplyLocal(root,map,()=>stopped,()=>marks++)||marks!=1)
                throw new InvalidOperationException("GUS draft replaced Game.ini receipt");
            if(!PendingGameIni.ApplyLocal(root,map,()=>stopped,()=>marks++,"GameUserSettings.ini")||
               marks!=2||!File.ReadAllText(gus).Contains("TamingSpeedMultiplier=5")||File.ReadAllText(path)!=before.Text)
                throw new InvalidOperationException("GUS draft not independently applied");
            if(PendingGameIni.ApplyLocal(root,map,()=>stopped,()=>marks++,"GameUserSettings.ini")||marks!=2)
                throw new InvalidOperationException("GUS draft replayed");
            Console.WriteLine("PASS pending Game.ini: encrypted stage, conflict preservation, one-shot replacement");
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
