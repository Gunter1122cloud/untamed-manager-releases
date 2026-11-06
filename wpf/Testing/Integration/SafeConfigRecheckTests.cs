using System;
using System.IO;
using System.Linq;
using System.Text;
using UntamedManager.Core;

public static class SafeConfigRecheckTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new Exception("INI/JSON recheck regression failed: "+reason);
        Console.WriteLine("PASS safe-config-recheck "+reason);
    }

    public static void Run(string root)
    {
        string Base(string name)=>Path.Combine(root,"config-commit-recheck-"+name);
        string Create(string rootInstall)
        {
            string directory=Path.Combine(rootInstall,"ShooterGame","Saved","Config","WindowsServer");
            Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"Game.ini");
            File.WriteAllText(path,"; preserved\r\n[ServerSettings]\r\nXPMultiplier=2\r\n",
                new UTF8Encoding(false));
            return path;
        }
        var stopped=new ProcessCheck(false,"confirmed stopped",null,true);
        var running=new ProcessCheck(true,"newly running",new ProcessIdentity{
            Pid=9990,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-1),
            ExecutablePath=@"C:\isolated\ArkAscendedServer.exe",
            Arguments="TheIsland_WP"
        });
        string blockedInstall=Base("blocked");
        string blockedFile=Create(blockedInstall);
        byte[] original=File.ReadAllBytes(blockedFile);
        var originalSnapshot=SafeConfigEditor.Read(
            blockedFile,blockedInstall,ConfigKind.GameIni,stopped);
        int observed=0;bool rejected=false;
        try
        {
            SafeConfigEditor.Save(originalSnapshot,
                SafeConfigEditor.SetIniValue(originalSnapshot,
                    "ServerSettings","XPMultiplier","4"),
                blockedInstall,stopped,false,()=>{
                    observed++;return running;
                });
        }
        catch(InvalidOperationException){rejected=true;}
        Check(rejected&&observed==1&&
            original.SequenceEqual(File.ReadAllBytes(blockedFile)),
            "server process starting between preview and commit prevents INI replacement");
        Check(Directory.GetFiles(Path.GetDirectoryName(blockedFile)!,
                "*.untamed-stage-*").Length==0,
            "blocked commit does not leave an executable temporary replacement");
        Check(!Directory.GetFiles(Path.GetDirectoryName(blockedFile)!,
                "*.bak").Any(),
            "failed process recheck leaves original INI without claiming a backup");

        string allowedInstall=Base("allowed");
        string allowedFile=Create(allowedInstall);
        var baseline=SafeConfigEditor.Read(
            allowedFile,allowedInstall,ConfigKind.GameIni,stopped);
        int secondCheck=0;
        string backup=SafeConfigEditor.Save(baseline,
            SafeConfigEditor.SetIniValue(baseline,
                "ServerSettings","XPMultiplier","6"),
            allowedInstall,stopped,false,()=>{
                secondCheck++;return stopped;
            });
        Check(secondCheck==1&&File.Exists(backup)&&
            File.ReadAllText(allowedFile).Contains("XPMultiplier=6") &&
            File.ReadAllText(backup).Contains("XPMultiplier=2"),
            "second confirmed-stopped observation permits atomic commit with intact protection copy");
    }
}
