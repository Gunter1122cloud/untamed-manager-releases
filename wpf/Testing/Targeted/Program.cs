using System;
using System.IO;
using System.Threading.Tasks;

if(args.Length!=1||!File.Exists(args[0])||
   !Path.GetFileName(args[0]).Equals("FakeSteamCMD.exe",StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Explicit isolated FakeSteamCMD.exe required");
string root=Path.Combine(Path.GetTempPath(),
    "Untamed-Integration-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root,"steamcmd.exe"),"offline synthetic steamcmd file");
await SteamCmdIsolatedUpdaterTests.Run(root,args[0]);
await ModDependencyPlannerTests.Run();
await CurseForgeDependencyResolverTests.Run();
await AsaApiReleaseStagingTests.Run();
Console.WriteLine("PASS T11-T13 isolated Windows targeted regression suite");
