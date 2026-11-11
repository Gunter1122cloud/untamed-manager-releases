using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Authorizes only OFFLINE, LOCAL, INSTALLATION-SCOPED configuration edits
/// for an already imported original Kova map. Never grants Store writer,
/// travel, SteamCMD or lifecycle ownership. Called again under native file
/// transaction locks before each mutating operation.
/// </summary>
public static class HistoricalLocalConfigGuard
{
    public static ProcessCheck DemandStopped(ClusterService cluster,LocalMap map,
        bool underOwnOperation=false)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(map);
        if(!cluster.Config.LocalMaps.Any(m=>ReferenceEquals(m,map))||
           !map.HistoricalStoreProtected||map.ObservedReadOnlyImported||
           map.Maintenance||
           string.IsNullOrWhiteSpace(map.HistoricalMapKey)||
           string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)||
           map.HistoricalArchiveSha256.Length!=64||
           !map.HistoricalArchiveSha256.All(Uri.IsHexDigit))
            throw new UnauthorizedAccessException("UM-HISTORICAL-CONFIG-IDENTITY-NOT-IMPORTED");
        if(string.IsNullOrWhiteSpace(map.InstallationPath)||
           !Path.IsPathFullyQualified(map.InstallationPath)||
           map.InstallationPath.StartsWith(@"\\",StringComparison.Ordinal))
            throw new UnauthorizedAccessException("UM-HISTORICAL-CONFIG-LOCAL-ROOT");
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(map.InstallationPath));
        if(!Directory.Exists(root)||
           new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-HISTORICAL-CONFIG-INSTALL-MISSING");
        for(DirectoryInfo? dir=new(root);dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new UnauthorizedAccessException("UM-HISTORICAL-CONFIG-REPARSE");
        if(map.Arguments.Contains("-converttostore",StringComparison.OrdinalIgnoreCase)||
           map.Arguments.Contains("-convertfromstore",StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("UM-HISTORICAL-CONFIG-CONVERSION-PROTECTED");
        new AsaManualRestartJournal(root).DemandNoPending();
        var journal=underOwnOperation?null:cluster.Operations.Observe(root);
        if(journal?.Phase is (UntamedOperationPhase.Prepared or
            UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator))
            throw new InvalidOperationException("UM-HISTORICAL-CONFIG-OPERATION-PENDING");

        ProcessCheck? chosen=null;
        int sameRoot=0;
        foreach(var other in cluster.Config.LocalMaps)
        {
            if(string.IsNullOrWhiteSpace(other.InstallationPath))continue;
            var sibling=Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(other.InstallationPath));
            if(!string.Equals(root,sibling,StringComparison.OrdinalIgnoreCase))
            {
                if(root.StartsWith(sibling+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)||
                   sibling.StartsWith(root+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("UM-HISTORICAL-CONFIG-NESTED-INSTALL");
                continue;
            }
            ++sameRoot;
            if(other.Maintenance||other.ObservedReadOnlyImported)
                throw new UnauthorizedAccessException("UM-HISTORICAL-CONFIG-OTHER-MANAGER");
            var state=cluster.Check(other);
            if(!state.ConfirmedStopped||state.Running||
               state.Identity!=null||state.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-HISTORICAL-CONFIG-ASA-OR-LOADER-RUNNING");
            if(ReferenceEquals(other,map))chosen=state;
        }
        if(sameRoot==0||chosen==null)
            throw new InvalidOperationException("UM-HISTORICAL-CONFIG-NO-OWNER");
        return chosen;
    }
}
