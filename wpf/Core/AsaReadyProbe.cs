using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// T02: real read-only ASA readiness observations. This is NOT a release gate
// for SaveWorld, Store writes, Start/Stop/Restart or character transfers.
public enum AsaRuntimeStage
{
    Unconfigured,
    ExecutableVerified,
    ProcessVerified,
    RconAuthenticated
}

public sealed record AsaReadinessSnapshot(
    Guid MapId, DateTimeOffset ObservedUtc, AsaRuntimeStage Stage,
    bool LoaderObserved, bool ApiInitialized, bool JoinAdvertised,
    string Explanation)
{
    // A successful ListPlayers response does not prove that an ASA client can join.
    public bool ProductionReady =>
        Stage == AsaRuntimeStage.RconAuthenticated && ApiInitialized && JoinAdvertised;
}

public static class AsaReadyProbe
{
    private static AsaReadinessSnapshot Result(LocalMap map,AsaRuntimeStage stage,
        string explanation,bool loader=false) =>
        new(map.Id,DateTimeOffset.UtcNow,stage,loader,false,false,explanation);

    private static bool Identical(ProcessIdentity? a,ProcessIdentity? b)
    {
        if(a is null || b is null)return a is null && b is null;
        return a.Pid>0 && a.Pid==b.Pid && a.StartedUtc==b.StartedUtc &&
            string.Equals(a.ExecutablePath,b.ExecutablePath,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Arguments,b.Arguments,StringComparison.Ordinal);
    }

    private static bool IsFixedLocalRoot(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
           path.StartsWith(@"\\",StringComparison.Ordinal))return false;
        try
        {
            string full=Path.GetFullPath(path);
            var drive=new DriveInfo(Path.GetPathRoot(full)!);
            if(drive.DriveType!=DriveType.Fixed)return false;
            for(DirectoryInfo? dir=new(full);dir!=null;dir=dir.Parent)
                if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)return false;
            return true;
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException
                                 or NotSupportedException){return false;}
    }

    // Path checks are independent from process checks and never launch executables.
    public static bool IsRegisteredServerBinary(LocalMap map)
    {
        if(map==null||!IsFixedLocalRoot(map.InstallationPath))return false;
        try
        {
            var expected=Path.GetFullPath(Path.Combine(map.InstallationPath,
                "ShooterGame","Binaries","Win64","ArkAscendedServer.exe"));
            if(!Path.GetFullPath(map.ExecutablePath).Equals(expected,StringComparison.OrdinalIgnoreCase)||
               !File.Exists(expected)||(File.GetAttributes(expected)&FileAttributes.ReparsePoint)!=0)
                return false;
            // The loader is optional for vanilla ASA; when configured it must
            // exist as a real local executable, never as a junction/UNC alias.
            if(!string.IsNullOrWhiteSpace(map.ApiLoaderPath))
            {
                string loader=Path.GetFullPath(map.ApiLoaderPath);
                if(!loader.StartsWith(Path.GetFullPath(map.InstallationPath).TrimEnd(
                    Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)||
                   !File.Exists(loader)||(File.GetAttributes(loader)&FileAttributes.ReparsePoint)!=0)
                    return false;
            }
            return true;
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException
                                 or NotSupportedException){return false;}
    }

    public static async Task<AsaReadinessSnapshot> ProbeAsync(ClusterService cluster,
        Guid mapId,CancellationToken cancellationToken=default)
    {
        if(cluster==null)throw new ArgumentNullException(nameof(cluster));
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId) ??
            throw new UnauthorizedAccessException("Only a registered local map can be probed");
        return await ObserveAsync(map,()=>cluster.Check(map),
            async ct=>await RconProtocol.Execute(map.RconHost,map.RconPort,
                SecureStore.Unprotect(map.RconPasswordProtected),"ListPlayers",ct),
            cancellationToken);
    }

    // Dependency-injected process/RCON transport for isolated Windows regression.
    // Installation and ownership preconditions are still enforced by ProbeAsync.
    internal static async Task<AsaReadinessSnapshot> ObserveAsync(LocalMap map,
        Func<ProcessCheck> inspect,Func<CancellationToken,Task<string>> listPlayers,
        CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(map==null||inspect==null||listPlayers==null)
            throw new ArgumentNullException(nameof(map));
        if(!IsRegisteredServerBinary(map))
            return Result(map,AsaRuntimeStage.Unconfigured,
                "ASA executable or loader path is not an authenticated local installation");
        if(map.Maintenance)
            return Result(map,AsaRuntimeStage.ExecutableVerified,
                "Maintenance mode: RCON readiness probe not performed");
        var before=inspect();
        if(!before.Running||before.ConfirmedStopped||before.Identity is null||
           !Path.GetFullPath(before.Identity.ExecutablePath).Equals(
               Path.GetFullPath(map.ExecutablePath),StringComparison.OrdinalIgnoreCase)||
           !string.Equals(before.Identity.Arguments,map.Arguments,StringComparison.Ordinal)||
           (string.IsNullOrWhiteSpace(map.ApiLoaderPath)!= (before.LoaderIdentity is null)))
            return Result(map,AsaRuntimeStage.ExecutableVerified,
                "ASA process or loader family is not unambiguously identified");
        bool hasLoader=before.LoaderIdentity!=null;
        if(map.RconPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(map.RconPasswordProtected))
            return Result(map,AsaRuntimeStage.ProcessVerified,
                "RCON address or local credential is not configured",hasLoader);
        try
        {
            string response=await listPlayers(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var after=inspect();
            if(!after.Running||after.ConfirmedStopped||
               !Identical(before.Identity,after.Identity)||
               !Identical(before.LoaderIdentity,after.LoaderIdentity))
                return Result(map,AsaRuntimeStage.ProcessVerified,
                    "ASA process identity changed during RCON request; readiness refused",hasLoader);
            if(string.IsNullOrWhiteSpace(response))
                return Result(map,AsaRuntimeStage.ProcessVerified,
                    "RCON reply was empty; readiness cannot be confirmed",hasLoader);
            return Result(map,AsaRuntimeStage.RconAuthenticated,
                "ASA process and local RCON ListPlayers verified; API initialization and client join remain unverified",
                hasLoader);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException
                                 or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            // Do not echo private paths, auth responses, credentials or raw exception messages.
            return Result(map,AsaRuntimeStage.ProcessVerified,
                "RCON authentication/transport refused or timed out; no readiness claim",hasLoader);
        }
    }
}
