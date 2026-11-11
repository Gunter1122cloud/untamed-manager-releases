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
    string Explanation,bool ApiRequired=false)
{
    // Runtime-ready means current PID + authenticated RCON + this launch's
    // join-advertising log. API is required ONLY for opt-in Loader launches.
    // This does NOT prove that a real player joined or travelled between maps.
    public bool ProductionReady =>
        Stage==AsaRuntimeStage.RconAuthenticated && JoinAdvertised &&
        (!ApiRequired || ApiInitialized);
}

public static class AsaReadyProbe
{
    private static AsaReadinessSnapshot Result(LocalMap map,AsaRuntimeStage stage,
        string explanation,bool loader=false,bool api=false,bool join=false) =>
        new(map.Id,DateTimeOffset.UtcNow,stage,loader,api,join,explanation,
            map.StartWithAsaApi);

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
            for(DirectoryInfo? dir=new(Path.GetDirectoryName(expected)!);dir!=null;dir=dir.Parent)
                if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)return false;
            if(!Path.GetFullPath(map.ExecutablePath).Equals(expected,StringComparison.OrdinalIgnoreCase)||
               !File.Exists(expected)||(File.GetAttributes(expected)&FileAttributes.ReparsePoint)!=0)
                return false;
            // Only explicit ASA API launch needs an authenticated loader.
            // An inactive cached LoaderPath never blocks direct ASA mode.
            if(map.StartWithAsaApi)
            {
                if(string.IsNullOrWhiteSpace(map.ApiLoaderPath))return false;
                string loader=Path.GetFullPath(map.ApiLoaderPath);
                if(!loader.StartsWith(Path.GetFullPath(map.InstallationPath).TrimEnd(
                    Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)||
                   !File.Exists(loader)||(File.GetAttributes(loader)&FileAttributes.ReparsePoint)!=0)
                    return false;
                for(DirectoryInfo? dir=new(Path.GetDirectoryName(loader)!);dir!=null;dir=dir.Parent)
                    if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)return false;
            }
            return true;
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException
                                 or NotSupportedException){return false;}
    }

    // Never trust a successful RCON authentication at a port that multiple
    // locally owned profiles claim. The response could belong to another map.
    internal static bool HasPortConflict(LocalMap map,
        System.Collections.Generic.IEnumerable<LocalMap> localMaps)
    {
        if(map==null||localMaps==null)throw new ArgumentNullException(nameof(map));
        return localMaps.Any(other=>other.Id!=map.Id &&
            ((map.RconPort>0 && other.RconPort==map.RconPort) ||
             (map.GamePort>0 && other.GamePort==map.GamePort)));
    }

    // A declared command-line port cannot silently disagree with the GUI
    // endpoint. A missing flag is not proof of a default; it stays unverified.
    internal static bool DeclaredPortMismatch(LocalMap map)
    {
        if(map==null)throw new ArgumentNullException(nameof(map));
        static bool Mismatched(string arguments,string expression,int configured)
        {
            var matches=System.Text.RegularExpressions.Regex.Matches(arguments??"",
                expression,System.Text.RegularExpressions.RegexOptions.IgnoreCase|
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if(matches.Count>1)return true;
            if(matches.Count==0)return false;
            return !int.TryParse(matches[0].Groups["port"].Value,out int value) ||
                value is <1 or >65535 || value!=configured;
        }
        return Mismatched(map.Arguments,
                   @"(?:^|[\s?])(?:-)?RCONPort\s*=\s*(?<port>\d+)(?=$|[\s?])",map.RconPort) ||
               Mismatched(map.Arguments,
                   @"(?:^|\s)-Port\s*=\s*(?<port>\d+)(?=$|\s)",map.GamePort);
    }

    public static async Task<AsaReadinessSnapshot> ProbeAsync(ClusterService cluster,
        Guid mapId,CancellationToken cancellationToken=default)
    {
        if(cluster==null)throw new ArgumentNullException(nameof(cluster));
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId) ??
            throw new UnauthorizedAccessException("Only a registered local map can be probed");
        if(HasPortConflict(map,cluster.Config.LocalMaps))
            return Result(map,AsaRuntimeStage.Unconfigured,
                "Conflicting local map/RCON port ownership; readiness probe refused");
        if(DeclaredPortMismatch(map))
            return Result(map,AsaRuntimeStage.Unconfigured,
                "Launch arguments disagree with configured game or RCON port");
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
        static bool Matches(string observed,string configured)
        {
            try{return Path.GetFullPath(observed).Equals(Path.GetFullPath(configured),
                StringComparison.OrdinalIgnoreCase);}
            catch(Exception ex) when(ex is IOException or ArgumentException or NotSupportedException)
            {return false;}
        }
        if(!before.Running||before.ConfirmedStopped||before.Identity is null||
           before.Identity.Pid<=0||
           !Matches(before.Identity.ExecutablePath,map.ExecutablePath)||
           !string.Equals(before.Identity.Arguments,map.Arguments,StringComparison.Ordinal)||
           (map.StartWithAsaApi!=(before.LoaderIdentity is not null))||
           (before.LoaderIdentity!=null &&
            (before.LoaderIdentity.Pid<=0 ||
             !Matches(before.LoaderIdentity.ExecutablePath,map.ApiLoaderPath)||
             !string.Equals(before.LoaderIdentity.Arguments,map.ApiLoaderArguments,StringComparison.Ordinal))))
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
            var log=AsaRuntimeLogEvidence.Inspect(map,before.Identity,
                before.LoaderIdentity);
            cancellationToken.ThrowIfCancellationRequested();
            // Reading a large rolling game/API log takes time. The ASA child
            // or Loader may exit and a PID may be reused AFTER RCON succeeds.
            // A third independent local OS inspection closes this race.
            var final=inspect();
            if(!final.Running||final.ConfirmedStopped||
               !Identical(before.Identity,final.Identity)||
               !Identical(before.LoaderIdentity,final.LoaderIdentity))
                return Result(map,AsaRuntimeStage.ProcessVerified,
                    "ASA/Loader process changed while confirming runtime logs; readiness refused");
            bool ready=log.JoinAdvertised &&
                (!map.StartWithAsaApi||log.ApiInitialized);
            return Result(map,AsaRuntimeStage.RconAuthenticated,
                ready?
                "ASA PID, RCON and current startup join-advertising log confirmed; live client join/travel remains unverified":
                "ASA PID and RCON confirmed; current startup join-advertising or required API load still unverified",
                hasLoader,log.ApiInitialized,log.JoinAdvertised);
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
