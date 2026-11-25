using System;
using System.Globalization;
using System.IO;
using System.Text;
using UntamedManager.Core;

/// <summary>
/// Tests current-run ArkApi evidence independently from Windows module
/// enumeration. These fixtures never launch ASA or an API Loader.
/// </summary>
internal static class AsaRuntimeLogEvidenceTests
{
    private static void Check(bool value,string reason)
    {
        if(!value)throw new InvalidOperationException(
            "ASA RUNTIME LOG EVIDENCE TEST FAILED: "+reason);
        Console.WriteLine("PASS ASA runtime log evidence: "+reason);
    }

    public static void Run(string testRoot)
    {
        string install=Path.Combine(testRoot,"asa-runtime-log-evidence");
        string apiLog=Path.Combine(install,"ShooterGame","Win64","logs","ArkApi.log");
        Directory.CreateDirectory(Path.GetDirectoryName(apiLog)!);
        var map=new LocalMap{Id=Guid.NewGuid(),Name="Astraeos",
            InstallationPath=install,StartWithAsaApi=true};
        var server=new ProcessIdentity{
            Pid=2272,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-1),
            ExecutablePath=Path.Combine(install,"ShooterGame","Binaries","Win64",
                "ArkAscendedServer.exe")
        };
        var loader=new ProcessIdentity{
            Pid=18336,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-2),
            ExecutablePath=Path.Combine(install,"ShooterGame","Binaries","Win64",
                "AsaApiLoader.exe")
        };
        string stamp=DateTime.Now.ToString("MM/dd/yy HH:mm",CultureInfo.InvariantCulture);
        string Line(string message)=>stamp+" [API] "+message;
        File.WriteAllText(apiLog,string.Join("\r\n",new[]{
            Line("Loading..."),
            Line("API was successfully loaded"),
            Line("Initialized hooks"),
            Line("Loading plugins.."),
            Line("Loaded all plugins")
        })+"\r\n",new UTF8Encoding(false));
        var ready=AsaRuntimeLogEvidence.Inspect(map,server,loader);
        Check(ready.ApiInitialized&&ready.PluginsInitialized,
            "current Loader-run API and plugin success markers are independently recognized");

        // Current ASA API builds rotate their log using the ASA child PID.
        string pidLog=Path.Combine(Path.GetDirectoryName(apiLog)!,
            "ArkApi_"+server.Pid+"_2026-11-25_07-00.log");
        File.WriteAllText(pidLog,string.Join("\r\n",new[]{
            Line("Loading..."),Line("API was successfully loaded"),
            Line("Initialized hooks"),Line("Loaded all plugins")
        })+"\r\n",new UTF8Encoding(false));
        var pidScoped=AsaRuntimeLogEvidence.Inspect(map,server,loader);
        Check(pidScoped.ApiInitialized&&pidScoped.PluginsInitialized,
            "rotated ArkApi_<ASA PID> logs are discovered and bound to the exact ASA child");
        File.Delete(pidLog);

        File.WriteAllText(apiLog,string.Join("\r\n",new[]{
            Line("Loading..."),
            Line("API was successfully loaded"),
            Line("Initialized hooks"),
            Line("Loading plugins..")
        })+"\r\n",new UTF8Encoding(false));
        var missingPluginCompletion=AsaRuntimeLogEvidence.Inspect(map,server,loader);
        Check(missingPluginCompletion.ApiInitialized&&
              !missingPluginCompletion.PluginsInitialized,
            "API initialization is not confused with plugin completion");

        File.WriteAllText(apiLog,string.Join("\r\n",new[]{
            Line("Loading..."),
            Line("API was successfully loaded"),
            Line("Initialized hooks"),
            Line("Loaded all plugins"),
            Line("Loading...")
        })+"\r\n",new UTF8Encoding(false));
        var laterAttempt=AsaRuntimeLogEvidence.Inspect(map,server,loader);
        Check(!laterAttempt.ApiInitialized&&!laterAttempt.PluginsInitialized,
            "a later incomplete Loader attempt invalidates an earlier successful log pair");

        File.WriteAllText(apiLog,string.Join("\r\n",new[]{
            Line("Loading..."),
            Line("API was successfully loaded"),
            Line("Initialized hooks"),
            Line("Loaded all plugins")
        })+"\r\n",new UTF8Encoding(false));
        var stale=AsaRuntimeLogEvidence.Inspect(map,server,
            new ProcessIdentity{Pid=loader.Pid,ExecutablePath=loader.ExecutablePath,
                Arguments=loader.Arguments,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(8)});
        Check(!stale.ApiInitialized&&!stale.PluginsInitialized,
            "positive API log markers predating the exact Loader start epoch are rejected");

        File.Delete(apiLog);
        string wrongPidLog=Path.Combine(Path.GetDirectoryName(apiLog)!,
            "ArkApi_16476_2026-11-25_07-00.log");
        File.WriteAllText(wrongPidLog,string.Join("\r\n",new[]{
            Line("Loading..."),Line("API was successfully loaded"),
            Line("Initialized hooks"),Line("Loaded all plugins")
        })+"\r\n",new UTF8Encoding(false));
        var wrongPid=AsaRuntimeLogEvidence.Inspect(map,server,loader);
        Check(!wrongPid.ApiInitialized&&!wrongPid.PluginsInitialized,
            "a successful ArkApi log belonging to a different ASA PID is rejected");
    }
}
