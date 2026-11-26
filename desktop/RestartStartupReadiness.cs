using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Independent, read-only NEW-instance readiness witness after Restart.
/// A new Windows PID or visible console is never enough. The same exact
/// ASA PID/start epoch is checked before AND after RCON authentication.
/// This observer never starts/stops ASA or changes Store/mods/sessions.
/// </summary>
internal sealed record RestartStartupProof(
    int AsaPid,DateTimeOffset AsaStartedUtc,
    bool LoaderRunning,bool AsaRunning,bool ApiDllLoaded,
    bool PluginsLoaded,bool RconResponding,bool JoinAdvertised,
    string Reason)
{
    internal bool ApiLogInitialized {get;init;}
    internal bool PluginLogInitialized {get;init;}
    internal bool Ready(bool apiMode)=>
        AsaRunning&&RconResponding&&JoinAdvertised&&
        (!apiMode||(LoaderRunning&&ApiDllLoaded&&ApiLogInitialized&&
            PluginsLoaded&&PluginLogInitialized));
}

internal static class RestartStartupReadiness
{
    private static bool Same(ProcessIdentity? current,ProcessIdentity expected)=>
        current!=null&&current.Pid==expected.Pid&&
        current.StartedUtc==expected.StartedUtc&&
        string.Equals(current.ExecutablePath,expected.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);

    // Shared Loader-to-ASA adoption for both restart paths. Like YARK's
    // process-start adoption, this only observes a child of the launched
    // Loader; it never starts another process or treats the parent as ASA.
    internal static async Task<ProcessCheck> WaitForApiChildAsync(
        LocalMap map,ProcessIdentity loader,int oldPid,
        Func<LocalMap,ProcessCheck> inspect,Func<int,int,bool> portOwner,
        CancellationToken ct,TimeSpan? maximum=null,TimeSpan? interval=null)
    {
        var duration=maximum??FreshStoreApiPairObserver.StartupObservationWindow;
        var poll=interval??TimeSpan.FromMilliseconds(850);
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        int missingLoader=0;
        bool childEverSeen=false;
        while(elapsed.Elapsed<duration)
        {
            ct.ThrowIfCancellationRequested();
            var family=inspect(map);
            bool sameLoader=Same(family.LoaderIdentity,loader);
            if(sameLoader)missingLoader=0;
            else if(++missingLoader>=3)
                throw new InvalidOperationException("UM-RESTART-API-LOADER-EXITED-OR-REPLACED");
            var child=family.Identity;
            bool correctChild=child is {Pid:>0}&&child.Pid!=oldPid&&child.Pid!=loader.Pid&&
                string.Equals(child.ExecutablePath,map.ExecutablePath,StringComparison.OrdinalIgnoreCase);
            if(correctChild)childEverSeen=true;
            else if(childEverSeen)
                throw new InvalidOperationException("UM-RESTART-NEW-ASA-EXITED-OR-REPLACED");
            if(sameLoader&&correctChild&&family.Running&&!family.ConfirmedStopped&&
                portOwner(map.RconPort,child!.Pid))return family;
            var remaining=duration-elapsed.Elapsed;
            if(remaining<=TimeSpan.Zero)break;
            await Task.Delay(poll<remaining?poll:remaining,ct);
        }
        throw new TimeoutException("UM-RESTART-API-CHILD-RCON-NOT-VERIFIED");
    }

    private static bool LoadedPlugins(LocalMap map,ProcessIdentity child)
    {
        if(!map.StartWithAsaApi)return true;
        try
        {
            string root=Path.GetFullPath(map.InstallationPath);
            string folder=Path.Combine(root,"ShooterGame","Binaries","Win64","ArkApi","Plugins");
            // An absent/empty Plugins directory is a valid empty plugin set;
            // the current-run ArkApi log separately proves plugin initialization.
            if(!Directory.Exists(folder))return true;
            var expected=Directory.GetDirectories(folder).Select(dir=>
                Path.Combine(dir,Path.GetFileName(dir)+".dll"))
                .Where(File.Exists).ToArray();
            if(expected.Length==0)return true;
            var loaded=AsaApiModuleEvidence.ObserveLoadedPaths(map,child);
            if(loaded==null)return false;
            // Named plugin DLL in its own enabled plugin directory is a
            // concrete installed plugin entry; helper DLLs are not counted
            // as separate plugins. Check ALL such entries.
            foreach(var file in expected)
            {
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||
                   !loaded.Contains(Path.GetFullPath(file)))
                    return false;
            }
            return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {return false;}
    }

    internal static async Task<RestartStartupProof> ObserveAsync(
        LocalMap map,ProcessIdentity expectedChild,
        ProcessIdentity? expectedLoader,Func<LocalMap,ProcessCheck> inspect,
        Func<int,int,bool> portOwner,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(expectedChild);
        var state=inspect(map);
        bool asa=state.Running&&!state.ConfirmedStopped&&
            Same(state.Identity,expectedChild);
        bool loader=!map.StartWithAsaApi||
            expectedLoader!=null&&Same(state.LoaderIdentity,expectedLoader);
        if(!asa||!loader)
            return new(expectedChild.Pid,expectedChild.StartedUtc,
                loader,asa,false,false,false,false,
                !asa?"UM-RESTART-NEW-ASA-EXITED-OR-REPLACED":
                    "UM-RESTART-API-LOADER-EXITED-OR-REPLACED");
        var runtime=AsaRuntimeLogEvidence.Inspect(map,expectedChild,
            map.StartWithAsaApi?expectedLoader:null);
        // Keep OS module evidence, current-run API log evidence and plugin
        // completion evidence separate. The old combined boolean reported
        // "DLL not loaded" even when only the log timestamp/parser was late.
        bool apiDll=!map.StartWithAsaApi||
            AsaApiModuleEvidence.IsLoaded(map,expectedChild);
        bool apiLog=!map.StartWithAsaApi||runtime.ApiInitialized;
        bool plugins=!map.StartWithAsaApi||
            apiDll&&LoadedPlugins(map,expectedChild);
        bool pluginLog=!map.StartWithAsaApi||runtime.PluginsInitialized;
        bool rcon=false;
        if(portOwner(map.RconPort,expectedChild.Pid))
        {
            try
            {
                string credential=SecureStore.Unprotect(map.RconPasswordProtected);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(9));
                _=await RconProtocol.Execute("127.0.0.1",map.RconPort,
                    credential,"ListPlayers",deadline.Token);
                var after=inspect(map);
                rcon=after.Running&&!after.ConfirmedStopped&&
                    Same(after.Identity,expectedChild)&&
                    (!map.StartWithAsaApi||
                       expectedLoader!=null&&Same(after.LoaderIdentity,expectedLoader))&&
                    portOwner(map.RconPort,expectedChild.Pid);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or
                System.Net.Sockets.SocketException or OperationCanceledException)
            {
                if(ct.IsCancellationRequested)ct.ThrowIfCancellationRequested();
                // Late RCON or temporary network response is NOT readiness.
            }
        }
        string reason=!runtime.JoinAdvertised?"UM-RESTART-JOIN-NOT-ADVERTISED":
            !apiDll?"UM-RESTART-ASA-API-DLL-NOT-LOADED":
            !apiLog?"UM-RESTART-ASA-API-LOG-NOT-INITIALIZED":
            !plugins?"UM-RESTART-PLUGIN-DLL-NOT-LOADED":
            !pluginLog?"UM-RESTART-PLUGIN-LOG-NOT-INITIALIZED":
            !rcon?"UM-RESTART-RCON-NOT-RESPONDING":"UM-RESTART-READY";
        return new(expectedChild.Pid,expectedChild.StartedUtc,
            loader,asa,apiDll,plugins,rcon,runtime.JoinAdvertised,reason)
        {
            ApiLogInitialized=apiLog,
            PluginLogInitialized=pluginLog
        };
    }

    internal static async Task<RestartStartupProof> WaitAsync(
        Func<CancellationToken,Task<RestartStartupProof>> observe,bool apiMode,
        TimeSpan maximum,TimeSpan interval,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observe);
        if(maximum<=TimeSpan.Zero||maximum>TimeSpan.FromMinutes(12)||
           interval<=TimeSpan.Zero||interval>TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(maximum));
        // One deadline covers both the probe and the polling delay. A slow
        // RCON/readiness observer must not extend the caller's entire budget.
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(maximum);
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        RestartStartupProof? last=null;
        try
        {
            while(true)
            {
                budget.Token.ThrowIfCancellationRequested();
                var sample=await observe(budget.Token).WaitAsync(budget.Token);
                budget.Token.ThrowIfCancellationRequested();
                if(elapsed.Elapsed>=maximum)break;
                last=sample;
                if(last.Ready(apiMode))return last;
                // A dead or replaced exact child/Loader is terminal. This
                // observer never dispatches another Start or Dino wipe.
                if(!last.AsaRunning||apiMode&&!last.LoaderRunning)return last;
                var remaining=maximum-elapsed.Elapsed;
                if(remaining<=TimeSpan.Zero)break;
                if(interval>=remaining)
                {
                    // There is no scheduled poll before the deadline.
                    // Windows timers may complete a fractional millisecond
                    // early: do not issue a last unscheduled probe.
                    await Task.Delay(remaining,budget.Token);
                    break;
                }
                await Task.Delay(interval,budget.Token);
            }
        }
        catch(OperationCanceledException) when(budget.IsCancellationRequested&&!ct.IsCancellationRequested)
        {
            // A deadline is a readiness failure, not a successful restart.
        }
        ct.ThrowIfCancellationRequested();
        if(last==null)throw new TimeoutException("UM-RESTART-READINESS-TIMEOUT");
        return last with{Reason="UM-RESTART-READINESS-TIMEOUT"};
    }
}
