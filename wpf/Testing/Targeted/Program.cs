using System;
using System.IO;
using System.Threading.Tasks;

if(args.Length==3&&args[0]=="--scheduler-claim-child")
{await SchedulerConcurrencyTests.Hold(args[1],args[2]);return;}
if(args.Length!=2||!File.Exists(args[0])||!File.Exists(args[1])||
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
await AsaApiIsolatedInstallerTests.Run();
await ModArgumentSafetyTests.Run(root);
await GuardedPluginUpdateTests.Run(root);
await IsolatedRconAdminQueueTests.Run(args[1]);
await SchedulerConcurrencyTests.Run(root);
UpdateOwnershipTests.Run(root);
await ManagedBackupRetentionTests.Run(root);
await RconTransportTests.Run();
DurableWatchdogTrackerTests.Run(root);
SafeConfigRecheckTests.Run(root);
RecurringReadOnlyRecoveryTests.Run(root);
Console.WriteLine("PASS T11-T13/T16 isolated Windows targeted regression suite");
