using System;
using System.IO;
using System.Threading;

// Not SteamCMD. A deterministic test binary that never downloads anything.
// It uses the real SteamCMD argument shape and touches only an isolated TEMP
// fixture marker. The v1.5 CI checks failed exit, timeout and INI tampering.
string[] a=Environment.GetCommandLineArgs().Skip(1).ToArray();
int install=Array.IndexOf(a,"+force_install_dir");
int app=Array.IndexOf(a,"+app_update");
if(install<0 || install+1>=a.Length || app<0 || app+1>=a.Length ||
   a[app+1]!="2430930" || !a.Contains("validate") ||
   !a.Contains("+quit") || !a.Contains("+login"))
{
    Console.Error.WriteLine("Synthetic SteamCMD received invalid app arguments");
    return 12;
}
string root=Path.GetFullPath(a[install+1]);
if(!Path.IsPathFullyQualified(root) ||
   !root.Contains("Untamed-Integration-",StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("FakeSteamCMD refused non-synthetic root");
    return 13;
}
if(Environment.GetEnvironmentVariable("FAKE_STEAM_TIMEOUT")=="1")
    await Task.Delay(TimeSpan.FromSeconds(60));
if(Environment.GetEnvironmentVariable("FAKE_STEAM_FAIL")=="1")
{
    Console.Error.WriteLine("Simulated incomplete download");
    return 7;
}
Directory.CreateDirectory(root);
if(Environment.GetEnvironmentVariable("FAKE_STEAM_TOUCH_INI")=="1")
{
    string ini=Path.Combine(root,"ShooterGame","Saved","Config","WindowsServer","Game.ini");
    if(File.Exists(ini))File.AppendAllText(ini,"\n[CORRUPTED]");
}
File.WriteAllText(Path.Combine(root,"synthetic-steam-success.marker"),
    "FakeSteamCMD completed test, not a real game installation");
Console.WriteLine("Success! App '2430930' fully installed.");
return 0;